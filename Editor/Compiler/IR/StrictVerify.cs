using System;
using System.Collections.Concurrent;
using System.IO;
using Microsoft.CodeAnalysis;

/// <summary>Phase-B strict-verification shadow (2026-07-14): CoreVerify's relaxed type checks contain two
/// GUESSES — "an unrecognized type name may be an enum" (Int32 interop allowed) and "a non-primitive name
/// is a reference type" (COPY compat allowed) — that can wave real type mismatches through to the VM
/// (B72 family). This shadow changes NO verification behavior: it collects type FACTS at the single choke
/// where Udon type names are minted (ExternResolver.GetUdonTypeName) and logs every relaxed-check pass
/// that depended on a guess about an unregistered or fact-contradicted name. The ledger feeds the Phase-D
/// polarity flip (strict-with-declared-relaxations); until then this is measurement only.
/// Phase-D stage-1 measurement (2026-07-16): the full tracked suite plus the full local harness produced
/// ZERO production-path entries — every relaxed-arm pass in production is fact-backed, so the declared
/// relaxation set is intentionally just the structural rules below plus the minted registry.</summary>
public static class UdonTypeFacts
{
    public readonly struct TypeFact
    {
        public readonly bool IsEnum;
        public readonly bool IsValueType;
        public TypeFact(bool isEnum, bool isValueType) { IsEnum = isEnum; IsValueType = isValueType; }
    }

    // Values are deterministic per name (SDK name ↔ symbol is 1:1 for every name that reaches the
    // registry), so concurrent TryAdd races during Phase-2 parallel emit are benign.
    static readonly ConcurrentDictionary<string, TypeFact> _facts = new();

    /// <summary>Record the minted name's facts. Names covered by a STRUCTURAL rule (primitives, arrays,
    /// the fold tags) are skipped: a folded name's runtime representation is fixed by the fold itself,
    /// not by whichever source symbol happened to mint it first (a struct folding to SystemObjectArray
    /// must not poison the registry with IsValueType=true).</summary>
    public static void Record(string udonName, ITypeSymbol symbol)
    {
        if (string.IsNullOrEmpty(udonName) || symbol == null) return;
        if (StructuralIsReference(udonName) != null) return;
        _facts.TryAdd(udonName, new TypeFact(symbol.TypeKind == TypeKind.Enum, symbol.IsValueType));
    }

    internal static void RecordForTest(string udonName, bool isEnum, bool isValueType)
        => _facts[udonName] = new TypeFact(isEnum, isValueType);

    /// <summary>FACT: is the name an enum tag (Int32-compatible)? true/false when known, null when the
    /// name never passed the minting choke — an unknown name is exactly what the relaxed check guesses
    /// about.</summary>
    public static bool? IsEnumFact(string udonName)
    {
        if (StructuralIsReference(udonName) != null) return false; // primitives/arrays/fold tags are never enums
        return _facts.TryGetValue(udonName, out var f) ? f.IsEnum : (bool?)null;
    }

    /// <summary>FACT: is the name's runtime representation a reference? Structural rules first (an Udon
    /// "…Array" IS a .NET array; the fold tags are object[]/component references by construction), then
    /// the registry (an SDK struct like UnityEngineBounds is a value type even though the relaxed
    /// prefix-list heuristic calls it a reference).</summary>
    public static bool? IsReferenceFact(string udonName)
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
                return true;
        }
        if (name != null && name.EndsWith("Array")) return true;
        return null;
    }
}

/// <summary>Append-only ledger of guess-dependent relaxed-check passes. In-memory always (drainable by
/// tests); mirrored to a file when <c>USUGAR_STRICT_SHADOW</c> names a path (set by the owner's test
/// invocations during the Phase-C measurement window). Never throws, never alters compilation.</summary>
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

    /// <summary>The relaxed ref-copy arm passed for (expected, actual): log unless BOTH names are
    /// fact-references.</summary>
    public static void AuditReferenceCopy(string expected, string actual, string context, string funcName)
    {
        var e = UdonTypeFacts.IsReferenceFact(expected);
        var a = UdonTypeFacts.IsReferenceFact(actual);
        if (e == true && a == true) return;
        RecordGuess("ref-copy", expected, actual, context, funcName,
            e == null ? "no-fact:" + expected
            : a == null ? "no-fact:" + actual
            : e == false ? "fact-contradicts:" + expected + " is a value type"
            : "fact-contradicts:" + actual + " is a value type");
    }

    /// <summary>The relaxed enum↔Int32 arm passed for `name`: log unless the name is a fact-enum.</summary>
    public static void AuditEnumInterop(string name, string context, string funcName)
    {
        var isEnum = UdonTypeFacts.IsEnumFact(name);
        if (isEnum == true) return;
        RecordGuess("enum-int32", "SystemInt32", name, context, funcName,
            isEnum == null ? "no-fact:" + name : "fact-contradicts:" + name + " is not an enum");
    }

    internal static string[] DrainForTest()
    {
        var outp = new System.Collections.Generic.List<string>();
        while (_entries.TryDequeue(out var e)) outp.Add(e);
        return outp.ToArray();
    }
}
