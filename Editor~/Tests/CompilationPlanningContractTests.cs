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
    public void BoundValueTable_DistinguishesNegativeFactsFromAbsence()
    {
        var scope = default(CallSiteBindingScope);
        var table = new BoundValueTable(
            Array.Empty<(
                IOperation Operation,
                CallSiteBindingScope Scope,
                ValueInfo Value)>(),
            new Dictionary<CallSiteBindingScope, bool>
            {
                [scope] = false,
            });

        Assert.False(table.MethodMentionsProgramLocalPayload(scope));
        Assert.Throws<InvalidOperationException>(() =>
            table.MethodMentionsProgramLocalPayload(null));

        var absent = new BoundValueTable(
            Array.Empty<(
                IOperation Operation,
                CallSiteBindingScope Scope,
                ValueInfo Value)>(),
            new Dictionary<CallSiteBindingScope, bool>());
        Assert.Throws<InvalidOperationException>(() =>
            absent.MethodMentionsProgramLocalPayload(scope));
    }

    [Fact]
    public void UasmEmitter_OwnsTheCompilerPipeline()
    {
        var assembly = typeof(UasmEmitter).Assembly;
        Assert.Null(assembly.GetType("ProgramLoweringPipeline"));
        Assert.DoesNotContain(
            typeof(UasmEmitter).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly),
            field => field.Name == "_pipeline");
    }

    [Fact]
    public void OperationLowerer_OwnsRecursiveDispatchWithoutCallbacks()
    {
        var assembly = typeof(UasmEmitter).Assembly;
        Assert.Null(assembly.GetType("EmitContext"));
        Assert.Null(assembly.GetType("LoweringDispatch"));
        Assert.Null(assembly.GetType("IHandler"));
        Assert.Null(assembly.GetType("IOperationHandler"));
        Assert.Null(assembly.GetType("IExpressionHandler"));

        var stateFields = typeof(LoweringState).GetFields(
            BindingFlags.Instance | BindingFlags.Public
            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        var operations = typeof(LoweringState).GetProperty(
            "Operations", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(operations);
        Assert.Equal(typeof(OperationLowerer), operations.PropertyType);
        Assert.DoesNotContain(
            typeof(OperationLowerer).GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(
            typeof(OperationLowerer).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType.IsGenericType
                     && field.FieldType.GetGenericTypeDefinition()
                     == typeof(Dictionary<,>));
        Assert.DoesNotContain(stateFields, field =>
            field.FieldType == typeof(CompilationSession)
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
    public void LoweringImplementation_IsNotPublicApi()
    {
        var implementationTypes = new[]
        {
            typeof(LoweringState),
            typeof(LoweringServices),
            typeof(OperationLowerer),
            typeof(IUdonTypeSystem),
            typeof(UdonTypeSystem),
            typeof(UdonTypeLowering),
            typeof(DelegateAbi),
            typeof(ClosedConversionPlan),
            typeof(InvocationHandler),
        };

        Assert.All(
            implementationTypes,
            type => Assert.False(type.IsPublic, type.FullName));
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
    }

    [Fact]
    public void BoundProgram_IsTheCompleteImmutableLoweringInput()
    {
        var fields = typeof(BoundProgram).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotEmpty(fields);
        Assert.All(fields, field => Assert.True(field.IsInitOnly, field.Name));
        Assert.Contains(fields,
            field => field.FieldType == typeof(CallableDefinitionPlan));
        Assert.Contains(fields,
            field => field.FieldType == typeof(FieldDiscoveryPlan));
        Assert.Contains(fields,
            field => ContainsType(
                field.FieldType,
                typeof(MethodContext.RegisteredCallableBody)));
        Assert.DoesNotContain(fields,
            field => field.FieldType == typeof(ReachabilityPlan));
        Assert.Contains(fields, field => field.FieldType == typeof(BoundAbiPlan));
        Assert.Contains(fields, field => field.FieldType == typeof(BoundUdonTypeSystem));
        Assert.Contains(fields, field => field.FieldType == typeof(UdonTypeFactRegistry));
        Assert.Contains(fields, field => field.FieldType == typeof(FrozenLayoutPlan));
        var forbiddenAuthorities = new[]
        {
            typeof(CompilationSession),
            typeof(Compilation),
            typeof(UdonAbiCatalog),
            typeof(UdonAbiBinder),
            typeof(MaterializingUdonTypeSystem),
            typeof(CallableBodyGraph),
            typeof(StructuredFunction),
            typeof(LoweringState),
        };
        Assert.DoesNotContain(fields, field =>
            forbiddenAuthorities.Any(forbidden =>
                ContainsType(field.FieldType, forbidden)));
        Assert.DoesNotContain(
            typeof(SyntheticDemandPlan).GetFields(
                BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic),
            field => ContainsType(
                field.FieldType, typeof(StructuredFunction)));
        Assert.DoesNotContain(
            typeof(MethodContext.RegisteredCallable).GetFields(
                BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic),
            field => ContainsType(
                field.FieldType, typeof(StructuredFunction)));
        Assert.Null(typeof(BoundProgram).Assembly.GetType("MethodAnalysisCache"));
        Assert.Null(typeof(BoundProgram).Assembly.GetType("MethodAnalysis"));
        Assert.Null(typeof(BoundProgram).Assembly.GetType("BoundMethodAnalysisTable"));
        Assert.Null(typeof(BoundProgram).Assembly.GetType("RecursionContext"));
        Assert.Null(typeof(BoundProgram).Assembly.GetType("ProgramDiscovery"));
        Assert.Null(typeof(BoundProgram).Assembly.GetType(
            "ProgramDiscoverySeedBuilder"));
        Assert.Null(typeof(BoundProgram).Assembly.GetType(
            "ProgramDiscoverySeed"));
        Assert.Null(typeof(BoundProgram).Assembly.GetType(
            "CompilationPlanner"));
        Assert.Null(typeof(BoundProgram).Assembly.GetType(
            "BoundCallSiteTable"));
        Assert.Null(typeof(BoundProgram).Assembly.GetType(
            "BoundInitializerTable"));
        Assert.Null(typeof(BoundProgram).Assembly.GetType(
            "BoundDeconstructionTable"));
        Assert.Null(typeof(BoundProgram).Assembly.GetType(
            "BoundConversionTable"));
        Assert.Null(typeof(BoundProgram).Assembly.GetType(
            "BoundSyntheticDispatchTable"));
        Assert.DoesNotContain(
            typeof(BoundProgram).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            method => method.Name.StartsWith("With", StringComparison.Ordinal));

        Assert.Null(typeof(RecursionInfo).GetMethod("Populate"));
        Assert.All(
            typeof(RecursionInfo).GetProperties(),
            property => Assert.Null(property.SetMethod));

        Assert.Null(typeof(LoweringState).GetProperty("Abi"));
        Assert.Null(typeof(LoweringServices).GetMethod(
            "RegisterGenericSpecialization",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(typeof(LoweringServices).GetMethod(
            "ResolveStructMember",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(typeof(LoweringServices).GetMethod(
            "RequireStructMember",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(typeof(LoweringServices).GetMethod(
            "SubstituteMethodTypeArgs",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(typeof(LoweringServices).GetMethod(
            "ResolveMostDerivedOverride",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(typeof(LoweringServices).GetMethod(
            "ResolveDispatchProperty",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.All(
            typeof(BoundAbiPlan).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => Assert.True(field.IsInitOnly, field.Name));
        Assert.DoesNotContain(
            typeof(BoundAbiPlan).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(UdonAbiBinder)
                     || field.FieldType == typeof(UdonAbiCatalog));
        Assert.DoesNotContain(
            typeof(BoundUdonTypeSystem).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(UdonTypeSystem)
                     || field.FieldType == typeof(UdonAbiCatalog)
                     || field.FieldType == typeof(CompilationSession)
                     || field.FieldType == typeof(Compilation));
        Assert.DoesNotContain(fields, field =>
            field.FieldType == typeof(BoundAbiPlanBuilder)
            || field.FieldType == typeof(MaterializingUdonTypeSystem)
            || field.FieldType == typeof(CompilationSession)
            || field.FieldType == typeof(Compilation)
            || field.FieldType == typeof(UdonAbiCatalog)
            || field.FieldType == typeof(UdonAbiBinder));

        Assert.DoesNotContain(
            typeof(CaptureScope).GetFields(
                BindingFlags.Instance | BindingFlags.Public),
            field => typeof(System.Collections.IList)
                .IsAssignableFrom(field.FieldType));
        Assert.All(
            typeof(CaptureScope).GetProperties(
                BindingFlags.Instance | BindingFlags.Public),
            property => Assert.True(
                property.SetMethod == null
                || !property.SetMethod.IsPublic,
                property.Name));
    }

    [Fact]
    public void FrozenTypeSnapshot_RejectsAnUnplannedQuestion()
    {
        var compilation = TestHelper.BuildCompilation(
            "class FrozenTypeQuestion { }",
            "FrozenTypeQuestion",
            out _);
        var snapshot = new BoundUdonTypeSystem(
            new Dictionary<string, UdonTypeLowering>());

        var error = Assert.Throws<InvalidOperationException>(() =>
            snapshot.GetStorageType(
                compilation.GetSpecialType(
                    SpecialType.System_Int32)));

        Assert.Contains(
            "absent from the bound program",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FrozenTypeFacts_AreDetachedAndRejectWrites()
    {
        var source = new UdonTypeFactRegistry();
        source.RecordForTest(
            "FrozenKnownType", isEnum: true, isValueType: true);

        var snapshot = source.FreezeCopy();
        source.RecordForTest(
            "LaterSessionType", isEnum: false, isValueType: false);

        Assert.True(snapshot.IsEnumFact("FrozenKnownType"));
        Assert.Null(snapshot.IsEnumFact("LaterSessionType"));
        Assert.Throws<InvalidOperationException>(() =>
            snapshot.RecordForTest(
                "ForbiddenWrite", isEnum: false, isValueType: false));
    }

    [Fact]
    public void FrozenAggregateLayouts_RejectAnUnplannedQuestion()
    {
        var compilation = TestHelper.BuildCompilation(
            "struct PlannedAggregate { public int Value; } "
            + "struct MissingAggregate { public int Value; }",
            "PlannedAggregate",
            out _);
        var planned = compilation.GetTypeByMetadataName("PlannedAggregate");
        var missing = compilation.GetTypeByMetadataName("MissingAggregate");
        var layouts = new AggregateLayoutTable();

        layouts.GetLayout(planned);
        layouts.Publish();

        var error = Assert.Throws<InvalidOperationException>(() =>
            layouts.GetLayout(missing));
        Assert.Contains(
            "absent from the bound program",
            error.Message,
            StringComparison.Ordinal);
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
    public void CallableDefinitionPlan_DoesNotExposeMutableInputs()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { void M() { } }");
        var compilation = CSharpCompilation.Create("ImmutablePlan", new[] { tree },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var method = compilation.GetTypeByMetadataName("C").GetMembers("M")
            .OfType<IMethodSymbol>().Single();
        var sourceMethods = new List<IMethodSymbol> { method };
        var callables = new CallableDefinitionPlan(
            sourceMethods, Array.Empty<IMethodSymbol>(), Array.Empty<IMethodSymbol>(),
            Array.Empty<IMethodSymbol>(), sourceMethods, Array.Empty<IMethodSymbol>(),
            Array.Empty<ClosureSpecializationCandidate>());

        sourceMethods.Clear();

        Assert.Equal(method, Assert.Single(callables.ProgramMethods));
        Assert.Empty(callables.Specializations);
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
        var planner = new SyntheticDemandPlanner();
        planner.SetExpectedDelegateSites(Array.Empty<string>());
        planner.RegisterEnumToString(planned);

        var plan = planner.PublishPlan();

        Assert.Equal(planned, Assert.Single(plan.EnumToStringTypes));
        Assert.All(
            typeof(SyntheticDemandPlanner).GetFields(
                    BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic)
                .Select(field => field.GetValue(planner))
                .OfType<System.Collections.ICollection>(),
            collection => Assert.Empty(collection));
        Assert.DoesNotContain(
            typeof(SyntheticDemandPlanner).GetFields(
                BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic),
            field => field.FieldType == typeof(SyntheticDemandPlan));
        Assert.Throws<InvalidOperationException>(
            () => planner.RegisterEnumToString(late));
    }

    static bool ContainsType(Type candidate, Type forbidden)
    {
        if (candidate == forbidden) return true;
        if (candidate.IsArray)
            return ContainsType(candidate.GetElementType(), forbidden);
        return candidate.IsGenericType
               && candidate.GetGenericArguments().Any(
                   argument => ContainsType(argument, forbidden));
    }
}
