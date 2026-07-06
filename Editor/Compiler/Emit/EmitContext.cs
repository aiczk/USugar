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
    public readonly BoundaryChecker Boundary;
    public readonly GenericContext Generics = new GenericContext();
    public readonly RecursionContext RecursionContext = new RecursionContext();

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

    /// <summary>Recursion/reentrancy analysis results for this class — see <see cref="RecursionInfo"/>
    /// for what each product field means. Populated in place by <c>UasmEmitter.BuildRecursionInfo</c>
    /// before body emission; each of its fields is null until then.</summary>
    public RecursionInfo Recursion => RecursionContext.Info;

    /// <summary>True when a call from <paramref name="caller"/> to <paramref name="callee"/> is a
    /// recursion-cycle edge (callee in caller's non-trivial SCC, including direct self-recursion).</summary>
    public bool IsRecursiveEdge(IMethodSymbol caller, IMethodSymbol callee)
        => RecursionContext.IsRecursiveEdge(caller, callee);

    /// <summary>True when a call from <paramref name="caller"/> to <paramref name="callee"/> lies in
    /// a recursion cycle (same non-trivial SCC or direct self-loop), tail or not ([Y3]).</summary>
    public bool IsCycleEdge(IMethodSymbol caller, IMethodSymbol callee)
        => RecursionContext.IsCycleEdge(caller, callee);

    /// <summary>[Q5] True when <paramref name="callee"/>'s transitive touch set contains the
    /// this-field <paramref name="field"/> (both compared by OriginalDefinition).</summary>
    public bool CalleeTouchesThisField(IMethodSymbol callee, IFieldSymbol field)
        => RecursionContext.CalleeTouchesThisField(callee, field);

    public int NextMethodIndex;
    public readonly List<(IMethodSymbol symbol, CFunction func)> PendingLocalFunctions = new();

    // Generic monomorphization
    public readonly List<IMethodSymbol> PendingGenericSpecs = new();
    // Immutable and scope-owned: built only by TypeParamScope.Compose, and written ONLY by the
    // EnterTypeParamScope machinery below (private setter). Read freely everywhere.
    public IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap => Generics.TypeParamMap;

    // Depth-1 type-param scope. EmitMethod is a non-recursive serial drain, so exactly one map is
    // active at a time; a nested Enter means a prior scope leaked (a compiler bug) and throws loudly
    // rather than silently inheriting someone else's map. Dispose is the SOLE clear site, so the map
    // is cleared even if body emission throws.
    public IDisposable EnterTypeParamScope(IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map)
        => Generics.EnterScope(map, CurrentMethod);

    // Wave-9 round-5 [X6]: first registered specialization per generic DEFINITION. Lambdas and
    // local functions hoisted from a generic body are keyed by IMethodSymbol and therefore SHARED
    // across that body's specializations — a capturing closure's capture cells are seeded by
    // whichever spec emitted LAST (last-spec-wins; VM-proven r1=8 vs 3). A second DISTINCT
    // instantiation of a definition whose body contains a capturing closure is loud (per-spec
    // closure environments are Stage-2 territory, design §8-3). LOOKUP-ONLY (§1.5).
    public readonly Dictionary<IMethodSymbol, IMethodSymbol> FirstGenericSpec
        = new(SymbolEqualityComparer.Default);

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
        foreach (var child in op.Children)
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
        foreach (var child in op.Children)
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

    public readonly Dictionary<ILocalSymbol, LocalBinding> LocalBindings = new(SymbolEqualityComparer.Default);

    // Stage 2 M1: structural closure-scope analysis (CaptureScopeAnalysis) — scope ownership, slot
    // assignment, and per-closure binding-scope/hop-distance chain shape. Built once per class in
    // UasmEmitter.Emit(); read-only, consumed by nothing yet (behavior-neutral — env alloc/access
    // codegen is Stage 2 M2). Self-contained (owns its own LambdaCaptureAnalyzer instance), so this
    // is a plain result holder, not shared mutable state.
    // Write-once: built once in UasmEmitter.Emit() via SetCaptureScope; a second set throws so a future
    // restructure cannot silently swap this frozen analysis artifact under in-flight emission.
    public CaptureScopeAnalysis CaptureScope { get; private set; }

    public void SetCaptureScope(CaptureScopeAnalysis value)
    {
        if (CaptureScope != null)
            throw new InvalidOperationException("EmitContext.CaptureScope is write-once (set once in Emit, never reassigned).");
        CaptureScope = value;
    }

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

    // Stage 2 M2: hoisted closure method → the param FIELD id of its hidden trailing __envp
    // parameter. Registered where the closure's params are laid out; read by EnvEmit.Leaf and the
    // TCO self-rebind when emission inside the closure body needs an outer scope's env.
    //
    // KEYING DISCIPLINE (Stage 2 M5 gotcha-3: a definition key here was last-spec-wins and wired
    // one generic spec's body to another spec's field — VM-proven wrong-value fault, fixed in
    // 5064f77). This map is intentionally MIXED-key: a capturing generic specialization that is
    // pinned to per-instantiation storage registers under its CONSTRUCTED symbol (each spec owns
    // its own field); a capturing closure with only ever one instantiation (non-generic, or a
    // generic local function sharing one T-free hoist) registers under its DEFINITION. Callers
    // never touch the dictionary directly — go through RegisterEnvpField / TryGetEnvpField, which
    // encode the constructed-first / definition-fallback lookup in exactly one place.
    readonly Dictionary<IMethodSymbol, string> _envpParamFields = new(SymbolEqualityComparer.Default);

    /// <summary>Register a hoisted closure's hidden __envp field. Pass the CONSTRUCTED symbol for a
    /// per-instantiation registration (each spec owns its own field), or a DEFINITION for a
    /// closure that only ever has one instantiation. See the field's keying-discipline comment.</summary>
    public void RegisterEnvpField(IMethodSymbol closureKey, string envpFieldId)
        => _envpParamFields[closureKey] = envpFieldId;

    /// <summary>Resolve a closure's __envp field: the CONSTRUCTED symbol first (per-instantiation
    /// storage), its ORIGINAL DEFINITION as fallback (shared/non-generic storage). The single
    /// lookup point for the mixed keying discipline documented on the backing field.</summary>
    public bool TryGetEnvpField(IMethodSymbol closure, out string envpFieldId)
        => _envpParamFields.TryGetValue(closure, out envpFieldId)
           || _envpParamFields.TryGetValue(closure.OriginalDefinition, out envpFieldId);

    // Round-7 follow-up [Q4]: foreach ITERATION variables. C# makes them READONLY, so invoking a
    // non-readonly struct member on one runs on a DEFENSIVE COPY (the classic foreach-struct-
    // mutation no-op); the loop variable's object[] is live storage in the flat emulation, so the
    // struct-instance-call receiver is CLONED when its chain roots at one of these locals
    // (VM-proven: loop-var reads after a mutating call 1112 vs CLR 102). MEMBERSHIP-ONLY set (§1.5).
    public readonly HashSet<ILocalSymbol> ForeachIterationLocals = new(SymbolEqualityComparer.Default);

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

    // static readonly field initializers (design §3.1/§3.6, feature B) — same shape as FieldInitOps,
    // kept separate so UasmEmitter can base-first reorder the static TIER independently, then splice
    // it in front of FieldInitOps (static tier runs before instance tier, mirroring C#'s static→instance
    // initializer order applied to per-program materialization).
    public readonly List<(string fieldName, IOperation initOp, ITypeSymbol fieldType)> StaticFieldInitOps = new();

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

    // Pending delegate bridges for dynamically hoisted lambdas/local functions. The carried map is the
    // creating method's immutable TypeParamMap by REFERENCE (it is per-EmitMethod fresh and never mutated,
    // so no snapshot copy is needed even though the drain runs after emission when the ambient map is null).
    public readonly List<(IMethodSymbol method, string bridgeExportName, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> resolvedTypeParamMap)> PendingDelegateBridges = new();

    // Multicast design (2026-07-03 §1): sig-part → (Invoke, resolved type-param map) for every
    // delegate signature this class combines/removes via `+=`/`-=` (CompoundAssignmentHandler). Drives
    // the per-class __dlg_fanout_/__dlg_combine_/__dlg_remove_{sig} synthetic emission (UasmEmitter,
    // sibling of EmitPendingDelegateBridges). Keyed on sig content, not occurrence — so two `+=` sites
    // sharing a signature dedupe to one fan-out/helper set. Carries the map by reference (immutable).
    public readonly Dictionary<string, (IMethodSymbol Invoke, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap)> PendingMulticastSigs = new();

    // B67: user enums whose ToString()/concat/interpolation needs a synthesized value→name helper. Keyed by
    // the enum symbol; the per-enum `__enumstr_{Name}` function is emitted once in the UasmEmitter drain
    // (EmitEnumToStringSynthetics), sibling of EmitMulticastSynthetics.
    public readonly HashSet<INamedTypeSymbol> PendingEnumToString = new(SymbolEqualityComparer.Default);

    // Variance design (2026-07-04 §2.2, B-1): per-(target, sig-S) sig adapter bridges — a same-program
    // variant method-group binding mints one of these instead of the plain bridge. delegateInvoke is the
    // DESTINATION delegate's own Invoke method (sig-S's param/return types for the conv-var declarations),
    // distinct from targetMethod (the real callee's own types, used only for the InternalCall). Sibling of
    // PendingDelegateBridges — same dedup-by-name-at-emission shape (UasmEmitter.EmitPendingSigAdapterBridges).
    public readonly List<(IMethodSymbol targetMethod, IMethodSymbol delegateInvoke, string adapterName, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> resolvedTypeParamMap)> PendingSigAdapterBridges = new();

    // Variance design (2026-07-04 §2.3, B-2): wrapper name → (outer sig-S Invoke, inner sig-T
    // Invoke-or-method, resolved type-param snapshot) for every wrapper-with-payload bridge needed
    // (third-party variant method-group hinge, or a delegate-VALUE variant conversion). Keyed by the
    // WRAPPER NAME (already unique per (outer,inner) sig pair — DelegateAbi.WrapperName) rather than a
    // single sig, since a wrapper's inner dispatch speaks the INNER bundle's own protocol, distinct from
    // the outer one two different sig-T's could both wrap to the same sig-S.
    public readonly Dictionary<string, (IMethodSymbol OuterInvoke, IMethodSymbol InnerInvoke, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap)> PendingWrapperSigs = new();

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
        Boundary = new BoundaryChecker(this);
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


    public const string ReflTypeIdField = "__refl_typeid";
    public const string ReflTypeIdsField = "__refl_typeids";
    public const string ReflTypeNameField = "__refl_typename";

    /// <summary>Declare reflection type IDs array.</summary>
    public void DeclareReflTypeIds(long[] typeIds)
    {
        DeclareField(ReflTypeIdsField, "SystemInt64Array", defaultValue: typeIds);
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

}
