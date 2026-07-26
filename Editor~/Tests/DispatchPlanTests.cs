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
    public void BoundTarget_FreezesThisOverrideAndPreservesBaseCall()
    {
        var tree = CSharpSyntaxTree.ParseText(@"
using UdonSharp;
public class PlanBase : UdonSharpBehaviour
{
    public virtual int Read() => 1;
    public int ReadThis() => Read();
}
public class PlanDerived : PlanBase
{
    public override int Read() => 2;
    public int ReadBase() => base.Read();
}");
        var compilation = CSharpCompilation.Create(
            "BoundTargetPlan",
            new[] { tree },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();
        var compiled = compilation.GetTypeByMetadataName(
            "PlanDerived");
        var invocations = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(syntax =>
                (Syntax: syntax,
                    Operation: (IInvocationOperation)
                        model.GetOperation(syntax)))
            .ToArray();
        var direct = invocations.Single(pair =>
            pair.Syntax.Expression is IdentifierNameSyntax);
        var baseCall = invocations.Single(pair =>
            pair.Syntax.Expression is MemberAccessExpressionSyntax);
        var dispatch = new VirtualDispatch(
            new ClassTypeObjectContext());

        DispatchPlan Resolve(IInvocationOperation operation)
        {
            var site = CallableSites.FromOperation(operation)
                .Single();
            return dispatch.Resolve(
                site, site.Target.ContainingType, compiled);
        }

        Assert.Equal(
            "PlanDerived",
            Resolve(direct.Operation).BoundTarget
                .ContainingType.Name);
        Assert.Equal(
            "PlanBase",
            Resolve(baseCall.Operation).BoundTarget
                .ContainingType.Name);
    }

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

    [Fact]
    public void InterfaceSite_RemainsRuntimeDispatchWhenNoTargetIsKnown()
    {
        var tree = CSharpSyntaxTree.ParseText(@"
interface I { int M(); }
class C { void Use(I value) { value.M(); } }");
        var compilation = CSharpCompilation.Create("EmptyDispatchPlan", new[] { tree },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();
        var invocationSyntax = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single();
        var invocation = (IInvocationOperation)model.GetOperation(invocationSyntax);
        var site = CallableSites.FromOperation(invocation).Single();

        var plan = new VirtualDispatch(new ClassTypeObjectContext())
            .Resolve(site, site.Target.ContainingType);

        Assert.Empty(plan.RuntimeTargets);
        Assert.Equal(DispatchPrecision.ClosedWorld, plan.Precision);
    }
}
