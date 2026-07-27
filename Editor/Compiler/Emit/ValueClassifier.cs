using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public enum ValueKind
{
    Other,
    Null,
    Delegate,
}

public enum ValueProvenance
{
    Unknown,
    LiteralNull,
    Conversion,
    DirectLambda,
    DirectMethodGroup,
    DelegateCreation,
    Local,
    Parameter,
    Field,
    Call,
}

public readonly struct ValueInfo
{
    public readonly IOperation Operation;
    public readonly ValueKind Kind;
    public readonly ValueProvenance Provenance;
    public readonly bool DelegateCapturesProgramLocalPayload;
    // Receiver-capture design v2 SS2(a): the delegate captures a value whose TYPE can hide arbitrary
    // payload from the static walk (delegate / object / object[] / delegate-containing aggregate) -
    // such a capture defeats type-based classification, so the value cannot be proven class-free.
    public readonly bool CapturesUnclassifiablePayload;
    public readonly bool IsDirectDelegateValue;
    public readonly RuntimeBundleKind CompilerBundle;
    public readonly bool MayContainCompilerBundle;

    public bool ContainsCompilerBundle
        => CompilerBundle != RuntimeBundleKind.None;

    public ValueInfo(
        IOperation operation,
        ValueKind kind,
        ValueProvenance provenance,
        bool delegateCapturesProgramLocalPayload,
        bool capturesUnclassifiablePayload,
        bool isDirectDelegateValue,
        RuntimeBundleKind compilerBundle,
        bool mayContainCompilerBundle)
    {
        Operation = operation;
        Kind = kind;
        Provenance = provenance;
        DelegateCapturesProgramLocalPayload = delegateCapturesProgramLocalPayload;
        CapturesUnclassifiablePayload = capturesUnclassifiablePayload;
        IsDirectDelegateValue = isDirectDelegateValue;
        CompilerBundle = compilerBundle;
        MayContainCompilerBundle = mayContainCompilerBundle;
    }
}

public static class ValueClassifier
{
    internal static ValueInfo ClassifyStable(
        IOperation value,
        TypeClassifierContext typeCtx,
        CaptureScopeAnalysis captureScope,
        IReadOnlyDictionary<ILocalSymbol, IOperation>
            stableLocalInitializers)
        => ClassifyStable(
            value, typeCtx, captureScope, stableLocalInitializers,
            new HashSet<ISymbol>(SymbolEqualityComparer.Default));

    static ValueInfo ClassifyStable(
        IOperation value,
        TypeClassifierContext typeCtx,
        CaptureScopeAnalysis captureScope,
        IReadOnlyDictionary<ILocalSymbol, IOperation>
            stableLocalInitializers,
        HashSet<ISymbol> tracing)
    {
        var unwrapped = UnwrapBoundaryConversions(value, typeCtx);
        if (unwrapped is ILocalReferenceOperation local
            && tracing.Add(local.Local)
            && stableLocalInitializers != null
            && stableLocalInitializers.TryGetValue(
                local.Local, out var initializer))
            return ClassifyStable(
                initializer, typeCtx, captureScope,
                stableLocalInitializers, tracing);
        return Classify(value, typeCtx, captureScope);
    }

    public static ValueInfo Classify(
        IOperation value,
        TypeClassifierContext typeCtx,
        CaptureScopeAnalysis captureScope)
    {
        var unwrapped = UnwrapBoundaryConversions(value, typeCtx);
        var staticType = unwrapped?.Type ?? value?.Type;
        if (unwrapped == null)
            return Create(
                null, ValueKind.Other, ValueProvenance.Unknown,
                false, false, false, RuntimeBundleKind.None,
                false);

        if (unwrapped.ConstantValue.HasValue && unwrapped.ConstantValue.Value == null)
            return Create(
                unwrapped, ValueKind.Null,
                ValueProvenance.LiteralNull, false, false, false,
                RuntimeBundleKind.None, false);

        var compilerBundle = CompilerBundleOf(
            unwrapped, typeCtx);
        var mayContainCompilerBundle =
            compilerBundle != RuntimeBundleKind.None
            || MayContainErasedCompilerBundle(
                unwrapped, typeCtx);

        if (TryGetDelegateTarget(unwrapped, out var target, out var provenance))
        {
            var (capturesPayload, capturesUnclassifiable) = DelegateTargetCaptureFlags(target, captureScope, typeCtx);
            return Create(
                unwrapped,
                ValueKind.Delegate,
                provenance,
                capturesPayload,
                capturesUnclassifiable,
                IsDirectDelegateProvenance(provenance),
                compilerBundle,
                mayContainCompilerBundle);
        }

        if (IsDelegateType(staticType))
            return Create(
                unwrapped, ValueKind.Delegate,
                ProvenanceOf(unwrapped), false, false, false,
                compilerBundle,
                mayContainCompilerBundle);

        return Create(
            unwrapped, ValueKind.Other,
            ProvenanceOf(unwrapped), false, false, false,
            compilerBundle,
            mayContainCompilerBundle);
    }

