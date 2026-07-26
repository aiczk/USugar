using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

internal readonly struct FieldInitializerPlan
{
    public readonly string FieldName;
    public readonly IOperation Operation;
    public readonly ITypeSymbol FieldType;

    public FieldInitializerPlan(string fieldName, IOperation operation, ITypeSymbol fieldType)
    {
        FieldName = fieldName;
        Operation = operation;
        FieldType = fieldType;
    }
}

/// <summary>
/// Immutable result of field discovery. It contains semantic declarations and initializer roots,
/// but no Structured IR; lowering publishes it only after the whole program plan is closed.
/// </summary>
internal sealed class FieldDiscoveryPlan
{
    public readonly IReadOnlyList<FieldDecl> Declarations;
    public readonly IReadOnlyList<FieldInitializerPlan> Initializers;
    public readonly IReadOnlyList<FieldInitializerPlan> StaticInitializers;
    public readonly IReadOnlyList<(string FieldName, INamedTypeSymbol AggregateType)> AggregateDefaults;
    public readonly IReadOnlyDictionary<string, string> FieldChangeCallbacks;

    public FieldDiscoveryPlan(
        IEnumerable<FieldDecl> declarations,
        IEnumerable<FieldInitializerPlan> initializers,
        IEnumerable<FieldInitializerPlan> staticInitializers,
        IEnumerable<(string FieldName, INamedTypeSymbol AggregateType)> aggregateDefaults,
        IReadOnlyDictionary<string, string> fieldChangeCallbacks)
    {
        Declarations = Array.AsReadOnly(declarations.Select(Clone).ToArray());
        Initializers = Array.AsReadOnly(initializers.ToArray());
        StaticInitializers = Array.AsReadOnly(staticInitializers.ToArray());
        AggregateDefaults = Array.AsReadOnly(aggregateDefaults.ToArray());
        FieldChangeCallbacks = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(fieldChangeCallbacks, StringComparer.Ordinal));
    }

    public IEnumerable<IOperation> InitializerOperations
        => Initializers.Select(initializer => initializer.Operation);

    static FieldDecl Clone(FieldDecl source)
        => new FieldDecl(source.Name, source.Type, source.Domain)
        {
            Flags = source.Flags,
            DefaultValue = source.DefaultValue,
            SyncMode = source.SyncMode,
        };
}

/// <summary>Single IR publication boundary for a completed field plan.</summary>
internal static class FieldPlanEmitter
{
    public static void Emit(FieldDiscoveryPlan plan, LoweringState state)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (state.Initializers.FieldInitOps.Count != 0
            || state.Initializers.StaticFieldInitOps.Count != 0
            || state.Initializers.FieldChangeCallbacks.Count != 0)
            throw new InvalidOperationException("Field discovery plan was published twice.");

        foreach (var declaration in plan.Declarations)
            state.Storage.DeclarePlannedField(Clone(declaration));
        foreach (var initializer in plan.Initializers)
            state.Initializers.FieldInitOps.Add((
                initializer.FieldName, initializer.Operation, initializer.FieldType));
        foreach (var initializer in plan.StaticInitializers)
            state.Initializers.StaticFieldInitOps.Add((
                initializer.FieldName, initializer.Operation, initializer.FieldType));
        foreach (var pair in plan.FieldChangeCallbacks)
            state.Initializers.FieldChangeCallbacks.Add(pair.Key, pair.Value);
    }

    static FieldDecl Clone(FieldDecl source)
        => new FieldDecl(source.Name, source.Type, source.Domain)
        {
            Flags = source.Flags,
            DefaultValue = source.DefaultValue,
            SyncMode = source.SyncMode,
        };
}

/// <summary>Mutable, discovery-only sink for one <see cref="FieldDiscoveryPlan"/>.</summary>
internal sealed class FieldDiscoveryPlanBuilder
{
    readonly List<FieldDecl> _declarations = new();
    readonly Dictionary<string, FieldDecl> _declarationsByName = new(StringComparer.Ordinal);

    public readonly List<FieldInitializerPlan> InstanceInitializers = new();
    public readonly List<FieldInitializerPlan> StaticInitializers = new();
    public readonly List<(string FieldName, INamedTypeSymbol AggregateType)> AggregateDefaults = new();
    public readonly Dictionary<string, string> FieldChangeCallbacks = new(StringComparer.Ordinal);

    public string DeclareField(string name, StorageType type, FieldFlags flags = FieldFlags.None,
        object defaultValue = null, string syncMode = null)
    {
        Declare(new FieldDecl(name, type, StorageDomain.User)
            { Flags = flags, DefaultValue = defaultValue, SyncMode = syncMode });
        return name;
    }

    public string DeclareGeneratedField(string name, StorageType type, object defaultValue = null)
    {
        Declare(new FieldDecl(name, type, StorageDomain.Generated) { DefaultValue = defaultValue });
        return name;
    }

    public bool TryDeclareVar(string name, StorageType type)
    {
        if (_declarationsByName.TryGetValue(name, out var existing))
        {
            EnsureCompatible(existing, new FieldDecl(name, type, StorageDomain.Generated));
            return false;
        }
        Declare(new FieldDecl(name, type, StorageDomain.Generated));
        return true;
    }

    public FieldDiscoveryPlan Build()
        => new FieldDiscoveryPlan(
            _declarations,
            InstanceInitializers,
            StaticInitializers,
            AggregateDefaults,
            FieldChangeCallbacks);

    void Declare(FieldDecl declaration)
    {
        if (_declarationsByName.TryGetValue(declaration.Name, out var existing))
        {
            EnsureCompatible(existing, declaration);
            return;
        }
        _declarationsByName.Add(declaration.Name, declaration);
        _declarations.Add(declaration);
    }

    static void EnsureCompatible(FieldDecl existing, FieldDecl requested)
    {
        if (existing.Domain == requested.Domain
            && existing.Type == requested.Type
            && existing.Flags == requested.Flags
            && string.Equals(existing.SyncMode, requested.SyncMode, StringComparison.Ordinal)
            && Equals(existing.DefaultValue, requested.DefaultValue))
            return;
        throw new InvalidOperationException(
            $"Storage '{requested.Name}' declaration conflicts during field discovery: "
            + $"existing {existing.Domain}/{existing.Type}, requested "
            + $"{requested.Domain}/{requested.Type}.");
    }
}
