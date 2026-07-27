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
/// Source-facing field metadata frozen by the same discovery pass that owns the
/// Udon heap declaration. Unity integration consumes this record instead of
/// walking the symbol hierarchy a second time.
/// </summary>
internal readonly struct SourceFieldPlan
{
    public readonly string Name;
    public readonly ISymbol Member;
    public readonly ITypeSymbol UserType;
    public readonly StorageType StorageType;
    public readonly bool IsSerialized;
    public readonly string SyncMode;

    public SourceFieldPlan(
        string name,
        ISymbol member,
        ITypeSymbol userType,
        StorageType storageType,
        bool isSerialized,
        string syncMode)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Member = member ?? throw new ArgumentNullException(nameof(member));
        UserType = userType ?? throw new ArgumentNullException(nameof(userType));
        StorageType = storageType;
        IsSerialized = isSerialized;
        SyncMode = syncMode;
    }
}

/// <summary>
/// Immutable result of field discovery. It contains semantic declarations and initializer roots,
/// but no Structured IR; lowering publishes it only after the whole program plan is closed.
/// </summary>
internal sealed class FieldDiscoveryPlan
{
    public readonly IReadOnlyList<FieldDecl> Declarations;
    public readonly IReadOnlyList<SourceFieldPlan> SourceFields;
    public readonly IReadOnlyList<FieldInitializerPlan> Initializers;
    public readonly IReadOnlyList<(string FieldName, INamedTypeSymbol AggregateType)> AggregateDefaults;
    public readonly IReadOnlyDictionary<string, string> FieldChangeCallbacks;

    public FieldDiscoveryPlan(
        IEnumerable<FieldDecl> declarations,
        IEnumerable<SourceFieldPlan> sourceFields,
        IEnumerable<FieldInitializerPlan> initializers,
        IEnumerable<(string FieldName, INamedTypeSymbol AggregateType)> aggregateDefaults,
        IReadOnlyDictionary<string, string> fieldChangeCallbacks)
    {
        Declarations = Array.AsReadOnly(declarations.Select(Clone).ToArray());
        SourceFields = Array.AsReadOnly(sourceFields.ToArray());
        Initializers = Array.AsReadOnly(initializers.ToArray());
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
        if (!ReferenceEquals(state.Program?.Fields, plan))
            throw new InvalidOperationException(
                "Field materialization requires the published "
                + "bound program.");
        if (state.FieldInitOps.Count != 0
            || state.FieldChangeCallbacks.Count != 0)
            throw new InvalidOperationException("Field discovery plan was published twice.");

        foreach (var declaration in plan.Declarations)
            state.Storage.DeclarePlannedField(Clone(declaration));
        foreach (var initializer in plan.Initializers)
            state.FieldInitOps.Add((
                initializer.FieldName, initializer.Operation, initializer.FieldType));
        foreach (var pair in plan.FieldChangeCallbacks)
            state.FieldChangeCallbacks.Add(pair.Key, pair.Value);
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
    readonly List<SourceFieldPlan> _sourceFields = new();
    readonly Dictionary<string, SourceFieldPlan> _sourceFieldsByName =
        new(StringComparer.Ordinal);

    public readonly List<FieldInitializerPlan> InstanceInitializers = new();
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
            FieldDecl.RequireCompatible(existing, new FieldDecl(name, type, StorageDomain.Generated), " during field discovery");
            return false;
        }
        Declare(new FieldDecl(name, type, StorageDomain.Generated));
        return true;
    }

    public void RecordSourceField(
        string name,
        ISymbol member,
        ITypeSymbol userType,
        StorageType storageType,
        bool isSerialized,
        string syncMode = null)
    {
        var field = new SourceFieldPlan(
            name, member, userType, storageType, isSerialized, syncMode);
        if (_sourceFieldsByName.TryGetValue(name, out var existing))
        {
            if (SymbolEqualityComparer.Default.Equals(existing.Member, member)
                && SymbolEqualityComparer.Default.Equals(
                    existing.UserType, userType)
                && existing.StorageType == storageType
                && existing.IsSerialized == isSerialized
                && string.Equals(
                    existing.SyncMode, syncMode,
                    StringComparison.Ordinal))
                return;
            // Unity's source-facing table is name-keyed. Discovery visits the
            // derived declaration first, so a legal non-serialized base
            // shadow keeps the leaf metadata while its distinct internal Udon
            // storage remains in Declarations.
            return;
        }
        _sourceFieldsByName.Add(name, field);
        _sourceFields.Add(field);
    }

    public FieldDiscoveryPlan Build()
        => new FieldDiscoveryPlan(
            _declarations,
            _sourceFields,
            InstanceInitializers,
            AggregateDefaults,
            FieldChangeCallbacks);

    void Declare(FieldDecl declaration)
    {
        if (_declarationsByName.TryGetValue(declaration.Name, out var existing))
        {
            FieldDecl.RequireCompatible(existing, declaration, " during field discovery");
            return;
        }
        _declarationsByName.Add(declaration.Name, declaration);
        _declarations.Add(declaration);
    }

}
