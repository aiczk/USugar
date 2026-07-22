using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Hand-enumeration audit 2026-07-17, Tier-1 item ③: IsSdkNamespace existed TWICE with diverging
/// semantics — ExternResolver used display-string StartsWith over {UnityEngine, VRC, TMPro, System}
/// (so a namespace literally named "SystemFoo" or "VRChat" was mis-classified SDK), while EmitPolicy
/// chain-walked EXACT segment names over {System, UnityEngine, VRC, Cinemachine, TMPro, Unity,
/// Microsoft} — so enum classification (ExternResolver.IsUserEnum / GetUdonTypeName's enum fold) and
/// struct classification (TypeClassifier.IsUserStruct) disagreed at the SDK boundary. Owner ruling:
/// ONE predicate, chain-walk exact-name semantics over the union list, homed in ExternResolver;
/// EmitPolicy delegates. These tests pin the unified boundary through the public consumer surfaces.
/// </summary>
public class SdkNamespaceUnificationTests
{
    // One source-declared marker enum + struct per namespace under test. Declaring extra members
    // inside a stub/BCL namespace (System, TMPro, VRC) is legal — namespaces merge — and mirrors
    // exactly why the predicate must namespace-check at all: in tests, SDK types are source stubs,
    // so DeclaringSyntaxReferences alone cannot separate SDK from user.
    const string BoundarySource = @"
namespace System { public enum UsgSysEnum { A } public struct UsgSysStruct { public int v; } }
namespace TMPro { public enum UsgTmpEnum { A } public struct UsgTmpStruct { public int v; } }
namespace VRC.UsgProbe { public enum UsgVrcEnum { A } public struct UsgVrcStruct { public int v; } }
namespace Unity { public enum UEnum { A, B } public struct UStruct { public int v; } }
namespace Cinemachine { public enum CmEnum { A } public struct CmStruct { public int v; } }
namespace Microsoft { public enum MsEnum { A } public struct MsStruct { public int v; } }
namespace SystemFoo { public enum SfEnum { A, B } public struct SfStruct { public int v; } }
namespace VRChat { public enum VcEnum { A } public struct VcStruct { public int v; } }
namespace TMProX { public enum TxEnum { A } public struct TxStruct { public int v; } }
namespace UnityEngineX { public enum UxEnum { A } public struct UxStruct { public int v; } }
namespace Outer.System.Inner { public enum NsEnum { A } public struct NsStruct { public int v; } }
namespace PlainUser { public enum PuEnum { A } public struct PuStruct { public int v; } }
public enum GlobalEnum { A }
public struct GlobalStruct { public int v; }
public class NsBoundaryCarrier
{
    public UnityEngine.KeyCode fKeyCode;
    public UnityEngine.Vector3 fVector3;
    public System.UsgSysEnum eSys;         public System.UsgSysStruct sSys;
    public TMPro.UsgTmpEnum eTmp;          public TMPro.UsgTmpStruct sTmp;
    public VRC.UsgProbe.UsgVrcEnum eVrc;   public VRC.UsgProbe.UsgVrcStruct sVrc;
    public Unity.UEnum eUnity;             public Unity.UStruct sUnity;
    public Cinemachine.CmEnum eCm;         public Cinemachine.CmStruct sCm;
    public Microsoft.MsEnum eMs;           public Microsoft.MsStruct sMs;
    public SystemFoo.SfEnum eSysFoo;       public SystemFoo.SfStruct sSysFoo;
    public VRChat.VcEnum eVrChat;          public VRChat.VcStruct sVrChat;
    public TMProX.TxEnum eTmpX;            public TMProX.TxStruct sTmpX;
    public UnityEngineX.UxEnum eUeX;       public UnityEngineX.UxStruct sUeX;
    public Outer.System.Inner.NsEnum eNested; public Outer.System.Inner.NsStruct sNested;
    public PlainUser.PuEnum ePlain;        public PlainUser.PuStruct sPlain;
    public GlobalEnum eGlobal;             public GlobalStruct sGlobal;
}";

    // ns label, enum field, struct field, SDK verdict. The enum verdict is pinned through
    // ExternResolver.IsUserEnum, the struct verdict through TypeClassifier.IsUserStruct — the two
    // consumers that formerly sat on the two diverging copies. isSdk == !isUser on both.
    static readonly (string Ns, string EnumField, string StructField, bool Sdk)[] BoundaryBattery =
    {
        // Unchanged SDK controls (in both old lists).
        ("System",            "eSys",    "sSys",    true),
        ("TMPro",             "eTmp",    "sTmp",    true),
        ("VRC.UsgProbe",      "eVrc",    "sVrc",    true),  // nested under VRC — chain-walk hits "VRC"
        // Union-list members ExternResolver's old copy was missing (accepted-boundary delta: these
        // source enums/classes are now SDK-tagged for ALL consumers, matching EmitPolicy's verdict).
        ("Unity",             "eUnity",  "sUnity",  true),
        ("Cinemachine",       "eCm",     "sCm",     true),
        ("Microsoft",         "eMs",     "sMs",     true),
        // The StartsWith bug class, one per old prefix (FIX: prefix-extended names are USER).
        ("SystemFoo",         "eSysFoo", "sSysFoo", false),
        ("VRChat",            "eVrChat", "sVrChat", false),
        ("TMProX",            "eTmpX",   "sTmpX",   false),
        ("UnityEngineX",      "eUeX",    "sUeX",    false),
        // Chain-walk semantics: ANY segment named after an SDK root marks the chain SDK (this was
        // always EmitPolicy's verdict; ExternResolver's prefix test said user — unified onto SDK).
        ("Outer.System.Inner", "eNested", "sNested", true),
        // User controls (unchanged on both sides).
        ("PlainUser",         "ePlain",  "sPlain",  false),
        ("<global>",          "eGlobal", "sGlobal", false),
    };

