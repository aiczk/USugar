using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Closed-specialization census for user-class type objects. Unlike the def-keyed call graph,
/// this walk retains constructed symbols and the active type-parameter map.</summary>
internal sealed class GenericTypeSpecCensus
{
    readonly Compilation _compilation;
    readonly Func<IMethodSymbol, IOperation> _bodyOf;
    readonly Func<INamedTypeSymbol, IEnumerable<IOperation>> _fieldInits;
    readonly HashSet<INamedTypeSymbol> _minted = new(SymbolEqualityComparer.Default);
    readonly HashSet<INamedTypeSymbol> _dispatchTypes = new(SymbolEqualityComparer.Default);
    readonly Dictionary<string, IMethodSymbol> _specializations = new(StringComparer.Ordinal);
    readonly Dictionary<string, ClosureSpecializationCandidate> _closures = new(StringComparer.Ordinal);
    readonly HashSet<string> _seenMethods = new(StringComparer.Ordinal);
    readonly HashSet<string> _seenFieldInits = new(StringComparer.Ordinal);
    readonly Queue<(IMethodSymbol Method, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> Map, SpecTrace Trace)> _queue = new();
    readonly Queue<(INamedTypeSymbol Type, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> Map,
        MintTrace MintTrace, SpecTrace MethodTrace)> _fieldQueue = new();
    readonly List<(IMethodSymbol Target, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> Map,
        SpecTrace Trace)> _dispatchSites = new();

    sealed class SpecTrace
    {
        public readonly IMethodSymbol Method;
        public readonly SpecTrace Parent;
        public SpecTrace(IMethodSymbol method, SpecTrace parent) { Method = method; Parent = parent; }
    }

    sealed class MintTrace
    {
        public readonly INamedTypeSymbol Type;
        public readonly MintTrace Parent;
        public MintTrace(INamedTypeSymbol type, MintTrace parent) { Type = type; Parent = parent; }
    }

    public GenericTypeSpecCensus(Compilation compilation, Func<IMethodSymbol, IOperation> bodyOf,
        Func<INamedTypeSymbol, IEnumerable<IOperation>> fieldInits, INamedTypeSymbol rootType)
    {
        _compilation = compilation;
        _bodyOf = bodyOf;
        _fieldInits = fieldInits;
        _dispatchTypes.Add(rootType);
    }

    public Result Build(ProgramDiscoverySeed plan)
    {
        foreach (var m in plan.ProgramMethods) EnqueueIfClosed(m, null, null);
        foreach (var m in plan.ForeignStatics) EnqueueIfClosed(m, null, null);
        foreach (var m in plan.StructMethods) EnqueueIfClosed(m, null, null);
        foreach (var m in plan.BaseInstanceMethods) EnqueueIfClosed(m, null, null);
        foreach (var op in plan.FieldInitOps) Walk(op, null, null, null);

        while (_queue.Count > 0 || _fieldQueue.Count > 0)
        {
            if (_queue.Count > 0)
            {
                var (method, map, trace) = _queue.Dequeue();
                Walk(_bodyOf(method.OriginalDefinition), map, trace, null);
                continue;
            }
            var (type, fieldMap, mintTrace, methodTrace) = _fieldQueue.Dequeue();
            foreach (var init in _fieldInits(type)) Walk(init, fieldMap, methodTrace, mintTrace);
        }
        return new Result(_minted, _specializations.Values.ToArray(), _closures.Values.ToArray());
    }

    void EnqueueIfClosed(IMethodSymbol method, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> ambient,
        SpecTrace parent)
    {
        if (method == null) return;
        var closureKind = method.MethodKind is MethodKind.LocalFunction
            or MethodKind.LambdaMethod or MethodKind.AnonymousFunction;
        if (!closureKind && method.DeclaringSyntaxReferences.Length == 0) return;
        var closed = TypeEnvironment.CloseMethod(_compilation, method, ambient);
        var map = closureKind ? ForClosure(closed, ambient) : TypeEnvironment.ForMethod(closed, ambient);
        // A closure symbol remains lexically attached to the open declaration type even while its
        // enclosing method trace is closed (for example Box<int>.Run's lambda still reports Box<T>).
        // Its containing-type arguments therefore come from the owner trace, not from this symbol.
        if ((!closureKind && ContainsOpen(closed.ContainingType))
            || closed.IsGenericMethod && closed.TypeArguments.Any(t => ContainsOpen(TypeEnvironment.CloseType(
                _compilation, t, ambient))))
            return;
        SpecializationExpansionPolicy.RequireFinite(closed, TraceMethods(parent));
        var lexicalOwners = closureKind ? LexicalOwners(closed, parent)
            : System.Collections.Immutable.ImmutableArray<IMethodSymbol>.Empty;
        var key = MethodKey(closed, map) + (closureKind
            ? "|source:" + SourceKey(closed)
              + "|owners:" + string.Join(">", lexicalOwners.Select(method => MethodKey(method, null)))
            : "");
        if (!_seenMethods.Add(key)) return;
        var hasEmittableBody = _bodyOf(closed.OriginalDefinition) != null;
        if (!closed.IsDefinition
            && !closureKind && hasEmittableBody)
            _specializations.Add(key, closed);
        if (closureKind)
            _closures.Add(key, new ClosureSpecializationCandidate(closed, TraceMethods(parent)));
        _queue.Enqueue((closed, map, new SpecTrace(closed, parent)));
    }

