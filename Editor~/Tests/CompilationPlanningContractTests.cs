using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace USugar.Tests;

public class CompilationPlanningContractTests
{
    [Fact]
    public void UasmEmitter_IsAThinPipelineFacade()
    {
        var fields = typeof(UasmEmitter).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        var pipeline = Assert.Single(fields);
        Assert.Equal("_pipeline", pipeline.Name);
        Assert.Equal(typeof(ProgramLoweringPipeline), pipeline.FieldType);
    }

    [Fact]
    public void Builder_FreezesRegistrationGatesOnce()
    {
        var tree = CSharpSyntaxTree.ParseText(@"
class C
{
    void Own() { int Local() => 1; }
    static void Foreign() { }
    void StructMember() { }
    void BaseCopy() { }
}");
        var compilation = CSharpCompilation.Create("RegistrationPlan", new[] { tree },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .ToDictionary(d => d.Identifier.ValueText, d => (IMethodSymbol)model.GetDeclaredSymbol(d));
        var localSyntax = tree.GetRoot().DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single();
        var local = (IMethodSymbol)model.GetDeclaredSymbol(localSyntax);
        var reach = new ReachableBodies
        {
            ForeignStatics = new[] { methods["Foreign"], local },
            StructMembers = new[] { methods["StructMember"] },
            BaseCopies = new[] { methods["Own"], methods["BaseCopy"] },
        };
        var builder = new ProgramPlanSeedBuilder(
            () => new[] { methods["Own"] }, _ => reach,
            () => Array.Empty<IOperation>(),
            () => new[] { local });

        var plan = builder.Build();

        Assert.Equal(new[] { methods["Foreign"] }, plan.ForeignStatics);
        Assert.Equal(new[] { methods["StructMember"] }, plan.StructMethods);
        Assert.Equal(new[] { methods["BaseCopy"] }, plan.BaseInstanceMethods);
        Assert.Contains(local, plan.Definitions, SymbolEqualityComparer.Default);
    }

    [Fact]
    public void PublishedPlan_DoesNotExposeMutableBuilderCollections()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { void M() { } }");
        var compilation = CSharpCompilation.Create("ImmutablePlan", new[] { tree },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var method = compilation.GetTypeByMetadataName("C").GetMembers("M")
            .OfType<IMethodSymbol>().Single();
        var sourceMethods = new List<IMethodSymbol> { method };
        var sourceRoots = new List<IMethodSymbol> { method };
        var callables = new CallableDefinitionPlan(
            sourceMethods, Array.Empty<IMethodSymbol>(), Array.Empty<IMethodSymbol>(),
            Array.Empty<IMethodSymbol>(), sourceMethods, Array.Empty<IMethodSymbol>(),
            Array.Empty<ClosureSpecializationCandidate>());
        var plan = new ProgramPlan(
            callables,
            new ReachableBodies().Freeze(Array.Empty<INamedTypeSymbol>()),
            sourceRoots,
            Array.Empty<IOperation>());

        sourceMethods.Clear();
        sourceRoots.Clear();

        Assert.Equal(method, Assert.Single(plan.Callables.ProgramMethods));
        Assert.Equal(method, Assert.Single(plan.CaptureRoots));
        Assert.Empty(plan.Callables.Specializations);
    }

    [Fact]
    public void SyntheticDemandPlan_RejectsDemandDiscoveredDuringLowering()
    {
        var tree = CSharpSyntaxTree.ParseText("enum Planned { A } enum Late { B }");
        var compilation = CSharpCompilation.Create("SyntheticPlan", new[] { tree },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var planned = compilation.GetTypeByMetadataName("Planned");
        var late = compilation.GetTypeByMetadataName("Late");
        var context = new SyntheticContext();
        context.SetExpectedDelegateSites(Array.Empty<string>());
        context.RegisterEnumToString(planned);

        var plan = context.PublishPlan();

        Assert.Equal(planned, Assert.Single(plan.EnumToStringTypes));
        Assert.Throws<InvalidOperationException>(() => context.RegisterEnumToString(late));
        context.VerifyEmissionComplete();
    }
}
