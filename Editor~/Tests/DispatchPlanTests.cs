using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Xunit;

namespace USugar.Tests;

public class DispatchPlanTests
{
    [Fact]
    public void InterfaceSite_ResolvesRuntimeAndLocalTargetsTogether()
    {
        var tree = CSharpSyntaxTree.ParseText(@"
interface I { int M(); }
class C : I { public int M() => 1; void Use(I value) { value.M(); } }");
        var compilation = CSharpCompilation.Create("DispatchPlan", new[] { tree },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();
        var iface = (INamedTypeSymbol)model.GetDeclaredSymbol(
            root.DescendantNodes().OfType<InterfaceDeclarationSyntax>().Single());
        var concrete = (INamedTypeSymbol)model.GetDeclaredSymbol(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single());
        var invocationSyntax = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var invocation = (IInvocationOperation)model.GetOperation(invocationSyntax);
        var site = CallableSites.FromOperation(invocation).Single();
        var types = new ClassTypeObjectContext();
        types.Seed(new[] { concrete });

        var plan = new VirtualDispatch(types).Resolve(site, iface, compiledClass: concrete);

        var runtime = Assert.Single(plan.RuntimeTargets);
        Assert.True(SymbolEqualityComparer.Default.Equals(concrete, runtime.Concrete));
        Assert.True(SymbolEqualityComparer.Default.Equals(
            concrete.GetMembers("M").OfType<IMethodSymbol>().Single(), runtime.Impl));
        Assert.True(SymbolEqualityComparer.Default.Equals(runtime.Impl, plan.Cross.LocalTarget));
        Assert.Equal(DispatchPrecision.ClosedWorld, plan.Precision);
    }
}
