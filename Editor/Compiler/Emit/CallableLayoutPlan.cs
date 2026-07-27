using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal sealed class CallableParameterPlan
{
    public readonly Func<int, string> Id;
    public readonly StorageType Type;

    public CallableParameterPlan(Func<int, string> id, StorageType type)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Type = type;
    }
}

internal sealed class CallableReturnPlan
{
    public readonly Func<int, string> Id;
    public readonly StorageType Type;

    public CallableReturnPlan(Func<int, string> id, StorageType type)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Type = type;
    }
}

/// <summary>Complete function/storage ABI for one named callable registration.</summary>
internal sealed class CallableLayoutPlan
{
    public readonly IMethodSymbol Method;
    public readonly Func<int, string> FunctionName;
    public readonly string ExportName;
    public readonly Func<int, string> SlotPrefix;
    public readonly Func<int, string> ReceiverId;
    public readonly MethodContext.ReceiverAbi Receiver;
    public readonly IReadOnlyList<CallableParameterPlan> Parameters;
    public readonly IReadOnlyList<CallableReturnPlan> Returns;
    public readonly MethodLayout Layout;
    public readonly ImmutableArray<ITypeSymbol> ClosureKeyArgs;
    public readonly ImmutableArray<IMethodSymbol> ClosureOwnerSpecs;
    public readonly INamedTypeSymbol ClosureContainingTypeSpec;
    public readonly Func<int, string> EnvironmentId;

    public CallableLayoutPlan(IMethodSymbol method, Func<int, string> functionName,
        string exportName = null, Func<int, string> slotPrefix = null,
        Func<int, string> receiverId = null,
        MethodContext.ReceiverAbi receiver = MethodContext.ReceiverAbi.None,
        IReadOnlyList<CallableParameterPlan> parameters = null,
        IReadOnlyList<CallableReturnPlan> returns = null, MethodLayout layout = null,
        ImmutableArray<ITypeSymbol> closureKeyArgs = default,
        ImmutableArray<IMethodSymbol> closureOwnerSpecs = default,
        INamedTypeSymbol closureContainingTypeSpec = null,
        Func<int, string> environmentId = null)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        FunctionName = functionName ?? throw new ArgumentNullException(nameof(functionName));
        ExportName = exportName;
        SlotPrefix = slotPrefix ?? (index => index.ToString());
        ReceiverId = receiverId;
        Receiver = receiver;
        Parameters = parameters ?? Array.Empty<CallableParameterPlan>();
        Returns = returns ?? Array.Empty<CallableReturnPlan>();
        Layout = layout;
        ClosureKeyArgs = closureKeyArgs;
        ClosureOwnerSpecs = closureOwnerSpecs;
        ClosureContainingTypeSpec = closureContainingTypeSpec;
        EnvironmentId = environmentId;
    }

    public bool IsClosure => !ClosureKeyArgs.IsDefault;
}

/// <summary>
/// Single authority for exact callable ABI planning and post-publication
/// materialization.
/// </summary>
internal sealed class CallableRegistrar
{
    readonly LoweringState _context;

    public CallableRegistrar(LoweringState context) => _context = context;

    public MethodContext.RegisteredCallable Register(
        CallableLayoutPlan plan,
        bool deferredBody = false)
    {
        if (plan?.Method == null || plan.FunctionName == null)
            throw new ArgumentException("A callable layout requires a method and function name.");
        EmitPolicy.RejectInParameters(plan.Method);
        var slot = _context.Methods.Reserve(plan.SlotPrefix);
        var index = slot.Index;
        var name = plan.FunctionName(index);

        string receiverId = null;
        if (plan.ReceiverId != null)
            receiverId = plan.ReceiverId(index);

        var parameterIds = new string[plan.Parameters.Count + (plan.EnvironmentId != null ? 1 : 0)];
        var parameterTypes = new StorageType[parameterIds.Length];
        for (var i = 0; i < plan.Parameters.Count; i++)
        {
            var parameter = plan.Parameters[i];
            var id = parameter.Id(index);
            parameterIds[i] = id;
            parameterTypes[i] = parameter.Type;
        }
        string environmentId = null;
        if (plan.EnvironmentId != null)
        {
            environmentId = plan.EnvironmentId(index);
            parameterIds[parameterIds.Length - 1] = environmentId;
            parameterTypes[parameterTypes.Length - 1] =
                new StorageType(EnvEmit.EnvType);
        }

        var returns = new ReturnSlot[plan.Returns.Count];
        for (var i = 0; i < plan.Returns.Count; i++)
        {
            var result = plan.Returns[i];
            var slotId = result.Id(index);
            returns[i] = new ReturnSlot(slotId, result.Type);
        }

        MethodContext.RegisteredCallable callable;
        if (plan.IsClosure)
            callable = _context.Methods.AddClosureCallable(
                plan.Method, plan.ClosureKeyArgs,
                plan.ClosureOwnerSpecs,
                plan.ClosureContainingTypeSpec,
                slot, name,
                parameterIds, parameterTypes, returns,
                environmentId, deferredBody);
        else
            callable = _context.Methods.AddCallable(
                plan.Method, slot, name, plan.ExportName,
                receiverId, parameterIds, parameterTypes,
                returns, plan.Receiver, plan.Layout,
                deferredBody);

        if (ContainsYield(plan.Method))
            _context.Methods.AddIteratorPlan(
                callable,
                $"__iter_{index}_{Sanitize(plan.Method.Name)}",
                $"__iter_bundle_{index}",
                $"__iter_result_{index}");
        return callable;
    }

