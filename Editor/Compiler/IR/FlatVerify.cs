using System;
using System.Collections.Generic;

// ============================================================================
// FlatVerify — post-condition verifier for a flattened Core function. Re-arms, as a checked
// invariant, the guarantee that a separate flat-operand type would give for free (operands cannot
// nest calls) but which the unified CValue gives up.
// Asserts: (1) every block has exactly one terminator; (2) terminator targets exist;
// (3) flat-block instruction operands are leaves (no nested calls); (4) CFieldRef(Load) never
// appears as a flat operand (must be materialized via CLoadField); (5) CFieldRef(Addr) appears
// only as a direct extern/internal-call argument; (6) no structured statement leaked into a flat block;
// (7, module overload) every slot/field COPY and internal-call boundary is type-compatible under
// DeclaredRelaxations. The module overload is the production entry point.
// ============================================================================

public static class FlatVerify
{
    /// <summary>Production flat-IR verification. The function-only overload checks shape; this
    /// module-aware entry also proves every COPY-equivalent edge against the authoritative slot,
    /// field, and callee declarations after flattening/coalescing/spill insertion.</summary>
    public static void Verify(CModule module)
    {
        if (module == null) throw new ArgumentNullException(nameof(module));

        var fields = CoreVerify.BuildFieldIndex(module);
        var functions = new Dictionary<string, CFunction>(StringComparer.Ordinal);
        foreach (var function in module.Functions)
            if (!functions.TryAdd(function.Name, function))
                throw new VerificationException(
                    $"Duplicate function declaration '{function.Name}' in flat module.");

        foreach (var function in module.Functions)
        {
            Verify(function);
            VerifyTypes(function, module.TypeFacts, fields, functions);
        }
    }

    public static void Verify(CFunction f)
    {
        if (f.Shape != Shape.Flat)
            throw new InvalidOperationException($"FlatVerify requires Shape=Flat, got {f.Shape} for {f.Name}");

        var blockIds = new HashSet<int>();
        foreach (var b in f.FlatBlocks)
        {
            if (!blockIds.Add(b.Id))
                throw new InvalidOperationException($"Duplicate flat block id {b.Id} in {f.Name}");
        }

        foreach (var b in f.FlatBlocks)
        {
            if (b.Terminator == null)
                throw new InvalidOperationException($"Flat block {b.Id} in {f.Name} has no terminator");

            foreach (var inst in b.Stmts)
                VerifyInstruction(inst, f.Name);

            VerifyTerminator(b.Terminator, blockIds, f.Name);
        }

        VerifyReentrantConservation(f);
        VerifyPreSpillStmtsShape(f);
    }

    sealed class TypeContext
    {
        public readonly CFunction Function;
        public readonly UdonTypeFactRegistry TypeFacts;
        public readonly IReadOnlyDictionary<string, StorageType> Fields;
        public readonly IReadOnlyDictionary<string, CFunction> Functions;

        public TypeContext(CFunction function, UdonTypeFactRegistry typeFacts,
            IReadOnlyDictionary<string, StorageType> fields,
            IReadOnlyDictionary<string, CFunction> functions)
        {
            Function = function;
            TypeFacts = typeFacts;
            Fields = fields;
            Functions = functions;
        }

        public StorageType SlotType(int slotId, string context)
        {
            if (slotId < 0 || slotId >= Function.Slots.Count)
                throw new VerificationException(
                    $"Undeclared slot{slotId} in {context} (function '{Function.Name}')");
            return Function.Slots[slotId].Type;
        }

        public void AssertType(StorageType expected, StorageType actual, string context)
        {
            var why = DeclaredRelaxations.WhyIncompatible(expected.Name, actual.Name, TypeFacts);
            if (why == null) return;
            throw new VerificationException(
                $"Flat type mismatch in {context}: expected '{expected}', got '{actual}' - {why} "
                + $"(function '{Function.Name}')");
        }

        public void AssertField(string fieldName, StorageType actual, string context)
        {
            if (!Fields.TryGetValue(fieldName, out var declared))
                throw new VerificationException(
                    $"Undeclared field '{fieldName}' in {context} (function '{Function.Name}')");
            AssertType(declared, actual, $"{context} field '{fieldName}'");
        }
    }

