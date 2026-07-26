using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Collects the per-class synthetic demand set before body emission.
/// Publishing is terminal: lowering consumes only the resulting <see cref="SyntheticDemandPlan"/>.
/// </summary>
internal sealed class SyntheticDemandPlanner
{
    // Per-spec closure bridges (design 2026-07-10 v3 SS2B): bridgeExportName -> the closure's
    // per-spec StructuredFunction. The pending-bridge drain resolves closure targets here (a bare
    // definition-symbol lookup cannot distinguish specs).
    readonly Dictionary<string, StructuredFunction> _closureBridgeFuncs = new();

    // MG auto-wrap (design 2026-07-11 v2): pending receiver-bridges — a class/struct instance method
    // group's bridge re-dispatches DelegateAbi.Env as the member's param0 (CA-M1 receiver ABI).
    readonly Dictionary<string, DelegateBridgeDemand> _receiverBridges = new();

    // Pending delegate bridges for dynamically hoisted lambdas/local functions. The carried map is
    // the creating method's immutable TypeParamMap by REFERENCE (per-EmitMethod fresh, never mutated,
    // so no snapshot copy is needed even though the drain runs after emission when the ambient map
    // is null).
    readonly Dictionary<string, DelegateBridgeDemand> _delegateBridges = new();

    // Multicast: sig-part -> signature plus the exact combine/remove operations used by this class.
    // Sites sharing a signature merge their flags; the drain emits one fan-out and only the helpers
    // actually referenced by lowering.
    readonly Dictionary<string, MulticastSigPlan> _multicastSigs = new();

    // B67: user enums whose ToString()/concat/interpolation needs the synthesized __enumstr_ helper.
    readonly HashSet<INamedTypeSymbol> _enumToString = new(SymbolEqualityComparer.Default);
    readonly HashSet<INamedTypeSymbol> _classToString = new(SymbolEqualityComparer.Default);

    // Variance (2026-07-04 design 2.2, B-1): per-(target, sig-S) sig adapter bridges. DelegateInvoke
    // is the DESTINATION delegate's own Invoke (conv-var declarations), distinct from TargetMethod
    // (the real callee, InternalCall only). Dedup-by-name at emission.
    readonly Dictionary<string, DelegateBridgeDemand> _sigAdapterBridges = new();

    // Variance (2026-07-04 design 2.3, B-2): wrapper name -> (outer sig-S Invoke, inner sig-T
    // Invoke-or-method, resolved map). Keyed by WRAPPER NAME (unique per (outer,inner) sig pair) -
    // a wrapper's inner dispatch speaks the INNER bundle's own protocol.
    readonly Dictionary<string, DelegateWrapperDemand> _wrapperSigs = new();

    bool _published;
    HashSet<string> _expectedDelegateSites;
    readonly Dictionary<string, DelegateBindingPlan> _plannedDelegateSites = new(StringComparer.Ordinal);

    public void SetExpectedDelegateSites(IEnumerable<string> sites)
    {
        RequireMutable();
        if (_expectedDelegateSites != null)
            throw new InvalidOperationException("Delegate demand census was set twice.");
        _expectedDelegateSites = new HashSet<string>(
            sites ?? throw new ArgumentNullException(nameof(sites)), StringComparer.Ordinal);
    }

    public void PlanDelegateBinding(string key, DelegateBindingPlan binding)
    {
        RequireMutable();
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Delegate site key is required.", nameof(key));
        if (_expectedDelegateSites == null || !_expectedDelegateSites.Contains(key))
            throw new InvalidOperationException(
                $"Delegate binding plan at '{key}' was absent from the pre-emission demand census.");
        if (_plannedDelegateSites.TryGetValue(key, out var existing))
        {
            if (SameBinding(existing, binding)) return;
            throw new InvalidOperationException(
                $"Delegate site '{key}' planned conflicting bindings "
                + $"'{existing.BridgeName}' and '{binding.BridgeName}'.");
        }
        _plannedDelegateSites.Add(key, binding);
    }