    public static bool IsDirectProgramLocalSafeDelegate(ValueInfo info)
        => info.Kind == ValueKind.Delegate
           && info.IsDirectDelegateValue
           && !info.DelegateCapturesProgramLocalPayload
           // v2 SS2(a): a delegate/object-typed capture can smuggle a program-local payload past the
           // type walk (transitive laundering, gates-audit probe) - not provably class-free.
           && !info.CapturesUnclassifiablePayload;

    public static IOperation UnwrapConversions(IOperation value)
    {
        var v = value;
        while (v is IConversionOperation c) v = c.Operand;
        return v;
    }

    static IOperation UnwrapBoundaryConversions(
        IOperation value,
        TypeClassifierContext typeCtx)
    {
        var current = value;
        while (current is IConversionOperation conversion)
        {
            var destination = conversion.Type;
            var destinationShape = destination == null
                ? default
                : TypeClassifier.ShapeOf(destination, typeCtx);
            // An explicit cast to a concrete native type is the source-level
            // type assertion which permits the value to enter that type's ABI.
            // Implicit erasure, object/object[], compiler bundles, and user
            // interfaces add no such proof and keep tracing to their carrier.
            if (!conversion.IsImplicit
                && destination != null
                && destination.TypeKind != TypeKind.Interface
                && !destinationShape.IsBundle
                && !destinationShape.ContainsUserClassPayload
                && !destinationShape.ContainsOpaqueObject)
                break;
            current = conversion.Operand;
        }
        return current;
    }

    static ValueInfo Create(
        IOperation operation,
        ValueKind kind,
        ValueProvenance provenance,
        bool delegateCapturesProgramLocalPayload,
        bool capturesUnclassifiablePayload,
        bool isDirectDelegateValue,
        RuntimeBundleKind compilerBundle,
        bool mayContainCompilerBundle)
        => new ValueInfo(
            operation,
            kind,
            provenance,
            delegateCapturesProgramLocalPayload,
            capturesUnclassifiablePayload,
            isDirectDelegateValue,
            compilerBundle,
            mayContainCompilerBundle);

    static bool IsDelegateType(ITypeSymbol type)
        => type is INamedTypeSymbol named && named.DelegateInvokeMethod != null;

    static RuntimeBundleKind CompilerBundleOf(
        IOperation value,
        TypeClassifierContext typeCtx)
    {
        if (value == null) return RuntimeBundleKind.None;
        var direct = value.Type == null
            ? RuntimeBundleKind.None
            : TypeClassifier.ShapeOf(value.Type, typeCtx).Bundle;
        if (direct != RuntimeBundleKind.None)
            return direct;

        IEnumerable<IOperation> carriers = value switch
        {
            IArrayCreationOperation array
                when array.Initializer != null
                => array.Initializer.ElementValues,
            IConditionalOperation conditional
                => new[]
                {
                    conditional.WhenTrue,
                    conditional.WhenFalse
                },
            ICoalesceOperation coalesce
                => new[]
                {
                    coalesce.Value,
                    coalesce.WhenNull
                },
            ISwitchExpressionOperation switchExpression
                => switchExpression.Arms.Select(arm => arm.Value),
            IConditionalAccessOperation conditionalAccess
                => new[] { conditionalAccess.WhenNotNull },
            _ => Array.Empty<IOperation>()
        };
        foreach (var carrier in carriers)
        {
            var nested = CompilerBundleOf(
                UnwrapConversions(carrier), typeCtx);
            if (nested != RuntimeBundleKind.None)
                return nested;
        }
        return RuntimeBundleKind.None;
    }

