using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Totality census for the extern layer's value/branch-producing silent defaults (hand-enumeration
/// audit 2026-07-17, Tier-1 item ②). Three hand-kept enumerations produce a VALUE (or license a
/// BRANCH) on a miss instead of failing. These do not pass through the mandatory ABI catalog:
///
///  1. EmitPolicy.ParseConstValue — the default arm's long/ulong TryParse-fail path returned null
///     silently (the be04dd6 SystemDecimal archetype's surviving sibling: a null constant reads as 0
///     at apply time). Now a loud throw; the census round-trips every constable primitive the
///     compiler mints and classifies the full minted-name domain.
///  2. ExternResolver.ConvertMethodNames returns null out-of-domain while callers gate on
///     IsConvertibleNumericType — two hand-synced copies of one domain. The census asserts domain
///     EQUALITY in both directions (the null return itself stays: callers legitimately probe).
///  3. Runtime type-test capability and storage used to be separate producers. The census now
///     consumes one UdonTypeLowering per family, recomputes non-injective storage tags, and pins
///     both the representation and the verdict carried by that same decision.
/// </summary>
public class ExternSilentDefaultCensusTests
{
    // ── Leg 1: ParseConstValue totality ──

    // Every Udon primitive type name with a compile-time-constable value, with a representative
    // raw constant (the invariant string form OperatorHandler/UasmEmitter feed in) and its parsed
    // CLR value. Keys must cover GetSpecialTypeName's minted domain minus NonConstableMintedNames,
    // plus the known extras ParseConstValue handles beyond SpecialType (SystemType).
    static readonly Dictionary<string, (string Raw, object Expected)> ConstableCensus = new()
    {
        ["SystemInt32"] = ("42", 42),
        ["SystemUInt32"] = ("42", 42u),
        ["SystemInt64"] = ("9223372036854775807", long.MaxValue),
        ["SystemUInt64"] = ("18446744073709551615", ulong.MaxValue),
        ["SystemInt16"] = ("-5", (short)-5),
        ["SystemUInt16"] = ("5", (ushort)5),
        ["SystemSByte"] = ("-3", (sbyte)-3),
        ["SystemByte"] = ("200", (byte)200),
        ["SystemSingle"] = ("1.5", 1.5f),
        ["SystemDouble"] = ("2.25", 2.25),
        ["SystemDecimal"] = ("-12.5", -12.5m),
        ["SystemBoolean"] = ("True", true),
        ["SystemString"] = ("hello", "hello"),
        ["SystemChar"] = ("A", 'A'),
        // Beyond the SpecialType domain: a typeof()/is-test type token — the raw Udon type name is
        // carried as a string and resolved to a CLR Type at apply time.
        ["SystemType"] = ("UnityEngineVector3", "UnityEngineVector3"),
    };

    // GetSpecialTypeName outputs that can never reach ParseConstValue with a non-null constant:
    // C#'s only compile-time constant types are the numeric primitives, bool, char, string, enum
    // (folded to its underlying primitive tag before this point) and decimal — everything else
    // constant-folds only to the literal `null`, which the early `value == "null"` return handles
    // before the switch.
    static readonly Dictionary<string, string> NonConstableMintedNames = new()
    {
        ["SystemObject"] = "only the null constant exists; handled by the early value == \"null\" return",
        ["SystemVoid"] = "no values of type void",
        ["SystemArray"] = "no array constants in C# (only null)",
        ["SystemDateTime"] = "no DateTime constants in C# (only default/new, which are not constants)",
        ["SystemIntPtr"] = "no IntPtr constants in C# (only null/zero via runtime expressions)",
        ["SystemUIntPtr"] = "no UIntPtr constants in C#",
        ["SystemEnum"] = "abstract base; a concrete enum constant folds to its underlying primitive tag",
        ["SystemValueType"] = "abstract base; no constants",
        ["SystemDelegate"] = "reference type; only null",
        ["SystemMulticastDelegate"] = "reference type; only null",
        ["SystemCollectionsIEnumerable"] = "interface; only null",
        ["SystemIDisposable"] = "interface; only null",
        ["SystemNullable"] = "boxed-object emulation; a constant T? is either null or folds to T's tag",
    };

