using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

internal sealed class LoweringState
{
    public Compilation Compilation { get; }
    public INamedTypeSymbol ClassSymbol { get; }
    internal BoundAbiPlan BoundAbi
        => RequireProgram().Abi;
    public FrozenLayoutPlan Planner { get; private set; }
    internal IUdonTypeSystem Types { get; private set; }
    internal BoundProgram Program { get; private set; }
    internal CallSiteBindingScope? CurrentBindingScope { get; private set; }
    internal ClosureIdentityPlan ClosureIdentities
        { get; private set; }
    internal CaptureScopeAnalysis Captures
        { get; private set; }
    internal RecursionInfo Recursion
        { get; private set; }

    // Mutable output and lowering state.
    public readonly StructuredModule Module;
    public readonly CoreBuilder Builder;
    public readonly StorageContext Storage;
    public readonly BoundaryChecker Boundary;
    internal OperationLowerer Operations { get; private set; }
    public IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
        TypeParamMap { get; private set; }
    public readonly Dictionary<(object Func, int ScopeId), int>
        ScopeEnvSlots = new();
    internal AggregateLayoutTable Aggregates
        { get; private set; } = new AggregateLayoutTable();
    public ClassTypeObjectContext ClassTypes
        { get; private set; } = new ClassTypeObjectContext();

    SyntheticDemandPlanner _syntheticDemandPlanner = new();
    readonly HashSet<string> _emittedDelegateSites =
        new(StringComparer.Ordinal);
    bool _syntheticEmissionVerified;
    internal SyntheticDemandPlanner SyntheticDemandPlanner
        => _syntheticDemandPlanner
           ?? throw new InvalidOperationException(
               "Synthetic demands were already published.");
    public readonly Stack<CLeaf> ConditionalAccessStack = new();
    public readonly Stack<List<(CLeaf Val, ITypeSymbol Type)>>
        UsingDisposableStack = new();
    public readonly Stack<int> LoopUsingDepthStack = new();
    public readonly Stack<string> SwitchBreakLabels = new();
    public readonly Stack<Dictionary<string, string>>
        GotoCaseLabels = new();
    int _switchLabelCounter;

    public string NextSwitchEndLabel()
        => $"__switchEnd_{++_switchLabelCounter}";

    public readonly List<(
        string FieldName,
        IOperation InitOp,
        ITypeSymbol FieldType)> FieldInitOps = new();
    public readonly List<(
        string FieldName,
        IOperation InitOp,
        ITypeSymbol FieldType)> StaticFieldInitOps = new();
    public readonly Dictionary<string, string>
        FieldChangeCallbacks = new();
    public readonly List<EmitDiagnostic> Diagnostics = new();
    public readonly HashSet<string> ReportedExterns = new();
    public readonly MethodContext Methods = new MethodContext();

