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
    public void GenericInterfaceDispatch_ClosedImplementationIsPlannedBeforeEmission()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public interface IClosedMap { U Map<U>(U value); }
public class ClosedMap : IClosedMap {
    public int marker;
    public U Map<U>(U value) { marker = 713; return value; }
}
public class ClosedMapHost : UdonSharpBehaviour {
    public int result;
    int Run(IClosedMap map, int value) { return map.Map<int>(value); }
    void Start() { result = Run(new ClosedMap(), 7); }
}
", "ClosedMapHost");

        Assert.Contains("Map_SystemInt32", uasm);
    }

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
        var planner = new LayoutPlanner(compilation);
        planner.PrepareCompilation();
        var plannedCount = planner.AllLayouts.Count;

        var outputs = new ConcurrentBag<string>();
        Parallel.ForEach(classes, type =>
            outputs.Add(new UasmEmitter(compilation, type, planner, TestHelper.RegistryFacts).Emit()));

        Assert.Equal(classes.Length, outputs.Count);
        Assert.Equal(plannedCount, planner.AllLayouts.Count);
        Assert.True(planner.IsFrozen);
    }

    [Fact]
    public void FrozenPlanner_RejectsEveryInterfaceFactWriter()
    {
        var tree = CSharpSyntaxTree.ParseText(TestHelper.StubSource + @"
public interface IFrozenFact { }
");
        var compilation = CSharpCompilation.Create("PlannerFreezeWriters", new[] { tree },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var iface = compilation.GetTypeByMetadataName("IFrozenFact");
        var planner = new LayoutPlanner(compilation);
        planner.Freeze();

        var structError = Assert.Throws<InvalidOperationException>(
            () => planner.RegisterStructImplementedInterface(iface));
        var classError = Assert.Throws<InvalidOperationException>(
            () => planner.RegisterClassImplementedInterface(iface, isBehaviour: false));
        var behaviourError = Assert.Throws<InvalidOperationException>(
            () => planner.RegisterClassImplementedInterface(iface, isBehaviour: true));

        Assert.Contains("frozen", structError.Message);
        Assert.Contains("frozen", classError.Message);
        Assert.Contains("frozen", behaviourError.Message);
    }
}
