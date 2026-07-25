using Xunit;

namespace USugar.Tests;

public class ComponentQueryDispatchTests
{
    static string Compile(string body, string field = "public VRC.Udon.UdonBehaviour f;")
        => TestHelper.CompileToUasm(
            "using UdonSharp; using UnityEngine; using VRC.Udon;\n"
            + "public class EQ : UdonSharpBehaviour { " + field + "\n  void Start(){ " + body + " } }");

    [Theory]
    [InlineData("f = GetComponent<UdonBehaviour>();", "__GetComponent__SystemType__UnityEngineComponent")]
    [InlineData("f = GetComponentInChildren<UdonBehaviour>();", "__GetComponentInChildren__SystemType__UnityEngineComponent")]
    [InlineData("f = GetComponentInChildren<UdonBehaviour>(true);", "__GetComponentInChildren__SystemType_SystemBoolean__UnityEngineComponent")]
    [InlineData("f = GetComponentInParent<UdonBehaviour>();", "__GetComponentInParent__SystemType__UnityEngineComponent")]
    [InlineData("f = GetComponentInParent<UdonBehaviour>(true);", "__GetComponentInParent__SystemType_SystemBoolean__UnityEngineComponent")]
    public void UdonBehaviourSingularQueriesUseErasedOverload(string body, string expected)
    {
        var uasm = Compile(body);
        Assert.Contains("EXTERN, \"UnityEngineComponent." + expected + "\"", uasm);
        Assert.DoesNotContain("__GetComponent__T", uasm);
        Assert.DoesNotContain("__T\"", uasm);
    }

    [Theory]
    [InlineData("UdonBehaviour[] a = GetComponents<UdonBehaviour>();", "__GetComponents__SystemType__UnityEngineComponentArray")]
    [InlineData("UdonBehaviour[] a = GetComponentsInChildren<UdonBehaviour>();", "__GetComponentsInChildren__SystemType__UnityEngineComponentArray")]
    [InlineData("UdonBehaviour[] a = GetComponentsInChildren<UdonBehaviour>(true);", "__GetComponentsInChildren__SystemType_SystemBoolean__UnityEngineComponentArray")]
    [InlineData("UdonBehaviour[] a = GetComponentsInParent<UdonBehaviour>();", "__GetComponentsInParent__SystemType__UnityEngineComponentArray")]
    [InlineData("UdonBehaviour[] a = GetComponentsInParent<UdonBehaviour>(true);", "__GetComponentsInParent__SystemType_SystemBoolean__UnityEngineComponentArray")]
    public void UdonBehaviourPluralQueriesUseErasedOverload(string body, string expected)
    {
        var uasm = Compile(body + " f = a[0];");
        Assert.Contains("EXTERN, \"UnityEngineComponent." + expected + "\"", uasm);
        Assert.DoesNotContain("TArray\"", uasm);
    }

    // The whole correctness argument for the erased arm is this instruction window: the token is
    // pushed as argument 1 of an extern that does not index any per-T dictionary. The __T forms push
    // (instance, bool, token); the erased forms declare (SystemType, SystemBoolean). Getting this
    // backwards is silently wrong — the SDK would filter on `true` and test inactivity on a Type.
    [Fact]
    public void ErasedQueryPushesTheTokenBeforeTheIncludeInactiveFlag()
    {
        var uasm = Compile("f = GetComponentInChildren<UdonBehaviour>(true);");
        var window = System.Array.ConvertAll(
            System.Array.FindAll(uasm.Split('\n'), l => l.TrimStart().StartsWith("PUSH")
                || l.TrimStart().StartsWith("EXTERN")),
            l => l.Trim());
        var at = System.Array.FindIndex(window,
            l => l.StartsWith("EXTERN, \"UnityEngineComponent.__GetComponentInChildren__SystemType_SystemBoolean__UnityEngineComponent\""));
        Assert.True(at >= 4, "the erased extern must be preceded by instance, token, flag and result");
        Assert.StartsWith("PUSH, __intnl_UnityEngineTransform_", window[at - 4]);
        Assert.StartsWith("PUSH, __const_SystemType_", window[at - 3]);
        Assert.StartsWith("PUSH, __const_SystemBoolean_", window[at - 2]);
        Assert.StartsWith("PUSH, __intnl_UnityEngineComponent_", window[at - 1]);
    }

