using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>Representation categories recorded at the two authoritative boundaries where Udon type
/// names enter a compilation: Roslyn source symbols and installed-SDK CLR operand types.
/// <see cref="RawCopyCompatibility"/> consumes them. Unknown names are rejected rather than
/// classified by naming heuristics.</summary>
public sealed class UdonTypeFactRegistry
{
    public readonly struct AssignabilityFact
        : IEquatable<AssignabilityFact>
    {
        public readonly UdonTypeId Actual;
        public readonly UdonTypeId Expected;

        public AssignabilityFact(UdonTypeId actual, UdonTypeId expected)
        {
            Actual = actual;
            Expected = expected;
        }

        public bool Equals(AssignabilityFact other)
            => Actual == other.Actual && Expected == other.Expected;

        public override bool Equals(object obj)
            => obj is AssignabilityFact other && Equals(other);

        public override int GetHashCode()
            => (Actual.GetHashCode() * 397) ^ Expected.GetHashCode();
    }

    public readonly struct TypeFact : IEquatable<TypeFact>
    {
        public readonly bool IsEnum;
        public readonly bool IsValueType;
        public TypeFact(bool isEnum, bool isValueType) { IsEnum = isEnum; IsValueType = isValueType; }
        public bool Equals(TypeFact other) => IsEnum == other.IsEnum && IsValueType == other.IsValueType;
        public override bool Equals(object obj) => obj is TypeFact other && Equals(other);
        public override int GetHashCode() => (IsEnum ? 1 : 0) | (IsValueType ? 2 : 0);
    }


    // Values are deterministic per name (Udon storage name ↔ representation category is 1:1), so
    // installed-SDK seeding and concurrent source-minting races during materialization are benign.
    readonly ConcurrentDictionary<UdonTypeId, TypeFact> _facts = new();
    readonly ConcurrentDictionary<AssignabilityFact, byte> _assignability
        = new();
    bool _frozen;

    internal bool IsFrozen => _frozen;

    /// <summary>Record the minted name's facts. Names covered by a STRUCTURAL rule (primitives, arrays,
    /// the fold tags) are skipped: a folded name's runtime representation is fixed by the fold itself,
    /// not by whichever source symbol happened to mint it first (a struct folding to SystemObjectArray
    /// must not poison the registry with IsValueType=true).</summary>
    public void Record(string udonName, ITypeSymbol symbol)
    {
        RequireMutable();
        if (string.IsNullOrEmpty(udonName) || symbol == null) return;
        RecordSourceLowering(
            UdonTypeIdentity.FromCanonicalStorageName(udonName), symbol);
    }

    /// <summary>
    /// Commit the representation evidence produced by one source lowering.
    /// The lowering step supplies an already-canonical storage identity; this
    /// registry never mints or reinterprets names.
    /// </summary>
    internal void RecordSourceLowering(UdonTypeId storageType,
        ITypeSymbol symbol)
    {
        RequireMutable();
        if (symbol == null) return;
        RecordHierarchy(storageType, symbol);
        Record(storageType,
            new TypeFact(symbol.TypeKind == TypeKind.Enum, symbol.IsValueType),
            symbol.ToDisplayString());
    }

    /// <summary>Record an installed-SDK CLR type before the editor boundary erases it to an Udon
    /// storage name. SDK ABI operands are verifier authorities too: without this source of
    /// facts, a legal derived reference passed to an SDK base-reference operand is rejected merely
    /// because the expected name did not originate in Roslyn source.</summary>
    internal void Record(string udonName, Type type)
    {
        RequireMutable();
        if (string.IsNullOrEmpty(udonName) || type == null) return;
        if (type.IsByRef) type = type.GetElementType();
        if (type == null || type.IsGenericParameter) return;
        var storageType =
            UdonTypeIdentity.FromCanonicalStorageName(udonName);
        RecordHierarchy(storageType, type);
        Record(storageType,
            new TypeFact(type.IsEnum, type.IsValueType),
            type.FullName ?? type.Name);
    }

    internal void Import(IEnumerable<KeyValuePair<string, TypeFact>> facts, string source)
    {
        RequireMutable();
        if (facts == null) return;
        foreach (var pair in facts)
        {
            if (string.IsNullOrEmpty(pair.Key)) continue;
            Record(UdonTypeIdentity.FromCanonicalStorageName(pair.Key),
                pair.Value, source);
        }
    }

    internal void ImportAssignability(
        IEnumerable<AssignabilityFact> facts)
    {
        RequireMutable();
        if (facts == null) return;
        foreach (var fact in facts)
            _assignability.TryAdd(fact, 0);
    }

