using System;
using System.Collections.Generic;

/// <summary>
/// Verifies structured Core IR invariants. Run after Core construction and after each structured
/// optimization pass. Throws <see cref="VerificationException"/> on the first violation found.
/// </summary>
public static class CoreVerify
{
    public static void Verify(CModule module)
    {
        var fields = BuildFieldIndex(module);
        foreach (var func in module.Functions)
            VerifyFunction(func, module.TypeFacts, fields);
    }

    /// <summary>Build the module's one declared-field index for both structured and flat verification.
    /// Return slots share the Udon heap namespace with ordinary fields, so a conflicting declaration
    /// is a verifier error rather than a codegen-order accident.</summary>
    internal static Dictionary<string, StorageType> BuildFieldIndex(CModule module)
    {
        var fields = new Dictionary<string, StorageType>(StringComparer.Ordinal);
        foreach (var field in module.Fields)
            if (!fields.TryAdd(field.Name, field.Type))
                throw new VerificationException($"Duplicate field declaration '{field.Name}'");
        foreach (var function in module.Functions)
            foreach (var returnSlot in function.ReturnSlots)
                if (!fields.TryAdd(returnSlot.Id, returnSlot.StorageType)
                    && fields[returnSlot.Id] != returnSlot.StorageType)
                    throw new VerificationException(
                        $"Return field '{returnSlot.Id}' has conflicting types '{fields[returnSlot.Id]}' and '{returnSlot.StorageType}'");
        return fields;
    }

    public static void VerifyFunction(CFunction func, UdonTypeFactRegistry typeFacts = null,
        IReadOnlyDictionary<string, StorageType> fields = null)
    {
        if (func.Shape != Shape.Structured)
            throw new VerificationException(
                $"CoreVerify requires Shape=Structured, got {func.Shape} for function '{func.Name}' " +
                "(func.Body is stale after CoreFlatten — verify before flattening, or use FlatVerify after)");

        var ctx = new VerifyContext(func, typeFacts ?? new UdonTypeFactRegistry(),
            fields ?? new Dictionary<string, StorageType>());
        foreach (var slot in func.Slots)
        {
            if (slot.Class != SlotClass.Pinned) continue;
            if (string.IsNullOrEmpty(slot.FixedName))
                throw new VerificationException(
                    $"Pinned slot{slot.Id} in function '{func.Name}' has no fixed heap name.");
            ctx.AssertField(slot.FixedName, slot.Type, $"pinned slot{slot.Id}");
        }
        VerifyBlock(func.Body, ctx);
        VerifyGotoLabels(func);
    }

    sealed class VerifyContext
    {
        public readonly CFunction Func;
        public readonly UdonTypeFactRegistry TypeFacts;
        public readonly IReadOnlyDictionary<string, StorageType> Fields;
        public readonly HashSet<int> DeclaredSlots = new();
        public int LoopDepth;

        public VerifyContext(CFunction func, UdonTypeFactRegistry typeFacts,
            IReadOnlyDictionary<string, StorageType> fields)
        {
            Func = func;
            TypeFacts = typeFacts;
            Fields = fields;
            for (int i = 0; i < func.Slots.Count; i++)
                DeclaredSlots.Add(i);
        }

        public void AssertSlotExists(int slotId, string context)
        {
            if (!DeclaredSlots.Contains(slotId))
                throw new VerificationException(
                    $"Undeclared slot{slotId} in {context} (function '{Func.Name}')");
        }

        // Phase-D flip (2026-07-16): the reference-prefix-list and unknown-means-enum heuristics are
        // deleted. Two types may differ only under RawCopyCompatibility (exact / SystemObject wildcard /
        // Nullable erasure / fact-enum↔Int32 / both-fact-reference COPY) — anything else, including a
        // name with no minted fact, throws with the reason. The relaxations are position-independent VM
        // representation truths, so the former CAssign-only enum arm now applies to every check site.
        public void AssertType(StorageType expected, StorageType actual, string context)
        {
            var why = RawCopyCompatibility.WhyIncompatible(expected.Name, actual.Name, TypeFacts);
            if (why == null) return;
            throw new VerificationException(
                $"Type mismatch in {context}: expected '{expected}', got '{actual}' — {why} (function '{Func.Name}')");
        }