    static void VerifyTypes(CFunction function, UdonTypeFactRegistry typeFacts,
        IReadOnlyDictionary<string, StorageType> fields,
        IReadOnlyDictionary<string, CFunction> functions)
    {
        var ctx = new TypeContext(function, typeFacts, fields, functions);
        for (int i = 0; i < function.Slots.Count; i++)
        {
            var slot = function.Slots[i];
            if (slot.Id != i)
                throw new VerificationException(
                    $"Slot declaration index {i} carries id {slot.Id} (function '{function.Name}')");
            if (slot.Class == SlotClass.Pinned)
            {
                if (string.IsNullOrEmpty(slot.FixedName))
                    throw new VerificationException(
                        $"Pinned slot{i} in function '{function.Name}' has no fixed heap name.");
                ctx.AssertField(slot.FixedName, slot.Type, $"pinned slot{i}");
            }
        }

        foreach (var block in function.FlatBlocks)
        {
            foreach (var instruction in block.Stmts)
                VerifyInstructionTypes(instruction, ctx);
            VerifyTerminatorTypes(block.Terminator, ctx);
        }
    }

    static void VerifyInstructionTypes(CStmt instruction, TypeContext ctx)
    {
        switch (instruction)
        {
            case CAssign assign:
                if (assign.Value is not CLeaf assignValue)
                    throw new VerificationException(
                        $"CAssign value is not a flat leaf: {assign.Value.GetType().Name} "
                        + $"(function '{ctx.Function.Name}')");
                VerifyTypedLeaf(assignValue, ctx, "CAssign value");
                ctx.AssertType(ctx.SlotType(assign.DestSlot, "CAssign"), assignValue.Type,
                    $"CAssign to slot{assign.DestSlot}");
                break;

            case CLoadField load:
                ctx.AssertType(ctx.SlotType(load.DestSlot, "CLoadField"), load.Type,
                    $"CLoadField destination slot{load.DestSlot}");
                ctx.AssertField(load.FieldName, load.Type, "CLoadField");
                break;

            case CStoreField store:
                VerifyTypedLeaf(store.Value, ctx, "CStoreField value");
                ctx.AssertField(store.FieldName, store.Value.Type, "CStoreField");
                break;

            case CExprStmt { Expr: CExternCall call }:
                foreach (var arg in call.Args)
                    VerifyTypedLeaf(arg, ctx, "extern-call argument");
                VerifyCallResult(call.Type, call.DestSlot, ctx, $"extern '{call.Sig}'");
                break;

            case CExprStmt { Expr: CInternalCall call }:
                foreach (var arg in call.Args)
                    VerifyTypedLeaf(arg, ctx, $"internal-call '{call.FuncName}' argument");
                VerifyCallResult(call.Type, call.DestSlot, ctx, $"internal call '{call.FuncName}'");
                VerifyInternalCall(call, ctx);
                break;

            default:
                throw new VerificationException(
                    $"Unknown flat instruction in type verifier: {instruction?.GetType().Name} "
                    + $"(function '{ctx.Function.Name}')");
        }
    }

    static void VerifyCallResult(StorageType resultType, int? destSlot, TypeContext ctx,
        string context)
    {
        if (resultType == StorageTypes.Void)
        {
            if (destSlot.HasValue)
                throw new VerificationException(
                    $"{context} is void but writes slot{destSlot.Value} "
                    + $"(function '{ctx.Function.Name}')");
            return;
        }

        if (!destSlot.HasValue)
            throw new VerificationException(
                $"{context} returns '{resultType}' without a destination slot "
                + $"(function '{ctx.Function.Name}')");
        ctx.AssertType(ctx.SlotType(destSlot.Value, context), resultType,
            $"{context} destination slot{destSlot.Value}");
    }

