using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class AggregateLayout
{
    public readonly struct FieldInfo
    {
        public readonly string Name;
        public readonly int Index;
        public readonly ITypeSymbol Type;
        public FieldInfo(string name, int index, ITypeSymbol type)
        { Name = name; Index = index; Type = type; }
    }

    public readonly IReadOnlyList<FieldInfo> Fields;
    readonly Dictionary<string, int> _nameToIndex;

    public int Count => Fields.Count;

    public bool TryGetIndex(string fieldName, out int index)
        => _nameToIndex.TryGetValue(fieldName, out index);

    public bool TryGetIndex(IFieldSymbol field, out int index)
    {
        if (_nameToIndex.TryGetValue(field.Name, out index)) return true;
        if (field.CorrespondingTupleField != null
            && _nameToIndex.TryGetValue(field.CorrespondingTupleField.Name, out index)) return true;
        // Reverse: check if any layout field's CorrespondingTupleField matches
        return false;
    }

    AggregateLayout(IReadOnlyList<FieldInfo> fields, Dictionary<string, int> nameToIndex)
    { Fields = fields; _nameToIndex = nameToIndex; }

    public static AggregateLayout Build(INamedTypeSymbol type)
    {
        var fields = new List<FieldInfo>();
        var nameToIndex = new Dictionary<string, int>();

        if (type.IsTupleType)
        {
            var elements = type.TupleElements;
            for (int i = 0; i < elements.Length; i++)
            {
                var name = elements[i].Name;
                fields.Add(new FieldInfo(name, i, elements[i].Type));
                nameToIndex[name] = i;
                var itemName = $"Item{i + 1}";
                if (name != itemName) nameToIndex[itemName] = i;
                if (elements[i].CorrespondingTupleField != null)
                {
                    var corrName = elements[i].CorrespondingTupleField.Name;
                    if (!nameToIndex.ContainsKey(corrName)) nameToIndex[corrName] = i;
                }
            }
        }
        else if (type.TypeKind == TypeKind.Struct)
        {
            // User struct → instance fields mapped to indices in declaration order. Auto-property backing
            // fields are implicitly declared but carry the property as AssociatedSymbol; map them by the
            // property name so `get`/`set`/`init` resolve to the same object[] element.
            int i = 0;
            foreach (var member in type.GetMembers())
            {
                if (member is not IFieldSymbol { IsStatic: false, IsConst: false } f) continue;
                if (!f.IsImplicitlyDeclared)
                {
                    fields.Add(new FieldInfo(f.Name, i, f.Type));
                    nameToIndex[f.Name] = i++;
                }
                else if (f.AssociatedSymbol is IPropertySymbol prop)
                {
                    fields.Add(new FieldInfo(prop.Name, i, f.Type));
                    nameToIndex[prop.Name] = i++;
                }
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"AggregateLayout.Build called on non-aggregate type '{type.Name}'");
        }

        return new AggregateLayout(fields.AsReadOnly(), nameToIndex);
    }
}

public class EmitContext
{
    // Core dependencies
    public readonly Compilation Compilation;
    public readonly INamedTypeSymbol ClassSymbol;
    public readonly CModule Module;
    public readonly CoreBuilder Builder;
    public readonly LayoutPlanner Planner;

    // Method bookkeeping
    public readonly Dictionary<IMethodSymbol, CFunction> MethodFunctions = new(SymbolEqualityComparer.Default);
    public readonly struct MethodSlot
    {
        public readonly int Index;
        public readonly string VarPrefix;
        public MethodSlot(int index, string varPrefix) { Index = index; VarPrefix = varPrefix; }
    }

    public readonly Dictionary<IMethodSymbol, MethodSlot> MethodSlots = new(SymbolEqualityComparer.Default);

    public MethodSlot RegisterMethod(IMethodSymbol method, Func<int, string> prefixFactory)
    {
        var idx = NextMethodIndex++;
        var slot = new MethodSlot(idx, prefixFactory(idx));
        MethodSlots[method] = slot;
        return slot;
    }
    /// <summary>Per-method return slots. Empty array for void. Length 1 for scalar. Length N for tuple.</summary>
    public readonly Dictionary<IMethodSymbol, ReturnSlot[]> MethodReturns = new(SymbolEqualityComparer.Default);
    public readonly Dictionary<IMethodSymbol, string[]> MethodParamVarIds = new(SymbolEqualityComparer.Default);
    public IMethodSymbol CurrentMethod;

    /// <summary>When emitting a user-struct method/ctor, the receiver object[] param var id; otherwise null.
    /// Makes <c>this</c> / <c>this.field</c> resolve to the receiver array instead of the Behaviour.</summary>
    public string CurrentStructReceiverParamId;

    /// <summary>For each internal method, the set of callees that lie in the same strongly-connected
    /// component (i.e. calls that can re-enter the caller). Calls along these edges must spill the
    /// caller's live values to the software stack, because Udon's flat heap shares param/local slots
    /// across call frames. Populated by <c>UasmEmitter.BuildRecursionInfo</c> before emit.</summary>
    public Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> RecursiveCallees;

    /// <summary>True when a call from <paramref name="caller"/> to <paramref name="callee"/> is a
    /// recursion-cycle edge (callee in caller's non-trivial SCC, including direct self-recursion).</summary>
    public bool IsRecursiveEdge(IMethodSymbol caller, IMethodSymbol callee)
        => caller != null && callee != null && RecursiveCallees != null
           // Reduce BOTH ends to OriginalDefinition: RecursiveCallees is keyed by definition, but a
           // monomorphized generic specialization (e.g. Fact<int>) emits with the constructed symbol as
           // _currentMethod/target — without this its self-edge would be missed and the frame not spilled.
           && RecursiveCallees.TryGetValue(caller.OriginalDefinition, out var callees)
           && callees.Contains(callee.OriginalDefinition);

