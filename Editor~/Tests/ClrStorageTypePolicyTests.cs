using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace USugar.Tests;

public class ClrStorageTypePolicyTests
{
    [Fact]
    public void ConstructedGenericClrNamesMatchTheRegistrySpelling()
    {
        Assert.Equal("SystemCollectionsGenericListSystemInt32",
            UdonTypeIdentity.FromStorage(typeof(List<int>)).Name);
        Assert.Equal("SystemCollectionsGenericIListSystemInt32",
            UdonTypeIdentity.FromStorage(typeof(IList<int>)).Name);
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

        Assert.Equal(UdonTypeIdentity.FromStorage(typeof(List<int>)).Name,
            session.Types.GetUdonTypeName(fields["list"].Type));
        Assert.Equal(UdonTypeIdentity.FromStorage(typeof(IList<int>)).Name,
            session.Types.GetUdonTypeName(fields["listInterface"].Type));
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
            var exactType = UdonTypeIdentity.FromStorage(symbol);
            Assert.True(session.Types.IsRegisteredUdonType(exactType));
            Assert.Null(session.TypeFacts.IsEnumFact(exactType));

            Assert.Equal(exactType.Name, session.Types.GetUdonTypeName(symbol));
            Assert.False(session.Types.IsFoldedEnum(symbol));
            Assert.True(session.Types.IsRuntimeDistinguishable(symbol));
            Assert.True(session.TypeFacts.IsEnumFact(exactType));
        }

        var unregistered = compilation.GetTypeByMetadataName("UnityEngine.HideFlags");
        Assert.NotNull(unregistered);
        Assert.False(session.Types.IsRegisteredUdonType(
            UdonTypeIdentity.FromStorage(unregistered)));
        Assert.Equal(StorageTypes.Int32.Name,
            session.Types.GetUdonTypeName(unregistered));
        Assert.True(session.Types.IsFoldedEnum(unregistered));
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
    public void LoweringDecisionCarriesStorageRepresentationAndRuntimeIdentity()
    {
        var compilation = TestHelper.BuildCompilation(@"
using System.Collections.Generic;
using UdonSharp;
using VRC.SDKBase;
public enum SourceMode { A, B }
public struct SourcePair { public int value; }
public class LoweringDecisionProbe : UdonSharpBehaviour
{
    public VRC_EventHandler.VrcBroadcastType registeredEnum;
    public UnityEngine.HideFlags foldedSdkEnum;
    public SourceMode foldedSourceEnum;
    public SourceMode[] foldedEnumArray;
    public int[] exactArray;
    public object[] collapsedArray;
    public SourcePair aggregate;
    public List<int> registeredGeneric;
    public object universalObject;
}", "LoweringDecisionProbe", out var carrier);
        var fields = carrier.GetMembers().OfType<IFieldSymbol>()
            .ToDictionary(field => field.Name, field => field.Type);
        var session = new CompilationSession(compilation, TestHelper.RegistryFacts);

        var registered = session.Types.Describe(fields["registeredEnum"]);
        Assert.Equal(UdonRepresentationKind.Exact, registered.Representation);
        Assert.Equal(
            "VRCSDKBaseVRC_EventHandlerVrcBroadcastType",
            registered.Storage.Name);
        Assert.True(registered.HasRegisteredTypeNode);
        Assert.Equal(UdonRuntimeTypeTest.Exact, registered.RuntimeTypeTest);

        foreach (var field in new[] { "foldedSdkEnum", "foldedSourceEnum" })
        {
            var folded = session.Types.Describe(fields[field]);
            Assert.Equal(
                UdonRepresentationKind.FoldedEnum, folded.Representation);
            Assert.Equal(StorageTypes.Int32, folded.Storage);
            Assert.False(folded.HasRegisteredTypeNode);
            Assert.Equal(
                UdonRuntimeTypeTest.Unsupported, folded.RuntimeTypeTest);
        }

        var foldedArray = session.Types.Describe(fields["foldedEnumArray"]);
        Assert.Equal(
            UdonRepresentationKind.NativeArray, foldedArray.Representation);
        Assert.Equal("SystemInt32Array", foldedArray.Storage.Name);
        Assert.Equal(
            UdonRuntimeTypeTest.Unsupported, foldedArray.RuntimeTypeTest);

        var exactArray = session.Types.Describe(fields["exactArray"]);
        Assert.Equal(UdonRuntimeTypeTest.Exact, exactArray.RuntimeTypeTest);
        var collapsedArray = session.Types.Describe(fields["collapsedArray"]);
        Assert.Equal(
            UdonRuntimeTypeTest.Unsupported, collapsedArray.RuntimeTypeTest);

        var aggregate = session.Types.Describe(fields["aggregate"]);
        Assert.Equal(
            UdonRepresentationKind.ObjectArrayBundle,
            aggregate.Representation);
        Assert.Equal(StorageTypes.ObjectArray, aggregate.Storage);
        Assert.Equal(
            RuntimeBundleKind.Aggregate,
            session.Types.SourceShape(fields["aggregate"]).Bundle);
        Assert.False(
            session.Types.SourceShape(fields["aggregate"])
                .ContainsUserClassPayload);

        var generic = session.Types.Describe(fields["registeredGeneric"]);
        Assert.Equal(UdonRepresentationKind.Exact, generic.Representation);
        Assert.True(generic.HasRegisteredTypeNode);

        var universal = session.Types.Describe(fields["universalObject"]);
        Assert.Equal(
            UdonRuntimeTypeTest.UniversalObject,
            universal.RuntimeTypeTest);
        Assert.True(
            session.Types.SourceShape(fields["universalObject"])
                .ContainsOpaqueObject);
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
    public void FoldedEnumCompoundArithmeticUsesUnderlyingIntegerExterns()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum TinyMode : byte { Zero, One }
public class EnumCompoundArithmetic : UdonSharpBehaviour
{
    public TinyMode folded;

    public void Apply()
    {
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
        Assert.DoesNotContain("TinyMode.__op_", uasm);
        Assert.DoesNotContain("SystemByte.__op_Addition", uasm);
        Assert.DoesNotContain("SystemByte.__op_Subtraction", uasm);
    }

    [Fact]
    public void RegisteredEnumCompoundArithmeticRejectsNumericStrongBox()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class RegisteredEnumCompoundArithmetic : UdonSharpBehaviour
{
    public DayOfWeek value;
    public void Apply() { value += 1; }
}", "RegisteredEnumCompoundArithmetic"));

        Assert.Contains("registered enum", error.Message);
        Assert.Contains("StrongBox", error.Message);
    }
}
