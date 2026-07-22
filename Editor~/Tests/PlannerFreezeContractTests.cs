using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace USugar.Tests;

public class PlannerFreezeContractTests
{
    [Fact]
    public void FrozenSharedPlanner_ParallelEmitDoesNotRegisterLayouts()
    {
        var tree = CSharpSyntaxTree.ParseText(TestHelper.StubSource + @"
public interface IPlanned { int Read(); }
public class PlannedA : UdonSharp.UdonSharpBehaviour, IPlanned { public int Read() => 1; }
public class PlannedB : UdonSharp.UdonSharpBehaviour, IPlanned { public int Read() => 2; }
");
        var compilation = CSharpCompilation.Create("PlannerFreeze", new[] { tree },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var classes = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Select(d => model.GetDeclaredSymbol(d) as INamedTypeSymbol)
            .Where(t => t != null && t.Name.StartsWith("Planned", StringComparison.Ordinal)
                && ExternResolver.IsUdonSharpBehaviour(t))
            .ToArray();
        var iface = compilation.GetTypeByMetadataName("IPlanned");

        var planner = new LayoutPlanner(compilation);
        foreach (var type in classes)
        {
            foreach (var implemented in type.AllInterfaces)
                planner.RegisterClassImplementedInterface(implemented, true);
            planner.Plan(type);
            foreach (var implemented in type.AllInterfaces) planner.Plan(implemented);
        }
        planner.Plan(iface);
        planner.Freeze();
        var plannedCount = planner.AllLayouts.Count;

        var outputs = new ConcurrentBag<string>();
        Parallel.ForEach(classes, type =>
            outputs.Add(new UasmEmitter(compilation, type, planner, TestHelper.RegistryFacts).Emit()));

        Assert.Equal(classes.Length, outputs.Count);
        Assert.Equal(plannedCount, planner.AllLayouts.Count);
        Assert.True(planner.IsFrozen);
    }
}
