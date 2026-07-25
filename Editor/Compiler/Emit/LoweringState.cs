using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

public sealed class LoweringState
{
    public readonly LoweringEnvironment Environment;
    public CompilationSession Session => Environment.Session;
    public Compilation Compilation => Environment.Compilation;
    public INamedTypeSymbol ClassSymbol => Environment.ClassSymbol;
    internal BoundAbiPlan BoundAbi => Program?.Abi
        ?? throw new InvalidOperationException(
            "ABI decisions are unavailable before the bound program is published.");
    public FrozenLayoutPlan Planner => Environment.Planner;
    public MethodAnalysisCache MethodAnalyses => Environment.MethodAnalyses;
    internal BoundProgram Program { get; private set; }

    // Mutable output and lowering state.
    public readonly StructuredModule Module;
    public readonly CoreBuilder Builder;
    public readonly StorageContext Storage;
    public readonly BoundaryChecker Boundary;
    public readonly ConversionLowerer Conversions;
    internal OperationLowerer Operations { get; private set; }
    public readonly GenericContext Generics = new GenericContext();
    public readonly RecursionContext RecursionContext = new RecursionContext();
    public readonly ClosureContext Closures = new ClosureContext();
    public readonly AggregateContext Aggregates = new AggregateContext();
    public readonly ClassTypeObjectContext ClassTypes = new ClassTypeObjectContext();

    /// <summary>CA-v2b-2: virtual-call lowering authority (dispatch set + devirt). Set by UasmEmitter after
    /// ClassTypes is seeded and before EmitMethods/BuildRecursionInfo, which both consume it.</summary>
    public VirtualDispatch VirtualDispatch;
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

    internal void PublishBoundProgram(BoundProgram program)
    {
        if (Program != null)
            throw new InvalidOperationException("Bound program was published twice.");
        if (program == null) throw new ArgumentNullException(nameof(program));
        if (!ReferenceEquals(Closures.IdentityPlan, program.ClosureIdentities)
            || !ReferenceEquals(Closures.CaptureScope, program.Captures)
            || !ReferenceEquals(RecursionContext.Info, program.Recursion))
            throw new InvalidOperationException(
                "Lowering contexts do not match the bound program's analysis artifacts.");
        Program = program;
    }

    internal void BeginBodyEmission()
    {
        if (Program == null)
            throw new InvalidOperationException(
                "Body emission cannot start before a BoundProgram is published.");
        Generics.BeginBodyEmission();
    }

    // Depth-1 type-param scope. EmitMethod is a non-recursive serial drain, so exactly one map is
    // active at a time; a nested Enter means a prior scope leaked (a compiler bug) and throws loudly
    // rather than silently inheriting someone else's map. Dispose is the SOLE clear site, so the map
    // is cleared even if body emission throws.
    public IDisposable EnterTypeParamScope(IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map)
        => Generics.EnterScope(map, Methods.CurrentMethod);

    public StorageType ResolveStorageType(ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParameterMap = null)
        => Environment.ResolveStorageType(type, typeParameterMap ?? Generics.TypeParamMap);

    public string SourceStorageName(ISymbol member) => Environment.SourceStorageName(member);

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
        var identityPlan = Closures.IdentityPlan
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
        Module = new StructuredModule(Session.TypeFacts, Environment.AbiCatalog)
            { ClassName = ClassSymbol.ToDisplayString() };
        Builder = new CoreBuilder(Module);
        Storage = new StorageContext(Module);
        Boundary = new BoundaryChecker(this);
        Conversions = new ConversionLowerer(this);
    }

}