    static bool MayContainErasedCompilerBundle(
        IOperation value,
        TypeClassifierContext typeCtx)
    {
        if (value == null) return false;
        var shape = value.Type == null
            ? default
            : TypeClassifier.ShapeOf(value.Type, typeCtx);
        if (!shape.ContainsOpaqueObject)
            return false;

        switch (value)
        {
            case ILiteralOperation:
            case IDefaultValueOperation:
            case IObjectCreationOperation:
                return false;
            case IArrayCreationOperation array:
                return array.Initializer?.ElementValues.Any(
                    element =>
                        CompilerBundleOf(
                            UnwrapConversions(element),
                            typeCtx)
                        != RuntimeBundleKind.None
                        || MayContainErasedCompilerBundle(
                            UnwrapConversions(element),
                            typeCtx)) == true;
            case IConditionalOperation conditional:
                return MayContainErasedCompilerBundle(
                           UnwrapConversions(
                               conditional.WhenTrue), typeCtx)
                       || MayContainErasedCompilerBundle(
                           UnwrapConversions(
                               conditional.WhenFalse), typeCtx);
            case ICoalesceOperation coalesce:
                return MayContainErasedCompilerBundle(
                           UnwrapConversions(coalesce.Value),
                           typeCtx)
                       || MayContainErasedCompilerBundle(
                           UnwrapConversions(coalesce.WhenNull),
                           typeCtx);
            case ISwitchExpressionOperation switchExpression:
                return switchExpression.Arms.Any(
                    arm => MayContainErasedCompilerBundle(
                        UnwrapConversions(arm.Value), typeCtx));
            case IConditionalAccessOperation conditionalAccess:
                return MayContainErasedCompilerBundle(
                    UnwrapConversions(conditionalAccess.WhenNotNull),
                    typeCtx);
            case IFieldReferenceOperation field:
                return field.Field.DeclaringSyntaxReferences
                    .Length != 0;
            case IPropertyReferenceOperation property:
                return property.Property.DeclaringSyntaxReferences
                    .Length != 0;
            case IInvocationOperation invocation:
                return invocation.TargetMethod.MethodKind
                           == MethodKind.DelegateInvoke
                       || (invocation.TargetMethod
                               .DeclaringSyntaxReferences.Length != 0
                           && !ExternResolver.IsSdkNamespace(
                               invocation.TargetMethod
                                   .ContainingNamespace));
            case ILocalReferenceOperation:
            case IParameterReferenceOperation:
            case IArrayElementReferenceOperation:
                return true;
            default:
                return true;
        }
    }

    static bool TryGetDelegateTarget(IOperation value, out IMethodSymbol target, out ValueProvenance provenance)
    {
        switch (value)
        {
            case IDelegateCreationOperation { Target: IAnonymousFunctionOperation af }:
                target = af.Symbol;
                provenance = ValueProvenance.DirectLambda;
                return true;
            case IDelegateCreationOperation { Target: IMethodReferenceOperation mr }:
                target = mr.Method;
                provenance = ValueProvenance.DirectMethodGroup;
                return true;
            case IDelegateCreationOperation:
                target = null;
                provenance = ValueProvenance.DelegateCreation;
                return false;
            case IAnonymousFunctionOperation af:
                target = af.Symbol;
                provenance = ValueProvenance.DirectLambda;
                return true;
            case IMethodReferenceOperation mr:
                target = mr.Method;
                provenance = ValueProvenance.DirectMethodGroup;
                return true;
            default:
                target = null;
                provenance = ValueProvenance.Unknown;
                return false;
        }
    }

    static (bool Payload, bool Unclassifiable) DelegateTargetCaptureFlags(
        IMethodSymbol target,
        CaptureScopeAnalysis captureScope,
        TypeClassifierContext typeCtx)
    {
        if (target == null || captureScope == null) return (false, false);
        // A struct-instance method group carries its bind-time receiver copy in DelegateAbi.Env.
        // Its aggregate identity is collapsed, so it remains program-local. A user-class receiver
        // uses the portable tagged class ABI and may cross with the delegate bundle.
        if (!target.IsStatic
            && target.MethodKind is not (MethodKind.LambdaMethod or MethodKind.LocalFunction)
            && target.ContainingType is INamedTypeSymbol recvCt && TypeClassifier.IsUserStruct(recvCt))
            return (true, true);
        if (!captureScope.ClosureScopes.TryGetValue(target.OriginalDefinition, out var closureScope)
            || closureScope?.BindingScope == null)
            return (false, false);
        bool payload = false, unclassifiable = false;
        for (var s = closureScope.BindingScope; s != null; s = captureScope.EffectiveParent(s))
            foreach (var cap in s.OwnedCaptures)
            {
                if (CapturedSymbolType(cap) is not { } t) continue;
                if (TypeClassifier.ContainsUserClassPayload(t, typeCtx)) payload = true;
                if (IsUnclassifiableCarrierType(t)) unclassifiable = true;
            }
        return (payload, unclassifiable);
    }

