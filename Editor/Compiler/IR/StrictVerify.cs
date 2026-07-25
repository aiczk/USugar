using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>Representation categories and directed assignability facts recorded at the two
/// authoritative boundaries where Udon type names enter a compilation: Roslyn source symbols and
/// installed-SDK CLR operand types. <see cref="RawCopyCompatibility"/> uses the category facts;
/// extern operand verification uses the inheritance facts. Unknown names are rejected rather than
/// classified by naming heuristics.</summary>
public sealed class UdonTypeFactRegistry
{
    public readonly struct TypeFact : IEquatable<TypeFact>
    {
        public readonly bool IsEnum;
        public readonly bool IsValueType;
        public TypeFact(bool isEnum, bool isValueType) { IsEnum = isEnum; IsValueType = isValueType; }
        public bool Equals(TypeFact other) => IsEnum == other.IsEnum && IsValueType == other.IsValueType;
        public override bool Equals(object obj) => obj is TypeFact other && Equals(other);
        public override int GetHashCode() => (IsEnum ? 1 : 0) | (IsValueType ? 2 : 0);
    }

    public readonly struct AssignabilityFact : IEquatable<AssignabilityFact>
    {
        public readonly string From;
        public readonly string To;

        public AssignabilityFact(string from, string to)
        {
            From = from ?? throw new ArgumentNullException(nameof(from));
            To = to ?? throw new ArgumentNullException(nameof(to));
        }

        public bool Equals(AssignabilityFact other)
            => string.Equals(From, other.From, StringComparison.Ordinal)
               && string.Equals(To, other.To, StringComparison.Ordinal);
        public override bool Equals(object obj)
            => obj is AssignabilityFact other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                return StringComparer.Ordinal.GetHashCode(From) * 397
                       ^ StringComparer.Ordinal.GetHashCode(To);
            }
        }
    }

    // Values are deterministic per name (Udon storage name ↔ representation category is 1:1), so
    // installed-SDK seeding and concurrent source-minting races during Phase-2 emit are benign.
    readonly ConcurrentDictionary<string, TypeFact> _facts = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<AssignabilityFact, byte> _assignability = new();
    readonly ConcurrentDictionary<Type, byte> _recordedClrHierarchy = new();
    readonly ConcurrentDictionary<ITypeSymbol, byte> _recordedSymbolHierarchy
        = new(SymbolEqualityComparer.Default);

    /// <summary>Record the minted name's facts. Names covered by a STRUCTURAL rule (primitives, arrays,
    /// the fold tags) are skipped: a folded name's runtime representation is fixed by the fold itself,
    /// not by whichever source symbol happened to mint it first (a struct folding to SystemObjectArray
    /// must not poison the registry with IsValueType=true).</summary>
    public void Record(string udonName, ITypeSymbol symbol)
    {
        if (string.IsNullOrEmpty(udonName) || symbol == null) return;
        Record(udonName,
            new TypeFact(symbol.TypeKind == TypeKind.Enum, symbol.IsValueType),
            symbol.ToDisplayString());
        RecordAssignability(udonName, symbol);
    }

    /// <summary>Record an installed-SDK CLR type before the editor boundary erases it to an Udon
    /// storage name. SDK ABI operands are verifier authorities too: without this source of
    /// facts, a legal derived reference passed to an SDK base-reference operand is rejected merely
    /// because the expected name did not originate in Roslyn source.</summary>
    internal void Record(string udonName, Type type)
    {
        if (string.IsNullOrEmpty(udonName) || type == null) return;
        if (type.IsByRef) type = type.GetElementType();
        if (type == null || type.IsGenericParameter) return;
        Record(udonName,
            new TypeFact(type.IsEnum, type.IsValueType),
            type.FullName ?? type.Name);
        RecordAssignability(udonName, type);
    }

    internal void Import(IEnumerable<KeyValuePair<string, TypeFact>> facts,
        IEnumerable<AssignabilityFact> assignability, string source)
    {
        if (facts != null)
            foreach (var pair in facts)
                Record(pair.Key, pair.Value, source);
        if (assignability != null)
            foreach (var relation in assignability)
                RecordAssignable(relation.From, relation.To);
    }

    internal KeyValuePair<string, TypeFact>[] Snapshot()
        => _facts.ToArray();

    internal AssignabilityFact[] AssignabilitySnapshot()
        => _assignability.Keys.ToArray();

    void Record(string udonName, TypeFact requested, string source)
    {
        if (string.IsNullOrEmpty(udonName)) return;
        if (StructuralIsReference(udonName) != null) return;
        while (true)
        {
            if (_facts.TryGetValue(udonName, out var existing))
            {
                if (!existing.Equals(requested))
                    throw new InvalidOperationException(
                        $"Udon type name '{udonName}' has conflicting facts: existing "
                        + $"enum={existing.IsEnum}, valueType={existing.IsValueType}; requested "
                        + $"enum={requested.IsEnum}, valueType={requested.IsValueType} for "
                        + $"'{source}'.");
                return;
            }
            if (_facts.TryAdd(udonName, requested)) return;
        }
    }

    internal void RecordForTest(string udonName, bool isEnum, bool isValueType)
        => _facts[udonName] = new TypeFact(isEnum, isValueType);

    internal void RecordAssignableForTest(string from, string to)
        => RecordAssignable(from, to);

    /// <summary>FACT: is the name an enum tag (Int32-compatible)? true/false when known, null when the
    /// neither authoritative boundary supplied it — an unknown name is exactly what the relaxed check
    /// would otherwise have to guess about.</summary>
    public bool? IsEnumFact(string udonName)
    {
        if (StructuralIsReference(udonName) != null) return false; // primitives/arrays/fold tags are never enums
        return _facts.TryGetValue(udonName, out var f) ? f.IsEnum : (bool?)null;
    }

    /// <summary>FACT: is the name's runtime representation a reference? Structural rules first (an Udon
    /// "…Array" IS a .NET array; the fold tags are object[]/component references by construction), then
    /// the registry (an SDK struct like UnityEngineBounds is a value type even though the relaxed
    /// prefix-list heuristic calls it a reference).</summary>
    public bool? IsReferenceFact(string udonName)
    {
        var structural = StructuralIsReference(udonName);
        if (structural != null) return structural;
        return _facts.TryGetValue(udonName, out var f) ? !f.IsValueType : (bool?)null;
    }

    /// <summary>FACT: can a value represented by <paramref name="from"/> be read as
    /// <paramref name="to"/> by an ordinary typed extern wrapper? Unlike raw Udon COPY
    /// compatibility this relation is directional. It is collected from CLR and Roslyn
    /// inheritance at the same two minting boundaries as the category facts.</summary>
    public bool? IsAssignableFact(string from, string to)
    {
        if (from == to) return true;
        if (IsKnownUdonBehaviourBase(from, to)) return true;
        if (_assignability.ContainsKey(new AssignabilityFact(from, to))) return true;

        var fromReference = IsReferenceFact(from);
        var toReference = IsReferenceFact(to);
        if (fromReference == null || toReference == null) return null;
        return false;
    }

    void RecordAssignability(string from, ITypeSymbol type)
    {
        if (!_recordedSymbolHierarchy.TryAdd(type, 0)) return;
        for (var current = type.BaseType; current != null; current = current.BaseType)
            RecordAssignable(from, ExternResolver.GetUdonTypeName(current));
        foreach (var implemented in type.AllInterfaces)
            RecordAssignable(from, ExternResolver.GetUdonTypeName(implemented));
    }

    void RecordAssignability(string from, Type type)
    {
        if (!_recordedClrHierarchy.TryAdd(type, 0)) return;
        for (var current = type.BaseType; current != null; current = current.BaseType)
            RecordAssignable(from, ExternResolver.GetUdonTypeName(current));
        foreach (var implemented in type.GetInterfaces())
            RecordAssignable(from, ExternResolver.GetUdonTypeName(implemented));
    }

    void RecordAssignable(string from, string to)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || from == to) return;
        _assignability.TryAdd(new AssignabilityFact(from, to), 0);
    }

    static bool IsKnownUdonBehaviourBase(string from, string to)
    {
        if (from != "VRCUdonCommonInterfacesIUdonEventReceiver"
            && from != "VRCUdonUdonBehaviour")
            return false;
        switch (to)
        {
            case "UnityEngineMonoBehaviour":
            case "UnityEngineBehaviour":
            case "UnityEngineComponent":
            case "UnityEngineObject":
                return true;
            default:
                return false;
        }
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

