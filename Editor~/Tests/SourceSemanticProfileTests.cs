using System;
using Xunit;

namespace USugar.Tests;

public class SourceSemanticProfileTests
{
    [Fact]
    public void RecordClass_IsRejectedWithSemanticReason()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
public record UnsupportedRecord(int Value);
public class RecordHost : UdonSharp.UdonSharpBehaviour
{
    void Start()
    {
        var value = new UnsupportedRecord(1);
    }
}", "RecordHost"));

        Assert.Contains("Record type", error.Message);
        Assert.Contains("runtime type identity", error.Message);
    }

    [Fact]
    public void NestedRecordType_IsRejectedAtTheUseSite()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using System.Collections.Generic;
public record UnsupportedRecord(int Value);
public class RecordHost : UdonSharp.UdonSharpBehaviour
{
    List<UnsupportedRecord> values;
    void Start() { }
}", "RecordHost"));

        Assert.Contains("UnsupportedRecord", error.Message);
    }

    [Fact]
    public void UnusedRecordDeclaration_DoesNotPoisonAnotherProgram()
    {
        var uasm = TestHelper.CompileToUasm(@"
public record UnusedRecord(int Value);
public class PlainHost : UdonSharp.UdonSharpBehaviour
{
    public int result;
    void Start() { result = 1; }
}", "PlainHost");

        Assert.NotNull(uasm);
    }

    [Fact]
    public void StaticConstructor_OnUsedSourceType_IsRejected()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
public static class StaticOwner
{
    static StaticOwner() { }
    public static int Get() => 1;
}
public class StaticConstructorHost : UdonSharp.UdonSharpBehaviour
{
    public int result;
    void Start() { result = StaticOwner.Get(); }
}", "StaticConstructorHost"));

        Assert.Contains("Static constructor", error.Message);
        Assert.Contains("type-initialization hook", error.Message);
    }

    [Fact]
    public void ExplicitBehaviourConstructor_IsRejected()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
public class ConstructorHost : UdonSharp.UdonSharpBehaviour
{
    public ConstructorHost() { }
    void Start() { }
}", "ConstructorHost"));

        Assert.Contains("Constructor", error.Message);
        Assert.Contains("UdonSharpBehaviour", error.Message);
    }

    [Fact]
    public void Destructor_OnConstructedSourceType_IsRejected()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
public class FinalizedValue
{
    ~FinalizedValue() { }
}
public class DestructorHost : UdonSharp.UdonSharpBehaviour
{
    void Start() { var value = new FinalizedValue(); }
}", "DestructorHost"));

        Assert.Contains("Destructor", error.Message);
        Assert.Contains("finalization", error.Message);
    }

    [Fact]
    public void ModuleInitializer_IsRejected()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using System.Runtime.CompilerServices;
public static class ModuleSetup
{
    [ModuleInitializer]
    public static void Initialize() { }
}
public class ModuleInitializerHost : UdonSharp.UdonSharpBehaviour
{
    void Start() { }
}", "ModuleInitializerHost"));

        Assert.Contains("Module initializer", error.Message);
        Assert.Contains("module-load hook", error.Message);
    }

    [Fact]
    public void PlainClassConstructor_RemainsSupported()
    {
        var uasm = TestHelper.CompileToUasm(@"
public class PlainValue
{
    public int Value;
    public PlainValue(int value) { Value = value; }
}
public class PlainConstructorHost : UdonSharp.UdonSharpBehaviour
{
    public int result;
    void Start() { result = new PlainValue(3).Value; }
}", "PlainConstructorHost");

        Assert.NotNull(uasm);
    }
}