    /// <summary>Wave-9 round-8 [Y3]: per internal method, ALL same-SCC callees — the UNFILTERED twin
    /// of <see cref="RecursiveCallees"/> (which keeps only edges carrying a non-tail call, because it
    /// drives frame SPILLS). The ref/out re-chain guard must fire on every recursion-cycle edge
    /// regardless of tail position: a re-chained ref in pure RETURN position (`return M(m-1, ref w);`)
    /// is a tail call (no spill needed) yet still threads every frame's write through the ONE shared
    /// param heap var and corrupts the outer frame's copy-back (VM-proven 21021 vs CLR 9021).
    /// Populated by <c>UasmEmitter.BuildRecursionInfo</c>.</summary>
    public Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> CycleCallees;

    /// <summary>True when a call from <paramref name="caller"/> to <paramref name="callee"/> lies in
    /// a recursion cycle (same non-trivial SCC or direct self-loop), tail or not ([Y3]).</summary>
    public bool IsCycleEdge(IMethodSymbol caller, IMethodSymbol callee)
        => caller != null && callee != null && CycleCallees != null
           && CycleCallees.TryGetValue(caller.OriginalDefinition, out var callees)
           && callees.Contains(callee.OriginalDefinition);

    /// <summary>Round-7 follow-up [Q5]: per internal method (keyed by OriginalDefinition), the
    /// this-FIELDS the method touches — directly (field reference through an implicit/explicit
    /// this/base receiver anywhere in its body) or transitively (closed over the internal call
    /// graph, including this-property accessor edges and the synthetic dispatch edges —
    /// conservative, §8-3). A ref/out argument rooted at a this-field hands the callee an alias
    /// of storage it can also reach directly; the caller-side copy-in/copy-back convention
    /// snapshots it (callee param reads go stale, callee direct field writes are reverted by the
    /// stale copy-back — VM-proven 19 vs CLR 59). Populated by UasmEmitter.BuildRecursionInfo;
    /// consulted by EmitUserMethodCall's ref/out-argument guard. Non-touching callees
    /// (Inc(ref field) / Swap(ref a, ref b)) stay legal.</summary>
    public Dictionary<IMethodSymbol, HashSet<IFieldSymbol>> ThisFieldTouches;

    /// <summary>[Q5] True when <paramref name="callee"/>'s transitive touch set contains the
    /// this-field <paramref name="field"/> (both compared by OriginalDefinition).</summary>
    public bool CalleeTouchesThisField(IMethodSymbol callee, IFieldSymbol field)
        => callee != null && field != null && ThisFieldTouches != null
           && ThisFieldTouches.TryGetValue(callee.OriginalDefinition, out var set)
           && set.Contains(field.OriginalDefinition);

    /// <summary>Syntax nodes of delegate-dispatch invocations that can re-enter their containing
    /// function: the containing function lies on a synthetic-edge-inclusive SCC cycle AND the dispatch
    /// is non-tail (design §4.2/§4.3). Computed by <c>UasmEmitter.BuildRecursionInfo</c>; keyed by the
    /// invocation's red SYNTAX node because operation trees are NOT shared between the analysis and emit
    /// walks (each GetSemanticModel call builds a fresh operation tree) while red syntax nodes ARE shared.
    /// MEMBERSHIP-ONLY — never enumerated (§1.5 determinism).</summary>
    public HashSet<SyntaxNode> ReentrantDispatchSites;

    /// <summary>Wave-9 round-9 [Y3]: direct-call invocation sites on a RECURSIVE edge that are in
    /// TAIL position (statement-form or return-form) — the frame reads nothing after them, so
    /// EmitCallToMethod flags the instruction TailSpared and InsertRecursionSpills skips the wrap.
    /// Without this, ONE non-tail site put the callee in RecursiveCalleeNames and EVERY site of
    /// that callee spilled (per-callee gating), overflowing the 512-entry __recurStack on deep
    /// mixed tail/non-tail recursion while the dispatch arm (per-site Reentrant marking) survived
    /// the identical shape. Keyed by red SYNTAX node like ReentrantDispatchSites (operation trees
    /// are not shared between analysis and emit walks). MEMBERSHIP-ONLY (§1.5).</summary>
    public HashSet<SyntaxNode> TailSparedDirectCallSites;

    /// <summary>Stage 2 M3 (§5.5, graft #2): the definition-keyed set of every function that got a
    /// recursion-graph node in BuildRecursionInfo (roots + local functions + lambda nodes). Consumed
    /// by <c>UasmEmitter.VerifyBridgeTargetsAreNodes</c> AFTER emission to assert every capturing
    /// delegate bridge target is a graph node — a capturing bridge with no node has its reentrancy
    /// protection silently missing (wave-10 [Z1] class). MEMBERSHIP-ONLY (§1.5).</summary>
    public HashSet<IMethodSymbol> RecursionGraphNodes;

    /// <summary>True if <paramref name="t"/> is <c>Nullable&lt;T&gt;</c>; yields the underlying T.
    /// Nullable is emulated as a boxed object (null | boxed T) — see ExternResolver type mapping.</summary>
    public static bool IsNullableT(ITypeSymbol t, out ITypeSymbol underlying)
    {
        if (t is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            underlying = n.TypeArguments[0];
            return true;
        }
        underlying = null;
        return false;
    }

    // ── Tail-call analysis (shared by named-method and recursive-lambda recursion detection) ──
    // A self-recursive call only needs spilling when it is NOT in tail position: a tail call reads nothing
    // of its frame afterwards, so the flat-heap clobber is harmless and deep tail recursion must not spill.

    /// <summary>Returns the call's argument list if <paramref name="op"/> is a self-recursive call to
    /// track, else default (and false via the out usage). Lets one tail walker serve named calls and
    /// delegate-variable invocations.</summary>
    public delegate bool SelfCallMatcher(IOperation op, out System.Collections.Immutable.ImmutableArray<IArgumentOperation> args);

    /// <summary>True if <paramref name="body"/> contains a NON-tail self-recursive call (per the matcher).
    /// Conditional (`cond ? a : self(..)`) branches count as tail positions; the condition does not.
    /// Wave-9 round-6 [X1]: STATEMENT-form tail positions count too — a void self-call that is the
    /// LAST statement executed before the function's implicit return (`M(m-1);` / `da(m-1);` as the
    /// final statement, including through nested blocks and if/else branches in tail position) reads
    /// nothing of its frame afterwards, exactly like `return M(m-1);`. Pre-fix these spilled every
    /// frame and overflowed the 512-entry __recurStack at depth (compile-clean VmFault on legal C#).</summary>
    public static bool HasNonTailSelfCall(IOperation body, SelfCallMatcher isSelf)
        => HasNonTailSelfCall(body, isSelf, tail: true);