    [Fact]
    public void ParseConstValue_RoundTripsEveryConstablePrimitive()
    {
        foreach (var (udonType, (raw, expected)) in ConstableCensus)
        {
            var parsed = EmitPolicy.ParseConstValue(udonType, raw);
            Assert.True(parsed != null, $"ParseConstValue({udonType}, \"{raw}\") returned null — missing arm");
            Assert.Equal(expected.GetType(), parsed.GetType());
            Assert.Equal(expected, parsed);
        }
    }

    [Fact]
    public void ParseConstValue_HexLiterals_ParseThroughTheIntArms()
    {
        Assert.Equal(42, EmitPolicy.ParseConstValue("SystemInt32", "0x2A"));
        Assert.Equal(42u, EmitPolicy.ParseConstValue("SystemUInt32", "0x2A"));
    }

    [Fact]
    public void ParseConstValue_NullLiteral_ReturnsNullForAnyType()
    {
        Assert.Null(EmitPolicy.ParseConstValue("SystemObject", "null"));
        Assert.Null(EmitPolicy.ParseConstValue("SystemString", "null"));
    }

    // The default arm's legitimate use: an SDK enum constant (KeyCode.Space, …) reaches
    // ParseConstValue under its registered Udon type name with its integral value — the arm
    // parses it as int (or long/ulong past the int range). Pinned so the loud-throw change
    // below replaces ONLY the silent-null path.
    [Fact]
    public void ParseConstValue_SdkEnumConstant_RidesTheIntegerDefaultArm()
    {
        Assert.Equal(32, EmitPolicy.ParseConstValue("UnityEngineKeyCode", "32"));
        Assert.Equal(4294967296L, EmitPolicy.ParseConstValue("UnityEngineKeyCode", "4294967296"));
        Assert.Equal(ulong.MaxValue, EmitPolicy.ParseConstValue("UnityEngineKeyCode", "18446744073709551615"));
    }

    [Fact]
    public void ParseConstValue_UnparseableDefaultArm_ThrowsNamingTypeAndRawValue()
    {
        // The be04dd6 archetype's surviving sibling: before the SystemDecimal arm existed, exactly
        // this shape (a non-integral raw string under a type with no arm) returned null and read
        // back as 0. The path is now loud.
        var ex = Assert.Throws<NotSupportedException>(
            () => EmitPolicy.ParseConstValue("SystemTimeSpan", "1.5:00:00"));
        Assert.Contains("SystemTimeSpan", ex.Message);
        Assert.Contains("1.5:00:00", ex.Message);
    }

    [Fact]
    public void ParseConstValue_Census_ClassifiesEveryMintedPrimitiveName()
    {
        // The domain is derived from the minting source of truth itself: every SpecialType
        // GetSpecialTypeName has an arm for, sanitized exactly as GetUdonTypeName's terminal
        // branch does. A new arm there must land in exactly one of the two census buckets.
        var minted = new HashSet<string>();
        foreach (var st in Enum.GetValues(typeof(SpecialType)).Cast<SpecialType>().Distinct())
        {
            if (st == SpecialType.None) continue;
            string name;
            try { name = ExternResolver.GetSpecialTypeName(st); }
            catch (NotSupportedException) { continue; } // not a type the compiler mints
            minted.Add(ExternResolver.SanitizeTypeName(name));
        }

        var constable = ConstableCensus.Keys.ToHashSet();
        var nonConstable = NonConstableMintedNames.Keys.ToHashSet();

        var overlap = constable.Intersect(nonConstable).ToList();
        Assert.True(overlap.Count == 0, "census buckets overlap: " + string.Join(", ", overlap));

        var unclassified = minted.Except(constable).Except(nonConstable).ToList();
        Assert.True(unclassified.Count == 0,
            "GetSpecialTypeName mints name(s) the ParseConstValue census does not classify — add each "
            + "to ConstableCensus (with a round-trip row) or to NonConstableMintedNames (with a reason): "
            + string.Join(", ", unclassified));

        var staleNonConstable = nonConstable.Except(minted).ToList();
        Assert.True(staleNonConstable.Count == 0,
            "NonConstableMintedNames lists name(s) GetSpecialTypeName no longer mints: "
            + string.Join(", ", staleNonConstable));

        // Constable names not minted by GetSpecialTypeName must be exactly the known extras.
        var extras = constable.Except(minted).ToList();
        Assert.Equal(new[] { "SystemType" }, extras);
    }

    // ── Leg 2: Convert-method domain equality ──

