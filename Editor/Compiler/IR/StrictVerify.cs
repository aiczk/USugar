using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Microsoft.CodeAnalysis;

/// <summary>Type FACTS recorded at the single choke where Udon type names are minted
/// (ExternResolver.GetUdonTypeName), plus structural rules for names whose runtime representation is
/// fixed by construction. History: Phase-B (2026-07-14) collected these as a measurement shadow of
/// CoreVerify's two relaxed-arm GUESSES ("unknown name may be an enum", "non-primitive name is a
/// reference"); Phase-D stage 1 (2026-07-16) measured ZERO production-path guess-dependent passes across
/// the full suite plus the full local harness; Phase-D stage 2 (2026-07-16) ENFORCED the flip — the
/// heuristics are deleted and <see cref="DeclaredRelaxations"/>, backed by these facts, is now the only
/// way two slot/COPY types may legally differ (CoreVerify and the test-side UasmValidator COPY check).</summary>
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

    // Values are deterministic per name (SDK name ↔ symbol is 1:1 for every name that reaches the
    // registry), so concurrent TryAdd races during Phase-2 parallel emit are benign.
    readonly ConcurrentDictionary<string, TypeFact> _facts = new(StringComparer.Ordinal);

    /// <summary>Record the minted name's facts. Names covered by a STRUCTURAL rule (primitives, arrays,
    /// the fold tags) are skipped: a folded name's runtime representation is fixed by the fold itself,
    /// not by whichever source symbol happened to mint it first (a struct folding to SystemObjectArray
    /// must not poison the registry with IsValueType=true).</summary>
    public void Record(string udonName, ITypeSymbol symbol)
    {
        if (string.IsNullOrEmpty(udonName) || symbol == null) return;
        if (StructuralIsReference(udonName) != null) return;
        var requested = new TypeFact(symbol.TypeKind == TypeKind.Enum, symbol.IsValueType);
        while (true)
        {
            if (_facts.TryGetValue(udonName, out var existing))
            {
                if (!existing.Equals(requested))
                    throw new InvalidOperationException(
                        $"Udon type name '{udonName}' has conflicting facts: existing "
                        + $"enum={existing.IsEnum}, valueType={existing.IsValueType}; requested "
                        + $"enum={requested.IsEnum}, valueType={requested.IsValueType} for "
                        + $"'{symbol.ToDisplayString()}'.");
                return;
            }
            if (_facts.TryAdd(udonName, requested)) return;
        }
    }

    internal void RecordForTest(string udonName, bool isEnum, bool isValueType)
        => _facts[udonName] = new TypeFact(isEnum, isValueType);

    /// <summary>FACT: is the name an enum tag (Int32-compatible)? true/false when known, null when the
    /// name never passed the minting choke — an unknown name is exactly what the relaxed check guesses
    /// about.</summary>
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

/// <summary>Routes the type-name minting choke into the registry owned by the active compilation.
/// Async-local scoping keeps parallel class emits isolated while avoiding a registry parameter on every
/// recursive type-name helper.</summary>
public static class UdonTypeFacts
{
    static readonly AsyncLocal<UdonTypeFactRegistry> _current = new();

    public static IDisposable RecordInto(UdonTypeFactRegistry registry)
    {
        if (registry == null) throw new ArgumentNullException(nameof(registry));
        var previous = _current.Value;
        _current.Value = registry;
        return new RecordingScope(previous);
    }

    public static void Record(string udonName, ITypeSymbol symbol) => _current.Value?.Record(udonName, symbol);

    sealed class RecordingScope : IDisposable
    {
        readonly UdonTypeFactRegistry _previous;
        bool _disposed;

        public RecordingScope(UdonTypeFactRegistry previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _current.Value = _previous;
            _disposed = true;
        }
    }
}

/// <summary>Phase-D declared-relaxation rules: the single predicate deciding when two Udon slot/COPY
/// types may legally differ — shared by CoreVerify (structured-IR slot checks) and the test-side
/// UasmValidator COPY check (B72 axis), so the relaxation table exists exactly once. Declared:
/// (1) SystemObject wildcard — Udon heap slots are dynamically typed; (2) Nullable erasure — NullableAbi
/// boxes Nullable&lt;T&gt; as object-or-boxed-T; (3) enum↔Int32 — ONLY for names with a recorded enum
/// fact (Udon stores enums as their underlying Int32); (4) reference COPY — ONLY when BOTH names are
/// fact references (a reference COPY copies a heap address; the VM enforces no type tag). Stage-1
/// measured the remaining declared table EMPTY (zero production guess-dependent passes), so anything
/// else — including a name with no minted fact — is incompatible, loudly.</summary>
public static class DeclaredRelaxations
{
    /// <summary>Null when the pair is compatible; otherwise the reason, naming the missing fact when
    /// the failure is an unminted name (a no-fact name at verify time never passed the minting choke
    /// of the same compile — itself suspicious).</summary>
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
        $"no fact recorded for '{name}' (the name never passed ExternResolver.GetUdonTypeName's minting"
        + " choke, so no declared relaxation can vouch for it)";

    static string Describe(string name, bool? isRef) =>
        $"'{name}' is a fact {(isRef == true ? "reference" : "value type")}";
}

/// <summary>Append-only ledger of guess-dependent relaxed-check passes. In-memory always (drainable by
/// tests); mirrored to a file when <c>USUGAR_STRICT_SHADOW</c> names a path. The Phase-D flip deleted
/// its CoreVerify audit hooks (the arms now enforce via <see cref="DeclaredRelaxations"/>); the ledger
/// stays as the shared instrument for future relaxed-arm measurements. Never throws, never alters
/// compilation.</summary>
public static class StrictVerifyLedger
{
    static readonly ConcurrentQueue<string> _entries = new();
    static readonly object _flushLock = new();
    static readonly string _filePath = Environment.GetEnvironmentVariable("USUGAR_STRICT_SHADOW");

    /// <summary>Func-name prefix reserved for the shadow's own self-checks (StrictVerifyShadowTests):
    /// they prove the logging path via the in-memory queue, so mirroring them would put permanent
    /// non-measurement noise in a measurement window whose goal state is zero entries.</summary>
    internal const string SelfTestFuncPrefix = "ssv_";

    public static void RecordGuess(string arm, string expected, string actual, string context,
        string funcName, string reason)
    {
        var line = "{\"arm\":\"" + arm + "\",\"expected\":\"" + expected + "\",\"actual\":\"" + actual
            + "\",\"context\":\"" + context + "\",\"func\":\"" + funcName + "\",\"reason\":\"" + reason + "\"}";
        _entries.Enqueue(line);
        if (string.IsNullOrEmpty(_filePath)
            || funcName?.StartsWith(SelfTestFuncPrefix, StringComparison.Ordinal) == true) return;
        try { lock (_flushLock) File.AppendAllText(_filePath, line + "\n"); }
        catch (IOException) { /* measurement must never break a compile */ }
    }

    internal static string[] DrainForTest()
    {
        var outp = new System.Collections.Generic.List<string>();
        while (_entries.TryDequeue(out var e)) outp.Add(e);
        return outp.ToArray();
    }
}
