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
    readonly HashSet<string> _seenMethods = new(StringComparer.Ordinal);
    readonly HashSet<string> _seenFieldInits = new(StringComparer.Ordinal);
    readonly Dictionary<IMethodSymbol, int> _specsPerDef = new(SymbolEqualityComparer.Default);
    readonly Queue<(IMethodSymbol Method, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> Map)> _queue = new();

    public GenericTypeSpecCensus(Compilation compilation, Func<IMethodSymbol, IOperation> bodyOf,
        Func<INamedTypeSymbol, IEnumerable<IOperation>> fieldInits)
    {
        _compilation = compilation;
        _bodyOf = bodyOf;
        _fieldInits = fieldInits;
    }

    public HashSet<INamedTypeSymbol> Build(ClassCompilePlan plan)
    {
        foreach (var m in plan.Methods) EnqueueIfClosed(m, null);
        foreach (var m in plan.ForeignStatics) EnqueueIfClosed(m, null);
        foreach (var m in plan.StructMethods) EnqueueIfClosed(m, null);
        foreach (var m in plan.BaseInstanceMethods) EnqueueIfClosed(m, null);
        foreach (var op in plan.FieldInitOps) Walk(op, null);

        while (_queue.Count > 0)
        {
            var (method, map) = _queue.Dequeue();
            Walk(_bodyOf(method.OriginalDefinition), map);
        }
        return _minted;
    }

    void EnqueueIfClosed(IMethodSymbol method, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> ambient)
    {
        if (method == null || method.DeclaringSyntaxReferences.Length == 0) return;
        var closed = TypeEnvironment.CloseMethod(_compilation, method, ambient);
        var map = TypeEnvironment.ForMethod(closed, ambient);
        if (ContainsOpen(closed.ContainingType) || closed.IsGenericMethod && closed.TypeArguments.Any(ContainsOpen))
            return;
        var key = MethodKey(closed, map);
        if (!_seenMethods.Add(key)) return;
        var def = closed.OriginalDefinition;
        _specsPerDef.TryGetValue(def, out var count);
        if (count >= 256)
            throw new NotSupportedException(
                $"Generic specialization census exceeded 256 instances of '{def.ToDisplayString()}'. This usually indicates polymorphic-recursive type growth.");
        _specsPerDef[def] = count + 1;
        _queue.Enqueue((closed, map));
    }

    void AddMint(INamedTypeSymbol raw, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map)
    {
        var closed = TypeEnvironment.CloseType(_compilation, raw, map) as INamedTypeSymbol;
        if (closed == null || !EmitPolicy.IsUserClassType(closed) || ContainsOpen(closed)) return;
        if (!_minted.Add(closed)) return;

        var classMap = TypeEnvironment.ForContainingType(closed, map);
        var fieldKey = ClassTypeObjectContext.SpecKey(closed);
        if (_seenFieldInits.Add(fieldKey))
            foreach (var init in _fieldInits(closed)) Walk(init, classMap);

        var ctor = closed.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0);
        EnqueueIfClosed(ctor, classMap);
    }

    void Walk(IOperation root, IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map)
    {
        if (root == null) return;
        foreach (var op in SelfAndDescendants(root))
        {
            foreach (var target in OperationMethodFacts.ConstructedTargets(op))
                EnqueueIfClosed(target, map);
            switch (op)
            {
                case IObjectCreationOperation oc when oc.Type is INamedTypeSymbol ct:
                    AddMint(ct, map);
                    break;
                case ITypeParameterObjectCreationOperation tp:
                    if (TypeEnvironment.CloseType(_compilation, tp.Type, map) is INamedTypeSymbol concrete) AddMint(concrete, map);
                    break;
            }
        }
    }

    static bool ContainsOpen(ITypeSymbol type) => ClassTypeObjectContext.ContainsTypeParameter(type);

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
}
