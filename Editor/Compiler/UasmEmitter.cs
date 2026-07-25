using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Stable public façade for one USugar compilation. Mutable planning/lowering state is owned by
/// <see cref="ProgramLoweringPipeline"/>; this type only coordinates construction, execution, and
/// result access.
/// </summary>
public sealed class UasmEmitter
{
    readonly ProgramLoweringPipeline _pipeline;

    public UasmEmitter(Compilation compilation, INamedTypeSymbol classSymbol,
        FrozenLayoutPlan planner = null, UdonAbiCatalog externRegistry = null)
    {
        _pipeline = new ProgramLoweringPipeline(
            compilation, classSymbol, planner, externRegistry);
    }

    public UasmEmitter(CompilationSession session, INamedTypeSymbol classSymbol,
        FrozenLayoutPlan planner = null)
    {
        _pipeline = new ProgramLoweringPipeline(session, classSymbol, planner);
    }

    public string Emit() => _pipeline.Emit();
    public uint GetHeapSize() => _pipeline.GetHeapSize();

    public IReadOnlyList<EmitDiagnostic> Diagnostics => _pipeline.Diagnostics;
    public CodeGenResult CodeGenResult => _pipeline.CodeGenResult;
    public StructuredModule Module => _pipeline.Module;
    public VerifiedFlatModule FlatModule => _pipeline.FlatModule;
    public CaptureScopeAnalysis CaptureScope => _pipeline.CaptureScope;
    public Compilation Compilation => _pipeline.Compilation;
    public INamedTypeSymbol ClassSymbol => _pipeline.ClassSymbol;

    internal CompilationSession Session => _pipeline.Session;
    internal FrozenLayoutPlan Planner => _pipeline.Planner;
    internal ResolvedEdgeResolver EdgeResolver => _pipeline.EdgeResolver;
    internal VirtualDispatch VirtualDispatchInstance => _pipeline.VirtualDispatchInstance;
    internal ClassTypeObjectContext ClassTypes => _pipeline.ClassTypes;
    internal RecursionInfo DebugRecursionInfo => _pipeline.DebugRecursionInfo;
    internal IReadOnlyList<string> DebugStaticInitializerOrder
        => _pipeline.DebugStaticInitializerOrder;
    internal IEnumerable<OperationKind> HandledOperationKinds
        => _pipeline.HandledOperationKinds;

    internal ResolvedEdgeResolver DebugBuildResolver() => _pipeline.DebugBuildResolver();

    internal static long ComputeTypeId(string typeName)
        => ProgramLoweringPipeline.ComputeTypeId(typeName);
    internal static bool IsComputedProperty(IPropertySymbol property)
        => ProgramLoweringPipeline.IsComputedProperty(property);
    internal static bool IsClosedForeignStaticTarget(IMethodSymbol method)
        => ProgramLoweringPipeline.IsClosedForeignStaticTarget(method);
    internal static bool IsBaseInstanceMethod(IMethodSymbol method, INamedTypeSymbol classSymbol)
        => ProgramLoweringPipeline.IsBaseInstanceMethod(method, classSymbol);
    internal static bool IsForeignStatic(IMethodSymbol method, INamedTypeSymbol classSymbol)
        => ProgramLoweringPipeline.IsForeignStatic(method, classSymbol);
}