    static bool ContainsYield(IMethodSymbol method)
        => method.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax())
            .Any(declaration => declaration.DescendantNodes()
                .OfType<YieldStatementSyntax>()
                .Any(yieldStatement => ReferenceEquals(
                    yieldStatement.Ancestors().FirstOrDefault(
                        node => node
                            is MethodDeclarationSyntax
                            or LocalFunctionStatementSyntax
                            or AccessorDeclarationSyntax
                            or AnonymousFunctionExpressionSyntax),
                    declaration)));

    static string Sanitize(string value)
        => NameAllocator.Sanitize(value);

    public void Materialize(BoundProgram program)
    {
        if (program == null)
            throw new ArgumentNullException(nameof(program));
        if (!ReferenceEquals(_context.Program, program))
            throw new InvalidOperationException(
                "Callable materialization requires the published "
                + "bound program.");

        foreach (var body in program.CallableBodies)
        {
            var callable = body.Callable;
            var function = _context.Module.AddFunction(
                callable.Name, callable.ExportName);
            if (callable.ReceiverFieldId != null)
            {
                _context.Storage.DeclareVar(
                    callable.ReceiverFieldId,
                    StorageTypes.ObjectArray);
                function.ParamFieldNames.Add(
                    callable.ReceiverFieldId);
            }
            for (var i = 0;
                 i < callable.ParamVarIds.Length;
                 i++)
            {
                _context.Storage.DeclareVar(
                    callable.ParamVarIds[i],
                    callable.ParamStorageTypes[i]);
                function.ParamFieldNames.Add(
                    callable.ParamVarIds[i]);
            }
            foreach (var result in callable.ReturnSlots)
            {
                _context.Storage.DeclareVar(
                    result.Id, result.StorageType);
                function.ReturnSlots.Add(result);
            }
            function.ReturnType =
                callable.ReturnSlots.Length == 1
                    ? callable.ReturnSlots[0].StorageType
                    : callable.ReturnSlots.Length > 1
                        ? StorageTypes.Void
                        : function.ReturnType;
            _context.Methods.AddMaterializedFunction(
                callable, function);
        }

        foreach (var iterator in program.IteratorPlans)
        {
            var function = _context.Module.AddFunction(
                iterator.ResumeName);
            _context.Storage.DeclareVar(
                iterator.BundleParamId,
                StorageTypes.ObjectArray);
            function.ParamFieldNames.Add(
                iterator.BundleParamId);
            _context.Storage.DeclareVar(
                iterator.ReturnId,
                StorageTypes.Boolean);
            function.ReturnSlots.Add(
                new ReturnSlot(
                    iterator.ReturnId,
                    StorageTypes.Boolean));
            function.ReturnType = StorageTypes.Boolean;
            for (var i = 0;
                 i < iterator.FrameParamIds.Length;
                 i++)
                _context.Storage.DeclareVar(
                    iterator.FrameParamIds[i],
                    iterator.FrameParamTypes[i]);
            if (iterator.FrameReceiverId != null)
                _context.Storage.DeclareVar(
                    iterator.FrameReceiverId,
                    StorageTypes.ObjectArray);
        }
    }
}
