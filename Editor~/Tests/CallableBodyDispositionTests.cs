using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Xunit;

namespace USugar.Tests;

public class CallableBodyDispositionTests
{
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
public class DedicatedClass
{
}
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
        var dedicatedClass = compilation.GetTypeByMetadataName(
            "DedicatedClass");
        var implicitConstructor = dedicatedClass
            .InstanceConstructors.Single();

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
            materializer.Get(implicitConstructor)
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