    [Fact]
    public void ConvertMethodDomain_EqualsIsConvertibleNumericType_BothDirections()
    {
        // ConvertMethodNames' key set and IsConvertibleNumericType's accepted set are two hand-kept
        // copies of one domain: callers gate a SystemConvert extern emission on the predicate, then
        // build the extern from the table. Probe every SpecialType through both public surfaces —
        // a key without predicate acceptance OR an accepted type without a method name is drift.
        var comp = CSharpCompilation.Create("ConvertDomainCensus", references: TestHelper.StandardRefs);
        var accepted = new HashSet<SpecialType>();
        foreach (var st in Enum.GetValues(typeof(SpecialType)).Cast<SpecialType>().Distinct())
        {
            if (st == SpecialType.None) continue;
            var sym = comp.GetSpecialType(st);
            var hasConvertName = ExternResolver.GetConvertMethodName(sym) != null;
            var isConvertible = ExternResolver.IsConvertibleNumericType(sym);
            Assert.True(hasConvertName == isConvertible,
                $"convert-domain drift at {st}: GetConvertMethodName {(hasConvertName ? "present" : "null")} "
                + $"but IsConvertibleNumericType == {isConvertible}");
            if (isConvertible) accepted.Add(st);
        }

        // Pin the domain itself so both copies cannot drift in lockstep unreviewed.
        var expected = new HashSet<SpecialType>
        {
            SpecialType.System_Byte, SpecialType.System_SByte,
            SpecialType.System_Int16, SpecialType.System_UInt16,
            SpecialType.System_Int32, SpecialType.System_UInt32,
            SpecialType.System_Int64, SpecialType.System_UInt64,
            SpecialType.System_Single, SpecialType.System_Double,
            SpecialType.System_Char, SpecialType.System_Decimal,
        };
        Assert.Equal(expected.OrderBy(s => s), accepted.OrderBy(s => s));
    }

    // ── Leg 3: fold-tag census for IsRuntimeDistinguishable ──

    const string ObjArrayTag = "SystemObjectArray";
    const string EventReceiverTag = "VRCUdonCommonInterfacesIUdonEventReceiver";
    const string ComponentArrayTag = "UnityEngineComponentArray";

    // Representative battery: one row per input FAMILY the fold collapses (or keeps unique), with
    // the expected Udon tag and the expected IsRuntimeDistinguishable verdict pinned per row.
    // family, field on the carrier class, expected tag, expected verdict.
    static readonly (string Family, string Field, string Tag, bool Distinguishable)[] FoldBattery =
    {
        ("sdk-delegate",      "fFunc",      ObjArrayTag, false),
        ("user-delegate",     "fUserDlg",   ObjArrayTag, false),
        ("user-struct",       "fStruct",    ObjArrayTag, false),
        ("tuple",             "fTuple",     ObjArrayTag, false),
        ("user-class-v1",     "fV1",        ObjArrayTag, false),
        ("delegate-array",    "fFuncArr",   ObjArrayTag, false),
        ("struct-array",      "fStructArr", ObjArrayTag, false),
        ("tuple-array",       "fTupleArr",  ObjArrayTag, false),
        ("object-array",      "fObjArr",    ObjArrayTag, false),
        ("jagged-array",      "fJagged",    ObjArrayTag, false),
        ("ndim-array",        "fNdim",      ObjArrayTag, false),
        ("behaviour-base",    "fUsb",       EventReceiverTag, false),
        ("behaviour-derived", "fDerived",   EventReceiverTag, false),
        ("udon-behaviour",    "fUdon",      EventReceiverTag, false),
        ("user-interface",    "fIface",     EventReceiverTag, false),
        ("behaviour-array",   "fDerivedArr", ComponentArrayTag, false),
        ("interface-array",   "fIfaceArr",  ComponentArrayTag, false),
        // Branch-owned collapses (explicit arms ABOVE the 3-tag list in the predicate):
        ("nullable",          "fNullable",  "SystemObject", false),
        // bare object is the documented fold-TARGET exception: its tag is shared with Nullable<T>,
        // yet `v is object` is the one universally answerable test, so the predicate says TRUE.
        ("bare-object",       "fObj",       "SystemObject", true),
        ("user-enum",         "fEnum",      "SystemInt32", false),
        ("user-enum-array",   "fEnumArr",   "SystemInt32Array", false),
        // int is the user-enum fold TARGET: the predicate accepts it (an erased enum value matching
        // `is int` is the accepted erasure asymmetry; rejecting int would reject every honest use).
        ("sdk-int",           "fInt",       "SystemInt32", true),
        // Uniquely-tagged controls — the predicate must keep saying TRUE (no over-rejection drift).
        ("sdk-string",        "fStr",       "SystemString", true),
        ("sdk-vector3",       "fVec",       "UnityEngineVector3", true),
        ("sdk-gameobject",    "fGo",        "UnityEngineGameObject", true),
        ("sdk-enum",          "fSdkEnum",   "UnityEngineKeyCode", true),
        ("int-array",         "fIntArr",    "SystemInt32Array", true),
    };