    [Fact]
    public void ErasedSingularNarrowsThroughAnExplicitRepresentationCast()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp; using UnityEngine; using VRC.SDKBase;
public class EQ2 : UdonSharpBehaviour { public VRC_Pickup f;
  void Start(){ f = GetComponent<VRC_Pickup>(); } }");
        Assert.Contains("EXTERN, \"UnityEngineComponent.__GetComponent__SystemType__UnityEngineComponent\"", uasm);
        Assert.Contains("%UnityEngineComponent,", uasm);
        Assert.Contains("%VRCSDKBaseVRC_Pickup,", uasm);
        Assert.DoesNotContain("__GetComponent__T", uasm);
    }

    [Theory]
    [InlineData("VRC.SDK3.Components.VRCPickup")]
    [InlineData("VRC.SDK3.Components.VRCStation")]
    [InlineData("VRC.SDK3.Components.VRCObjectSync")]
    [InlineData("VRC.SDK3.Components.VRCAvatarPedestal")]
    [InlineData("VRC.SDK3.Video.Components.Base.BaseVRCVideoPlayer")]
    [InlineData("UnityEngine.Rigidbody")]
    [InlineData("UnityEngine.Transform")]
    public void RegisteredGenericGettersKeepTheGenericExtern(string type)
    {
        var uasm = TestHelper.CompileToUasm(
            "using UdonSharp; using UnityEngine;\npublic class EQ3 : UdonSharpBehaviour { public "
            + type + " f;\n  void Start(){ f = GetComponent<" + type + ">(); } }");
        Assert.Contains("EXTERN, \"UnityEngineComponent.__GetComponent__T\"", uasm);
        Assert.DoesNotContain("__GetComponent__SystemType__UnityEngineComponent", uasm);
    }

    [Fact]
    public void UnloweredPluralErasedQueryIsALoudReject()
    {
        var ex = Assert.Throws<System.NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp; using UnityEngine; using VRC.SDKBase;
public class EQ4 : UdonSharpBehaviour { public VRC_Pickup f;
  void Start(){ VRC_Pickup[] a = GetComponents<VRC_Pickup>(); f = a[0]; } }"));
        Assert.Contains("no registered array constructor", ex.Message);
    }

    [Fact]
    public void EveryGenericGetterFamilyAgreesOnItsLegalTokenSet()
    {
        var families = new[]
        {
            "__GetComponent__T", "__GetComponentInChildren__T",
            "__GetComponentInChildren__SystemBoolean__T", "__GetComponentInParent__T",
            "__GetComponentInParent__SystemBoolean__T", "__GetComponents__TArray",
            "__GetComponentsInChildren__TArray", "__GetComponentsInChildren__SystemBoolean__TArray",
            "__GetComponentsInParent__TArray", "__GetComponentsInParent__SystemBoolean__TArray",
        };
        var reference = OwnersOf(families[0]);
        Assert.NotEmpty(reference);
        foreach (var family in families)
            Assert.Equal(reference, OwnersOf(family));
        Assert.DoesNotContain("VRCUdonUdonBehaviour", reference);
        Assert.DoesNotContain("VRCUdonCommonInterfacesIUdonEventReceiver", reference);
        Assert.DoesNotContain("VRCSDKBaseVRC_Pickup", reference);
        Assert.Contains("VRCSDK3ComponentsVRCPickup", reference);
    }

    static System.Collections.Generic.SortedSet<string> OwnersOf(string suffix)
    {
        var owners = new System.Collections.Generic.SortedSet<string>(System.StringComparer.Ordinal);
        foreach (var name in ExternRegistry.All)
        {
            var at = name.IndexOf('.');
            if (at > 0 && name.Substring(at + 1) == suffix) owners.Add(name.Substring(0, at));
        }
        return owners;
    }
}