    public static void PlanOperation(
        IOperation operation,
        LoweringServices lowering)
    {
        if (operation is IDelegateCreationOperation creation)
        {
            lowering.PlanDelegateBridge(creation);
            return;
        }
        if (operation is IConversionOperation conversion
            && DelegateDemandPolicy.TryGetVariantConversion(
                lowering.Compilation, conversion, lowering.State.Types,
                lowering.State.TypeParamMap,
                out var outerInvoke, out var innerInvoke))
        {
            lowering.PlanWrapperSig(
                outerInvoke, innerInvoke,
                lowering.State.TypeParamMap);
            return;
        }
        if (operation is ICompoundAssignmentOperation compound
            && compound.OperatorKind
                is BinaryOperatorKind.Add
                or BinaryOperatorKind.Subtract
            && lowering.ResolveType(compound.Type)
                is INamedTypeSymbol delegateType
            && delegateType.DelegateInvokeMethod is { } invoke)
        {
            var signature = DelegateAbi.BuildSigPart(
                invoke, lowering.State.Types,
                lowering.State.TypeParamMap);
            lowering.PlanMulticastSig(
                signature, invoke,
                compound.OperatorKind == BinaryOperatorKind.Add
                    ? MulticastOperations.Combine
                    : MulticastOperations.Remove);
            return;
        }
        if (operation is IBinaryOperation binary
            && binary.OperatorKind
                is BinaryOperatorKind.Add
                or BinaryOperatorKind.Subtract
            && lowering.ResolveType(binary.Type)
                is INamedTypeSymbol binaryDelegate
            && binaryDelegate.DelegateInvokeMethod
                is { } binaryInvoke)
        {
            lowering.PlanMulticastSig(
                DelegateAbi.BuildSigPart(
                    binaryInvoke, lowering.State.Types,
                    lowering.State.TypeParamMap),
                binaryInvoke,
                binary.OperatorKind == BinaryOperatorKind.Add
                    ? MulticastOperations.Combine
                    : MulticastOperations.Remove);
            return;
        }
        if (operation is IEventAssignmentOperation eventAssignment
            && eventAssignment.EventReference
                is IEventReferenceOperation eventReference
            && eventReference.Event.Type is { } eventType
            && lowering.ResolveType(eventType)
                is INamedTypeSymbol eventDelegate
            && eventDelegate.DelegateInvokeMethod
                is { } eventInvoke)
        {
            lowering.PlanMulticastSig(
                DelegateAbi.BuildSigPart(
                    eventInvoke, lowering.State.Types,
                    lowering.State.TypeParamMap),
                eventInvoke,
                eventAssignment.Adds
                    ? MulticastOperations.Combine
                    : MulticastOperations.Remove);
            return;
        }
        if (operation is IInvocationOperation invocation
            && invocation.TargetMethod.Name == "ToString"
            && invocation.TargetMethod.Parameters.Length == 0
            && invocation.Instance != null)
        {
            var instanceType = lowering.ResolveType(
                invocation.Instance.Type);
            if (instanceType is INamedTypeSymbol nullable
                && nullable.OriginalDefinition.SpecialType
                == SpecialType.System_Nullable_T)
                instanceType = nullable.TypeArguments[0];
            lowering.PlanEnumToStringDemand(
                instanceType, rejectFlags: false);
            var isBase = invocation.Instance
                is IInstanceReferenceOperation
                {
                    Syntax:
                    Microsoft.CodeAnalysis.CSharp.Syntax
                        .BaseExpressionSyntax
                };
            var family = isBase
                         && invocation.Instance.Type?.SpecialType
                         == SpecialType.System_Object
                ? lowering.CurrentMethod?.ContainingType
                : invocation.Instance.Type;
            lowering.PlanClassToStringDemand(family);
        }
        else if (operation is IInterpolationOperation interpolation)
        {
            lowering.PlanEnumToStringDemand(
                interpolation.Expression.Type,
                rejectFlags: false);
            lowering.PlanClassToStringDemand(
                interpolation.Expression.Type);
        }
        else if (operation is IBinaryOperation
                 {
                     OperatorKind: BinaryOperatorKind.Add
                 } concat
                 && lowering.ResolveType(concat.Type)
                     ?.SpecialType == SpecialType.System_String)
        {
            var left =
                LoweringServices.UnwrapConcatOperand(
                    concat.LeftOperand)?.Type;
            var right =
                LoweringServices.UnwrapConcatOperand(
                    concat.RightOperand)?.Type;
            lowering.PlanEnumToStringDemand(
                left, rejectFlags: false);
            lowering.PlanEnumToStringDemand(
                right, rejectFlags: false);
            lowering.PlanClassToStringDemand(left);
            lowering.PlanClassToStringDemand(right);
        }
        else if (operation is ICompoundAssignmentOperation
                 {
                     OperatorKind: BinaryOperatorKind.Add
                 } compoundConcat
                 && lowering.ResolveType(
                         compoundConcat.Target.Type)
                     ?.SpecialType == SpecialType.System_String)
        {
            var value =
                LoweringServices.UnwrapConcatOperand(
                    compoundConcat.Value)?.Type;
            lowering.PlanEnumToStringDemand(
                value, rejectFlags: false);
            lowering.PlanClassToStringDemand(value);
        }
    }