        public void AssertField(string fieldName, StorageType actualType, string context)
        {
            if (!Fields.TryGetValue(fieldName, out var declaredType))
                throw new VerificationException(
                    $"Undeclared field '{fieldName}' in {context} (function '{Func.Name}')");
            AssertType(declaredType, actualType, $"{context} field '{fieldName}'");
        }
    }

    static void VerifyBlock(CBlock block, VerifyContext ctx)
    {
        foreach (var stmt in block.Stmts)
            VerifyStmt(stmt, ctx);
    }

    static void VerifyStmt(CStmt stmt, VerifyContext ctx)
    {
        switch (stmt)
        {
            case CAssign assign:
                ctx.AssertSlotExists(assign.DestSlot, "CAssign");
                VerifyExpr(assign.Value, ctx);
                // Type check: assigned value must match slot type
                var slotType = ctx.Func.Slots[assign.DestSlot].Type;
                ctx.AssertType(slotType, assign.Value.Type, $"CAssign to slot{assign.DestSlot}");
                break;

            case CStoreField store:
                VerifyExpr(store.Value, ctx);
                ctx.AssertField(store.FieldName, store.Value.Type, "CStoreField");
                break;

            case CProgramVariableStore store:
                VerifyProgramReceiver(store.Instance, ctx, "CProgramVariableStore receiver");
                VerifyExpr(store.VariableName, ctx);
                VerifyExpr(store.Value, ctx);
                ctx.AssertType(StorageTypes.String, store.VariableName.Type,
                    "CProgramVariableStore variable name");
                ctx.AssertType(store.VariableType, store.Value.Type,
                    "CProgramVariableStore value");
                break;

            case CIf ifStmt:
                VerifyExpr(ifStmt.Cond, ctx);
                ctx.AssertType(StorageTypes.Boolean, ifStmt.Cond.Type, "CIf condition");
                VerifyBlock(ifStmt.Then, ctx);
                VerifyBlock(ifStmt.Else, ctx);
                break;

            case CWhile whileStmt:
                VerifyBlock(whileStmt.CondBlock, ctx);
                VerifyExpr(whileStmt.Cond, ctx);
                ctx.AssertType(StorageTypes.Boolean, whileStmt.Cond.Type, "CWhile condition");
                ctx.LoopDepth++;
                VerifyBlock(whileStmt.Body, ctx);
                ctx.LoopDepth--;
                break;

            case CFor forStmt:
                VerifyBlock(forStmt.Init, ctx);
                VerifyBlock(forStmt.CondBlock, ctx);
                if (forStmt.Cond != null)
                {
                    VerifyExpr(forStmt.Cond, ctx);
                    ctx.AssertType(StorageTypes.Boolean, forStmt.Cond.Type, "CFor condition");
                }
                ctx.LoopDepth++;
                VerifyBlock(forStmt.Body, ctx);
                ctx.LoopDepth--;
                VerifyBlock(forStmt.Update, ctx);
                break;

            case CBreak:
                if (ctx.LoopDepth <= 0)
                    throw new VerificationException(
                        $"CBreak outside of loop (function '{ctx.Func.Name}')");
                break;

            case CContinue:
                if (ctx.LoopDepth <= 0)
                    throw new VerificationException(
                        $"CContinue outside of loop (function '{ctx.Func.Name}')");
                break;

            case CReturn ret:
                var returnsVoid = !ctx.Func.ReturnType.HasValue
                    || ctx.Func.ReturnType.Value.Name == "SystemVoid";
                if (returnsVoid && ret.Value != null)
                    throw new VerificationException(
                        $"Void function '{ctx.Func.Name}' returns a value of type '{ret.Value.Type}'");
                if (!returnsVoid && ret.Value == null && ctx.Func.ReturnSlots.Count == 0)
                    throw new VerificationException(
                        $"Non-void function '{ctx.Func.Name}' returns without a value (expected '{ctx.Func.ReturnType}')");
                if (ret.Value != null)
                {
                    VerifyExpr(ret.Value, ctx);
                    ctx.AssertType(ctx.Func.ReturnType.Value, ret.Value.Type, "CReturn");
                }
                break;

            case CExprStmt exprStmt:
                VerifyExpr(exprStmt.Expr, ctx);
                // Future improvement: warn if exprStmt.Expr is pure (no side effects),
                // as pure expression statements are dead code. Requires a warning mechanism
                // since CoreVerify currently only throws exceptions.
                break;

            case CBlock block:
                VerifyBlock(block, ctx);
                break;

            case CGoto:
            case CLabel:
                break; // goto/label pairing is verified by VerifyGotoLabels

            default:
                throw new VerificationException($"Unknown CStmt type: {stmt.GetType().Name}");
        }
    }

