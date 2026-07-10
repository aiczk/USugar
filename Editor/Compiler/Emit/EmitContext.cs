using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class EmitContext
{
    // Core dependencies
    public readonly Compilation Compilation;
    public readonly INamedTypeSymbol ClassSymbol;
    public readonly CModule Module;
    public readonly CoreBuilder Builder;
    public readonly LayoutPlanner Planner;
    public readonly StorageContext Storage;
    public readonly BoundaryChecker Boundary;
    public readonly GenericContext Generics = new GenericContext();
    public readonly RecursionContext RecursionContext = new RecursionContext();
    public readonly ClosureContext Closures = new ClosureContext();
    public readonly AggregateContext Aggregates = new AggregateContext();
    public readonly SyntheticContext Synthetics = new SyntheticContext();
    public readonly ControlFlowContext ControlFlow = new ControlFlowContext();
    public readonly InitializationContext Initializers = new InitializationContext();
    public readonly DiagnosticContext DiagnosticState = new DiagnosticContext();
    public readonly MethodContext Methods = new MethodContext();
    public readonly struct MethodSlot
    {
        public readonly int Index;
        public readonly string VarPrefix;
        public MethodSlot(int index, string varPrefix) { Index = index; VarPrefix = varPrefix; }
    }

    // Depth-1 type-param scope. EmitMethod is a non-recursive serial drain, so exactly one map is
    // active at a time; a nested Enter means a prior scope leaked (a compiler bug) and throws loudly
    // rather than silently inheriting someone else's map. Dispose is the SOLE clear site, so the map
    // is cleared even if body emission throws.
    public IDisposable EnterTypeParamScope(IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map)
        => Generics.EnterScope(map, Methods.CurrentMethod);

    /// <summary>What about a generic definition's body closures pins it to a single instantiation.
    /// <c>UsedParams</c> (round-8 [Y2]): which of the generic's OWN type parameters (by kind+ordinal,
    /// Y8-robust) a closure's signature or body references — the closure is hoisted ONCE keyed by
    /// IMethodSymbol and emitted under the FIRST spec's type map, so a second instantiation that changes
    /// one of THOSE params would silently run the first instantiation's type. B64: this is per-param, so
    /// a second instantiation that only varies a param NO closure touches (MBox&lt;T&gt;.Run&lt;U&gt;
    /// where the closure uses T only, U varies) is legal.
    /// <c>Capturing</c> (B64 soundness): whether any body closure captures a local/parameter owned by the
    /// generic's OWN scope (an ancestor-scope capture — a generic local function over the enclosing
    /// non-generic method's local — is shared correctly and is NOT flagged). This only matters for a STATIC
    /// method: its inlined per-call-site specialization shares the one hoisted closure across specs WITHOUT
    /// re-seeding a per-activation env record, so a captured value written by one instantiation is read by
    /// the other (VM-proven: static HE.Run&lt;int&gt;/&lt;string&gt; returns 3+3, not 3+4). INSTANCE methods
    /// (behaviour or struct receiver) get per-activation env records that de-alias across instantiations
    /// (VM-proven Match with DIVERGENT values: MinA3, Box=110) — the case Stage-2 §8.1 correctly retired;
    /// the caller (<see cref="ThrowIfClosureAliasesInstantiation"/>) applies the static-only gate. (The
    /// original §8.1 "proof" happened to only exercise instance methods, so the static gap went unseen.)
    /// Single-instantiation multi-call capture de-aliases everywhere; the pin only fires on a second
    /// DISTINCT instantiation.</summary>
    public readonly struct ClosurePinInfo
    {
        public readonly HashSet<(TypeParameterKind Kind, int Ordinal)> UsedParams;
        public readonly bool Capturing;
        public ClosurePinInfo(HashSet<(TypeParameterKind, int)> used, bool capturing)
        { UsedParams = used; Capturing = capturing; }
    }

    public static ClosurePinInfo GenericBodyClosurePins(Compilation compilation, IMethodSymbol def)
    {
        var used = new HashSet<(TypeParameterKind, int)>();
        var syntaxRef = def.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return new ClosurePinInfo(used, false);
        var syntax = syntaxRef.GetSyntax();
        var op = compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax);
        // B68/B69 (ControlC): if `def` is itself a generic LOCAL FUNCTION, its declaration's operation IS an
        // ILocalFunctionOperation — walk its BODY, not the node itself. `def` is monomorphized per call site
        // (Lf<int>/Lf<string> emit as separate functions, no shared hoist), so its own type-param usage never
        // aliases; only a NESTED closure inside it can pin. (A generic METHOD def already yields its body
        // block here, so this only changes the LF-def case.)
        var body = op is ILocalFunctionOperation lfDef ? lfDef.Body : op;
        // B53: only THIS definition's own instantiation dimension pins it — a nested generic local
        // function's own (unrelated) type parameter must not. Every reference test filters to `def`'s
        // params (method type params, or the containing type's for a generic-struct member).
        bool capturing = false;
        WalkClosurePins(body, used, ref capturing, def);
        return new ClosurePinInfo(used, capturing);
    }

    // Y8-robust ownership: a body-walk's type-parameter symbol is fresh (reference-distinct) from the
    // declaration's, so compare the DECLARING method/type's OriginalDefinition, never symbol identity.
    static bool OwnedByDef(ITypeParameterSymbol tp, IMethodSymbol def)
    {
        if (tp.TypeParameterKind == TypeParameterKind.Method)
            return tp.DeclaringMethod != null
                && SymbolEqualityComparer.Default.Equals(tp.DeclaringMethod.OriginalDefinition, def.OriginalDefinition);
        if (tp.TypeParameterKind == TypeParameterKind.Type)
            return def.ContainingType is { IsGenericType: true } ct && tp.DeclaringType != null
                && SymbolEqualityComparer.Default.Equals(tp.DeclaringType.OriginalDefinition, ct.OriginalDefinition);
        return false;
    }

    // Visits EVERY closure: collects the union of def-owned type parameters any closure references
    // (UsedParams) and whether any closure captures an enclosing local/parameter (Capturing).
    static void WalkClosurePins(
        IOperation op, HashSet<(TypeParameterKind, int)> used, ref bool capturing, IMethodSymbol def)
    {
        if (op == null) return;
        switch (op)
        {
            case IAnonymousFunctionOperation af:
                CollectClosureTypeParams(af.Symbol, af.Body, def, used);
                if (!capturing && ClosureCapturesDefScopedVar(af.Symbol, af.Body, def)) capturing = true;
                break;
            case ILocalFunctionOperation lf when lf.Symbol != null:
                CollectClosureTypeParams(lf.Symbol, lf.Body, def, used);
                if (!capturing && ClosureCapturesDefScopedVar(lf.Symbol, lf.Body, def)) capturing = true;
                break;
        }
        foreach (var child in op.ChildOps())
            WalkClosurePins(child, used, ref capturing, def);
    }

    // Does this closure capture a local/parameter whose scope is `def` ITSELF or a closure nested inside
    // def? Only those alias across instantiations: def emits one owner body per spec but they SHARE the
    // one hoisted closure, and def's own locals/params take a fresh value in each instantiation's
    // activation, so the shared capture cell leaks one spec's value into the other's read. A capture of an
    // ANCESTOR scope's variable (e.g. a generic local function Lf<T> capturing the enclosing non-generic
    // method's local) is shared legitimately — there is one ancestor activation, so both specs must read
    // it, and the per-spec __envp keying (Stage-2 M5) plumbs it correctly. A `this`/field capture is
    // invariant across activations and never aliases. B78: the closure's own declarations are collected via
    // the SHARED LambdaCaptureAnalyzer.CollectInsideSymbols (the former inline twin missed out-var /
    // deconstruction declarations, so a closure using only its own out-var locals was mistaken for a
    // def-scope capture and false-rejected).
    static bool ClosureCapturesDefScopedVar(IMethodSymbol closureSym, IOperation closureBody, IMethodSymbol def)
    {
        if (closureBody == null) return false;
        var inside = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        if (closureSym != null) foreach (var p in closureSym.Parameters) inside.Add(p);
        LambdaCaptureAnalyzer.CollectInsideSymbols(closureBody, inside);
        foreach (var d in closureBody.DescendantsAndSelf())
        {
            if (d is ILocalReferenceOperation lr && !inside.Contains(lr.Local)
                && CapturedVarBelongsToDefScope(lr.Local, def)) return true;
            if (d is IParameterReferenceOperation pr && !inside.Contains(pr.Parameter)
                && CapturedVarBelongsToDefScope(pr.Parameter, def)) return true;
        }
        return false;
    }

    // Walk the captured variable's declaring-method chain: is `def` on it? True ⇒ the variable lives in
    // def's own activation (or a closure nested in def), so it takes a distinct value per instantiation.
    // False ⇒ it belongs to an ancestor scope shared across all instantiations.
    static bool CapturedVarBelongsToDefScope(ISymbol capturedVar, IMethodSymbol def)
    {
        for (var s = capturedVar.ContainingSymbol as IMethodSymbol; s != null; s = s.ContainingSymbol as IMethodSymbol)
            if (SymbolEqualityComparer.Default.Equals(s.OriginalDefinition, def.OriginalDefinition))
                return true;
        return false;
    }

    static void CollectClosureTypeParams(
        IMethodSymbol closureSym, IOperation closureBody, IMethodSymbol def, HashSet<(TypeParameterKind, int)> used)
    {
        if (closureSym != null)
        {
            CollectTypeParams(closureSym.ReturnType, def, used);
            foreach (var p in closureSym.Parameters) CollectTypeParams(p.Type, def, used);
        }
        CollectOperationTypeParams(closureBody, def, used);
    }

    static void CollectOperationTypeParams(IOperation op, IMethodSymbol def, HashSet<(TypeParameterKind, int)> used)
    {
        if (op == null) return;
        CollectTypeParams(op.Type, def, used);
        if (op is ITypeOfOperation typeOf) CollectTypeParams(typeOf.TypeOperand, def, used);
        if (op is IIsTypeOperation isType) CollectTypeParams(isType.TypeOperand, def, used);
        // B73: a type parameter used ONLY in a pattern (`o is T x`, `case T t:`, recursive `T { … }`) is a
        // real per-instantiation type test — route the pattern's matched type through the same collector so
        // the closure pin sees it (shared with the capture walk via LambdaCaptureAnalyzer.PatternInfo).
        var (matchedType, _) = LambdaCaptureAnalyzer.PatternInfo(op);
        if (matchedType != null) CollectTypeParams(matchedType, def, used);
        if (op is IInvocationOperation inv)
            foreach (var ta in inv.TargetMethod.TypeArguments) CollectTypeParams(ta, def, used);
        foreach (var child in op.ChildOps())
            CollectOperationTypeParams(child, def, used);
    }

    static void CollectTypeParams(ITypeSymbol t, IMethodSymbol def, HashSet<(TypeParameterKind, int)> used)
    {
        switch (t)
        {
            case ITypeParameterSymbol tp when OwnedByDef(tp, def):
                used.Add((tp.TypeParameterKind, tp.Ordinal));
                break;
            case IArrayTypeSymbol at:
                CollectTypeParams(at.ElementType, def, used);
                break;
            case INamedTypeSymbol nt when nt.IsGenericType:
                foreach (var ta in nt.TypeArguments) CollectTypeParams(ta, def, used);
                break;
        }
    }

    static bool MethodTypeArgsDiffer(IMethodSymbol a, IMethodSymbol b)
    {
        var xa = a.TypeArguments;
        var xb = b.TypeArguments;
        if (xa.Length != xb.Length) return true;
        for (int i = 0; i < xa.Length; i++)
            if (!SymbolEqualityComparer.Default.Equals(xa[i], xb[i])) return true;
        return false;
    }

    // The type argument a constructed method substitutes for its own (method-kind) or its containing
    // type's (type-kind) parameter at the given ordinal; null if out of range.
    static ITypeSymbol SubstituteTypeArg(IMethodSymbol m, TypeParameterKind kind, int ordinal)
    {
        if (kind == TypeParameterKind.Method)
            return ordinal < m.TypeArguments.Length ? m.TypeArguments[ordinal] : null;
        if (kind == TypeParameterKind.Type && m.ContainingType is { } ct)
            return ordinal < ct.TypeArguments.Length ? ct.TypeArguments[ordinal] : null;
        return null;
    }

    /// <summary>Shared [Y2]/B64 reject on a SECOND DISTINCT instantiation of a generic whose body
    /// contains a closure. Loud when EITHER a closure-used type parameter varies between the two specs
    /// (the hoist was emitted with the first spec's types), OR the two specs differ in a METHOD type
    /// argument and a closure captures a variable owned by the generic method's own scope (the closure is
    /// hoisted once and SHARED across the method's specs, so its capture cells alias across them —
    /// VM-proven: a differing captured value leaks one spec's value into the other's read). A capture in a
    /// generic-STRUCT member that differs only in the CONTAINING type argument does NOT alias — each
    /// struct specialization emits its own closure copy (B45 M1, VM-proven Box&lt;int&gt;/Box&lt;string&gt;
    /// = 110), so it stays legal; and a capture of an ANCESTOR scope (a generic local function capturing
    /// the enclosing non-generic method's local) is shared correctly (Stage-2 M5). A closure that neither
    /// uses a VARYING param nor def-scope-captures across a method-arg change is legal (B64:
    /// MBox&lt;T&gt;.Run&lt;U&gt; with a capture-free closure that uses only the constant T). The type-param
    /// check runs first so a closure that both captures and uses a varying param reports the
    /// distinguishable type-param message (§8.2). No-op when the two specs are the same symbol.</summary>
    public static void ThrowIfClosureAliasesInstantiation(
        Compilation compilation, IMethodSymbol firstSpec, IMethodSymbol constructed)
    {
        if (SymbolEqualityComparer.Default.Equals(firstSpec, constructed)) return;
        var pin = GenericBodyClosurePins(compilation, constructed.OriginalDefinition);
        foreach (var (kind, ordinal) in pin.UsedParams)
        {
            // B70 (A15): a TYPE-kind param varies when the CONTAINING generic struct is instantiated at two
            // args (GS15<int>.Run vs GS15<string>.Run). VM-proven this DOES alias — the nested LF is one
            // shared hoist across the two struct specs, emitted with the first spec's T (divergent probe:
            // default(T) via GD<int>/GD<string> returned 410 not 310, i.e. both ran with T=int). So a
            // type-kind variance pins exactly like a method-kind one; both are checked here.
            var a = SubstituteTypeArg(firstSpec, kind, ordinal);
            var b = SubstituteTypeArg(constructed, kind, ordinal);
            if (a != null && b != null && !SymbolEqualityComparer.Default.Equals(a, b))
                throw new System.NotSupportedException(
                    $"Generic method '{constructed.Name}' is instantiated with more than one type-argument "
                    + "combination but contains a lambda or local function whose signature or body uses the "
                    + "generic's type parameters, one of which varies between those instantiations. The hoisted "
                    + "closure is shared across instantiations in the flat-heap model and was emitted with the "
                    + "first instantiation's types. Use a single instantiation, or keep the closure independent "
                    + "of the varying type parameter.");
        }
        // Capture aliasing is confined to a STATIC generic method: its inlined per-call-site specialization
        // path shares the one hoisted closure across the specs WITHOUT re-seeding a per-activation env
        // record, so a differing captured value leaks between specs (VM-proven: a static HE.Run<int>/<string>
        // that captures its int param returns 3+3, not 3+4). An INSTANCE generic method (on the behaviour or
        // a struct receiver) DOES get per-activation env records that de-alias across instantiations
        // (VM-proven Match: MinA3 Gen<int>/Gen<long>, Box<int>/Box<string> = 110), so it stays legal — this
        // is the case Stage-2 §8.1 correctly retired. A differing CONTAINING type argument likewise emits a
        // fresh closure per spec (B45). So the capture pin fires only on a static method whose OWN type
        // arguments differ between the specs.
        if (pin.Capturing && constructed.IsStatic && MethodTypeArgsDiffer(firstSpec, constructed))
            throw new System.NotSupportedException(
                $"Static generic method '{constructed.Name}' is instantiated with more than one method "
                + "type-argument combination but contains a lambda or local function that captures "
                + "locals/parameters. Its inlined specialization shares one hoisted closure across the specs "
                + "without a per-activation env record, so a value captured by one instantiation leaks into the "
                + "other (VM-proven aliasing). Use a single instantiation, make the closure capture-free, or "
                + "move the method onto a UdonSharpBehaviour/struct instance (whose captures de-alias).");
    }

    // Persistent local symbol → field name mapping (survives scope pop). Holds NON-captured locals
    // only: a captured local has no flat field — its cell lives in the owning scope's env record
    // (Stage 2, TryGetEnvBinding / EnvEmit), so per-activation captures no longer alias.
    public readonly struct LocalBinding
    {
        public readonly string Id;
        public LocalBinding(string id) { Id = id; }
    }

    // Round-7 follow-up [Q4]: foreach ITERATION variables. C# makes them READONLY, so invoking a
    // non-readonly struct member on one runs on a DEFENSIVE COPY (the classic foreach-struct-
    // mutation no-op); the loop variable's object[] is live storage in the flat emulation, so the
    // struct-instance-call receiver is CLONED when its chain roots at one of these locals
    // (VM-proven: loop-var reads after a mutating call 1112 vs CLR 102). MEMBERSHIP-ONLY set (§1.5).
    public readonly HashSet<ILocalSymbol> ForeachIterationLocals = new(SymbolEqualityComparer.Default);

    // Dispatch delegates (Core IR-based)
    Action<IOperation> _visitOperation;
    Func<IOperation, CLeaf> _visitExpression;
    Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> _emitPatternCheck;

    public Action<IOperation> VisitOperation => _visitOperation
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");
    public Func<IOperation, CLeaf> VisitExpression => _visitExpression
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");
    public Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> EmitPatternCheck => _emitPatternCheck
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");

    public void InitializeDispatchers(
        Action<IOperation> visitOp,
        Func<IOperation, CLeaf> visitExpr,
        Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> emitPattern)
    {
        _visitOperation = visitOp ?? throw new ArgumentNullException(nameof(visitOp));
        _visitExpression = visitExpr ?? throw new ArgumentNullException(nameof(visitExpr));
        _emitPatternCheck = emitPattern ?? throw new ArgumentNullException(nameof(emitPattern));
    }

    public EmitContext(Compilation compilation, INamedTypeSymbol classSymbol, LayoutPlanner planner)
    {
        Compilation = compilation;
        ClassSymbol = classSymbol;
        Module = new CModule { ClassName = classSymbol.ToDisplayString() };
        Builder = new CoreBuilder(Module);
        Planner = planner;
        Storage = new StorageContext(Module);
        Boundary = new BoundaryChecker(this);
    }

    // ══════════════════════════════════════════════════════════════════
    // Variable naming utilities (replaces VariableTable)
    // ══════════════════════════════════════════════════════════════════

    // ── Software recursion stack ──
    // Udon's flat heap shares param/local slots across call frames, so recursion-cycle calls must spill
    // the caller's live values to a heap-backed LIFO stack (boxed object[]) and reload after the call.

    public const string RecurStackId = RecurStack.StackId;
    public const string RecurSpId = RecurStack.SpId;
    /// <summary>Max boxed values held across all live recursion frames (depth × live-vars-per-frame).
    /// Wave-12 [V1]: 512 → 8192. Legal non-tail recursion at depth ~600 with per-frame closure state
    /// (~9 spilled slots per logical frame, VM-proven ER05/ER11 budget probes) needs ~5400 entries —
    /// the old 512 budget VmFaulted compile-clean code at depths plain C# handles trivially. The
    /// array is allocated once per program and ONLY when a recursion cycle exists
    /// (EnsureRecursionStack is on-demand), so non-recursive programs pay nothing; the size lives in
    /// the heap-default side channel, not the UASM text.</summary>
    public const int RecurStackSize = RecurStack.Size;


    public const string ReflTypeIdField = "__refl_typeid";
    public const string ReflTypeIdsField = "__refl_typeids";
    public const string ReflTypeNameField = "__refl_typename";

    /// <summary>Declare reflection type IDs array.</summary>
    public void DeclareReflTypeIds(long[] typeIds)
    {
        Storage.DeclareField(ReflTypeIdsField, "SystemInt64Array", defaultValue: typeIds);
    }

}