    // A capture of this TYPE can carry arbitrary hidden payload (a delegate's env, a boxed value,
    // an object[] cell) that the static type walk cannot see - classification cannot prove it clean.
    // CW28: both legs recurse aggregate fields — the delegate leg always did, but the object/object[]
    // legs tested only the capture's top-level type, so `struct Box { object o; }` laundered the same
    // payload its unwrapped `object` twin loudly trips on (probe-proven struct-wrapping smuggle).
    static bool IsUnclassifiableCarrierType(ITypeSymbol t)
    {
        var shape = TypeClassifier.ShapeOf(t, new TypeClassifierContext(null));
        return shape.ContainsDelegate || shape.ContainsOpaqueObject;
    }

    static ITypeSymbol CapturedSymbolType(ISymbol symbol)
        => symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            // Class receiver capture (design 2026-07-10 v2 §1.6, SOUNDNESS GATE): the synthetic
            // receiver key carries the v1 class itself — without this arm a receiver-capturing
            // delegate would classify clean and silently cross a program boundary.
            IMethodSymbol member => member.ContainingType,
            _ => null,
        };

    static bool IsDirectDelegateProvenance(ValueProvenance provenance)
        => provenance is ValueProvenance.DirectLambda or ValueProvenance.DirectMethodGroup;

    static ValueProvenance ProvenanceOf(IOperation value)
        => value switch
        {
            IConversionOperation => ValueProvenance.Conversion,
            ILocalReferenceOperation => ValueProvenance.Local,
            IParameterReferenceOperation => ValueProvenance.Parameter,
            IFieldReferenceOperation => ValueProvenance.Field,
            IInvocationOperation => ValueProvenance.Call,
            IDelegateCreationOperation => ValueProvenance.DelegateCreation,
            _ => ValueProvenance.Unknown,
        };
}

/// <summary>
/// Frozen value-provenance and method-payload decisions for every operation
/// specialization consumed by body lowering.
/// </summary>
internal sealed class BoundValueTable
{
    readonly Dictionary<
        IOperation,
        Dictionary<CallSiteBindingScope, ValueInfo>> _values;
    readonly Dictionary<CallSiteBindingScope, bool>
        _methodMentionsProgramLocalPayload;

    internal BoundValueTable(
        IEnumerable<(
            IOperation Operation,
            CallSiteBindingScope Scope,
            ValueInfo Value)> values,
        IDictionary<CallSiteBindingScope, bool>
            methodMentionsProgramLocalPayload)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));
        _values = new Dictionary<
            IOperation,
            Dictionary<CallSiteBindingScope, ValueInfo>>(
            OperationReferenceComparer.Instance);
        foreach (var entry in values)
        {
            if (!_values.TryGetValue(
                    entry.Operation, out var byScope))
            {
                byScope =
                    new Dictionary<CallSiteBindingScope, ValueInfo>();
                _values.Add(entry.Operation, byScope);
            }
            if (!byScope.TryAdd(entry.Scope, entry.Value))
                throw new InvalidOperationException(
                    $"Value semantics '{entry.Operation?.Syntax}' "
                    + "were materialized twice.");
        }
        _methodMentionsProgramLocalPayload =
            new Dictionary<CallSiteBindingScope, bool>(
                methodMentionsProgramLocalPayload
                ?? throw new ArgumentNullException(
                    nameof(methodMentionsProgramLocalPayload)));
    }

    internal ValueInfo Require(
        IOperation operation,
        CallSiteBindingScope? scope)
    {
        if (operation != null
            && scope.HasValue
            && _values.TryGetValue(operation, out var byScope)
            && byScope.TryGetValue(scope.Value, out var value))
            return value;
        throw new InvalidOperationException(
            $"Value semantics '{operation?.Syntax}' were absent "
            + "from the bound program.");
    }

    internal bool MethodMentionsProgramLocalPayload(
        CallSiteBindingScope? scope)
    {
        if (!scope.HasValue)
            throw new InvalidOperationException(
                "Method payload semantics were requested outside "
                + "a bound semantic scope.");
        if (_methodMentionsProgramLocalPayload.TryGetValue(
                scope.Value, out var result))
            return result;
        throw new InvalidOperationException(
            $"Method payload semantics '{scope.Value}' were absent "
            + "from the bound program.");
    }

    sealed class OperationReferenceComparer
        : IEqualityComparer<IOperation>
    {
        internal static readonly OperationReferenceComparer Instance = new();

        public bool Equals(IOperation x, IOperation y)
            => ReferenceEquals(x, y);

        public int GetHashCode(IOperation obj)
            => System.Runtime.CompilerServices.RuntimeHelpers
                .GetHashCode(obj);
    }
}