    const string FoldCarrierSource = @"
public delegate int CensusDlg(int x);
public struct CensusStruct { public int v; }
public class CensusV1 { public int v; }
public class CensusDerived : UdonSharp.UdonSharpBehaviour { }
public interface ICensusIface { void M(); }
public enum CensusEnum { A, B }
public class CensusCarrier
{
    public System.Func<int> fFunc;
    public CensusDlg fUserDlg;
    public CensusStruct fStruct;
    public (int, int) fTuple;
    public CensusV1 fV1;
    public System.Func<int>[] fFuncArr;
    public CensusStruct[] fStructArr;
    public (int, int)[] fTupleArr;
    public object[] fObjArr;
    public int[][] fJagged;
    public int[,] fNdim;
    public UdonSharp.UdonSharpBehaviour fUsb;
    public CensusDerived fDerived;
    public VRC.Udon.UdonBehaviour fUdon;
    public ICensusIface fIface;
    public CensusDerived[] fDerivedArr;
    public ICensusIface[] fIfaceArr;
    public int? fNullable;
    public object fObj;
    public CensusEnum fEnum;
    public CensusEnum[] fEnumArr;
    public int fInt;
    public string fStr;
    public UnityEngine.Vector3 fVec;
    public UnityEngine.GameObject fGo;
    public UnityEngine.KeyCode fSdkEnum;
    public int[] fIntArr;
}";

    static (Dictionary<string, ITypeSymbol> Types,
        CompilationSession Session) BuildFoldBattery()
    {
        var compilation = TestHelper.BuildCompilation(
            FoldCarrierSource, "CensusCarrier", out var carrier);
        var types = carrier.GetMembers().OfType<IFieldSymbol>()
            .ToDictionary(f => f.Name, f => f.Type);
        return (types,
            new CompilationSession(compilation, TestHelper.RegistryFacts));
    }

    [Fact]
    public void FoldCensus_EveryBatteryRow_MatchesPinnedTagAndVerdict()
    {
        var (types, session) = BuildFoldBattery();
        foreach (var (family, field, tag, distinguishable) in FoldBattery)
        {
            var lowering = session.Types.Describe(types[field]);
            Assert.True(tag == lowering.Storage.Name,
                $"fold drift: family '{family}' now folds to "
                + $"'{lowering.Storage}', pinned '{tag}'");
            Assert.True(distinguishable == lowering.IsRuntimeDistinguishable,
                $"runtime identity drift: family '{family}' produced "
                + $"'{lowering.RuntimeTypeTest}'");
        }
    }

    [Fact]
    public void FoldCensus_NonInjectiveTags_AreExactlyThePredicateKnownCollapses()
    {
        // Recompute non-injectivity from the battery itself (not from the pinned Tag column): a tag
        // produced by 2+ distinct input families is a runtime collapse the predicate MUST know.
        // A new fold output that lands on a battery family shows up here as an unexpected
        // non-injective tag. Its runtime verdict comes from the same lowering object, so there is
        // no separate predicate list to keep synchronized.
        var (types, session) = BuildFoldBattery();
        var familiesByTag = new Dictionary<string, HashSet<string>>();
        foreach (var (family, field, _, _) in FoldBattery)
        {
            var tag = session.Types.Describe(types[field]).Storage.Name;
            if (!familiesByTag.TryGetValue(tag, out var set))
                familiesByTag[tag] = set = new HashSet<string>();
            set.Add(family);
        }

        var nonInjective = familiesByTag.Where(kv => kv.Value.Count >= 2)
            .Select(kv => kv.Key).OrderBy(t => t).ToList();

        var expectedNonInjective = new List<string>
        {
            ObjArrayTag,
            EventReceiverTag,
            ComponentArrayTag,
            "SystemObject",
            "SystemInt32",
            "SystemInt32Array",
        }.OrderBy(t => t).ToList();

        Assert.Equal(expectedNonInjective, nonInjective);
    }
}
