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
    public void OperationLowerer_OwnsRecursiveDispatchWithoutCallbacks()
    {
        var assembly = typeof(UasmEmitter).Assembly;
        Assert.Null(assembly.GetType("EmitContext"));
        Assert.Null(assembly.GetType("LoweringDispatch"));

        var environmentFields = typeof(LoweringEnvironment).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.NotEmpty(environmentFields);
        Assert.All(environmentFields, field => Assert.True(field.IsInitOnly, field.Name));

        var stateFields = typeof(LoweringState).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.Contains(stateFields,
            field => field.Name == nameof(LoweringState.Environment)
                     && field.FieldType == typeof(LoweringEnvironment));
        var operations = typeof(LoweringState).GetProperty(
            "Operations", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(operations);
        Assert.Equal(typeof(OperationLowerer), operations.PropertyType);
        Assert.DoesNotContain(
            typeof(OperationLowerer).GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(stateFields, field =>
            field.FieldType == typeof(CompilationSession)
            || field.FieldType == typeof(Compilation)
            || field.FieldType == typeof(LayoutPlanBuilder)
            || field.FieldType == typeof(UdonAbiCatalog));
    }

    [Fact]
    public void AssignmentHandlers_ComposeTheSingleLValueCapability()
    {
        var assembly = typeof(UasmEmitter).Assembly;
        Assert.Null(assembly.GetType("AssignmentHandlerBase"));

        var handlers = new[]
        {
            typeof(SimpleAssignmentHandler),
            typeof(CompoundAssignmentHandler),
            typeof(NullableHandler),
            typeof(DeconstructionAssignmentHandler),
        };
        Assert.All(handlers, handler => Assert.Equal(typeof(object), handler.BaseType));

        foreach (var handler in handlers.Take(3))
        {
            Assert.Contains(
                handler.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
                field => field.FieldType == typeof(LValueLowerer));
        }
        Assert.DoesNotContain(
            typeof(DeconstructionAssignmentHandler).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(LValueLowerer));
    }

    [Fact]
    public void InvocationHandler_IsACompositionRootForNarrowCapabilities()
    {
        var fields = typeof(InvocationHandler).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        Assert.Contains(fields, field => field.FieldType == typeof(DelegateInvocationLowerer));
        Assert.Contains(fields, field => field.FieldType == typeof(ExternInvocationLowerer));
        Assert.Contains(fields, field => field.FieldType == typeof(InvocationIntrinsicEmitter));
        Assert.Contains(fields, field => field.FieldType == typeof(MemberInvocationLowerer));
        Assert.DoesNotContain(fields, field => typeof(Delegate).IsAssignableFrom(field.FieldType));

        Assert.Equal(
            typeof(NdimArrayLowerer),
            typeof(LoweringServices).GetProperty(
                "Ndim", BindingFlags.Instance | BindingFlags.NonPublic)?.PropertyType);
    }

    [Fact]
    public void FieldDiscoveryPlan_IsSemanticAndIrFree()
    {
        var planFields = typeof(FieldDiscoveryPlan).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(planFields, field =>
            field.FieldType == typeof(LoweringState)
            || field.FieldType == typeof(StructuredModule)
            || field.FieldType == typeof(StorageContext));
        Assert.DoesNotContain(
            typeof(FieldDiscoveryPlan).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(LoweringState)
                || parameter.ParameterType == typeof(StructuredModule)));

        var fieldsMember = typeof(ProgramDiscovery).GetField(nameof(ProgramDiscovery.Fields));
        Assert.NotNull(fieldsMember);
        Assert.Equal(typeof(FieldDiscoveryPlan), fieldsMember.FieldType);
    }

    [Fact]
    public void BoundProgram_IsTheCompleteImmutableLoweringInput()
    {
        var fields = typeof(BoundProgram).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotEmpty(fields);
        Assert.All(fields, field => Assert.True(field.IsInitOnly, field.Name));
        Assert.Contains(fields, field => field.FieldType == typeof(ProgramDiscovery));
        Assert.Contains(fields, field => field.FieldType == typeof(ClosureIdentityPlan));
        Assert.Contains(fields, field => field.FieldType == typeof(CaptureScopeAnalysis));
        Assert.Contains(fields, field => field.FieldType == typeof(CallableBodyGraph));
        Assert.Contains(fields, field => field.FieldType == typeof(RecursionInfo));
        Assert.Contains(fields, field => field.FieldType == typeof(SyntheticDemandPlan));
        Assert.Contains(fields, field => field.FieldType == typeof(BoundCallSiteTable));
        Assert.Contains(fields, field => field.FieldType == typeof(BoundAbiPlan));
        Assert.DoesNotContain(
            typeof(BoundProgram).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            method => method.Name.StartsWith("With", StringComparison.Ordinal));

        Assert.Null(typeof(RecursionInfo).GetMethod("Populate"));
        Assert.All(
            typeof(RecursionInfo).GetProperties(),
            property => Assert.Null(property.SetMethod));

        Assert.Null(typeof(LoweringState).GetProperty("Abi"));
        Assert.DoesNotContain(
            typeof(LoweringEnvironment).GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => field.FieldType == typeof(UdonAbiBinder));
        Assert.All(
            typeof(BoundAbiPlan).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => Assert.True(field.IsInitOnly, field.Name));
    }

    [Fact]
    public void CallableSpecializations_AreKeyedByExactClrSymbols()
    {
        var compilation = TestHelper.BuildCompilation(@"
using UdonSharp;
public class KeyedSpecs : UdonSharpBehaviour
{
    T Id<T>(T value) => value;
}
", "KeyedSpecs", out var type);
        var definition = type.GetMembers("Id").OfType<IMethodSymbol>().Single();
        var intSpec = definition.Construct(compilation.GetSpecialType(SpecialType.System_Int32));
        var stringSpec = definition.Construct(compilation.GetSpecialType(SpecialType.System_String));
        var callables = new CallableDefinitionPlan(
            Array.Empty<IMethodSymbol>(),
            Array.Empty<IMethodSymbol>(),
            Array.Empty<IMethodSymbol>(),
            Array.Empty<IMethodSymbol>(),
            new[] { definition },
            new[] { intSpec, stringSpec },
            Array.Empty<ClosureSpecializationCandidate>());

        Assert.Equal(2, callables.SpecializationsByKey.Count);
        Assert.Equal(
            intSpec,
            callables.SpecializationsByKey[SpecializationKey.ForMethod(intSpec)],
            SymbolEqualityComparer.Default);
        Assert.Equal(
            stringSpec,
            callables.SpecializationsByKey[SpecializationKey.ForMethod(stringSpec)],
            SymbolEqualityComparer.Default);
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
        var builder = new ProgramDiscoverySeedBuilder(
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
        var plan = new ProgramDiscovery(
            callables,
            new ReachableBodies().Freeze(Array.Empty<INamedTypeSymbol>()),
            sourceRoots,
            Array.Empty<IOperation>(),
            new FieldDiscoveryPlanBuilder().Build());

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