    internal void SetOperationLowerer(OperationLowerer operations)
    {
        if (Operations != null)
            throw new InvalidOperationException("Operation lowerer was set twice.");
        Operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    internal void SetClosurePlans(
        ClosureIdentityPlan identities,
        CaptureScopeAnalysis captures)
    {
        if (ClosureIdentities != null
            || Captures != null)
            throw new InvalidOperationException(
                "Closure plans were set twice.");
        ClosureIdentities = identities
            ?? throw new ArgumentNullException(nameof(identities));
        Captures = captures
            ?? throw new ArgumentNullException(nameof(captures));
    }

    internal void SetRecursionPlan(RecursionInfo recursion)
    {
        if (Recursion != null)
            throw new InvalidOperationException(
                "The recursion plan was set twice.");
        Recursion = recursion
            ?? throw new ArgumentNullException(nameof(recursion));
    }

    internal SyntheticDemandPlan PublishSyntheticDemands()
    {
        var planner = SyntheticDemandPlanner;
        var plan = planner.PublishPlan();
        _syntheticDemandPlanner = null;
        return plan;
    }

    internal void RecordEmittedDelegateSite(string site)
    {
        RequireProgram().SyntheticDemands
            .RequireDelegateBinding(site);
        _emittedDelegateSites.Add(site);
    }

    internal void VerifySyntheticEmissionComplete()
    {
        if (_syntheticEmissionVerified)
            throw new InvalidOperationException(
                "Synthetic demand emission was verified twice.");
        foreach (var site in RequireProgram()
                     .SyntheticDemands.DelegateSites)
            if (!_emittedDelegateSites.Contains(site))
                throw new InvalidOperationException(
                    $"Planned delegate site '{site}' was not emitted "
                    + "during body emission.");
        _emittedDelegateSites.Clear();
        _syntheticEmissionVerified = true;
    }

    internal void PublishBoundProgram(BoundProgram program)
    {
        if (Program != null)
            throw new InvalidOperationException("Bound program was published twice.");
        if (program == null) throw new ArgumentNullException(nameof(program));
        if (!ReferenceEquals(
                ClosureIdentities,
                program.ClosureIdentities)
            || !ReferenceEquals(Captures, program.Captures)
            || !ReferenceEquals(Recursion, program.Recursion))
            throw new InvalidOperationException(
                "Planned analyses do not match the bound program.");
        Module.PublishSemantics(program.Abi, program.TypeFacts);
        Program = program;
        Planner = program.Layouts;
        Types = program.Types;
        ClosureIdentities = program.ClosureIdentities;
        Captures = program.Captures;
        Recursion = program.Recursion;
        Aggregates = program.Aggregates;
        ClassTypes = program.ClassTypes;
    }

    BoundProgram RequireProgram()
        => Program
           ?? throw new InvalidOperationException(
               "Bound semantics are unavailable before the bound program is published.");

    internal IDisposable EnterBindingScope(CallSiteBindingScope scope)
    {
        var previous = CurrentBindingScope;
        CurrentBindingScope = scope;
        return new BindingScopeToken(this, previous);
    }

    sealed class BindingScopeToken : IDisposable
    {
        readonly LoweringState _state;
        readonly CallSiteBindingScope? _previous;
        bool _disposed;

        public BindingScopeToken(
            LoweringState state,
            CallSiteBindingScope? previous)
        {
            _state = state;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                throw new InvalidOperationException(
                    "BindingScopeToken disposed twice.");
            _disposed = true;
            _state.CurrentBindingScope = _previous;
        }
    }

    // Depth-1 type-param scope. EmitMethod is a non-recursive serial drain, so exactly one map is
    // active at a time; a nested Enter means a prior scope leaked (a compiler bug) and throws loudly
    // rather than silently inheriting someone else's map. Dispose is the SOLE clear site, so the map
    // is cleared even if body emission throws.
    public IDisposable EnterTypeParamScope(IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map)
    {
        if (TypeParamMap != null)
            throw new InvalidOperationException(
                "EnterTypeParamScope: a type-param map is already "
                + "active on entry to "
                + $"'{Methods.CurrentMethod?.ToDisplayString() ?? "(none)"}'; "
                + "a prior scope was not disposed.");
        TypeParamMap = map;
        return new TypeParamScopeToken(this, null);
    }

    public IDisposable EnterTypeParamOverlay(
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map)
    {
        var previous = TypeParamMap;
        TypeParamMap = map;
        return new TypeParamScopeToken(this, previous);
    }

    sealed class TypeParamScopeToken : IDisposable
    {
        readonly LoweringState _state;
        readonly IReadOnlyDictionary<
            ITypeParameterSymbol,
            ITypeSymbol> _previous;
        bool _disposed;

        public TypeParamScopeToken(
            LoweringState state,
            IReadOnlyDictionary<
                ITypeParameterSymbol,
                ITypeSymbol> previous)
        {
            _state = state;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                throw new InvalidOperationException(
                    "TypeParamScopeToken disposed twice.");
            _disposed = true;
            _state.TypeParamMap = _previous;
        }
    }

    public StorageType ResolveStorageType(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap = null)
    {
        var map = typeParameterMap ?? TypeParamMap;
        return Types.GetStorageType(type, map);
    }

    internal ITypeSymbol ResolveSourceType(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
    {
        if (type == null) return null;
        var map = typeParameterMap ?? TypeParamMap;
        return Types.Resolve(type, map);
    }

    public string SourceStorageName(IFieldSymbol field)
        => RequireProgram().RequireSourceStorageName(field);

    public bool TryGetEnvBinding(
        ISymbol symbol,
        out (CaptureScope Scope, int Slot) binding)
    {
        binding = default;
        var captures = Captures;
        if (captures == null || symbol == null) return false;
        if (captures.CapturedSlots.TryGetValue(
                symbol, out var direct))
        {
            binding = direct;
            return true;
        }
        if (symbol is IMethodSymbol receiverMethod
            && captures.CapturedSlots.TryGetValue(
                receiverMethod.OriginalDefinition,
                out var receiverBinding))
        {
            binding = receiverBinding;
            return true;
        }
        if (symbol is IParameterSymbol parameter
            && parameter.ContainingSymbol is IMethodSymbol method
            && !ReferenceEquals(method, method.OriginalDefinition))
        {
            var definitionParameters =
                method.OriginalDefinition.Parameters;
            if (parameter.Ordinal < definitionParameters.Length
                && captures.CapturedSlots.TryGetValue(
                    definitionParameters[parameter.Ordinal],
                    out var reKeyed))
            {
                binding = reKeyed;
                return true;
            }
        }
        return false;
    }

    /// <summary>Composite key args for a hoisted-closure registration or lookup (2026-07-11 pre-fuzz
    /// audit HIGH fix): the closure's own type args ⊕ the args of its LEXICAL enclosing generic
    /// owners (declaration chain), each resolved against the current emission's owner chain
    /// (CurrentOwnerSpecs, captured into the registration identity). The
    /// former composition used the lookup site's AMBIENT spec vector, which keyed the TARGET by the
    /// REGISTRAR's own spec dimension: a self-recursive / mutually-recursive generic local function
    /// then re-composed its own args on every hop (key length grew, every lookup missed, the pending
    /// drain re-registered forever — VM-proven compile hang). The lexical chain is the same from
    /// every reference site, so registration and lookup can never skew.</summary>
    public readonly struct ClosureIdentity
    {
        public readonly System.Collections.Immutable.ImmutableArray<ITypeSymbol> KeyArgs;
        public readonly System.Collections.Immutable.ImmutableArray<IMethodSymbol> OwnerSpecs;
        public ClosureIdentity(
            System.Collections.Immutable.ImmutableArray<ITypeSymbol> keyArgs,
            System.Collections.Immutable.ImmutableArray<IMethodSymbol> ownerSpecs)
        { KeyArgs = keyArgs; OwnerSpecs = ownerSpecs; }
    }

    public ClosureIdentity ResolveClosureIdentity(IMethodSymbol closure)
    {
        var b = System.Collections.Immutable.ImmutableArray.CreateBuilder<ITypeSymbol>();
        var owners = System.Collections.Immutable.ImmutableArray.CreateBuilder<IMethodSymbol>();
        if (closure.TypeArguments.Length > 0) b.AddRange(closure.TypeArguments);
        var identityPlan = ClosureIdentities
            ?? throw new InvalidOperationException("Closure identity plan was not frozen before emission.");
        foreach (var ownerDef in identityPlan.GetLexicalOwners(closure))
        {
            IMethodSymbol spec = null;
            foreach (var os in Methods.CurrentOwnerSpecs)
                if (ClosureIdentityPlan.SameSourceDefinition(os, ownerDef)) { spec = os; break; }
            if (spec == null)
            {
                if (ownerDef.TypeParameters.Length > 0
                    || ownerDef.ContainingType is { IsGenericType: true })
                    throw new InvalidOperationException(
                        $"Closure '{closure.ToDisplayString()}' has no active lexical owner "
                        + $"specialization for '{ownerDef.ToDisplayString()}'.");
                continue;
            }
            if (!owners.Any(existing => SymbolEqualityComparer.Default.Equals(existing, spec)))
                owners.Add(spec);
            if (ownerDef.TypeParameters.Length > 0) b.AddRange(spec.TypeArguments);
            // Containing-type dimension once, at the outermost named method (feature G: a generic
            // struct/class member's closure binds the container's T too).
            if (ownerDef.ContainingSymbol is not IMethodSymbol
                && spec.ContainingType is { IsGenericType: true } ct)
                b.AddRange(ct.TypeArguments);
        }
        return new ClosureIdentity(b.ToImmutable(), owners.ToImmutable());
    }

    public System.Collections.Immutable.ImmutableArray<ITypeSymbol> ComposeClosureKeyArgs(IMethodSymbol closure)
        => ResolveClosureIdentity(closure).KeyArgs;

    // Round-7 follow-up [Q4]: foreach ITERATION variables. C# makes them READONLY, so invoking a
    // non-readonly struct member on one runs on a DEFENSIVE COPY (the classic foreach-struct-
    // mutation no-op); the loop variable's object[] is live storage in the flat emulation, so the
    // struct-instance-call receiver is CLONED when its chain roots at one of these locals
    // (VM-proven: loop-var reads after a mutating call 1112 vs CLR 102). MEMBERSHIP-ONLY set (§1.5).
    public readonly HashSet<ILocalSymbol> ForeachIterationLocals = new(SymbolEqualityComparer.Default);

    public LoweringState(
        Compilation compilation,
        INamedTypeSymbol classSymbol,
        FrozenLayoutPlan planner,
        IUdonTypeSystem types)
    {
        Compilation = compilation
            ?? throw new ArgumentNullException(nameof(compilation));
        ClassSymbol = classSymbol
            ?? throw new ArgumentNullException(nameof(classSymbol));
        Planner = planner
            ?? throw new ArgumentNullException(nameof(planner));
        Types = types
            ?? throw new ArgumentNullException(nameof(types));
        Module = new StructuredModule()
            { ClassName = ClassSymbol.ToDisplayString() };
        Builder = new CoreBuilder(Module);
        Storage = new StorageContext(Module);
        Boundary = new BoundaryChecker(this);
    }

}
