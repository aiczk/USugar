using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Xunit;

namespace USugar.Tests;

public class PackageCompatibilityTests
{
    [Fact]
    public void ExplicitNativeCastsFromObjectArrayEstablishExternBoundaryTypes()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;

public class ObjectArrayCastCompatibility : UdonSharpBehaviour
{
    public UdonSharpBehaviour Target;
    public bool Result;

    void Start()
    {
        object[] track = new object[] { ""https://example.invalid/"" };
        string originalUrl = (string)((object[])(object)track)[0];
        Result = string.IsNullOrEmpty(originalUrl);

        object[] callback = new object[] { Target, ""Refresh"" };
        UdonSharpBehaviour receiver =
            (UdonSharpBehaviour)((object[])(object)callback)[0];
        receiver.SendCustomEvent((string)callback[1]);
    }
}
", "ObjectArrayCastCompatibility");

        Assert.Contains(
            "SystemString.__IsNullOrEmpty__SystemString__SystemBoolean",
            uasm);
        Assert.Contains(
            "VRCUdonCommonInterfacesIUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid",
            uasm);
    }

    [Fact]
    public void RawSetProgramVariableUsesTheTypedProgramChannel()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;

public class ProgramVariableCompatibility : UdonSharpBehaviour
{
    public UdonSharpBehaviour Target;
    public object Value;

    void Start()
    {
        Target.SetProgramVariable(""SelectedIndex"", Value);
    }
}
", "ProgramVariableCompatibility");

        Assert.Contains(
            "VRCUdonCommonInterfacesIUdonEventReceiver.__SetProgramVariable__SystemString_SystemObject__SystemVoid",
            uasm);
    }

    [Fact]
    public void UnknownObjectStillCannotCrossAnOrdinaryExternBoundary()
    {
        var ex = Assert.Throws<System.NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;

public class OrdinaryExternObjectBoundary : UdonSharpBehaviour
{
    public object Value;
    void Start() { Debug.Log((object)Value); }
}
", "OrdinaryExternObjectBoundary"));

        Assert.Contains("may carry a compiler bundle", ex.Message);
    }

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
            "EXTERN, \"VRCSDK3ComponentsVRCPickup.__set_InteractionText__SystemString\"", uasm);
        Assert.Contains(
            "EXTERN, \"VRCSDK3ComponentsVRCPickup.__set_UseText__SystemString\"", uasm);
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
        var types = new CompilationSession(
            compilation, TestHelper.RegistryFacts).Types;

        Assert.Equal(
            "VRCUdonCommonInterfacesIUdonEventReceiver",
            types.GetUdonTypeName(fieldType));
        Assert.Equal(
            "VRCUdonCommonInterfacesIUdonEventReceiver",
            types.GetUdonTypeName(fieldType, emptyMap));

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
    public void BoxedGenericConversionsUseClosedTargetAndPreserveTokenSource()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDK3.Data;
using VRC.SDKBase;

namespace VRC.SDK3.Data
{
    public struct DataToken
    {
        public object Reference { get { return null; } }
        public static explicit operator int(DataToken token) { return 0; }
        public static explicit operator string(DataToken token) { return null; }
    }
}

public enum ByteMode : byte { Zero, One }

public class BoxedGenericConversionHost : UdonSharpBehaviour
{
    static T FromToken<T>(DataToken token) { return (T)(object)token; }
    static T FromReference<T>(DataToken token) { return (T)token.Reference; }
    static T FromFloat<T>(float value) { return (T)(object)value; }
    static T FromInt<T>(int value) { return (T)(object)value; }

    void Start()
    {
        UdonSharpBehaviour behaviour = FromToken<UdonSharpBehaviour>(default(DataToken));
        int number = FromToken<int>(default(DataToken));
        string text = FromToken<string>(default(DataToken));
        VRCUrl url = FromToken<VRCUrl>(default(DataToken));
        behaviour = FromReference<UdonSharpBehaviour>(default(DataToken));
        number = FromReference<int>(default(DataToken));
        number = FromFloat<int>(1.9f);
        ByteMode mode = FromInt<ByteMode>(1);
    }
}
", "BoxedGenericConversionHost");

        Assert.Contains(
            "VRCSDK3DataDataToken.__op_Explicit__VRCSDK3DataDataToken__SystemInt32", uasm);
        Assert.Contains(
            "VRCSDK3DataDataToken.__op_Explicit__VRCSDK3DataDataToken__SystemString", uasm);
        Assert.Contains(
            "VRCSDK3DataDataToken.__get_Reference__SystemObject", uasm);
        Assert.Contains(
            "SystemConvert.__ToInt32__SystemObject__SystemInt32", uasm);
        Assert.Contains(
            "SystemMath.__Truncate__SystemDouble__SystemDouble", uasm);
        Assert.Contains(
            "SystemConvert.__ToByte__SystemInt32__SystemByte", uasm);
        Assert.DoesNotContain("USUGAR_TYPED_VIEW", uasm);
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

    [Fact]
    public void ClosedGenericUnityObjectTruthinessUsesDeclaredOperatorAbi()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;

public class GenericUnityTruthiness : UdonSharpBehaviour
{
    static bool IsAlive<T>(T value) { return (bool)(object)value; }
    void Start() { bool alive = IsAlive<UdonSharpBehaviour>(this); }
}
");

        Assert.Contains(
            "UnityEngineObject.__op_Implicit__UnityEngineObject__SystemBoolean", uasm);
        Assert.DoesNotContain(
            "UnityEngineObject.__op_Implicit__VRCUdonCommonInterfacesIUdonEventReceiver__SystemBoolean",
            uasm);
    }

    [Fact]
    public void DataTokenEqualityPreservesReadonlyRefAbi()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;

namespace VRC.SDK3.Data
{
    public struct DataToken
    {
        public static bool operator ==(in DataToken left, in string right) { return false; }
        public static bool operator !=(in DataToken left, in string right) { return true; }
    }
}

public class DataTokenEqualityCompatibility : UdonSharpBehaviour
{
    void Start()
    {
        VRC.SDK3.Data.DataToken token = default(VRC.SDK3.Data.DataToken);
        bool live = token == ""LIVE"";
    }
}
", "DataTokenEqualityCompatibility");

        Assert.Contains(
            "VRCSDK3DataDataToken.__op_Equality__VRCSDK3DataDataTokenRef_SystemStringRef__SystemBoolean",
            uasm);
    }

    [Fact]
    public void InstantiateWithParentLowersThroughRegisteredShimSequence()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;

public class InstantiateParentCompatibility : UdonSharpBehaviour
{
    public GameObject Template;
    public Transform Parent;

    void Start()
    {
        GameObject clone = Instantiate(Template, Parent);
    }
}
");

        Assert.Contains(
            "VRCInstantiate.__Instantiate__UnityEngineGameObject__UnityEngineGameObject", uasm);
        Assert.Contains(
            "UnityEngineGameObject.__get_transform__UnityEngineTransform", uasm);
        Assert.Contains(
            "UnityEngineTransform.__SetParent__UnityEngineTransform_SystemBoolean__SystemVoid", uasm);
        Assert.DoesNotContain(
            "VRCInstantiate.__Instantiate__UnityEngineGameObject_UnityEngineTransform__UnityEngineGameObject",
            uasm);
    }
}
