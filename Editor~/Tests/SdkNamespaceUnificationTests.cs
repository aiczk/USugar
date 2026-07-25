using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Hand-enumeration audit 2026-07-17, Tier-1 item ③: IsSdkNamespace existed TWICE with diverging
/// semantics — ExternResolver used display-string StartsWith over {UnityEngine, VRC, TMPro, System}
/// (so a namespace literally named "SystemFoo" or "VRChat" was mis-classified SDK), while EmitPolicy
/// chain-walked EXACT segment names over {System, UnityEngine, VRC, Cinemachine, TMPro, Unity,
/// Microsoft}. The unified namespace predicate remains the authority for source aggregate
/// classification. Enum storage has a stronger authority: membership in the installed Udon type
/// registry. These tests keep those two contracts separate so a namespace heuristic cannot again
/// masquerade as evidence that an enum has a runtime tag.
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

    // Namespace classification still governs source structs. Every marker enum in this battery is
    // deliberately absent from the Udon type registry and therefore folds independently of this
    // namespace verdict.
    static readonly (string Ns, string EnumField, string StructField, bool StructSdk)[] BoundaryBattery =
    {
        // Unchanged SDK controls (in both old lists).
        ("System",            "eSys",    "sSys",    true),
        ("TMPro",             "eTmp",    "sTmp",    true),
        ("VRC.UsgProbe",      "eVrc",    "sVrc",    true),  // nested under VRC — chain-walk hits "VRC"
        // Union-list members ExternResolver's old copy was missing.
        ("Unity",             "eUnity",  "sUnity",  true),
        ("Cinemachine",       "eCm",     "sCm",     true),
        ("Microsoft",         "eMs",     "sMs",     true),
        // The StartsWith bug class, one per old prefix (FIX: prefix-extended names are USER).
        ("SystemFoo",         "eSysFoo", "sSysFoo", false),
        ("VRChat",            "eVrChat", "sVrChat", false),
        ("TMProX",            "eTmpX",   "sTmpX",   false),
        ("UnityEngineX",      "eUeX",    "sUeX",    false),
        // Chain-walk semantics: ANY segment named after an SDK root marks the chain SDK (this was
        // always EmitPolicy's verdict; ExternResolver's prefix test said user).
        ("Outer.System.Inner", "eNested", "sNested", true),
        // User controls (unchanged on both sides).
        ("PlainUser",         "ePlain",  "sPlain",  false),
        ("<global>",          "eGlobal", "sGlobal", false),
    };

    static (System.Collections.Generic.Dictionary<string, ITypeSymbol> Types,
        CompilationSession Session) BuildBoundary()
    {
        var compilation = TestHelper.BuildCompilation(
            BoundarySource, "NsBoundaryCarrier", out var carrier);
        var types = carrier.GetMembers().OfType<IFieldSymbol>()
            .ToDictionary(f => f.Name, f => f.Type);
        return (types, new CompilationSession(compilation, TestHelper.RegistryFacts));
    }

    [Fact]
    public void StructClassificationUsesTheExactSegmentNamespaceBoundary()
    {
        var (types, _) = BuildBoundary();
        foreach (var (ns, _, structField, sdk) in BoundaryBattery)
        {
            var isUserStruct = TypeClassifier.IsUserStruct((INamedTypeSymbol)types[structField]);
            Assert.True(isUserStruct == !sdk,
                $"struct boundary drift: namespace '{ns}' pinned {(sdk ? "SDK" : "USER")} but IsUserStruct == {isUserStruct}");
        }
    }

    [Fact]
    public void SourceEnumsWithoutRegisteredTagsFoldRegardlessOfNamespace()
    {
        var (types, session) = BuildBoundary();
        foreach (var (ns, enumField, _, _) in BoundaryBattery)
        {
            var type = types[enumField];
            var exactName = ExternResolver.GetExactUdonTypeName(type);
            Assert.False(session.Types.IsRegisteredUdonTypeName(exactName),
                $"test marker unexpectedly entered the Udon registry: namespace '{ns}', type '{exactName}'");
            Assert.True(session.Types.IsUserEnum(type),
                $"unregistered enum did not fold: namespace '{ns}', type '{exactName}'");
            Assert.Equal("SystemInt32", session.Types.GetUdonTypeName(type));
            Assert.False(session.Types.IsRuntimeDistinguishable(type));
        }
    }

    [Fact]
    public void RegisteredSdkControlsKeepTheirNativeClassification()
    {
        var (types, session) = BuildBoundary();
        Assert.True(session.Types.IsRegisteredUdonTypeName("UnityEngineKeyCode"));
        Assert.False(session.Types.IsUserEnum(types["fKeyCode"]));
        Assert.Equal("UnityEngineKeyCode", session.Types.GetUdonTypeName(types["fKeyCode"]));
        Assert.True(session.Types.IsRuntimeDistinguishable(types["fKeyCode"]));
        Assert.False(TypeClassifier.IsUserStruct((INamedTypeSymbol)types["fVector3"]));
        Assert.Equal("UnityEngineVector3", session.Types.GetUdonTypeName(types["fVector3"]));
    }

    [Fact]
    public void UnityNamespaceAloneDoesNotGrantAnEnumRuntimeIdentity()
    {
        var (types, session) = BuildBoundary();
        var t = types["eUnity"];
        Assert.False(session.Types.IsRegisteredUdonTypeName("UnityUEnum"));
        Assert.True(session.Types.IsUserEnum(t));
        Assert.Equal("SystemInt32", session.Types.GetUdonTypeName(t));
        Assert.False(session.Types.IsRuntimeDistinguishable(t));
    }

    [Fact]
    public void PrefixExtendedNamespaceEnumAlsoUsesRegistryAuthority()
    {
        var (types, session) = BuildBoundary();
        var t = types["eSysFoo"];
        Assert.True(session.Types.IsUserEnum(t));
        Assert.Equal("SystemInt32", session.Types.GetUdonTypeName(t));
        Assert.False(session.Types.IsRuntimeDistinguishable(t));
    }

    [Fact]
    public void UnregisteredEnumsInSdkAndPrefixLikeNamespacesFoldEndToEnd()
    {
        var uasm = TestHelper.CompileToUasm(@"
namespace Unity { public enum Mode { Off, On } }
namespace SystemFoo { public enum Mode { Off, On } }
public class NsFixBehaviour : UdonSharp.UdonSharpBehaviour
{
    public Unity.Mode unityMode;
    public SystemFoo.Mode prefixMode;
    public string Show() { return unityMode.ToString() + prefixMode.ToString(); }
}", "NsFixBehaviour");
        Assert.DoesNotContain("UnityMode", uasm);
        Assert.DoesNotContain("SystemFooMode", uasm);
        Assert.Contains("unityMode: %SystemInt32", uasm);
        Assert.Contains("prefixMode: %SystemInt32", uasm);
        Assert.Contains("__enumstr_NUnity_TMode", uasm);
        Assert.Contains("__enumstr_NSystemFoo_TMode", uasm);
    }
}