    static bool HasNonTailSelfCall(IOperation body, SelfCallMatcher isSelf, bool tail)
    {
        if (body == null) return false;
        if (body is IReturnOperation ret) return NonTailInTailExpr(ret.ReturnedValue, isSelf);
        if (tail)
        {
            switch (body)
            {
                // Method/accessor bodies arrive as IMethodBodyOperation (block XOR expression body).
                case IMethodBodyBaseOperation mb:
                    return HasNonTailSelfCall(mb.BlockBody, isSelf, tail: true)
                        || HasNonTailSelfCall(mb.ExpressionBody, isSelf, tail: true);
                // Only a block's LAST statement stays in tail position. Wave-9 round-8 [Y7]/[Y8]:
                // hoisted lambda/local-function BLOCK bodies carry an IMPLICIT value-less trailing
                // IReturnOperation (method bodies via IMethodBodyOperation do not), so the statement
                // BEFORE it is the real tail position — without the skip every hoisted tail-if self
                // dispatch/call spilled per frame and overflowed the 512-entry __recurStack at depth.
                case IBlockOperation block:
                {
                    var ops = block.Operations;
                    int last = ops.Length - 1;
                    if (last >= 0 && ops[last] is IReturnOperation { ReturnedValue: null, IsImplicit: true })
                        last--;
                    for (int i = 0; i < ops.Length; i++)
                        if (HasNonTailSelfCall(ops[i], isSelf, tail: i == last)) return true;
                    return false;
                }
                // A statement-form if/else in tail position: branches stay tail, the condition does not
                // (mirrors the expression-form conditional rule in NonTailInTailExpr). Loops, usings
                // etc. deliberately fall through to the generic non-tail walk below — code
                // (back-edges, Dispose) runs after their last statement.
                case IConditionalOperation cond:
                    if (AnySelfCall(cond.Condition, isSelf)) return true;
                    return HasNonTailSelfCall(cond.WhenTrue, isSelf, tail: true)
                        || HasNonTailSelfCall(cond.WhenFalse, isSelf, tail: true);
                // Wave-9 round-9: a SWITCH in tail position — unlike loops/usings, nothing runs after
                // an arm's last statement except the implicit break out of the switch (arms cannot
                // fall through), so each arm's trailing statement (before that break) stays tail.
                // The switch value and case clauses are not; a trailing `goto case`/`goto label`
                // stays on the generic walk (another arm's body runs after it). Pre-fix every
                // switch-arm tail self-call spilled per frame and overflowed the 512-entry
                // __recurStack at depth (compile-clean VmFault on legal C#).
                case ISwitchOperation sw:
                {
                    if (AnySelfCall(sw.Value, isSelf)) return true;
                    foreach (var swCase in sw.Cases)
                    {
                        foreach (var clause in swCase.Clauses)
                            if (AnySelfCall(clause, isSelf)) return true;
                        var caseBody = swCase.Body;
                        int caseLast = caseBody.Length - 1;
                        if (caseLast >= 0 && caseBody[caseLast] is IBranchOperation { BranchKind: BranchKind.Break })
                            caseLast--;
                        for (int i = 0; i < caseBody.Length; i++)
                            if (HasNonTailSelfCall(caseBody[i], isSelf, tail: i == caseLast)) return true;
                    }
                    return false;
                }
                // Wave-9 round-9: a LABELED statement in tail position — the label wrapper changes
                // where control can ARRIVE, not what runs after, so the wrapped statement keeps the
                // tail flag (`TAIL: M(m-1);` as the final statement is exactly `M(m-1);`).
                case ILabeledOperation labeledStmt:
                    return HasNonTailSelfCall(labeledStmt.Operation, isSelf, tail: true);
                case IExpressionStatementOperation exprStmt:
                    return NonTailInTailStatement(exprStmt.Operation, isSelf);
            }
        }
        if (isSelf(body, out _)) return true; // self-call as a statement / non-tail position
        foreach (var child in body.Children)
        {
            if (child is ILocalFunctionOperation || child is IAnonymousFunctionOperation) continue;
            if (HasNonTailSelfCall(child, isSelf, tail: false)) return true;
        }
        return false;
    }

    /// <summary>The discarded-result twin of NonTailInTailExpr: classify the expression of a TAIL
    /// statement. A matched self-call is tail (only its argument/receiver subexpressions are non-tail);
    /// a call carrying ref/out arguments is NOT spared — its copy-back reads the param heap vars AFTER
    /// the call. (The [Q2] re-chained-ref reject no longer rides this classification — round-8 [Y3]
    /// moved it to the unfiltered IsCycleEdge so tail re-chains reject too.)</summary>
    static bool NonTailInTailStatement(IOperation expr, SelfCallMatcher isSelf)
    {
        if (expr == null) return false;
        if (isSelf(expr, out var args)
            && (expr is not IInvocationOperation refInv
                || refInv.TargetMethod.Parameters.All(p => p.RefKind == RefKind.None)))
        {
            foreach (var a in args)
                if (AnySelfCall(a, isSelf)) return true;
            return AnySelfCall((expr as IInvocationOperation)?.Instance, isSelf);
        }
        // Wave-9 round-8 [Y1]/[Y4]: `d?.Invoke(args);` as the tail statement — the WhenNotNull
        // dispatch is the last thing the frame runs (the null arm skips straight to the implicit
        // return), so it stays in tail position; the receiver leg does not.
        if (expr is IConditionalAccessOperation condAcc)
        {
            if (AnySelfCall(condAcc.Operation, isSelf)) return true;
            return NonTailInTailStatement(condAcc.WhenNotNull, isSelf);
        }
        return AnySelfCall(expr, isSelf);
    }

