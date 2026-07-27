using System;
using System.Linq;
using Xunit;

namespace USugar.Tests;

public sealed class ProgramInitializationTests
{
    [Fact]
    public void RuntimeInitializer_IsGuardedFromEveryExport()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
public class InitBarrier : UdonSharpBehaviour
{
    int[][] values = new int[2][];
    public void BeforeStart() { values[0] = new int[1]; }
    void Start() { values[1] = new int[1]; }
}", "InitBarrier", out var emitter);

        var initializer = Assert.Single(
            emitter.Module.Functions.Where(f => f.Name == ProgramInitializationEmitter.FunctionName));
        Assert.Null(initializer.ExportName);

        var exports = emitter.Module.Functions.Where(f => f.ExportName != null).ToArray();
        Assert.Contains(exports, f => f.ExportName == "_start");
        Assert.Contains(exports, f => f.ExportName == "BeforeStart");
        Assert.All(exports, function =>
        {
            var first = Assert.IsType<CExprStmt>(
                function.Entry.Instructions.First());
            var call = Assert.IsType<CInternalCall>(first.Expr);
            Assert.Equal(ProgramInitializationEmitter.FunctionName, call.FuncName);
        });
    }

    [Fact]
    public void RuntimeInitializer_IsOwnedOnlyByConstructionFunction()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
public class InitOnce : UdonSharpBehaviour
{
    int[][] values = new int[2][];
    public void Ping() { }
}", "InitOnce", out var emitter);

        Assert.Contains(emitter.Module.Fields,
            field => field.Name == ProgramInitializationEmitter.StateField
                     && field.Type == StorageTypes.Boolean
                     && field.DefaultValue is false);
        var initializer = Assert.Single(
            emitter.FlatModule.Functions.Where(f => f.Name == ProgramInitializationEmitter.FunctionName));
        Assert.Single(initializer.Blocks.SelectMany(block => block.Instructions)
            .OfType<CExprStmt>()
            .Select(statement => statement.Expr)
            .OfType<CExternCall>()
            .Where(call => call.Sig.Text.Contains(".__ctor__", StringComparison.Ordinal)));
    }

    [Fact]
    public void ClassTypeIdentity_IsAHeapConstant_NotRuntimeConstruction()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
public class Payload { public int Value; }
public class TypeObjectHost : UdonSharpBehaviour
{
    void Start() { var payload = new Payload(); }
}", "TypeObjectHost", out var emitter);

        var typeObject = Assert.Single(emitter.Module.Fields
            .Where(field => field.Name.StartsWith("__typeobj_", StringComparison.Ordinal)));
        Assert.Equal(StorageTypes.String, typeObject.Type);
        Assert.Equal("usugar-class:N9_543a5061796c6f6164_0_", typeObject.DefaultValue);
        Assert.DoesNotContain(emitter.Module.Functions,
            function => function.Name == ProgramInitializationEmitter.FunctionName);
    }

}
