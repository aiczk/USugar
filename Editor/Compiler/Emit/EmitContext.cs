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
    public readonly RecursionInfo Recursion = new RecursionInfo();

    /// <summary>True when a call from <paramref name="caller"/> to <paramref name="callee"/> is a
    /// recursion-cycle edge (callee in caller's non-trivial SCC, including direct self-recursion).</summary>
    public bool IsRecursiveEdge(IMethodSymbol caller, IMethodSymbol callee)
        => caller != null && callee != null && Recursion.RecursiveCallees != null
           // Reduce BOTH ends to OriginalDefinition: RecursiveCallees is keyed by definition, but a
           // monomorphized generic specialization (e.g. Fact<int>) emits with the constructed symbol as
           // _currentMethod/target — without this its self-edge would be missed and the frame not spilled.
           && Recursion.RecursiveCallees.TryGetValue(caller.OriginalDefinition, out var callees)
           && callees.Contains(callee.OriginalDefinition);

    /// <summary>True when a call from <paramref name="caller"/> to <paramref name="callee"/> lies in
    /// a recursion cycle (same non-trivial SCC or direct self-loop), tail or not ([Y3]).</summary>
    public bool IsCycleEdge(IMethodSymbol caller, IMethodSymbol callee)
        => caller != null && callee != null && Recursion.CycleCallees != null
           && Recursion.CycleCallees.TryGetValue(caller.OriginalDefinition, out var callees)
           && callees.Contains(callee.OriginalDefinition);

    /// <summary>[Q5] True when <paramref name="callee"/>'s transitive touch set contains the
    /// this-field <paramref name="field"/> (both compared by OriginalDefinition).</summary>
    public bool CalleeTouchesThisField(IMethodSymbol callee, IFieldSymbol field)
        => callee != null && field != null && Recursion.ThisFieldTouches != null
           && Recursion.ThisFieldTouches.TryGetValue(callee.OriginalDefinition, out var set)
           && set.Contains(field.OriginalDefinition);

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
    /// <c>TypeParamDependent</c> (round-8 [Y2] widening): a closure whose SIGNATURE, BODY, or captured
    /// variables reference the enclosing generic's type parameters — the closure is hoisted ONCE keyed
    /// by IMethodSymbol and its function types/body were emitted under the FIRST spec's map, so a
    /// second instantiation would silently run the first instantiation's types. (Stage 2 §8.1: the
    /// former <c>Capturing</c> tier is retired — a NON-type-param-dependent capturing closure shares
    /// one T-free hoist and its captures live in per-activation env records, so multiple instantiations
    /// no longer alias. B45 M2: the former <c>StructMemberCapturing</c> tier (wave-14) is likewise
    /// retired — once CaptureScopeAnalysis walks user-struct member bodies (B45 M1), a struct-hosted
    /// capturing closure gets the SAME per-activation env record as a class-method one, so the T-free
    /// multi-instantiation case is sound for struct members too. Only T-dependence still pins.)</summary>
    public enum ClosurePin { None, TypeParamDependent }

    /// <summary>[X6]/[Y2]/wave-14 gate: does the generic DEFINITION's body contain an
    /// instantiation-pinning closure? Walks the definition's own operation tree (specs share it).
    /// Only type-param dependence pins now (B45 M2 retired the struct-member capture tier — struct-hosted
    /// closures get the same per-activation env records as class ones, so capture alone no longer pins).</summary>
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

    // Stage 2 §8.2: the walk no longer early-returns on capture — capture alone no longer pins an
    // instantiation (per-activation env records de-alias multi-instantiation captures, class- and
    // struct-hosted alike since B45 M2). It visits EVERY closure and only pins on TypeParamDependence:
    // a closure whose signature, body, or a captured variable references the enclosing generic's type
    // parameters cannot share one T-free hoist across specs. Granularity stays per-definition (§8.2):
    // one T-dependent closure pins the whole definition; no partial legalization.
    void WalkClosurePins(IOperation op, ref ClosurePin pin)
    {
        if (op == null || pin != ClosurePin.None) return;
        switch (op)
        {
            case IAnonymousFunctionOperation af:
                if (ClosureUsesMethodTypeParam(af.Symbol, af.Body)) { pin = ClosurePin.TypeParamDependent; return; }
                break;
            case ILocalFunctionOperation lf when lf.Symbol != null:
                if (ClosureUsesMethodTypeParam(lf.Symbol, lf.Body)) { pin = ClosurePin.TypeParamDependent; return; }
                break;
        }
        foreach (var child in op.Children)
        {
            WalkClosurePins(child, ref pin);
            if (pin != ClosurePin.None) return;
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

    /// <summary>Shared [Y2] reject for a SECOND distinct instantiation of a generic whose body pins it
    /// to one instantiation via a type-param-dependent closure (round-8; the former capture-only tier
    /// is retired in Stage 2 §8.1). No-op for <see cref="ClosurePin.None"/>.</summary>
    public static void ThrowIfClosurePinsInstantiation(ClosurePin pin, string methodName)
    {
        switch (pin)
        {
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

    // Pending delegate bridges for dynamically hoisted lambdas/local functions
    public readonly List<(IMethodSymbol method, string bridgeExportName, Dictionary<ITypeParameterSymbol, ITypeSymbol> resolvedTypeParamMap)> PendingDelegateBridges = new();

    // Multicast design (2026-07-03 §1): sig-part → (Invoke, resolved type-param snapshot) for every
    // delegate signature this class combines/removes via `+=`/`-=` (CompoundAssignmentHandler). Drives
    // the per-class __dlg_fanout_/__dlg_combine_/__dlg_remove_{sig} synthetic emission (UasmEmitter,
    // sibling of EmitPendingDelegateBridges). Keyed on sig content, not occurrence — so two `+=` sites
    // sharing a signature dedupe to one fan-out/helper set. Snapshot mirrors PendingDelegateBridges:
    // this dict is read AFTER body emission completes, when a generic method's ambient TypeParamMap
    // may already be cleared.
    public readonly Dictionary<string, (IMethodSymbol Invoke, Dictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap)> PendingMulticastSigs = new();

    // Variance design (2026-07-04 §2.2, B-1): per-(target, sig-S) sig adapter bridges — a same-program
    // variant method-group binding mints one of these instead of the plain bridge. delegateInvoke is the
    // DESTINATION delegate's own Invoke method (sig-S's param/return types for the conv-var declarations),
    // distinct from targetMethod (the real callee's own types, used only for the InternalCall). Sibling of
    // PendingDelegateBridges — same dedup-by-name-at-emission shape (UasmEmitter.EmitPendingSigAdapterBridges).
    public readonly List<(IMethodSymbol targetMethod, IMethodSymbol delegateInvoke, string adapterName, Dictionary<ITypeParameterSymbol, ITypeSymbol> resolvedTypeParamMap)> PendingSigAdapterBridges = new();

    // Variance design (2026-07-04 §2.3, B-2): wrapper name → (outer sig-S Invoke, inner sig-T
    // Invoke-or-method, resolved type-param snapshot) for every wrapper-with-payload bridge needed
    // (third-party variant method-group hinge, or a delegate-VALUE variant conversion). Keyed by the
    // WRAPPER NAME (already unique per (outer,inner) sig pair — DelegateAbi.WrapperName) rather than a
    // single sig, since a wrapper's inner dispatch speaks the INNER bundle's own protocol, distinct from
    // the outer one two different sig-T's could both wrap to the same sig-S.
    public readonly Dictionary<string, (IMethodSymbol OuterInvoke, IMethodSymbol InnerInvoke, Dictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap)> PendingWrapperSigs = new();

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
