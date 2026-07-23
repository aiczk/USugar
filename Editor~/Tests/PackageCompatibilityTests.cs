using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Xunit;

namespace USugar.Tests;

public class PackageCompatibilityTests
{
    [Fact]
    public void PickupMembersUseSdk3ExternOwnerAndRegistrySetterShape()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDKBase;
using VRC.SDK3.Components;

public class PickupCompatibility : UdonSharpBehaviour
{
    public VRC_Pickup BasePickup;
    public VRCPickup ConcretePickup;

    void Start()
    {
        BasePickup.InteractionText = ""base interaction"";
        BasePickup.UseText = ""base use"";
        BasePickup.Drop();
        ConcretePickup.InteractionText = ""concrete interaction"";
        ConcretePickup.UseText = ""concrete use"";
        ConcretePickup.Drop();
    }
}
");

        Assert.Contains(
            "VRCSDK3ComponentsVRCPickup.__set_InteractionText__SystemString", uasm);
        Assert.Contains(
            "VRCSDK3ComponentsVRCPickup.__set_UseText__SystemString", uasm);
        Assert.Contains(
            "VRCSDK3ComponentsVRCPickup.__Drop__SystemVoid", uasm);
        Assert.Contains("%VRCSDKBaseVRC_Pickup", uasm);
        Assert.DoesNotContain("VRCSDKBaseVRC_Pickup.__", uasm);
        Assert.DoesNotContain(
            "VRCSDK3ComponentsVRCPickup.__set_InteractionText__SystemString__SystemVoid", uasm);
    }

    [Fact]
    public void ConstructedGenericBehaviourAlwaysUsesEventReceiverStorage()
    {
        var compilation = TestHelper.BuildCompilation(@"
using UdonSharp;

public class DataList<T> : UdonSharpBehaviour
{
}

public static class DataListExt
{
    public static void Add<T>(this DataList<T> list, T value) { }
}

public class GenericBehaviourHost : UdonSharpBehaviour
{
    public DataList<UdonSharpBehaviour> Values;
    void Start() { Values.Add(this); }
}
", "GenericBehaviourHost", out var host);
        var fieldType = ((IFieldSymbol)host.GetMembers("Values")[0]).Type;
        var emptyMap = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(
            SymbolEqualityComparer.Default);

        Assert.Equal(
            "VRCUdonCommonInterfacesIUdonEventReceiver",
            ExternResolver.GetUdonTypeName(fieldType));
        Assert.Equal(
            "VRCUdonCommonInterfacesIUdonEventReceiver",
            ExternResolver.GetUdonTypeName(fieldType, emptyMap));

        TestHelper.CompileToUasm(@"
using UdonSharp;

public class DataList<T> : UdonSharpBehaviour
{
}

public static class DataListExt
{
    public static void Add<T>(this DataList<T> list, T value) { }
}

public class GenericBehaviourHost : UdonSharpBehaviour
{
    public DataList<UdonSharpBehaviour> Values;
    void Start() { Values.Add(this); }
}
", "GenericBehaviourHost");
    }

    [Fact]
    public void GenericBehaviourHelperIsSpecializedFromConcreteRootCall()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;

public static class TokenUtil
{
    public static T[] Copy<T>(T[] values) { return values; }
}

public class TypedList<T> : UdonSharpBehaviour
{
    public static TypedList<T> New(T[] values)
    {
        T[] copy = TokenUtil.Copy(values);
        return null;
    }
}

public class GenericHelperHost : UdonSharpBehaviour
{
    void Start()
    {
        TypedList<int> values = TypedList<int>.New(new[] { 1, 2, 3 });
    }
}
", "GenericHelperHost");
    }

    [Fact]
    public void SdkInterfacePropertiesUseRegisteredExterns()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDK3.StringLoading;

public class StringDownloadCompatibility : UdonSharpBehaviour
{
    public IVRCStringDownload Download;

    void Start()
    {
        string result = Download.Result;
        string url = Download.Url.Get();
    }
}
");

        Assert.Contains(
            "VRCSDK3StringLoadingIVRCStringDownload.__get_Result__SystemString", uasm);
        Assert.Contains(
            "VRCSDK3StringLoadingIVRCStringDownload.__get_Url__VRCSDKBaseVRCUrl", uasm);
        Assert.Contains("VRCSDKBaseVRCUrl.__Get__SystemString", uasm);
        Assert.Contains("%VRCSDK3StringLoadingIVRCStringDownload", uasm);
        Assert.DoesNotContain("__iface_VRCSDK3StringLoadingIVRCStringDownload", uasm);
    }
}