    /// <summary>Verify that every CGoto target has a corresponding CLabel in the same function.</summary>
    static void VerifyGotoLabels(CFunction func)
    {
        var labels = new HashSet<string>();
        var gotos = new HashSet<string>();
        CollectLabelsAndGotos(func.Body, labels, gotos);

        foreach (var target in gotos)
        {
            if (!labels.Contains(target))
                throw new VerificationException(
                    $"CGoto targets undefined label '{target}' (function '{func.Name}')");
        }
    }

    static void CollectLabelsAndGotos(CBlock block, HashSet<string> labels, HashSet<string> gotos)
    {
        foreach (var stmt in block.Stmts)
            CollectLabelsAndGotosStmt(stmt, labels, gotos);
    }

    static void CollectLabelsAndGotosStmt(CStmt stmt, HashSet<string> labels, HashSet<string> gotos)
    {
        switch (stmt)
        {
            case CLabel lbl:
                if (!labels.Add(lbl.Label))
                    throw new VerificationException(
                        $"Duplicate label '{lbl.Label}' (function label names must be unique)");
                break;
            case CGoto gt:
                gotos.Add(gt.Label);
                break;
            case CBlock blk:
                CollectLabelsAndGotos(blk, labels, gotos);
                break;
            case CIf hif:
                CollectLabelsAndGotos(hif.Then, labels, gotos);
                CollectLabelsAndGotos(hif.Else, labels, gotos);
                break;
            case CWhile hw:
                CollectLabelsAndGotos(hw.CondBlock, labels, gotos);
                CollectLabelsAndGotos(hw.Body, labels, gotos);
                break;
            case CFor hf:
                CollectLabelsAndGotos(hf.Init, labels, gotos);
                CollectLabelsAndGotos(hf.CondBlock, labels, gotos);
                CollectLabelsAndGotos(hf.Update, labels, gotos);
                CollectLabelsAndGotos(hf.Body, labels, gotos);
                break;
        }
    }

