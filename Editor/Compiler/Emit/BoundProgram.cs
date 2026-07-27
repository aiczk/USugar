using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Complete immutable semantic input to body lowering. Every callable specialization, closure,
/// capture, recursion edge, synthetic helper, and field has been resolved before this is created.
/// </summary>
internal sealed class BoundProgram
{
    public readonly CallableDefinitionPlan Callables;
    public readonly IReadOnlyList<
        MethodContext.RegisteredCallableBody> CallableBodies;
    public readonly IReadOnlyList<
        MethodContext.IteratorPlan> IteratorPlans;
    readonly IReadOnlyDictionary<
        IMethodSymbol,
        MethodContext.RegisteredCallable> _callableRegistry;
    readonly IReadOnlyDictionary<
        SpecializationKey,
        MethodContext.ClosureSpec> _closureRegistry;
    public readonly FieldDiscoveryPlan Fields;
    public readonly ClosureIdentityPlan ClosureIdentities;
    public readonly CaptureScopeAnalysis Captures;
    public readonly RecursionInfo Recursion;
    public readonly SyntheticDemandPlan SyntheticDemands;
    readonly IReadOnlyDictionary<
        BoundCallSiteKey, BoundCallSite> _callSites;
    readonly IReadOnlyDictionary<
        BoundInitializerKey, BoundInitializer> _initializers;
    readonly IReadOnlyDictionary<
        INamedTypeSymbol,
        IReadOnlyList<BoundClassFieldInitializer>> _classInitializers;
    readonly IReadOnlyDictionary<
        BoundDeconstructionKey, IMethodSymbol> _deconstructions;
    readonly IReadOnlyDictionary<
        BoundConversionKey, ClosedConversionPlan> _conversions;
    public readonly BoundConstantTable Constants;
    public readonly BoundMethodBodyTable MethodBodies;
    public readonly BoundValueTable Values;
    public readonly IReadOnlyDictionary<IFieldSymbol, string> SourceStorageNames;
    readonly IReadOnlyDictionary<
        BoundSyntheticDispatchKey, DispatchPlan> _syntheticDispatch;
    public readonly IMethodSymbol SyntheticObjectToStringSlot;
    public readonly BoundAbiPlan Abi;
    public readonly BoundUdonTypeSystem Types;
    public readonly UdonTypeFactRegistry TypeFacts;
    public readonly FrozenLayoutPlan Layouts;
    public readonly AggregateLayoutTable Aggregates;
    public readonly ClassTypeObjectContext ClassTypes;

