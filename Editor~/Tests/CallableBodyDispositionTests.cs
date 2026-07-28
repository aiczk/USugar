using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Xunit;

namespace USugar.Tests;

public class CallableBodyDispositionTests
{
    [Fact]
    public void RecordPrimaryConstructor_DedicatedLoweringDoesNotEmitCallable()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
public record BodyDispositionRecord(int Value);
public class BodyDispositionHost : UdonSharpBehaviour
{
    public int result;
    void Start()
    {
        var left = new BodyDispositionRecord(7);
        var right = new BodyDispositionRecord(7);
        result = left == right ? left.Value : 0;
    }
}", "BodyDispositionHost", out var emitter);

        Assert.DoesNotContain(
            emitter.FlatModule.Functions,
            function => function.Name.Contains(
                "BodyDispositionRecord",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            emitter.FlatModule.Functions,
            function => function.Name.Contains(
                "op_Equality",
                StringComparison.Ordinal)
                || function.Name.Contains(
                    "EqualityContract",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Materializer_ClassifiesEveryBodyDisposition()
    {
        var compilation = TestHelper.BuildCompilation(@"
public abstract class BodyDispositionKinds
{
    public void Source() { }
    public int Auto { get; set; }
    public abstract void Missing();

    [System.Runtime.InteropServices.DllImport(""native"")]
    public static extern int Native();
}
public record DedicatedRecord(int Value);
", "BodyDispositionKinds", out var type);
        var materializer =
            new BoundMethodBodyTable.Materializer(
                compilation);
        var source = type.GetMembers("Source")
            .OfType<IMethodSymbol>().Single();
        var autoGetter = type.GetMembers("Auto")
            .OfType<IPropertySymbol>().Single()
            .GetMethod;
        var missing = type.GetMembers("Missing")
            .OfType<IMethodSymbol>().Single();
        var native = type.GetMembers("Native")
            .OfType<IMethodSymbol>().Single();
        var record = compilation.GetTypeByMetadataName(
            "DedicatedRecord");
        var recordConstructor = record.InstanceConstructors
            .Single(constructor =>
                constructor.Parameters.Length == 1
                && constructor.Parameters[0].Type
                    .SpecialType
                    == SpecialType.System_Int32);

        Assert.Equal(
            CallableBodyDisposition.SourceBody,
            materializer.Get(source).Disposition);
        Assert.Equal(
            CallableBodyDisposition
                .SynthesizedAutoAccessor,
            materializer.Get(autoGetter).Disposition);
        Assert.Equal(
            CallableBodyDisposition.NoBody,
            materializer.Get(missing).Disposition);
        Assert.Equal(
            CallableBodyDisposition.Unsupported,
            materializer.Get(native).Disposition);
        Assert.Equal(
            CallableBodyDisposition.DedicatedLowering,
            materializer.Get(recordConstructor)
                .Disposition);
    }

    [Fact]
    public void Materializer_PartialImplementationOwnsExecutableBody()
    {
        var compilation = TestHelper.BuildCompilation(@"
public partial class PartialBodyHost
{
    partial void Hook();
    public void Run() { Hook(); }
}
public partial class PartialBodyHost
{
    partial void Hook() { }
}
", "PartialBodyHost", out var type);
        var run = type.GetMembers("Run")
            .OfType<IMethodSymbol>().Single();
        var invocation = compilation
            .GetSemanticModel(
                run.DeclaringSyntaxReferences[0]
                    .SyntaxTree)
            .GetOperation(
                run.DeclaringSyntaxReferences[0]
                    .GetSyntax())
            .Descendants()
            .OfType<IInvocationOperation>()
            .Single();
        var target = invocation.TargetMethod;
        var materializer =
            new BoundMethodBodyTable.Materializer(
                compilation);

        Assert.NotNull(target.PartialImplementationPart);
        Assert.Equal(
            CallableBodyDisposition.SourceBody,
            materializer.Get(target).Disposition);
    }
}