    static IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> ForClosure(IMethodSymbol method,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> parent)
        => !method.IsGenericMethod ? parent : TypeParamScope.Compose(parent, true, new[]
        {
            ((IReadOnlyList<ITypeParameterSymbol>)method.OriginalDefinition.TypeParameters,
             (IReadOnlyList<ITypeSymbol>)method.TypeArguments)
        });

    void AddMint(INamedTypeSymbol raw, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map,
        SpecTrace methodTrace, MintTrace parentMint)
    {
        var closed = TypeEnvironment.CloseType(_compilation, raw, map) as INamedTypeSymbol;
        if (closed == null || !TypeClassifier.IsUserClass(closed) || ContainsOpen(closed)) return;
        SpecializationExpansionPolicy.RequireFinite(closed, TraceTypes(parentMint));
        if (!_minted.Add(closed)) return;
        _dispatchTypes.Add(closed);

        foreach (var site in _dispatchSites)
            EnqueueDispatchTarget(site.Target, site.Map, site.Trace, closed);

        var classMap = TypeEnvironment.ForContainingType(closed, map);
        var fieldKey = ClassTypeObjectContext.SpecKey(closed);
        if (_seenFieldInits.Add(fieldKey))
            _fieldQueue.Enqueue((closed, classMap, new MintTrace(closed, parentMint), methodTrace));

        var ctor = closed.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0);
        EnqueueIfClosed(ctor, classMap, methodTrace);
    }

    void Walk(IOperation root, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map,
        SpecTrace methodTrace, MintTrace mintTrace)
    {
        if (root == null) return;
        foreach (var op in SelfAndDescendants(root))
        {
            if (op is ILocalFunctionOperation local
                && !SameDefinition(local.Symbol, methodTrace?.Method))
                EnqueueIfClosed(local.Symbol, map, methodTrace);
            else if (op is IAnonymousFunctionOperation lambda
                && !SameDefinition(lambda.Symbol, methodTrace?.Method))
                EnqueueIfClosed(lambda.Symbol, map, methodTrace);
            else if (op is IDelegateCreationOperation
                     { Target: IAnonymousFunctionOperation delegateLambda })
                EnqueueIfClosed(delegateLambda.Symbol, map, methodTrace);
            foreach (var site in CallableSites.FromOperation(op))
            {
                EnqueueIfClosed(site.Target, map, methodTrace);
                _dispatchSites.Add((site.Target, map, methodTrace));
                foreach (var concrete in _dispatchTypes)
                    EnqueueDispatchTarget(site.Target, map, methodTrace, concrete);
            }
            switch (op)
            {
                case IObjectCreationOperation oc when oc.Type is INamedTypeSymbol ct:
                    AddMint(ct, map, methodTrace, mintTrace);
                    break;
                case ITypeParameterObjectCreationOperation tp:
                    if (TypeEnvironment.CloseType(_compilation, tp.Type, map) is INamedTypeSymbol concrete)
                        AddMint(concrete, map, methodTrace, mintTrace);
                    break;
            }
        }
    }


    void EnqueueDispatchTarget(IMethodSymbol rawTarget,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map, SpecTrace trace,
        INamedTypeSymbol concrete)
    {
        var target = TypeEnvironment.CloseMethod(_compilation, rawTarget, map);
        var owner = target.ContainingType;
        if (owner == null || !IsDispatchCompatible(concrete, owner)) return;

        IMethodSymbol implementation = null;
        if (owner.TypeKind == TypeKind.Interface)
            implementation = VirtualDispatch.FindInterfaceMethodImplementation(concrete, target);
        else if (VirtualDispatch.IsVirtualCall(target))
            implementation = VirtualDispatch.MostDerivedImpl(
                concrete, VirtualDispatch.SlotIntroducer(target));
        if (implementation == null && target.AssociatedSymbol is IEventSymbol targetEvent)
        {
            var eventImpl = concrete.GetMembers().OfType<IEventSymbol>().FirstOrDefault(evt =>
                evt.Name == targetEvent.Name || evt.ExplicitInterfaceImplementations.Any(i =>
                    SymbolEqualityComparer.Default.Equals(i.OriginalDefinition,
                        targetEvent.OriginalDefinition)));
            implementation = target.MethodKind == MethodKind.EventAdd
                ? eventImpl?.AddMethod : eventImpl?.RemoveMethod;
        }
        if (implementation == null || implementation.IsAbstract) return;
        if (target.IsGenericMethod && implementation.IsGenericMethod
            && implementation.TypeArguments.Any(ClassTypeObjectContext.ContainsTypeParameter))
            implementation = implementation.Construct(target.TypeArguments.ToArray());
        var implementationMap = TypeEnvironment.ForContainingType(concrete, map);
        EnqueueIfClosed(implementation, implementationMap, trace);
        // The reach pass cannot resolve dispatch against a class type minted later in this census.
        // Preserve the selected implementation as a late registration even when it is a non-generic
        // definition; CompilationPlanner removes methods that were already registered eagerly.
        if (implementation.DeclaringSyntaxReferences.Length > 0
            && _bodyOf(implementation.OriginalDefinition) != null)
            _specializations.TryAdd(MethodKey(implementation, implementationMap), implementation);
    }

    static bool IsDispatchCompatible(INamedTypeSymbol concrete, INamedTypeSymbol owner)
        => owner.TypeKind == TypeKind.Interface
            ? concrete.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, owner))
            : VirtualDispatch.IsAssignable(concrete, owner);

    static bool ContainsOpen(ITypeSymbol type) => ClassTypeObjectContext.ContainsTypeParameter(type);

    static bool SameDefinition(IMethodSymbol left, IMethodSymbol right)
        => left != null && right != null && SymbolEqualityComparer.Default.Equals(
            left.OriginalDefinition, right.OriginalDefinition);

    static string MethodKey(IMethodSymbol method, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map)
        => (method.OriginalDefinition.GetDocumentationCommentId() ?? method.OriginalDefinition.ToDisplayString())
           + "|" + ClassTypeObjectContext.SpecKey(method.ContainingType)
           + "|" + string.Join("|", method.TypeArguments.Select(ClassTypeObjectContext.SpecKey));

    static string SourceKey(IMethodSymbol method)
    {
        if (method.DeclaringSyntaxReferences.Length == 0) return method.ToDisplayString();
        var syntax = method.DeclaringSyntaxReferences[0];
        return syntax.SyntaxTree.FilePath + ":" + syntax.Span.Start + ":" + syntax.Span.Length;
    }

    static System.Collections.Immutable.ImmutableArray<IMethodSymbol> LexicalOwners(
        IMethodSymbol closure, SpecTrace trace)
    {
        var result = System.Collections.Immutable.ImmutableArray.CreateBuilder<IMethodSymbol>();
        for (var symbol = closure.OriginalDefinition.ContainingSymbol;
             symbol is IMethodSymbol owner;
             symbol = owner.ContainingSymbol)
            for (var current = trace; current != null; current = current.Parent)
                if (ClosureIdentityPlan.SameSourceDefinition(current.Method, owner))
                {
                    result.Add(current.Method);
                    break;
                }
        return result.ToImmutable();
    }

    static System.Collections.Immutable.ImmutableArray<IMethodSymbol> TraceMethods(SpecTrace trace)
    {
        var result = System.Collections.Immutable.ImmutableArray.CreateBuilder<IMethodSymbol>();
        for (; trace != null; trace = trace.Parent) result.Add(trace.Method);
        return result.ToImmutable();
    }

    static System.Collections.Immutable.ImmutableArray<INamedTypeSymbol> TraceTypes(MintTrace trace)
    {
        var result = System.Collections.Immutable.ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        for (; trace != null; trace = trace.Parent) result.Add(trace.Type);
        return result.ToImmutable();
    }

    static IEnumerable<IOperation> SelfAndDescendants(IOperation op, bool root = true)
    {
        yield return op;
        if (!root && op is ILocalFunctionOperation or IAnonymousFunctionOperation)
            yield break;
        foreach (var child in op.ChildOps())
            foreach (var nested in SelfAndDescendants(child, false)) yield return nested;
    }

    internal sealed class Result
    {
        public readonly HashSet<INamedTypeSymbol> MintedClasses;
        public readonly IMethodSymbol[] MethodSpecializations;
        public readonly ClosureSpecializationCandidate[] ClosureSpecializations;

        public Result(HashSet<INamedTypeSymbol> mintedClasses, IMethodSymbol[] methodSpecializations,
            ClosureSpecializationCandidate[] closureSpecializations)
        {
            MintedClasses = mintedClasses;
            MethodSpecializations = methodSpecializations;
            ClosureSpecializations = closureSpecializations;
        }
    }
}

internal readonly struct ClosureSpecializationCandidate
{
    public readonly IMethodSymbol Method;
    public readonly System.Collections.Immutable.ImmutableArray<IMethodSymbol> OwnerSpecs;

    public ClosureSpecializationCandidate(IMethodSymbol method,
        System.Collections.Immutable.ImmutableArray<IMethodSymbol> ownerSpecs)
    {
        Method = method;
        OwnerSpecs = ownerSpecs;
    }
}