    public BoundProgram(
        CallableDefinitionPlan callables,
        IEnumerable<
            MethodContext.RegisteredCallableBody> callableBodies,
        IEnumerable<MethodContext.IteratorPlan> iteratorPlans,
        FieldDiscoveryPlan fields,
        ClosureIdentityPlan closureIdentities,
        CaptureScopeAnalysis captures,
        RecursionInfo recursion,
        SyntheticDemandPlan syntheticDemands,
        IDictionary<BoundCallSiteKey, BoundCallSite> callSites,
        IDictionary<BoundInitializerKey, BoundInitializer> initializers,
        IDictionary<
            INamedTypeSymbol,
            IReadOnlyList<BoundClassFieldInitializer>> classInitializers,
        IDictionary<
            BoundDeconstructionKey, IMethodSymbol> deconstructions,
        IDictionary<
            BoundConversionKey, ClosedConversionPlan> conversions,
        BoundConstantTable constants,
        BoundMethodBodyTable methodBodies,
        BoundValueTable values,
        IReadOnlyDictionary<IFieldSymbol, string> sourceStorageNames,
        IMethodSymbol syntheticObjectToStringSlot,
        IDictionary<
            BoundSyntheticDispatchKey, DispatchPlan> syntheticDispatch,
        BoundAbiPlan abi,
        BoundUdonTypeSystem types,
        UdonTypeFactRegistry typeFacts,
        FrozenLayoutPlan layouts,
        AggregateLayoutTable aggregates,
        ClassTypeObjectContext classTypes)
    {
        Callables = callables
            ?? throw new ArgumentNullException(nameof(callables));
        var bodyArray = (callableBodies
            ?? throw new ArgumentNullException(nameof(callableBodies)))
            .ToArray();
        CallableBodies = Array.AsReadOnly(bodyArray);
        IteratorPlans = Array.AsReadOnly((
            iteratorPlans
            ?? throw new ArgumentNullException(nameof(iteratorPlans)))
            .ToArray());
        var callableRegistry = new Dictionary<
            IMethodSymbol,
            MethodContext.RegisteredCallable>(
            SymbolEqualityComparer.Default);
        var closureRegistry = new Dictionary<
            SpecializationKey,
            MethodContext.ClosureSpec>();
        foreach (var body in bodyArray)
        {
            if (body?.Callable == null)
                throw new ArgumentException(
                    "A bound callable body cannot be null.",
                    nameof(callableBodies));
            if (body.Closure == null)
                callableRegistry.Add(
                    body.Method, body.Callable);
            else
                closureRegistry.Add(
                    new SpecializationKey(
                        body.Method, body.Closure.KeyArgs),
                    body.Closure);
        }
        _callableRegistry = new ReadOnlyDictionary<
            IMethodSymbol,
            MethodContext.RegisteredCallable>(
            callableRegistry);
        _closureRegistry = new ReadOnlyDictionary<
            SpecializationKey,
            MethodContext.ClosureSpec>(
            closureRegistry);
        Fields = fields
            ?? throw new ArgumentNullException(nameof(fields));
        ClosureIdentities = closureIdentities
            ?? throw new ArgumentNullException(nameof(closureIdentities));
        Captures = captures ?? throw new ArgumentNullException(nameof(captures));
        Recursion = recursion ?? throw new ArgumentNullException(nameof(recursion));
        SyntheticDemands = syntheticDemands
            ?? throw new ArgumentNullException(nameof(syntheticDemands));
        _callSites = Freeze(callSites, nameof(callSites));
        _initializers = Freeze(initializers, nameof(initializers));
        if (classInitializers == null)
            throw new ArgumentNullException(nameof(classInitializers));
        var classCopy = new Dictionary<
            INamedTypeSymbol,
            IReadOnlyList<BoundClassFieldInitializer>>(
            SymbolEqualityComparer.Default);
        foreach (var pair in classInitializers)
            classCopy.Add(
                pair.Key,
                Array.AsReadOnly(
                    (pair.Value
                     ?? Array.Empty<BoundClassFieldInitializer>())
                    .ToArray()));
        _classInitializers = new ReadOnlyDictionary<
            INamedTypeSymbol,
            IReadOnlyList<BoundClassFieldInitializer>>(classCopy);
        _deconstructions = Freeze(
            deconstructions, nameof(deconstructions));
        _conversions = Freeze(conversions, nameof(conversions));
        foreach (var conversion in _conversions.Values)
            if (conversion.DelegateWrapperName is { } wrapper
                && !SyntheticDemands.WrapperSignatures.ContainsKey(wrapper))
                throw new ArgumentException(
                    $"Delegate wrapper '{wrapper}' was absent from synthetic demands.",
                    nameof(conversions));
        Constants = constants
            ?? throw new ArgumentNullException(nameof(constants));
        MethodBodies = methodBodies
            ?? throw new ArgumentNullException(nameof(methodBodies));
        Values = values ?? throw new ArgumentNullException(nameof(values));
        SourceStorageNames = new ReadOnlyDictionary<IFieldSymbol, string>(
            new Dictionary<IFieldSymbol, string>(
                sourceStorageNames
                ?? throw new ArgumentNullException(nameof(sourceStorageNames)),
                SymbolEqualityComparer.Default));
        SyntheticObjectToStringSlot = syntheticObjectToStringSlot
            ?? throw new ArgumentNullException(
                nameof(syntheticObjectToStringSlot));
        _syntheticDispatch = Freeze(
            syntheticDispatch, nameof(syntheticDispatch));
        Abi = abi ?? throw new ArgumentNullException(nameof(abi));
        Types = types ?? throw new ArgumentNullException(nameof(types));
        TypeFacts = typeFacts ?? throw new ArgumentNullException(nameof(typeFacts));
        if (!TypeFacts.IsFrozen)
            throw new ArgumentException(
                "A bound program requires frozen type facts.",
                nameof(typeFacts));
        Layouts = layouts
            ?? throw new ArgumentNullException(nameof(layouts));
        Aggregates = aggregates
            ?? throw new ArgumentNullException(nameof(aggregates));
        ClassTypes = classTypes
            ?? throw new ArgumentNullException(nameof(classTypes));
    }

    public string RequireSourceStorageName(IFieldSymbol field)
    {
        if (field != null
            && SourceStorageNames.TryGetValue(field, out var name))
            return name;
        throw new InvalidOperationException(
            $"Source storage name for '{field?.ToDisplayString()}' was not bound.");
    }

    public bool ContainsCallable(IMethodSymbol method)
        => method != null
           && _callableRegistry.ContainsKey(method);