    internal KeyValuePair<string, TypeFact>[] Snapshot()
        => _facts
            .Select(pair => new KeyValuePair<string, TypeFact>(
                pair.Key.Name, pair.Value))
            .ToArray();

    internal AssignabilityFact[] AssignabilitySnapshot()
        => _assignability.Keys.ToArray();

    internal UdonTypeFactRegistry FreezeCopy()
    {
        var copy = new UdonTypeFactRegistry();
        copy.Import(Snapshot(), "bound program snapshot");
        copy.ImportAssignability(AssignabilitySnapshot());
        copy._frozen = true;
        return copy;
    }

    public bool IsAssignableTo(string actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual)
            || string.IsNullOrWhiteSpace(expected))
            return false;
        return IsAssignableTo(
            UdonTypeIdentity.FromCanonicalStorageName(actual),
            UdonTypeIdentity.FromCanonicalStorageName(expected));
    }

    public bool IsAssignableTo(UdonTypeId actual, UdonTypeId expected)
        => actual == expected
           || _assignability.ContainsKey(
               new AssignabilityFact(actual, expected));

    internal void RecordAssignableForTest(
        string actual, string expected)
    {
        RequireMutable();
        RecordAssignable(
            UdonTypeIdentity.FromCanonicalStorageName(actual),
            UdonTypeIdentity.FromCanonicalStorageName(expected));
    }

    void Record(UdonTypeId type, TypeFact requested, string source)
    {
        if (StructuralIsReference(type.Name) != null) return;
        while (true)
        {
            if (_facts.TryGetValue(type, out var existing))
            {
                if (!existing.Equals(requested))
                    throw new InvalidOperationException(
                        $"Udon type name '{type}' has conflicting facts: existing "
                        + $"enum={existing.IsEnum}, valueType={existing.IsValueType}; requested "
                        + $"enum={requested.IsEnum}, valueType={requested.IsValueType} for "
                        + $"'{source}'.");
                return;
            }
            if (_facts.TryAdd(type, requested)) return;
        }
    }

    void RecordHierarchy(UdonTypeId actual, Type type)
    {
        RecordAssignable(actual, actual);
        if (type.IsArray)
        {
            RecordAssignable(actual,
                UdonTypeIdentity.FromCanonicalStorageName(
                    "SystemArray"));
            RecordAssignable(actual,
                UdonTypeIdentity.FromCanonicalStorageName(
                    "SystemObject"));
            return;
        }

        for (var current = type.BaseType;
             current != null;
             current = current.BaseType)
            RecordAssignable(actual,
                UdonTypeIdentity.FromStorage(current));
        foreach (var implemented in type.GetInterfaces())
            RecordAssignable(actual,
                UdonTypeIdentity.FromStorage(implemented));
    }

    void RecordHierarchy(UdonTypeId actual, ITypeSymbol symbol)
    {
        RecordAssignable(actual, actual);
        if (symbol is IArrayTypeSymbol)
        {
            RecordAssignable(actual,
                UdonTypeIdentity.FromCanonicalStorageName(
                    "SystemArray"));
            RecordAssignable(actual,
                UdonTypeIdentity.FromCanonicalStorageName(
                    "SystemObject"));
            return;
        }

        if (symbol is not INamedTypeSymbol named) return;
        for (var current = named.BaseType;
             current != null;
             current = current.BaseType)
            RecordAssignable(actual,
                UdonTypeIdentity.FromStorage(current));
        foreach (var implemented in named.AllInterfaces)
            RecordAssignable(actual,
                UdonTypeIdentity.FromStorage(implemented));
    }

    void RecordAssignable(UdonTypeId actual, UdonTypeId expected)
        => _assignability.TryAdd(
            new AssignabilityFact(actual, expected), 0);

    internal void RecordForTest(string udonName, bool isEnum, bool isValueType)
    {
        RequireMutable();
        _facts[UdonTypeIdentity.FromCanonicalStorageName(udonName)]
            = new TypeFact(isEnum, isValueType);
    }

    void RequireMutable()
    {
        if (_frozen)
            throw new InvalidOperationException(
                "The Udon type-fact snapshot is frozen.");
    }

    /// <summary>FACT: is the name an enum tag (Int32-compatible)? true/false when known, null when the
    /// neither authoritative boundary supplied it — an unknown name is exactly what the relaxed check
    /// would otherwise have to guess about.</summary>
    public bool? IsEnumFact(string udonName)
    {
        if (string.IsNullOrEmpty(udonName)) return null;
        return IsEnumFact(
            UdonTypeIdentity.FromCanonicalStorageName(udonName));
    }

    public bool? IsEnumFact(UdonTypeId type)
    {
        if (StructuralIsReference(type.Name) != null) return false; // primitives/arrays/fold tags are never enums
        return _facts.TryGetValue(type, out var f) ? f.IsEnum : (bool?)null;
    }

    /// <summary>FACT: is the name's runtime representation a reference? Structural rules first (an Udon
    /// "…Array" IS a .NET array; the fold tags are object[]/component references by construction), then
    /// the registry (an SDK struct like UnityEngineBounds is a value type even though the relaxed
    /// prefix-list heuristic calls it a reference).</summary>
    public bool? IsReferenceFact(string udonName)
    {
        if (string.IsNullOrEmpty(udonName)) return null;
        return IsReferenceFact(
            UdonTypeIdentity.FromCanonicalStorageName(udonName));
    }

    public bool? IsReferenceFact(UdonTypeId type)
    {
        var structural = StructuralIsReference(type.Name);
        if (structural != null) return structural;
        return _facts.TryGetValue(type, out var f)
            ? !f.IsValueType
            : (bool?)null;
    }







    static bool? StructuralIsReference(string name)
    {
        switch (name)
        {
            case "SystemBoolean":
            case "SystemByte":
            case "SystemSByte":
            case "SystemInt16":
            case "SystemUInt16":
            case "SystemInt32":
            case "SystemUInt32":
            case "SystemInt64":
            case "SystemUInt64":
            case "SystemSingle":
            case "SystemDouble":
            case "SystemDecimal":
            case "SystemChar":
                return false;
            case "SystemObject":
            case "SystemString":
            case "SystemType":
            case "VRCUdonCommonInterfacesIUdonEventReceiver":
            // Hardcoded by StorageContext.DeclareThis as the `this` heap var's concrete component type.
            // RemapUdonType folds this name to IUdonEventReceiver at the minting choke, so it can never
            // be fact-recorded — but it IS VRC.Udon.UdonBehaviour, a component class, a reference by
            // construction (its COPY into IUdonEventReceiver-typed vars is the this-upcast).
            case "VRCUdonUdonBehaviour":
                return true;
        }
        if (name != null && name.EndsWith("Array")) return true;
        return null;
    }
}