    internal SyntheticDemandPlan PublishPlan()
    {
        RequireMutable();
        if (_expectedDelegateSites == null)
            throw new InvalidOperationException("Synthetic demand plan has no delegate-site census.");
        foreach (var site in _expectedDelegateSites)
            if (!_plannedDelegateSites.ContainsKey(site))
                throw new InvalidOperationException(
                    $"Delegate site '{site}' was not bound during synthetic demand planning.");
        var plan = new SyntheticDemandPlan(
            _closureBridgeFuncs,
            _plannedDelegateSites,
            _receiverBridges.Values,
            _delegateBridges.Values,
            _multicastSigs,
            _enumToString,
            _classToString,
            _sigAdapterBridges.Values,
            _wrapperSigs);
        _published = true;
        _closureBridgeFuncs.Clear();
        _plannedDelegateSites.Clear();
        _receiverBridges.Clear();
        _delegateBridges.Clear();
        _multicastSigs.Clear();
        _enumToString.Clear();
        _classToString.Clear();
        _sigAdapterBridges.Clear();
        _wrapperSigs.Clear();
        _expectedDelegateSites = null;
        return plan;
    }

    void RequireMutable()
    {
        if (_published)
            throw new InvalidOperationException(
                "Synthetic demands cannot change after publication.");
    }

    public void RegisterClosureBridge(string name, StructuredFunction function)
    {
        RequireMutable();
        if (function == null)
            throw new ArgumentNullException(nameof(function));
        _closureBridgeFuncs[name] = function;
    }

    public bool RegisterClosureBridgeAlias(
        string sourceName,
        string aliasName)
    {
        RequireMutable();
        if (!_closureBridgeFuncs.TryGetValue(
                sourceName, out var function))
            return false;
        RegisterClosureBridge(aliasName, function);
        return true;
    }

    public void RegisterReceiverBridge(DelegateBindingPlan binding)
    {
        RequireMutable();
        if (binding.Kind != DelegateBindingKind.Receiver)
            throw new ArgumentException("Receiver bridge demand requires a receiver binding.", nameof(binding));
        var demand = new DelegateBridgeDemand(
            binding, binding.TargetMethod, null);
        RegisterUnique(
            _receiverBridges, demand, "receiver bridge");
    }

    public void RegisterDelegateBridge(DelegateBindingPlan binding,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        RequireMutable();
        var demand = new DelegateBridgeDemand(
            binding, binding.TargetMethod, typeParamMap);
        RegisterUnique(
            _delegateBridges, demand, "delegate bridge");
    }

    public void RegisterSigAdapter(DelegateBindingPlan binding, IMethodSymbol invoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        RequireMutable();
        if (binding.Kind != DelegateBindingKind.SignatureAdapter)
            throw new ArgumentException("Signature adapter demand requires an adapter binding.", nameof(binding));
        var demand = new DelegateBridgeDemand(
            binding, invoke, typeParamMap);
        RegisterUnique(
            _sigAdapterBridges, demand, "signature adapter");
    }