    static void VerifyInternalCall(CInternalCall call, TypeContext ctx)
    {
        if (call.FuncName == "__indirect")
        {
            if (call.Args.Count != 1)
                throw new VerificationException(
                    $"__indirect requires exactly one method-pointer argument, got {call.Args.Count} "
                    + $"(function '{ctx.Function.Name}')");
            ctx.AssertType(StorageTypes.UInt32, call.Args[0].Type,
                "__indirect method pointer");
            return;
        }

        if (!ctx.Functions.TryGetValue(call.FuncName, out var target))
            throw new VerificationException(
                $"CInternalCall references unknown function '{call.FuncName}' "
                + $"(function '{ctx.Function.Name}')");
        if (call.Args.Count != target.ParamFieldNames.Count)
            throw new VerificationException(
                $"CInternalCall to '{call.FuncName}' passes {call.Args.Count} args but target declares "
                + $"{target.ParamFieldNames.Count} parameter fields (function '{ctx.Function.Name}')");

        for (int i = 0; i < call.Args.Count; i++)
        {
            var parameterField = target.ParamFieldNames[i];
            if (!ctx.Fields.TryGetValue(parameterField, out var parameterType))
                throw new VerificationException(
                    $"CInternalCall target '{call.FuncName}' parameter {i} references undeclared field "
                    + $"'{parameterField}' (function '{ctx.Function.Name}')");
            ctx.AssertType(parameterType, call.Args[i].Type,
                $"CInternalCall '{call.FuncName}' parameter {i} ('{parameterField}')");
        }

        var targetReturnType = target.ReturnType ?? StorageTypes.Void;
        ctx.AssertType(targetReturnType, call.Type,
            $"CInternalCall '{call.FuncName}' result");
        if (call.DestSlot.HasValue)
        {
            if (target.ReturnSlots.Count != 1)
                throw new VerificationException(
                    $"CInternalCall '{call.FuncName}' has a destination but target declares "
                    + $"{target.ReturnSlots.Count} return slots (function '{ctx.Function.Name}')");
            ctx.AssertType(target.ReturnSlots[0].StorageType, call.Type,
                $"CInternalCall '{call.FuncName}' return field");
        }
    }

    static void VerifyTerminatorTypes(CTerminator terminator, TypeContext ctx)
    {
        switch (terminator)
        {
            case CJump:
                break;
            case CBranch branch:
                VerifyTypedLeaf(branch.Cond, ctx, "CBranch condition");
                ctx.AssertType(StorageTypes.Boolean, branch.Cond.Type, "CBranch condition");
                break;
            case CRet ret:
            {
                var returnsVoid = !ctx.Function.ReturnType.HasValue
                    || ctx.Function.ReturnType.Value == StorageTypes.Void;
                if (returnsVoid && ret.Value != null)
                    throw new VerificationException(
                        $"Void function '{ctx.Function.Name}' returns '{ret.Value.Type}'");
                if (!returnsVoid && ret.Value == null && ctx.Function.ReturnSlots.Count == 0)
                    throw new VerificationException(
                        $"Non-void function '{ctx.Function.Name}' returns without a value");
                if (ret.Value != null)
                {
                    VerifyTypedLeaf(ret.Value, ctx, "CRet value");
                    ctx.AssertType(ctx.Function.ReturnType.Value, ret.Value.Type, "CRet");
                }
                break;
            }
            default:
                throw new VerificationException(
                    $"Unknown flat terminator in type verifier: {terminator?.GetType().Name} "
                    + $"(function '{ctx.Function.Name}')");
        }
    }

    static void VerifyTypedLeaf(CLeaf leaf, TypeContext ctx, string context)
    {
        switch (leaf)
        {
            case CSlotRef slot:
                ctx.AssertType(ctx.SlotType(slot.SlotId, context), slot.Type,
                    $"{context} slot{slot.SlotId}");
                break;
            case CFieldAddr field:
                ctx.AssertField(field.FieldName, field.Type, context);
                break;
            case CConst:
            case CFuncRef:
                break;
            default:
                throw new VerificationException(
                    $"{context} is not a typed flat leaf: {leaf?.GetType().Name} "
                    + $"(function '{ctx.Function.Name}')");
        }
    }

    /// <summary>PreSpillStmts positional contract (wave-12 r2 [V1], see <see cref="CExternCall.PreSpillStmts"/>):
    /// a Reentrant SendCustomEvent's PreSpillStmts=N claims the N statements immediately preceding it in the
    /// same flat block are its own param copy-ins — void SetProgramVariable extern calls — so
    /// InsertRecursionSpillsFunc can pull them inside the spill window. Nothing else confirmed that shape
    /// before; a future edit inserting a statement between a hand-emitted copy-in/dispatch pair (e.g.
    /// HandlerBase's interface-setter dispatch) would silently desync the count from reality. Checked
    /// structurally rather than trusting the producer, mirroring VerifyReentrantConservation's stance on
    /// the sibling Reentrant flag.</summary>
    static void VerifyPreSpillStmtsShape(CFunction f)
    {
        foreach (var b in f.FlatBlocks)
        {
            for (int i = 0; i < b.Stmts.Count; i++)
            {
                if (b.Stmts[i] is not CExprStmt { Expr: CExternCall { PreSpillStmts: > 0 } ec }) continue;
                int n = ec.PreSpillStmts;
                if (n > i)
                    throw new InvalidOperationException(
                        $"{f.Name}: block {b.Id} instruction {i}: PreSpillStmts={n} exceeds the {i} statement(s) " +
                        "available before it (a rebuild pass moved or dropped the copy-ins)");

                for (int k = i - n; k < i; k++)
                {
                    if (!IsVoidSetProgramVariableCopyIn(b.Stmts[k]))
                        throw new InvalidOperationException(
                            $"{f.Name}: block {b.Id} instruction {i}: PreSpillStmts={n} expects a void " +
                            $"SetProgramVariable copy-in at index {k}, found {b.Stmts[k]?.GetType().Name} " +
                            "(the spill window would capture the wrong statements)");
                }
            }
        }
    }

