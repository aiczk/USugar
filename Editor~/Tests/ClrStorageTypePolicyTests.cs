using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace USugar.Tests;

public class ClrStorageTypePolicyTests
{
    enum UserMode : short { A, B }
    interface IUserContract { }
    delegate int UserDelegate(int value);

    static bool Registered(string udonTypeName)
        => udonTypeName == "SystemInt32"
           || udonTypeName == "SystemInt16"
           || udonTypeName == "SystemDayOfWeek"
           || udonTypeName == "SystemString"
           || udonTypeName == "SystemCollectionsGenericListSystemInt32"
           || udonTypeName == "SystemCollectionsGenericIListSystemInt32";

    [Fact]
    public void ConstructedGenericClrNamesMatchTheRegistrySpelling()
    {
        Assert.Equal("SystemCollectionsGenericListSystemInt32",
            ExternResolver.GetUdonTypeName(typeof(List<int>)));
        Assert.Equal("SystemCollectionsGenericIListSystemInt32",
            ExternResolver.GetUdonTypeName(typeof(IList<int>)));

        Assert.Equal("SystemCollectionsGenericListSystemInt32",
            ExternResolver.GetUdonStorageTypeName(typeof(List<int>), Registered));
        Assert.Equal("SystemCollectionsGenericIListSystemInt32",
            ExternResolver.GetUdonStorageTypeName(typeof(IList<int>), Registered));
    }