/// <summary>Raw VM COPY compatibility shared by structured IR verification, flat IR verification,
/// and the independent UASM validator. Legal mismatches are SystemObject, Nullable erasure,
/// fact-backed enum/Int32 representation, and two fact-backed reference types. The final rule is
/// valid only for COPY: it moves a reference strongbox without enforcing an extern's CLR operand
/// type.</summary>
public static class RawCopyCompatibility
{
    /// <summary>Null when the pair is compatible; otherwise the reason, naming the missing fact when
    /// the failure is an unknown name (a no-fact name at verify time came from neither source minting
    /// nor the installed SDK ABI snapshot — itself suspicious).</summary>
    public static string WhyIncompatible(string expected, string actual, UdonTypeFactRegistry facts)
    {
        if (facts == null) throw new ArgumentNullException(nameof(facts));
        if (expected == actual) return null;
        if (expected == "SystemObject" || actual == "SystemObject") return null;
        if (IsNullableErasure(expected, actual) || IsNullableErasure(actual, expected)) return null;
        if (expected == "SystemInt32" && facts.IsEnumFact(actual) == true) return null;
        if (actual == "SystemInt32" && facts.IsEnumFact(expected) == true) return null;
        var e = facts.IsReferenceFact(expected);
        var a = facts.IsReferenceFact(actual);
        if (e == true && a == true) return null;
        if (e == null) return NoFact(expected);
        if (a == null) return NoFact(actual);
        return $"facts deny every declared relaxation ({Describe(expected, e)}; {Describe(actual, a)})";
    }

    static bool IsNullableErasure(string boxed, string bare) =>
        boxed.StartsWith("SystemNullable", StringComparison.Ordinal)
        && boxed.Substring("SystemNullable".Length) == bare;

    static string NoFact(string name) =>
        $"no fact recorded for '{name}' (neither source type minting nor the installed SDK ABI snapshot"
        + " classified it, so no declared relaxation can vouch for it)";

    static string Describe(string name, bool? isRef) =>
        $"'{name}' is a fact {(isRef == true ? "reference" : "value type")}";
}