    // <paramref name="allowAddr"/> permits a CFieldAddr (a heap address) in this position. The CLeaf TYPE
    // proves an operand is a leaf, but cannot distinguish a value-leaf (CSlotRef/CConst/CFuncRef) from an
    // address-leaf (CFieldAddr) — and a CFieldAddr is only meaningful as an extern/internal out/ref argument,
    // never as an assigned value, store value, condition, return value, select arm, or cross-call operand.
    // This mirrors FlatVerify.RequireLeaf one stage earlier, so a misplaced address fails at the structured
    // level instead of emitting a heap address where the Udon VM expects a value.
    static void VerifyExpr(CValue expr, VerifyContext ctx, bool allowAddr = false)
    {
        switch (expr)
        {
            case CConst:
                break; // always valid

            case CSlotRef slotRef:
                ctx.AssertSlotExists(slotRef.SlotId, "CSlotRef");
                var declaredType = ctx.Func.Slots[slotRef.SlotId].Type;
                ctx.AssertType(declaredType, slotRef.Type, $"CSlotRef slot{slotRef.SlotId}");
                break;

            case CRepresentationCast cast:
                VerifyExpr(cast.Source, ctx);
                if (cast.Source.Type == cast.Type)
                    throw new VerificationException(
                        $"Redundant CRepresentationCast[{cast.Kind}] from '{cast.Source.Type}' to itself "
                        + $"(function '{ctx.Func.Name}')");
                break;

            case CFieldLoad fieldLoad:
                ctx.AssertField(fieldLoad.FieldName, fieldLoad.Type, "CFieldLoad");
                break;

            case CProgramVariableLoad load:
                VerifyProgramReceiver(load.Instance, ctx, "CProgramVariableLoad receiver");
                VerifyExpr(load.VariableName, ctx);
                ctx.AssertType(StorageTypes.String, load.VariableName.Type,
                    "CProgramVariableLoad variable name");
                break;

            case CFieldAddr fa:
                ctx.AssertField(fa.FieldName, fa.Type, "CFieldAddr");
                if (!allowAddr)
                    throw new VerificationException(
                        $"CFieldAddr '[{fa.FieldName}]' (heap address) is only valid as an extern/internal " +
                        $"out/ref argument, not as a value (function '{ctx.Func.Name}')");
                break;

            case CExternCall call:
                foreach (var arg in call.Args)
                    VerifyExpr(arg, ctx, allowAddr: true); // out/ref params take a CFieldAddr
                UdonAbiVerifier.VerifyInvocation(call, ctx.TypeFacts, ctx.Func.Name);
                break;

            case CInternalCall call:
                foreach (var arg in call.Args)
                    VerifyExpr(arg, ctx, allowAddr: true);
                break;

            case CSelect sel:
                VerifyExpr(sel.Cond, ctx); // value operands — addresses rejected
                ctx.AssertType(StorageTypes.Boolean, sel.Cond.Type, "CSelect condition");
                VerifyExpr(sel.TrueVal, ctx);
                VerifyExpr(sel.FalseVal, ctx);
                // Branch types may differ from result type due to inheritance
                // (e.g., RenderTexture vs Texture). Udon VM handles implicit conversion.
                break;

            case CCrossCall cc:
                VerifyProgramReceiver(cc.Instance, ctx, "CCrossCall receiver");
                VerifyExpr(cc.EventName, ctx);
                ctx.AssertType(StorageTypes.String, cc.EventName.Type, "CCrossCall event name");
                if (cc.EventName is CConst { Value: string eventName }
                    && string.IsNullOrEmpty(eventName))
                    throw new VerificationException(
                        $"CCrossCall has an empty event name (function '{ctx.Func.Name}')");
                var parameterIds = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < cc.Params.Count; i++)
                {
                    var parameter = cc.Params[i];
                    if (parameter == null)
                        throw new VerificationException(
                            $"CCrossCall parameter {i} is null (function '{ctx.Func.Name}')");
                    if (parameter.Ordinal != i)
                        throw new VerificationException(
                            $"CCrossCall parameter '{parameter.Id}' has ordinal {parameter.Ordinal}; "
                            + $"expected canonical ordinal {i} (function '{ctx.Func.Name}')");
                    if (string.IsNullOrEmpty(parameter.Id) || !parameterIds.Add(parameter.Id))
                        throw new VerificationException(
                            $"CCrossCall has an empty or duplicate parameter id '{parameter.Id}' "
                            + $"(function '{ctx.Func.Name}')");
                    VerifyExpr(parameter.Value, ctx);
                    ctx.AssertType(parameter.StorageType, parameter.Value.Type,
                        $"CCrossCall parameter {parameter.Ordinal} ('{parameter.Id}')");
                }

                var returnIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var returnSlot in cc.Returns)
                {
                    if (returnSlot == null || string.IsNullOrEmpty(returnSlot.Id)
                        || !returnIds.Add(returnSlot.Id))
                        throw new VerificationException(
                            $"CCrossCall has an empty or duplicate return id '{returnSlot?.Id}' "
                            + $"(function '{ctx.Func.Name}')");
                }
                if (cc.Returns.Count == 1)
                    ctx.AssertType(cc.Returns[0].StorageType, cc.Type, "CCrossCall result");
                else
                    ctx.AssertType(StorageTypes.Void, cc.Type,
                        cc.Returns.Count == 0 ? "CCrossCall void result" : "CCrossCall tuple result");
                break;

            case CFuncRef:
                break;

            default:
                throw new VerificationException($"Unknown CValue type: {expr.GetType().Name}");
        }
    }

    static void VerifyProgramReceiver(CLeaf instance, VerifyContext ctx, string context)
    {
        VerifyExpr(instance, ctx);
        // Delegate bundles deliberately erase their target to SystemObject. Interface/GetComponent
        // paths may carry a behaviour through the Component tag until the extern consumes it.
        if (instance.Type != StorageTypes.UdonEventReceiver
            && instance.Type != StorageTypes.UdonBehaviour
            && instance.Type != StorageTypes.Component
            && instance.Type != StorageTypes.Object)
            throw new VerificationException(
                $"{context} has non-program type '{instance.Type}' "
                + $"(function '{ctx.Func.Name}')");
    }
}

/// <summary>Exception thrown when Core IR verification fails.</summary>
public sealed class VerificationException : Exception
{
    public VerificationException(string message) : base(message) { }
}