    static bool NonTailInTailExpr(IOperation expr, SelfCallMatcher isSelf)
    {
        if (expr == null) return false;
        if (isSelf(expr, out var args)) // a tail self-call; only its arguments are non-tail
        {
            foreach (var a in args)
                if (AnySelfCall(a, isSelf)) return true;
            return false;
        }
        if (expr is IConditionalOperation cond) // branches stay in tail position; the condition does not
        {
            if (AnySelfCall(cond.Condition, isSelf)) return true;
            return NonTailInTailExpr(cond.WhenTrue, isSelf) || NonTailInTailExpr(cond.WhenFalse, isSelf);
        }
        return AnySelfCall(expr, isSelf); // any self-call buried in a non-tail expression
    }

    static bool AnySelfCall(IOperation op, SelfCallMatcher isSelf)
    {
        if (op == null) return false;
        if (isSelf(op, out _)) return true;
        foreach (var child in op.Children)
        {
            if (child is ILocalFunctionOperation || child is IAnonymousFunctionOperation) continue;
            if (AnySelfCall(child, isSelf)) return true;
        }
        return false;
    }

    /// <summary>Generalized delegate-dispatch matcher: ANY-receiver delegate Invoke (design §4.2;
    /// the pre-§4 local-variable-only matcher was removed per deletion #12).</summary>
    public static bool IsDelegateDispatch(IOperation op)
        => op is IInvocationOperation inv && inv.TargetMethod?.MethodKind == MethodKind.DelegateInvoke;

    /// <summary>True when THIS specific dispatch operation occurs in NON-tail position within
    /// <paramref name="body"/> (per-site tail sparing, design §4.3/§4.4: tail dispatches are never
    /// marked Reentrant so bundle-driven deep tail recursion stays spill-free). Reference-equality
    /// matcher — body and site must come from the SAME operation tree.</summary>
    public static bool IsNonTailDispatchSite(IOperation body, IOperation site)
        => HasNonTailSelfCall(body, (IOperation op, out System.Collections.Immutable.ImmutableArray<IArgumentOperation> args) =>
        {
            if (ReferenceEquals(op, site) && op is IInvocationOperation inv)
            {
                args = inv.Arguments;
                return true;
            }
            args = default;
            return false;
        });
    public int NextMethodIndex;
    public readonly List<(IMethodSymbol symbol, CFunction func)> PendingLocalFunctions = new();

    // Generic monomorphization
    public readonly List<IMethodSymbol> PendingGenericSpecs = new();
    public Dictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap;

    // Wave-9 round-5 [X6]: first registered specialization per generic DEFINITION. Lambdas and
    // local functions hoisted from a generic body are keyed by IMethodSymbol and therefore SHARED
    // across that body's specializations — a capturing closure's capture cells are seeded by
    // whichever spec emitted LAST (last-spec-wins; VM-proven r1=8 vs 3). A second DISTINCT
    // instantiation of a definition whose body contains a capturing closure is loud (per-spec
    // closure environments are Stage-2 territory, design §8-3). LOOKUP-ONLY (§1.5).
    public readonly Dictionary<IMethodSymbol, IMethodSymbol> FirstGenericSpec
        = new(SymbolEqualityComparer.Default);

    /// <summary>How a generic definition's body closures pin it to a single instantiation.
    /// <c>Capturing</c>: a capturing lambda/local function (the [X6] round-5 reject — shared capture
    /// cells are seeded last-spec-wins). <c>TypeParamDependent</c> (round-8 [Y2] widening): a closure
    /// whose SIGNATURE or BODY references the enclosing generic's type parameters — the closure is
    /// hoisted ONCE keyed by IMethodSymbol and its function types/body were emitted under the FIRST
    /// spec's map, so a second instantiation would silently run the first instantiation's types.</summary>
    public enum ClosurePin { None, Capturing, TypeParamDependent }

