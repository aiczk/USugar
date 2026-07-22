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

    public Result Build(ClassCompilePlan plan)
    {
        foreach (var m in plan.Callables.ProgramMethods) EnqueueIfClosed(m, null, null);
        foreach (var m in plan.Callables.ForeignStatics) EnqueueIfClosed(m, null, null);
        foreach (var m in plan.Callables.StructMethods) EnqueueIfClosed(m, null, null);
        foreach (var m in plan.Callables.BaseInstanceMethods) EnqueueIfClosed(m, null, null);
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
        return new Result(_minted, _specializations.Values.ToArray());
    }

    void EnqueueIfClosed(IMethodSymbol method, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> ambient,
        SpecTrace parent)
    {
        if (method == null || method.DeclaringSyntaxReferences.Length == 0) return;
        var closed = TypeEnvironment.CloseMethod(_compilation, method, ambient);
        var map = TypeEnvironment.ForMethod(closed, ambient);
        if (ContainsOpen(closed.ContainingType) || closed.IsGenericMethod && closed.TypeArguments.Any(ContainsOpen))
            return;
        RejectExpandingCycle(closed, parent);
        var key = MethodKey(closed, map);
        if (!_seenMethods.Add(key)) return;
        var hasEmittableBody = _bodyOf(closed.OriginalDefinition) != null;
        if (!closed.IsDefinition
            && closed.MethodKind is not (MethodKind.LocalFunction or MethodKind.LambdaMethod
                or MethodKind.AnonymousFunction) && hasEmittableBody)
            _specializations.Add(key, closed);
        _queue.Enqueue((closed, map, new SpecTrace(closed, parent)));
    }

    void AddMint(INamedTypeSymbol raw, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map,
        SpecTrace methodTrace, MintTrace parentMint)
    {
        var closed = TypeEnvironment.CloseType(_compilation, raw, map) as INamedTypeSymbol;
        if (closed == null || !TypeClassifier.IsUserClass(closed) || ContainsOpen(closed)) return;
        RejectExpandingMint(closed, parentMint);
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
        if (!rawTarget.IsGenericMethod && !rawTarget.ContainingType.IsGenericType) return;
        var target = TypeEnvironment.CloseMethod(_compilation, rawTarget, map);
        var owner = target.ContainingType;
        if (owner == null || !VirtualDispatch.IsAssignable(concrete, owner)) return;

        IMethodSymbol implementation = null;
        if (owner.TypeKind == TypeKind.Interface)
            implementation = FindInterfaceImplementation(concrete, target);
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
        if (target.IsGenericMethod && implementation.IsGenericMethod)
            implementation = implementation.Construct(target.TypeArguments.ToArray());
        EnqueueIfClosed(implementation, TypeEnvironment.ForContainingType(concrete, map), trace);
    }

    static IMethodSymbol FindInterfaceImplementation(INamedTypeSymbol concrete, IMethodSymbol target)
    {
        if (concrete.FindImplementationForInterfaceMember(target) is IMethodSymbol method)
            return method;
        if (target.AssociatedSymbol == null) return null;
        var member = concrete.FindImplementationForInterfaceMember(target.AssociatedSymbol);
        return member switch
        {
            IMethodSymbol accessor => accessor,
            IPropertySymbol property when target.MethodKind == MethodKind.PropertyGet => property.GetMethod,
            IPropertySymbol property when target.MethodKind == MethodKind.PropertySet => property.SetMethod,
            IEventSymbol evt when target.MethodKind == MethodKind.EventAdd => evt.AddMethod,
            IEventSymbol evt when target.MethodKind == MethodKind.EventRemove => evt.RemoveMethod,
            _ => null,
        };
    }

    static bool ContainsOpen(ITypeSymbol type) => ClassTypeObjectContext.ContainsTypeParameter(type);

    static void RejectExpandingCycle(IMethodSymbol current, SpecTrace trace)
    {
        for (var ancestor = trace; ancestor != null; ancestor = ancestor.Parent)
        {
            if (!SymbolEqualityComparer.Default.Equals(
                    current.OriginalDefinition, ancestor.Method.OriginalDefinition)) continue;
            var before = SpecializationArguments(ancestor.Method);
            var after = SpecializationArguments(current);
            if (before.Count != after.Count || !StrictlyEmbeds(after, before)) continue;
            throw new NotSupportedException(
                $"Generic specialization expands recursively: '{Display(ancestor.Method)}' -> '{Display(current)}'. "
                + "The closed-specialization set would be infinite.");
        }
    }

    static void RejectExpandingMint(INamedTypeSymbol current, MintTrace trace)
    {
        for (var ancestor = trace; ancestor != null; ancestor = ancestor.Parent)
        {
            if (!SymbolEqualityComparer.Default.Equals(
                    current.OriginalDefinition, ancestor.Type.OriginalDefinition)) continue;
            if (!ContainsType(current, ancestor.Type)
                || SymbolEqualityComparer.Default.Equals(current, ancestor.Type)) continue;
            throw new NotSupportedException(
                $"Generic type specialization expands recursively through field initializers: "
                + $"'{ancestor.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' -> "
                + $"'{current.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}'. "
                + "The closed-specialization set would be infinite.");
        }
    }

    static IReadOnlyList<ITypeSymbol> SpecializationArguments(IMethodSymbol method)
    {
        var result = new List<ITypeSymbol>();
        var owners = new Stack<INamedTypeSymbol>();
        for (var type = method.ContainingType; type != null; type = type.ContainingType) owners.Push(type);
        while (owners.Count > 0) result.AddRange(owners.Pop().TypeArguments);
        if (method.IsGenericMethod) result.AddRange(method.TypeArguments);
        return result;
    }

    static bool StrictlyEmbeds(IReadOnlyList<ITypeSymbol> containers, IReadOnlyList<ITypeSymbol> values)
    {
        var strict = false;
        for (var i = 0; i < containers.Count; i++)
        {
            if (!ContainsType(containers[i], values[i])) return false;
            if (!SymbolEqualityComparer.Default.Equals(containers[i], values[i])) strict = true;
        }
        return strict;
    }

    static bool ContainsType(ITypeSymbol container, ITypeSymbol value)
    {
        if (SymbolEqualityComparer.Default.Equals(container, value)) return true;
        if (container is IArrayTypeSymbol array) return ContainsType(array.ElementType, value);
        if (container is not INamedTypeSymbol named) return false;
        foreach (var argument in named.TypeArguments)
            if (ContainsType(argument, value)) return true;
        return named.ContainingType != null && ContainsType(named.ContainingType, value);
    }

    static string Display(IMethodSymbol method)
        => method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    static string MethodKey(IMethodSymbol method, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map)
        => (method.OriginalDefinition.GetDocumentationCommentId() ?? method.OriginalDefinition.ToDisplayString())
           + "|" + ClassTypeObjectContext.SpecKey(method.ContainingType)
           + "|" + string.Join("|", method.TypeArguments.Select(ClassTypeObjectContext.SpecKey));

    static IEnumerable<IOperation> SelfAndDescendants(IOperation op)
    {
        yield return op;
        foreach (var child in op.ChildOps())
            foreach (var nested in SelfAndDescendants(child)) yield return nested;
    }

    internal sealed class Result
    {
        public readonly HashSet<INamedTypeSymbol> MintedClasses;
        public readonly IMethodSymbol[] MethodSpecializations;

        public Result(HashSet<INamedTypeSymbol> mintedClasses, IMethodSymbol[] methodSpecializations)
        {
            MintedClasses = mintedClasses;
            MethodSpecializations = methodSpecializations;
        }
    }
}
