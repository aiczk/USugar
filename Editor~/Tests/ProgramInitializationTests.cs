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
            var first = Assert.IsType<CExprStmt>(function.Body.Stmts.First());
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
            emitter.Module.Functions.Where(f => f.Name == ProgramInitializationEmitter.FunctionName));
        Assert.Single(initializer.FlatBlocks.SelectMany(block => block.Stmts)
            .OfType<CExprStmt>()
            .Select(statement => statement.Expr)
            .OfType<CExternCall>()
            .Where(call => call.Sig.Text.Contains(".__ctor__", StringComparison.Ordinal)));
    }

}