    [Fact]
    public void ClrAndSymbolGenericNameProducersUseTheSameSpelling()
    {
        var compilation = TestHelper.BuildCompilation(@"
using System.Collections.Generic;
public class GenericNameCarrier
{
    public List<int> list;
    public IList<int> listInterface;
}", "GenericNameCarrier", out var carrier);
        var fields = carrier.GetMembers().OfType<IFieldSymbol>()
            .ToDictionary(field => field.Name);
        var session = new CompilationSession(compilation, TestHelper.RegistryFacts);

        Assert.Equal(ExternResolver.GetUdonTypeName(typeof(List<int>)),
            session.Types.GetUdonTypeName(fields["list"].Type));
        Assert.Equal(ExternResolver.GetUdonTypeName(typeof(IList<int>)),
            session.Types.GetUdonTypeName(fields["listInterface"].Type));
    }

    [Fact]
    public void RegisteredSdkEnumKeepsItsUdonIdentity()
        => Assert.Equal("SystemDayOfWeek",
            ExternResolver.GetUdonStorageTypeName(typeof(DayOfWeek), Registered));

    [Fact]
    public void UnregisteredUserEnumUsesItsUnderlyingStorage()
        => Assert.Equal("SystemInt16",
            ExternResolver.GetUdonStorageTypeName(typeof(UserMode), Registered));

    [Fact]
    public void UserInterfaceAndItsArrayUseBehaviourStorage()
    {
        Assert.Equal("VRCUdonCommonInterfacesIUdonEventReceiver",
            ExternResolver.GetUdonStorageTypeName(typeof(IUserContract), Registered));
        Assert.Equal("UnityEngineComponentArray",
            ExternResolver.GetUdonStorageTypeName(typeof(IUserContract[]), Registered));
    }

    [Fact]
    public void DelegateAndDelegateArrayUseObjectArrayBundles()
    {
        Assert.Equal("SystemObjectArray",
            ExternResolver.GetUdonStorageTypeName(typeof(UserDelegate), Registered));
        Assert.Equal("SystemObjectArray",
            ExternResolver.GetUdonStorageTypeName(typeof(UserDelegate[]), Registered));
    }

    [Fact]
    public void NullableAndMultiDimensionalArraysUseTheirFoldedStorage()
    {
        Assert.Equal("SystemObject",
            ExternResolver.GetUdonStorageTypeName(typeof(int?), Registered));
        Assert.Equal("SystemObjectArray",
            ExternResolver.GetUdonStorageTypeName(typeof(int[,]), Registered));
    }

    [Fact]
    public void ClrAndMetadataSymbolProducersAgreeOnTheEnumFamily()
    {
        var compilation = CSharpCompilation.Create(
            "StorageTypeCrossProducerCensus",
            references: TestHelper.StandardRefs.Concat(new[]
            {
                MetadataReference.CreateFromFile(typeof(ClrStorageTypePolicyTests)
                    .Assembly.Location),
            }),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var session = new CompilationSession(compilation, TestHelper.RegistryFacts);

        foreach (var type in new[]
                 {
                     typeof(DayOfWeek),
                     typeof(DayOfWeek[]),
                     typeof(UserMode),
                     typeof(UserMode[]),
                 })
        {
            var elementType = type.IsArray ? type.GetElementType() : type;
            var symbol = compilation.GetTypeByMetadataName(elementType.FullName);
            Assert.NotNull(symbol);
            ITypeSymbol storageSymbol = type.IsArray
                ? compilation.CreateArrayTypeSymbol(symbol)
                : symbol;

            var fromSymbol = session.Types.GetUdonTypeName(storageSymbol);
            var fromClr = ExternResolver.GetUdonStorageTypeName(
                type, session.Types.IsRegisteredUdonTypeName);

            Assert.Equal(fromClr, fromSymbol);
        }
    }

    [Fact]
    public void TypeRegistryIsTheSingleEnumAuthorityEvenWhenExternFactsAreAbsent()
    {
        var compilation = TestHelper.BuildCompilation(@"
using UdonSharp;
public class EnumAuthorityProbe : UdonSharpBehaviour { }
", "EnumAuthorityProbe", out _);
        var session = new CompilationSession(compilation, TestHelper.RegistryFacts);

        foreach (var metadataName in new[]
                 {
                     "VRC.SDKBase.VRC_EventHandler+VrcBroadcastType",
                     "UnityEngine.Rendering.ReflectionProbeType",
                 })
        {
            var symbol = compilation.GetTypeByMetadataName(metadataName);
            Assert.NotNull(symbol);
            var exactName = ExternResolver.GetExactUdonTypeName(symbol);
            Assert.True(session.Types.IsRegisteredUdonTypeName(exactName));
            Assert.Null(session.TypeFacts.IsEnumFact(exactName));

            Assert.Equal(exactName, session.Types.GetUdonTypeName(symbol));
            Assert.False(session.Types.IsUserEnum(symbol));
            Assert.True(session.Types.IsRuntimeDistinguishable(symbol));
            Assert.True(session.TypeFacts.IsEnumFact(exactName));
        }

        var unregistered = compilation.GetTypeByMetadataName("UnityEngine.HideFlags");
        Assert.NotNull(unregistered);
        Assert.False(session.Types.IsRegisteredUdonTypeName(
            ExternResolver.GetExactUdonTypeName(unregistered)));
        Assert.Equal(StorageTypes.Int32.Name,
            session.Types.GetUdonTypeName(unregistered));
        Assert.True(session.Types.IsUserEnum(unregistered));
        Assert.False(session.Types.IsRuntimeDistinguishable(unregistered));
    }

    [Fact]
    public void RegisteredFactlessEnumsKeepTheirHeapTags()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDKBase;
using UnityEngine;
using UnityEngine.Rendering;
public class RegisteredEnumFields : UdonSharpBehaviour
{
    public VRC_EventHandler.VrcBroadcastType broadcast;
    public ReflectionProbeType probe;
    public HideFlags unregistered;
}
", "RegisteredEnumFields");

        Assert.Contains(
            "broadcast: %VRCSDKBaseVRC_EventHandlerVrcBroadcastType", uasm);
        Assert.Contains(
            "probe: %UnityEngineRenderingReflectionProbeType", uasm);
        Assert.Contains("unregistered: %SystemInt32", uasm);
    }

    [Fact]
    public void RegisteredFactlessEnumToStringRejectsInsteadOfPrintingItsInteger()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDKBase;
public class RegisteredEnumToString : UdonSharpBehaviour
{
    public VRC_EventHandler.VrcBroadcastType value;
    public string Read() => value.ToString();
}
", "RegisteredEnumToString"));

        Assert.Contains("No registered Udon extern implements method", error.Message);
        Assert.Contains("ToString", error.Message);
    }

    [Fact]
    public void EnumCompoundAddAndSubtractUseUnderlyingIntegerExterns()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public enum TinyMode : byte { Zero, One }
public class EnumCompoundArithmetic : UdonSharpBehaviour
{
    public DayOfWeek registered;
    public TinyMode folded;

    public void Apply()
    {
        registered += 1;
        registered -= 1;
        folded += 1;
        folded -= 1;
    }
}", "EnumCompoundArithmetic");

        Assert.Contains(
            "SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
            uasm);
        Assert.Contains(
            "SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32",
            uasm);
        Assert.DoesNotContain("SystemDayOfWeek.__op_", uasm);
        Assert.DoesNotContain("TinyMode.__op_", uasm);
        Assert.DoesNotContain("SystemByte.__op_Addition", uasm);
        Assert.DoesNotContain("SystemByte.__op_Subtraction", uasm);
    }
}