    public void RegisterMulticast(string signature, IMethodSymbol invoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap,
        MulticastOperations operation)
    {
        RequireMutable();
        _multicastSigs[signature] = _multicastSigs.TryGetValue(signature, out var existing)
            ? existing.With(operation)
            : new MulticastSigPlan(invoke, typeParamMap, operation);
    }

    public void RegisterEnumToString(INamedTypeSymbol enumType)
    {
        RequireMutable();
        _enumToString.Add(enumType);
    }

    public void RegisterClassToString(INamedTypeSymbol classType)
    {
        RequireMutable();
        _classToString.Add(classType);
    }

    public void RegisterWrapper(DelegateBindingPlan binding, IMethodSymbol outerInvoke,
        IMethodSymbol innerInvoke,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeParamMap)
    {
        RequireMutable();
        if (binding.Kind != DelegateBindingKind.Wrapper)
            throw new ArgumentException("Wrapper demand requires a wrapper binding.", nameof(binding));
        if (!_wrapperSigs.ContainsKey(binding.BridgeName))
            _wrapperSigs.Add(binding.BridgeName, new DelegateWrapperDemand(
                binding,
                outerInvoke, innerInvoke, typeParamMap));
    }

    void RegisterUnique(Dictionary<string, DelegateBridgeDemand> demands,
        DelegateBridgeDemand demand, string category)
    {
        var name = demand.Binding.BridgeName;
        if (!demands.TryGetValue(name, out var existing))
        {
            demands.Add(name, demand);
            return;
        }
        RequireSameDemand(existing, demand, category);
    }

    static void RequireSameDemand(
        DelegateBridgeDemand existing,
        DelegateBridgeDemand demand,
        string category)
    {
        if (SameDemandMethod(
                existing.Binding.TargetMethod,
                demand.Binding.TargetMethod)
            && SameDemandMethod(
                existing.SignatureMethod,
                demand.SignatureMethod))
            return;
        var name = demand.Binding.BridgeName;
        throw new InvalidOperationException(
            $"Synthetic {category} name '{name}' maps to both "
            + $"'{existing.Binding.TargetMethod}' and '{demand.Binding.TargetMethod}'.");
    }

    static bool SameDemandMethod(IMethodSymbol left, IMethodSymbol right)
        => SymbolEqualityComparer.Default.Equals(left, right)
           || left != null && right != null
              && left.MethodKind is MethodKind.LambdaMethod or MethodKind.LocalFunction
              && right.MethodKind is MethodKind.LambdaMethod or MethodKind.LocalFunction
              && ClosureIdentityPlan.SameSourceDefinition(left, right);

    static bool SameBinding(DelegateBindingPlan left, DelegateBindingPlan right)
        => left != null && right != null
           && left.Kind == right.Kind
           && left.BridgeName == right.BridgeName
           && left.InnerBridgeName == right.InnerBridgeName
           && SameDemandMethod(left.TargetMethod, right.TargetMethod);
}

internal static class DelegateDemandPolicy
{
    public static bool TryGetVariantConversion(
        Compilation compilation,
        IConversionOperation conversion,
        IUdonTypeSystem types,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>
            typeParameterMap,
        out IMethodSymbol outerInvoke,
        out IMethodSymbol innerInvoke)
    {
        if (types == null)
            throw new ArgumentNullException(nameof(types));
        outerInvoke = null;
        innerInvoke = null;
        var destination = TypeEnvironment.CloseType(
                compilation, conversion.Type, typeParameterMap)
            as INamedTypeSymbol;
        var source = TypeEnvironment.CloseType(
                compilation, conversion.Operand.Type,
                typeParameterMap)
            as INamedTypeSymbol;
        if (destination?.DelegateInvokeMethod
                is not { } destinationInvoke
            || source?.DelegateInvokeMethod
                is not { } sourceInvoke
            || SymbolEqualityComparer.Default.Equals(
                destination, source)
            || DelegateAbi.BuildSigPart(
                   destinationInvoke, types, typeParameterMap)
               == DelegateAbi.BuildSigPart(
                   sourceInvoke, types, typeParameterMap))
            return false;
        outerInvoke = destinationInvoke;
        innerInvoke = sourceInvoke;
        return true;
    }
}