    static System.Collections.Generic.Dictionary<string, ITypeSymbol> BuildBoundaryTypes()
    {
        TestHelper.BuildCompilation(BoundarySource, "NsBoundaryCarrier", out var carrier);
        return carrier.GetMembers().OfType<IFieldSymbol>()
            .ToDictionary(f => f.Name, f => f.Type);
    }

    [Fact]
    public void EnumAndStructClassification_AgreeAtEveryBoundaryRow()
    {
        var types = BuildBoundaryTypes();
        foreach (var (ns, enumField, structField, sdk) in BoundaryBattery)
        {
            var isUserEnum = ExternResolver.IsUserEnum(types[enumField]);
            var isUserStruct = TypeClassifier.IsUserStruct((INamedTypeSymbol)types[structField]);
            Assert.True(isUserEnum == !sdk,
                $"enum boundary drift: namespace '{ns}' pinned {(sdk ? "SDK" : "USER")} but IsUserEnum == {isUserEnum}");
            Assert.True(isUserStruct == !sdk,
                $"struct boundary drift: namespace '{ns}' pinned {(sdk ? "SDK" : "USER")} but IsUserStruct == {isUserStruct}");
        }
    }

    [Fact]
    public void SdkControls_KeepTheirNativeClassification()
    {
        // Real stub SDK types (compiled-in stand-ins for registered Udon types) must stay SDK on
        // both surfaces — the union predicate must not over-reject the genuine registry.
        var types = BuildBoundaryTypes();
        Assert.False(ExternResolver.IsUserEnum(types["fKeyCode"]));
        Assert.Equal("UnityEngineKeyCode", ExternResolver.GetUdonTypeName(types["fKeyCode"]));
        Assert.False(TypeClassifier.IsUserStruct((INamedTypeSymbol)types["fVector3"]));
        Assert.Equal("UnityEngineVector3", ExternResolver.GetUdonTypeName(types["fVector3"]));
    }

    // Shape (a), ACCEPTED BOUNDARY (owner ruling 2026-07-17): a source-declared enum in a namespace
    // literally named 'Unity' now classifies as SDK-tagged for ExternResolver's consumers too (it
    // already did for EmitPolicy's): it keeps its registered-style tag "UnityUEnum" instead of
    // folding to the underlying int, gets NO B67 name synthesis, and an is-test against it is
    // licensed (unique tag). If no such tag exists in the Udon registry, the miss surfaces LOUDLY
    // at extern/type validation (AssertEmittedExternsValid / per-call-site IsExternValid), never
    // silently — the cost of claiming an SDK root namespace as your own.
    [Fact]
    public void UnityNamespaceEnum_ClassifiesSdkTagged_NotUserEnum()
    {
        var types = BuildBoundaryTypes();
        var t = types["eUnity"];
        Assert.False(ExternResolver.IsUserEnum(t));
        Assert.Equal("UnityUEnum", ExternResolver.GetUdonTypeName(t));
        Assert.True(ExternResolver.IsRuntimeDistinguishable(t, null));
    }

    // Shape (b), FIX: a source enum in a namespace named 'SystemFoo' was mis-classified SDK by the
    // old StartsWith("System") test — GetUdonTypeName kept a bogus unregistered "SystemFooSfEnum"
    // tag and IsRuntimeDistinguishable dishonestly said true (the runtime value is a bare int).
    // It now classifies USER: folds to the underlying int tag, is-tests reject honestly, and B67
    // ToString name synthesis applies.
    [Fact]
    public void SystemFooNamespaceEnum_ClassifiesUserEnum_FoldsToUnderlyingInt()
    {
        var types = BuildBoundaryTypes();
        var t = types["eSysFoo"];
        Assert.True(ExternResolver.IsUserEnum(t));
        Assert.Equal("SystemInt32", ExternResolver.GetUdonTypeName(t));
        Assert.False(ExternResolver.IsRuntimeDistinguishable(t, null));
    }

    // Shape (b) end-to-end: the SystemFoo enum compiles through the full emitter as a user enum —
    // the field's heap slot is the underlying SystemInt32 (never the unregistered "SystemFooMode"
    // type the old classification minted) and ToString routes through the synthesized B67 helper.
    [Fact]
    public void SystemFooEnum_EndToEnd_EmitsUnderlyingIntAndSynthesizedToString()
    {
        var uasm = TestHelper.CompileToUasm(@"
namespace SystemFoo { public enum Mode { Off, On } }
public class NsFixBehaviour : UdonSharp.UdonSharpBehaviour
{
    public SystemFoo.Mode mode;
    public string Show() { return mode.ToString(); }
}", "NsFixBehaviour");
        Assert.DoesNotContain("SystemFooMode", uasm);
        Assert.Contains("mode: %SystemInt32", uasm);
        Assert.Contains("__enumstr_NSystemFoo_TMode", uasm);
    }
}
