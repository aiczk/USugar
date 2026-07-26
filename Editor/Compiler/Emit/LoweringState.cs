using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

internal sealed class LoweringState
{
    readonly LoweringEnvironment Environment;
    public Compilation Compilation => Environment.Compilation;
    public INamedTypeSymbol ClassSymbol => Environment.ClassSymbol;
    internal BoundAbiPlan BoundAbi => Program?.Abi
        ?? throw new InvalidOperationException(
            "ABI decisions are unavailable before the bound program is published.");
    public FrozenLayoutPlan Planner
        => Program?.Layouts ?? Environment.Planner;
    internal IUdonTypeSystem Types
        => Program != null ? Program.Types : Environment.Types;
    internal BoundProgram Program { get; private set; }
    internal CallSiteBindingScope? CurrentBindingScope { get; private set; }
    ClosureIdentityPlan _plannedClosureIdentities;
    CaptureScopeAnalysis _plannedCaptures;
    RecursionInfo _plannedRecursion;
    internal ClosureIdentityPlan ClosureIdentities
        => Program?.ClosureIdentities ?? _plannedClosureIdentities;
    internal CaptureScopeAnalysis Captures
        => Program?.Captures ?? _plannedCaptures;
    internal RecursionInfo Recursion
        => Program?.Recursion ?? _plannedRecursion;

    // Mutable output and lowering state.
    public readonly StructuredModule Module;
    public readonly CoreBuilder Builder;
    public readonly StorageContext Storage;
    public readonly BoundaryChecker Boundary;
    internal OperationLowerer Operations { get; private set; }
    public readonly GenericContext Generics = new GenericContext();
    public readonly ClosureContext Closures = new ClosureContext();
    readonly AggregateLayoutTable _aggregateLayouts = new AggregateLayoutTable();
    internal AggregateLayoutTable Aggregates
        => Program?.Aggregates ?? _aggregateLayouts;
    readonly ClassTypeObjectContext _classTypes =
        new ClassTypeObjectContext();
    public ClassTypeObjectContext ClassTypes
        => Program?.ClassTypes ?? _classTypes;

    public readonly SyntheticContext Synthetics = new SyntheticContext();
    public readonly ControlFlowContext ControlFlow = new ControlFlowContext();
    public readonly InitializationContext Initializers = new InitializationContext();
    public readonly DiagnosticContext DiagnosticState = new DiagnosticContext();
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
        if (_plannedClosureIdentities != null
            || _plannedCaptures != null)
            throw new InvalidOperationException(
                "Closure plans were set twice.");
        _plannedClosureIdentities = identities
            ?? throw new ArgumentNullException(nameof(identities));
        _plannedCaptures = captures
            ?? throw new ArgumentNullException(nameof(captures));
    }

    internal void SetRecursionPlan(RecursionInfo recursion)
    {
        if (_plannedRecursion != null)
            throw new InvalidOperationException(
                "The recursion plan was set twice.");
        _plannedRecursion = recursion
            ?? throw new ArgumentNullException(nameof(recursion));
    }

    internal void PublishBoundProgram(BoundProgram program)
    {
        if (Program != null)
            throw new InvalidOperationException("Bound program was published twice.");
        if (program == null) throw new ArgumentNullException(nameof(program));
        if (!ReferenceEquals(
                _plannedClosureIdentities,
                program.ClosureIdentities)
            || !ReferenceEquals(_plannedCaptures, program.Captures)
            || !ReferenceEquals(_plannedRecursion, program.Recursion))
            throw new InvalidOperationException(
                "Planned analyses do not match the bound program.");
        Module.PublishSemantics(program.Abi, program.TypeFacts);
        Program = program;
        _plannedClosureIdentities = null;
        _plannedCaptures = null;
        _plannedRecursion = null;
    }

    internal void BeginBodyEmission()
    {
        if (Program == null)
            throw new InvalidOperationException(
                "Body emission cannot start before a BoundProgram is published.");
        Generics.BeginBodyEmission();
    }

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
        => Generics.EnterScope(map, Methods.CurrentMethod);

    public StorageType ResolveStorageType(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap = null)
    {
        var map = typeParameterMap ?? Generics.TypeParamMap;
        return Types.GetStorageType(type, map);
    }

    internal ITypeSymbol ResolveSourceType(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap = null)
    {
        if (type == null) return null;
        var map = typeParameterMap ?? Generics.TypeParamMap;
        return Program != null
            ? Program.Types.Resolve(type, map)
            : TypeEnvironment.CloseType(Compilation, type, map);
    }

    public string SourceStorageName(ISymbol member) => Environment.SourceStorageName(member);

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

    public LoweringState(LoweringEnvironment environment)
    {
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        Module = new StructuredModule()
            { ClassName = ClassSymbol.ToDisplayString() };
        Builder = new CoreBuilder(Module);
        Storage = new StorageContext(Module);
        Boundary = new BoundaryChecker(this);
    }

}