    /// <summary>[X6]/[Y2] gate: does the generic DEFINITION's body contain an instantiation-pinning
    /// closure? Walks the definition's own operation tree (specs share it). Capturing dominates.</summary>
    public ClosurePin GenericBodyClosurePin(Compilation compilation, IMethodSymbol def)
    {
        var syntaxRef = def.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return ClosurePin.None;
        var syntax = syntaxRef.GetSyntax();
        var body = compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax);
        var pin = ClosurePin.None;
        WalkClosurePins(body, ref pin);
        return pin;
    }

    void WalkClosurePins(IOperation op, ref ClosurePin pin)
    {
        if (op == null || pin == ClosurePin.Capturing) return;
        switch (op)
        {
            case IAnonymousFunctionOperation af:
                if (CaptureAnalyzer.HasCaptures(af)) { pin = ClosurePin.Capturing; return; }
                if (ClosureUsesMethodTypeParam(af.Symbol, af.Body)) pin = ClosurePin.TypeParamDependent;
                break;
            case ILocalFunctionOperation lf when lf.Symbol != null:
                if (IsCapturingLocalFunction(lf.Symbol)
                    || CaptureAnalyzer.GetLocalFunctionCaptures(lf.Symbol).Length > 0)
                { pin = ClosurePin.Capturing; return; }
                if (ClosureUsesMethodTypeParam(lf.Symbol, lf.Body)) pin = ClosurePin.TypeParamDependent;
                break;
        }
        foreach (var child in op.Children)
        {
            WalkClosurePins(child, ref pin);
            if (pin == ClosurePin.Capturing) return;
        }
    }

    static bool ClosureUsesMethodTypeParam(IMethodSymbol closureSym, IOperation closureBody)
    {
        if (closureSym != null
            && (TypeUsesMethodTypeParam(closureSym.ReturnType)
                || closureSym.Parameters.Any(p => TypeUsesMethodTypeParam(p.Type))))
            return true;
        return OperationUsesMethodTypeParam(closureBody);
    }

    static bool OperationUsesMethodTypeParam(IOperation op)
    {
        if (op == null) return false;
        if (TypeUsesMethodTypeParam(op.Type)) return true;
        if (op is ITypeOfOperation typeOf && TypeUsesMethodTypeParam(typeOf.TypeOperand)) return true;
        if (op is IIsTypeOperation isType && TypeUsesMethodTypeParam(isType.TypeOperand)) return true;
        if (op is IInvocationOperation inv
            && inv.TargetMethod.TypeArguments.Any(TypeUsesMethodTypeParam)) return true;
        foreach (var child in op.Children)
            if (OperationUsesMethodTypeParam(child)) return true;
        return false;
    }

    static bool TypeUsesMethodTypeParam(ITypeSymbol t) => t switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol at => TypeUsesMethodTypeParam(at.ElementType),
        INamedTypeSymbol nt => nt.IsGenericType && nt.TypeArguments.Any(TypeUsesMethodTypeParam),
        _ => false,
    };

    /// <summary>Shared [X6]/[Y2] reject for a SECOND distinct instantiation of a generic whose body
    /// pins it to one instantiation (capturing closure, or — round-8 — a closure referencing the
    /// generic's type parameters). No-op for <see cref="ClosurePin.None"/>.</summary>
    public static void ThrowIfClosurePinsInstantiation(ClosurePin pin, string methodName)
    {
        switch (pin)
        {
            case ClosurePin.Capturing:
                throw new System.NotSupportedException(
                    $"Generic method '{methodName}' is instantiated with more than one type-argument "
                    + "combination but contains a lambda or local function that captures locals/parameters. "
                    + "The hoisted closure and its capture cells are shared across instantiations in the "
                    + "flat-heap model, so one instantiation would read the other's captured values. "
                    + "Use a single instantiation, or make the closure capture-free.");
            case ClosurePin.TypeParamDependent:
                throw new System.NotSupportedException(
                    $"Generic method '{methodName}' is instantiated with more than one type-argument "
                    + "combination but contains a lambda or local function whose signature or body uses "
                    + "the method's type parameters. The hoisted closure is shared across instantiations "
                    + "in the flat-heap model and was emitted with the first instantiation's types. "
                    + "Use a single instantiation, or keep the closure independent of the type parameters.");
        }
    }

    // Persistent local symbol → field name mapping (survives scope pop). Holds NON-captured locals
    // only: a captured local has no flat field — its cell lives in the owning scope's env record
    // (Stage 2, TryGetEnvBinding / EnvEmit), so per-activation captures no longer alias.
    public readonly struct LocalBinding
    {
        public readonly string Id;
        public LocalBinding(string id) { Id = id; }
    }

    public readonly Dictionary<ILocalSymbol, LocalBinding> LocalBindings = new(SymbolEqualityComparer.Default);

    // Lambda capture analysis (replaces HandlerBase.HasCaptures pre-v2.2).
    // See LambdaCaptureAnalyzer for rationale on manual walker vs Roslyn AnalyzeDataFlow.
    public readonly LambdaCaptureAnalyzer CaptureAnalyzer;

    // Stage 2 M1: structural closure-scope analysis (CaptureScopeAnalysis) — scope ownership, slot
    // assignment, and per-closure binding-scope/hop-distance chain shape. Built once per class in
    // UasmEmitter.Emit(); read-only, consumed by nothing yet (behavior-neutral — env alloc/access
    // codegen is Stage 2 M2). Self-contained (owns its own LambdaCaptureAnalyzer instance), so this
    // is a plain result holder, not shared mutable state.
    public CaptureScopeAnalysis CaptureScope;

    // Stage 2 M2 (design §4.1): resolve a symbol's env binding (owning scope, 1-based env slot).
    // Single source of truth is CaptureScope.CapturedSlots; this helper adds the generic-spec
    // re-keying (a constructed spec's IParameterSymbol never compares equal to the definition's —
    // re-key through ContainingSymbol.OriginalDefinition + ordinal). A symbol that resolves here
    // must NEVER get a flat LocalBindings field — every read/write routes through the env record.
    public bool TryGetEnvBinding(ISymbol symbol, out (CaptureScope Scope, int Slot) binding)
    {
        binding = default;
        if (CaptureScope == null || symbol == null) return false;
        if (CaptureScope.CapturedSlots.TryGetValue(symbol, out var direct))
        {
            binding = direct;
            return true;
        }
        if (symbol is IParameterSymbol p
            && p.ContainingSymbol is IMethodSymbol m
            && !ReferenceEquals(m, m.OriginalDefinition))
        {
            var defParams = m.OriginalDefinition.Parameters;
            if (p.Ordinal < defParams.Length
                && CaptureScope.CapturedSlots.TryGetValue(defParams[p.Ordinal], out var reKeyed))
            {
                binding = reKeyed;
                return true;
            }
        }
        return false;
    }

    // Stage 2 M2: (function, capture-bearing scope id) → the scratch slot holding that scope's LIVE
    // env-record reference in that function's frame. Keyed per CFunction because an env-ref scratch
    // is frame state: a hoisted closure reaches its declaring scopes through __envp + parent hops
    // instead (EnvEmit.Leaf).
    public readonly Dictionary<(object Func, int ScopeId), int> ScopeEnvSlots = new();

    // Stage 2 M2: hoisted closure method (definition-keyed) → the param FIELD id of its hidden
    // trailing __envp parameter. Registered where the closure's params are laid out; read by
    // EnvEmit.Leaf when emission inside the closure body needs an outer scope's env.
    public readonly Dictionary<IMethodSymbol, string> EnvpParamFields
        = new(SymbolEqualityComparer.Default);

    // Round-7 follow-up [Q4]: foreach ITERATION variables. C# makes them READONLY, so invoking a
    // non-readonly struct member on one runs on a DEFENSIVE COPY (the classic foreach-struct-
    // mutation no-op); the loop variable's object[] is live storage in the flat emulation, so the
    // struct-instance-call receiver is CLONED when its chain roots at one of these locals
    // (VM-proven: loop-var reads after a mutating call 1112 vs CLR 102). MEMBERSHIP-ONLY set (§1.5).
    public readonly HashSet<ILocalSymbol> ForeachIterationLocals = new(SymbolEqualityComparer.Default);

    // §2.8 round-3 [A]: local functions whose bodies capture enclosing locals/params. A method-group
    // conversion of such a local function is a closure exactly like a capturing lambda, but it is an
    // IMethodReferenceOperation — invisible to the lambda analyzer — so the guards consult this set
    // to treat it as capturing-lambda-EQUIVALENT (direct stores, the recipient pre-scan, the taint
    // walk, returns). Pre-scanned in UasmEmitter.BuildRecursionInfo from the recursion-info bodies
    // BEFORE any emission (order-independent). MEMBERSHIP-ONLY set (§1.5).
    public readonly HashSet<IMethodSymbol> CapturingLocalFunctions = new(SymbolEqualityComparer.Default);

    /// <summary>Method symbol is a local function that captures enclosing locals/params (§2.8
    /// round-3 [A]). Checks the original definition too: symbol identity across semantic models is
    /// value-based for local functions (syntax + container), same mechanism the recursion graph
    /// relies on.</summary>
    public bool IsCapturingLocalFunction(IMethodSymbol m)
        => m != null && m.MethodKind == MethodKind.LocalFunction
           && (CapturingLocalFunctions.Contains(m) || CapturingLocalFunctions.Contains(m.OriginalDefinition));

    /// <summary>Round-7 follow-up [Q3]: `in` parameters (RefKind.In) are a loud declaration-side
    /// reject. The flat-heap calling convention copies arguments by value with no copy-back, so an
    /// `in` param is neither a readonly ALIAS of the caller's storage (VM-proven: a callee observing
    /// a caller field write through the param read 1 vs CLR 5) nor protected by the readonly
    /// DEFENSIVE COPY (a mutating struct method on the param wrote the param storage, 11 vs CLR 1).
    /// Called at every user-method registration point (class/base/struct/foreign-static methods,
    /// generic specializations, local functions); delegates with `in` params already reject via
    /// DelegateAbi.ValidateNoRefOutParams (RefKind != None).</summary>
    public static void RejectInParameters(IMethodSymbol method)
    {
        foreach (var p in method.Parameters)
            if (p.RefKind == RefKind.In)
                throw new System.NotSupportedException(
                    $"'in' parameter '{p.Name}' on '{method.Name}' is not supported: the flat-heap "
                    + "calling convention copies by value, so 'in' would silently lose its readonly-"
                    + "alias and defensive-copy semantics. Use a by-value parameter, or ref if "
                    + "write-back is intended.");
    }

    /// <summary>M4 [T1]: a [NetworkCallable] method's parameters cross the network, but a delegate
    /// value is a program-local object[] bundle — its target reference and funcaddr are meaningless
    /// in any other client's program, so it can never be marshalled. Pre-fix (probed at 931a9ab)
    /// this compiled CLEAN: the method exported unmangled with a SystemObjectArray param var, a
    /// silent runtime miscompile. The delegate-typed RETURN flavor also compiled clean, even though
    /// stock UdonSharp forbids ANY return type on [NetworkCallable] ("cannot have a return type") —
    /// rejected here for the same bundle reason. Called from the class first-pass registration loop
    /// (own + inherited behaviour methods, before the generic skip), so every compile of a class
    /// hits it exactly once per method.</summary>
    public static void RejectNetworkCallableDelegates(IMethodSymbol method)
    {
        if (!LayoutPlanner.IsNetworkCallable(method)) return;
        foreach (var p in method.Parameters)
            if (ContainsDelegateType(p.Type))
                throw new System.NotSupportedException(
                    $"[NetworkCallable] method '{method.Name}' cannot take delegate-typed parameter "
                    + $"'{p.Name}': a delegate value is a program-local object[] bundle and cannot "
                    + "cross a network call. Pass plain data instead and re-create the delegate "
                    + "locally on the receiving side.");
        if (ContainsDelegateType(method.ReturnType))
            throw new System.NotSupportedException(
                $"[NetworkCallable] method '{method.Name}' cannot return a delegate-typed value: "
                + "a delegate value is a program-local object[] bundle and cannot cross a network "
                + "call. Return plain data instead and re-create the delegate locally on the "
                + "receiving side.");
    }

    /// <summary>Delegate proper, or an array (of arrays…) of delegates. Deliberately NARROW (not
    /// object / delegate-tuples / type params): [NetworkCallable] methods with object params are
    /// outside this policy item and must not start rejecting.</summary>
    static bool ContainsDelegateType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol n && n.DelegateInvokeMethod != null) return true;
        if (type is IArrayTypeSymbol a) return ContainsDelegateType(a.ElementType);
        return false;
    }

    // Aggregate type support — tuples and user-defined structs share the object[] emulation.
    public static bool IsAggregateType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return false;
        // Armor (design §1.2): a delegate value is an object[] BUNDLE copied by reference — it must never
        // ride the aggregate clone-on-read machinery (a clone would break reference identity and
        // (target, method) equality). Single choke point for every clone path.
        if (named.TypeKind == TypeKind.Delegate) return false;
        return named.IsTupleType || IsUserStruct(named);
    }

    /// <summary>Source-defined value struct (object[]-emulated). Excludes SDK/native structs
    /// (Vector3, Color, …) — which have native Udon extern types — by namespace, since in the test
    /// environment SDK types are source stubs (so syntax-refs alone can't tell them apart).</summary>
    public static bool IsUserStruct(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Struct || type.SpecialType != SpecialType.None) return false;
        if (type.DeclaringSyntaxReferences.Length == 0) return false; // from a referenced assembly = native
        return !IsSdkNamespace(type.ContainingNamespace);
    }

    /// <summary>The parameterless void Dispose() of a user type (public or explicit IDisposable impl),
    /// or null. Used to route a `using` resource's implicit Dispose through a real method call rather
    /// than a non-existent SystemObjectArray.__Dispose__ extern when the disposable is a user struct.</summary>
    public static IMethodSymbol FindStructDisposeMethod(ITypeSymbol type)
    {
        foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
            if (!m.IsStatic && m.Parameters.Length == 0 && m.ReturnsVoid
                && (m.Name == "Dispose"
                    || m.ExplicitInterfaceImplementations.Any(e => e.Name == "Dispose")))
                return m;
        return null;
    }

    static bool IsSdkNamespace(INamespaceSymbol ns)
    {
        for (var n = ns; n != null && !n.IsGlobalNamespace; n = n.ContainingNamespace)
        {
            if (n.Name is "System" or "UnityEngine" or "VRC" or "Cinemachine"
                or "TMPro" or "Unity" or "Microsoft")
                return true;
        }
        return false;
    }

    readonly Dictionary<ITypeSymbol, AggregateLayout> _aggregateLayoutCache = new(SymbolEqualityComparer.Default);

    public AggregateLayout GetAggregateLayout(INamedTypeSymbol type)
    {
        if (_aggregateLayoutCache.TryGetValue(type, out var cached)) return cached;
        var layout = AggregateLayout.Build(type);
        _aggregateLayoutCache[type] = layout;
        return layout;
    }

    // Field initializers to emit at _start
    public readonly List<(string fieldName, IOperation initOp, ITypeSymbol fieldType)> FieldInitOps = new();

    // FieldChangeCallback: fieldName → propertyName
    public readonly Dictionary<string, string> FieldChangeCallbacks = new();

    // Conditional access stack (for ?. operator): the evaluated instance leaf. For a delegate-typed
    // receiver this is the BUNDLE leaf itself (design §2.6) — `d?.Invoke()` dispatches on it, and any
    // delegate-valued expression (local/param/element/call result) is a legal ?.Invoke receiver.
    public readonly Stack<CLeaf> ConditionalAccessStack = new();

    // using declaration Dispose tracking
    public readonly Stack<List<(CLeaf val, ITypeSymbol type)>> UsingDisposableStack = new();

    /// <summary>Stack of using-stack depths at loop/switch entry points.
    /// Used to limit Dispose emission for break/continue to scopes inside the loop.</summary>
    public readonly Stack<int> LoopUsingDepthStack = new();

    // Switch break label stack — top is non-null inside switch body, null sentinel inside loop body.
    // StatementHandler.VisitBranch reads top to distinguish switch breaks (goto end label) from loop breaks (CBreak).
    public readonly Stack<string> SwitchBreakLabels = new();

    int _switchLabelCounter;
    /// <summary>Generate a unique end label for a switch statement (per EmitContext = per class).</summary>
    public string NextSwitchEndLabel() => $"__switchEnd_{++_switchLabelCounter}";

    // goto-case / goto-default → sanitized UASM landing label, per enclosing switch (innermost on top). The
    // Roslyn target name ("case 2:", "default") is not a valid UASM label token, so both the case-body label
    // (SwitchHandler) and the goto (StatementHandler.VisitBranch) resolve through this shared map.
    public readonly Stack<Dictionary<string, string>> GotoCaseLabels = new();

    // Delegate fields: tracks which user fields are delegate-typed and were expanded to bundles
    public readonly HashSet<string> DelegateFields = new();

    // Pending delegate bridges for dynamically hoisted lambdas/local functions
    public readonly List<(IMethodSymbol method, string bridgeExportName, Dictionary<ITypeParameterSymbol, ITypeSymbol> resolvedTypeParamMap)> PendingDelegateBridges = new();

    // Diagnostics collected during emission
    public readonly List<EmitDiagnostic> Diagnostics = new();
    public readonly HashSet<string> ReportedExterns = new();

    // Dispatch delegates (Core IR-based)
    Action<IOperation> _visitOperation;
    Func<IOperation, CLeaf> _visitExpression;
    Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> _emitPatternCheck;
    Func<INamedTypeSymbol, CLeaf> _emitNewAggregate;

    public Action<IOperation> VisitOperation => _visitOperation
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");
    public Func<IOperation, CLeaf> VisitExpression => _visitExpression
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");
    public Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> EmitPatternCheck => _emitPatternCheck
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");
    /// <summary>Allocate + default-initialize a fresh object[]-backed aggregate (struct/tuple) as a value.
    /// Exposed so non-handler emit paths (e.g. default-initializing an aggregate field) can reuse it.</summary>
    public Func<INamedTypeSymbol, CLeaf> EmitNewAggregate => _emitNewAggregate
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");

    /// <summary>Aggregate (struct/tuple) instance fields with NO explicit initializer. C# default-initializes
    /// them to a zeroed struct; in the object[] emulation that requires a fresh default object[] (else the heap
    /// var stays null and a field write faults). Reference-type / array fields correctly stay null and are absent here.</summary>
    public readonly List<(string fieldName, INamedTypeSymbol aggType)> AggregateFieldDefaults = new();

    public void InitializeDispatchers(
        Action<IOperation> visitOp,
        Func<IOperation, CLeaf> visitExpr,
        Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> emitPattern,
        Func<INamedTypeSymbol, CLeaf> emitNewAggregate)
    {
        _visitOperation = visitOp ?? throw new ArgumentNullException(nameof(visitOp));
        _visitExpression = visitExpr ?? throw new ArgumentNullException(nameof(visitExpr));
        _emitPatternCheck = emitPattern ?? throw new ArgumentNullException(nameof(emitPattern));
        _emitNewAggregate = emitNewAggregate ?? throw new ArgumentNullException(nameof(emitNewAggregate));
    }

    public EmitContext(Compilation compilation, INamedTypeSymbol classSymbol, LayoutPlanner planner)
    {
        Compilation = compilation;
        ClassSymbol = classSymbol;
        Module = new CModule { ClassName = classSymbol.ToDisplayString() };
        Builder = new CoreBuilder(Module);
        Planner = planner;
        CaptureAnalyzer = new LambdaCaptureAnalyzer(compilation);
    }

    // ══════════════════════════════════════════════════════════════════
    // Variable naming utilities (replaces VariableTable)
    // ══════════════════════════════════════════════════════════════════

    readonly Dictionary<string, int> _counters = new();
    readonly HashSet<string> _declaredFieldNames = new();
    readonly Dictionary<string, string> _thisVars = new();
    readonly Dictionary<string, string> _structConstIds = new();

    int NextIndex(string key)
    {
        _counters.TryGetValue(key, out var n);
        _counters[key] = n + 1;
        return n;
    }

    /// <summary>Declare a field in Module. Idempotent — returns existing name if already declared.</summary>
    public string DeclareField(string name, string type, FieldFlags flags = FieldFlags.None,
        object defaultValue = null, string syncMode = null)
    {
        if (_declaredFieldNames.Contains(name)) return name;
        var field = new FieldDecl(name, type) { Flags = flags, DefaultValue = defaultValue, SyncMode = syncMode };
        Module.Fields.Add(field);
        _declaredFieldNames.Add(name);
        return name;
    }

    /// <summary>Declare a named variable field. Idempotent.</summary>
    public string DeclareVar(string id, string type)
    {
        if (_declaredFieldNames.Contains(id)) return id;
        Module.Fields.Add(new FieldDecl(id, type));
        _declaredFieldNames.Add(id);
        return id;
    }

    /// <summary>Try to declare a variable. Returns true if newly declared.</summary>
    public bool TryDeclareVar(string id, string type)
    {
        if (_declaredFieldNames.Contains(id)) return false;
        Module.Fields.Add(new FieldDecl(id, type));
        _declaredFieldNames.Add(id);
        return true;
    }

    /// <summary>Declare a local variable with unique field name.</summary>
    public string DeclareLocal(string name, string type)
    {
        var idx = NextIndex($"lcl_{name}_{type}");
        var id = $"__lcl_{name}_{type}_{idx}";
        Module.Fields.Add(new FieldDecl(id, type));
        _declaredFieldNames.Add(id);
        return id;
    }

    /// <summary>Declare a "this" reference field with type remapping for Udon heap.</summary>
    public string DeclareThis(string udonType)
    {
        var heapType = SupportedThisTypes.Contains(udonType) ? udonType : "VRCUdonUdonBehaviour";
        var idx = NextIndex($"this_{heapType}");
        var id = $"__this_{heapType}_{idx}";
        Module.Fields.Add(new FieldDecl(id, heapType) { DefaultValue = "this" });
        _declaredFieldNames.Add(id);
        return id;
    }

    /// <summary>Declare or reuse a "this" reference for the given type.</summary>
    public string DeclareThisOnce(string udonType)
    {
        if (_thisVars.TryGetValue(udonType, out var existing)) return existing;
        var id = DeclareThis(udonType);
        _thisVars[udonType] = id;
        return id;
    }

    static readonly HashSet<string> SupportedThisTypes = new()
    {
        "UnityEngineGameObject", "UnityEngineTransform", "VRCUdonUdonBehaviour",
    };

    // ── Software recursion stack ──
    // Udon's flat heap shares param/local slots across call frames, so recursion-cycle calls must spill
    // the caller's live values to a heap-backed LIFO stack (boxed object[]) and reload after the call.

    public const string RecurStackId = "__recurStack";
    public const string RecurSpId = "__recurSp";
    /// <summary>Max boxed values held across all live recursion frames (depth × live-vars-per-frame).</summary>
    public const int RecurStackSize = 512;
    bool _recurStackDeclared;

    /// <summary>Idempotently declare the per-program recursion stack (object[] backing + int stack pointer).
    /// Heap default allocates the backing array and zeroes the pointer; LIFO spill/reload keeps it balanced.</summary>
    public void EnsureRecursionStack()
    {
        if (_recurStackDeclared) return;
        _recurStackDeclared = true;
        Module.Fields.Add(new FieldDecl(RecurStackId, "SystemObjectArray") { DefaultValue = new object[RecurStackSize] });
        _declaredFieldNames.Add(RecurStackId);
        Module.Fields.Add(new FieldDecl(RecurSpId, "SystemInt32") { DefaultValue = 0 });
        _declaredFieldNames.Add(RecurSpId);
    }


    /// <summary>Declare reflection type IDs array.</summary>
    public void DeclareReflTypeIds(long[] typeIds)
    {
        DeclareField("__refl_typeids", "SystemInt64Array", defaultValue: typeIds);
    }

    /// <summary>Set const value on an existing field.</summary>
    public void SetFieldConstValue(string name, object value)
    {
        var field = Module.Fields.FirstOrDefault(f => f.Name == name);
        if (field != null) field.DefaultValue = value;
    }

    /// <summary>Check if a field name has been declared.</summary>
    public bool IsFieldDeclared(string name) => _declaredFieldNames.Contains(name);

    /// <summary>Allocate a Scratch slot for a temporary value (slot-based, coalesced by register allocator).</summary>
    public int AllocTemp(string type) => Builder.AllocScratch(type);

    /// <summary>Declare a struct constant field with deduplication (e.g., Vector3.zero).</summary>
    public string DeclareStructConst(string type, object value)
    {
        var key = $"{type}_{value}";
        if (_structConstIds.TryGetValue(key, out var existing)) return existing;
        var idx = NextIndex($"structconst_{type}");
        var id = $"__const_{type}_{idx}";
        Module.Fields.Add(new FieldDecl(id, type) { DefaultValue = value });
        _declaredFieldNames.Add(id);
        _structConstIds[key] = id;
        return id;
    }

    /// <summary>Get the Udon type of a declared field by its ID.</summary>
    public string GetFieldType(string id)
    {
        return Module.Fields.FirstOrDefault(f => f.Name == id)?.Type;
    }

    // ── Constant parsing (moved from VariableTable) ──

    /// <summary>Parse a string constant value to a typed CLR object.</summary>
    public static object ParseConstValue(string udonType, string value)
    {
        if (value == "null") return null;
        return udonType switch
        {
            "SystemInt32" => value.StartsWith("0x") ? Convert.ToInt32(value, 16) : int.Parse(value),
            "SystemUInt32" => value.StartsWith("0x") ? Convert.ToUInt32(value, 16) : uint.Parse(value),
            "SystemInt64" => long.Parse(value),
            "SystemUInt64" => ulong.Parse(value),
            "SystemInt16" => short.Parse(value),
            "SystemUInt16" => ushort.Parse(value),
            "SystemSByte" => sbyte.Parse(value),
            "SystemSingle" => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            "SystemDouble" => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            "SystemBoolean" => bool.Parse(value),
            "SystemString" => value,
            "SystemByte" => byte.Parse(value),
            "SystemChar" => value[0],
            "SystemType" => value, // Udon type name, resolved to CLR Type at apply time
            _ => long.TryParse(value, out var longVal)
                ? (longVal is >= int.MinValue and <= int.MaxValue ? (object)(int)longVal : longVal)
                : ulong.TryParse(value, out var ulongVal) ? (object)ulongVal : null,
        };
    }
}