    static bool IsVoidSetProgramVariableCopyIn(CStmt stmt)
        => stmt is CExprStmt { Expr: CExternCall { DestSlot: null } call }
           && call.Type.Name == "SystemVoid"
           && call.Sig.Text == ExternResolver.EventReceiverSetProgramVariable;

    /// <summary>Reentrant-flag conservation (design §4.3): CoreFlatten and CoalesceSlots/RemapInst both
    /// REBUILD call instructions, so a rebuild that forgets to copy the flag silently loses the
    /// dispatch-site recursion spill — exactly the failure object-identity marking died of. The flat
    /// instruction stream must carry exactly CFunction.ReentrantSiteCount flags (creation-counted by
    /// CoreBuilder, dead-code-adjusted by CoreFlatten).</summary>
    static void VerifyReentrantConservation(CFunction f)
    {
        int flagged = 0;
        foreach (var b in f.FlatBlocks)
            foreach (var inst in b.Stmts)
                if (inst is CExprStmt es
                    && (es.Expr is CExternCall { Reentrant: true } || es.Expr is CInternalCall { Reentrant: true }))
                    flagged++;
        if (flagged != f.ReentrantSiteCount)
            throw new InvalidOperationException(
                $"{f.Name}: Reentrant dispatch-site flag conservation violated — expected {f.ReentrantSiteCount} flagged call(s), found {flagged} (a rebuild pass dropped or duplicated the flag)");
    }

    static void VerifyInstruction(CStmt inst, string fn)
    {
        switch (inst)
        {
            case CAssign a: RequireLeaf(a.Value, fn, "assign value"); break;
            case CStoreField sf: RequireLeaf(sf.Value, fn, "store value"); break;
            case CLoadField _: break;
            case CExprStmt es:
                switch (es.Expr)
                {
                    case CExternCall ec:
                        foreach (var arg in ec.Args) RequireLeaf(arg, fn, "extern arg", allowAddr: true);
                        break;
                    case CInternalCall ic:
                        foreach (var arg in ic.Args) RequireLeaf(arg, fn, "internal-call arg", allowAddr: true);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"{fn}: flat CExprStmt must wrap a call, got {es.Expr?.GetType().Name}");
                }
                break;
            default:
                throw new InvalidOperationException(
                    $"{fn}: structured statement {inst?.GetType().Name} leaked into a flat block");
        }
    }

    static void VerifyTerminator(CTerminator t, HashSet<int> blockIds, string fn)
    {
        switch (t)
        {
            case CJump j: RequireBlock(j.TargetBlockId, blockIds, fn); break;
            case CBranch br:
                RequireLeaf(br.Cond, fn, "branch condition");
                RequireBlock(br.TrueBlockId, blockIds, fn);
                RequireBlock(br.FalseBlockId, blockIds, fn);
                break;
            case CRet ret:
                if (ret.Value != null) RequireLeaf(ret.Value, fn, "return value");
                break;
            default:
                throw new InvalidOperationException($"{fn}: unknown terminator {t?.GetType().Name}");
        }
    }

    static void RequireLeaf(CValue v, string fn, string ctx, bool allowAddr = false)
    {
        switch (v)
        {
            case CSlotRef _:
            case CConst _:
            case CFuncRef _:
                break;
            case CFieldAddr _:
                if (!allowAddr)
                    throw new InvalidOperationException(
                        $"{fn}: {ctx}: CFieldAddr is only valid as a direct extern/internal-call argument");
                break;
            case CFieldLoad _:
                throw new InvalidOperationException(
                    $"{fn}: {ctx}: CFieldLoad is not a flat leaf (must be materialized via CLoadField)");
            default:
                throw new InvalidOperationException(
                    $"{fn}: {ctx}: operand must be a leaf, got nested {v?.GetType().Name}");
        }
    }

    static void RequireBlock(int id, HashSet<int> blockIds, string fn)
    {
        if (!blockIds.Contains(id))
            throw new InvalidOperationException($"{fn}: terminator targets nonexistent block {id}");
    }
}