    public MethodContext.RegisteredCallable RequireCallable(
        IMethodSymbol method)
        => method != null
           && _callableRegistry.TryGetValue(
               method, out var callable)
            ? callable
            : throw new InvalidOperationException(
                $"Callable '{method?.ToDisplayString()}' "
                + "was absent from the bound program.");

    public bool TryGetClosure(
        IMethodSymbol definition,
        ImmutableArray<ITypeSymbol> keyArgs,
        out MethodContext.ClosureSpec closure)
    {
        closure = null;
        if (definition == null) return false;
        if (_closureRegistry.TryGetValue(
                new SpecializationKey(
                    definition, keyArgs),
                out closure))
            return true;
        foreach (var pair in _closureRegistry)
        {
            if (!ClosureIdentityPlan.SameSourceDefinition(
                    pair.Key.Definition, definition)
                || pair.Key.Arguments.Length
                != keyArgs.Length)
                continue;
            var sameArguments = true;
            for (var i = 0; i < keyArgs.Length; i++)
                if (!SymbolEqualityComparer.Default.Equals(
                        pair.Key.Arguments[i], keyArgs[i]))
                {
                    sameArguments = false;
                    break;
                }
            if (!sameArguments) continue;
            closure = pair.Value;
            return true;
        }
        return false;
    }

    public MethodContext.ClosureSpec RequireClosure(
        IMethodSymbol definition,
        ImmutableArray<ITypeSymbol> keyArgs)
        => TryGetClosure(definition, keyArgs, out var closure)
            ? closure
            : throw new InvalidOperationException(
                $"Hoisted closure '{definition?.Name}' has no registration "
                + "for the bound specialization.");

    public BoundCallSite RequireCallSite(
        SyntaxNode syntax,
        CallableSiteKind kind,
        CallSiteBindingScope? scope)
    {
        var key = new BoundCallSiteKey(syntax, kind, scope);
        if (_callSites.TryGetValue(key, out var site))
            return site;
        throw new InvalidOperationException(
            $"Callable site '{syntax}' ({kind}) "
            + "was absent from the bound program.");
    }

    public BoundInitializer RequireInitializer(
        IOperation operation,
        INamedTypeSymbol mintedType = null)
    {
        var key = new BoundInitializerKey(
            operation.Syntax, mintedType);
        if (_initializers.TryGetValue(key, out var binding))
            return binding;
        throw new InvalidOperationException(
            $"Initializer '{operation.Syntax}' was absent from the bound program "
            + $"for '{mintedType?.ToDisplayString() ?? "program fields"}'.");
    }

    public IReadOnlyList<BoundClassFieldInitializer>
        RequireClassInitializers(INamedTypeSymbol type)
    {
        if (type != null
            && _classInitializers.TryGetValue(type, out var plan))
            return plan;
        throw new InvalidOperationException(
            $"Class initializer plan '{type?.ToDisplayString()}' "
            + "was absent from the bound program.");
    }

    public IMethodSymbol RequireDeconstruction(
        IOperation operation,
        CallSiteBindingScope scope)
    {
        var key = new BoundDeconstructionKey(
            operation.Syntax, scope);
        if (_deconstructions.TryGetValue(key, out var method))
            return method;
        throw new InvalidOperationException(
            $"Deconstruction '{operation.Syntax}' was absent from the bound program.");
    }

    public ClosedConversionPlan RequireConversion(
        IConversionOperation operation,
        CallSiteBindingScope scope)
    {
        var key = new BoundConversionKey(operation, scope);
        if (_conversions.TryGetValue(key, out var plan))
            return plan;
        throw new InvalidOperationException(
            $"Conversion '{operation?.Syntax}' was absent from the bound program.");
    }

    public DispatchPlan RequireSyntheticDispatch(
        INamedTypeSymbol receiverType,
        IMethodSymbol target)
    {
        var key = new BoundSyntheticDispatchKey(
            receiverType, target);
        if (_syntheticDispatch.TryGetValue(key, out var dispatch))
            return dispatch;
        throw new InvalidOperationException(
            $"Synthetic dispatch '{receiverType}.{target.Name}' was absent "
            + "from the bound program.");
    }

    static IReadOnlyDictionary<TKey, TValue> Freeze<TKey, TValue>(
        IDictionary<TKey, TValue> source,
        string parameterName)
    {
        if (source == null)
            throw new ArgumentNullException(parameterName);
        return new ReadOnlyDictionary<TKey, TValue>(
            new Dictionary<TKey, TValue>(source));
    }
}
