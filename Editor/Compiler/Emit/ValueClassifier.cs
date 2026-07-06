using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public enum ValueKind
{
    Unknown,
    Null,
    Native,
    ProgramLocalPayload,
    Aggregate,
    ObjectArray,
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
    public readonly ITypeSymbol StaticType;
    public readonly ValueKind Kind;
    public readonly ValueProvenance Provenance;
    public readonly bool ContainsProgramLocalPayload;
    public readonly bool DelegateCapturesProgramLocalPayload;
    public readonly bool IsDirectDelegateValue;

    public ValueInfo(
        IOperation operation,
        ITypeSymbol staticType,
        ValueKind kind,
        ValueProvenance provenance,
        bool containsProgramLocalPayload,
        bool delegateCapturesProgramLocalPayload,
        bool isDirectDelegateValue)
    {
        Operation = operation;
        StaticType = staticType;
        Kind = kind;
        Provenance = provenance;
        ContainsProgramLocalPayload = containsProgramLocalPayload;
        DelegateCapturesProgramLocalPayload = delegateCapturesProgramLocalPayload;
        IsDirectDelegateValue = isDirectDelegateValue;
    }
}

public static class ValueClassifier
{
    public static ValueInfo Classify(
        IOperation value,
        TypeClassifierContext typeCtx,
        CaptureScopeAnalysis captureScope)
    {
        var unwrapped = UnwrapConversions(value);
        var staticType = unwrapped?.Type ?? value?.Type;
        if (unwrapped == null)
            return Create(null, staticType, ValueKind.Unknown, ValueProvenance.Unknown, false, false, false);

        if (unwrapped.ConstantValue.HasValue && unwrapped.ConstantValue.Value == null)
            return Create(unwrapped, staticType, ValueKind.Null, ValueProvenance.LiteralNull, false, false, false);

        if (TryGetDelegateTarget(unwrapped, out var target, out var provenance))
        {
            var capturesPayload = DelegateTargetCapturesProgramLocalPayload(target, captureScope, typeCtx);
            return Create(
                unwrapped,
                staticType,
                ValueKind.Delegate,
                provenance,
                capturesPayload,
                capturesPayload,
                IsDirectDelegateProvenance(provenance));
        }

        if (IsDelegateType(staticType))
            return Create(
                unwrapped,
                staticType,
                ValueKind.Delegate,
                ProvenanceOf(unwrapped),
                false,
                false,
                false);

        if (staticType == null)
            return Create(unwrapped, staticType, ValueKind.Unknown, ProvenanceOf(unwrapped), false, false, false);
        if (TypeClassifier.ContainsProgramLocalPayload(staticType, typeCtx))
            return Create(unwrapped, staticType, ValueKind.ProgramLocalPayload, ProvenanceOf(unwrapped), true, false, false);
        if (TypeClassifier.IsAggregateValue(staticType))
            return Create(unwrapped, staticType, ValueKind.Aggregate, ProvenanceOf(unwrapped), false, false, false);
        if (TypeClassifier.IsObjectArrayEmulated(staticType))
            return Create(unwrapped, staticType, ValueKind.ObjectArray, ProvenanceOf(unwrapped), false, false, false);

        return Create(unwrapped, staticType, ValueKind.Native, ProvenanceOf(unwrapped), false, false, false);
    }

    public static bool IsDirectProgramLocalSafeDelegate(ValueInfo info)
        => info.Kind == ValueKind.Delegate
           && info.IsDirectDelegateValue
           && !info.DelegateCapturesProgramLocalPayload;

    public static IOperation UnwrapConversions(IOperation value)
    {
        var v = value;
        while (v is IConversionOperation c) v = c.Operand;
        return v;
    }

    static ValueInfo Create(
        IOperation operation,
        ITypeSymbol staticType,
        ValueKind kind,
        ValueProvenance provenance,
        bool containsProgramLocalPayload,
        bool delegateCapturesProgramLocalPayload,
        bool isDirectDelegateValue)
        => new ValueInfo(
            operation,
            staticType,
            kind,
            provenance,
            containsProgramLocalPayload,
            delegateCapturesProgramLocalPayload,
            isDirectDelegateValue);

    static bool IsDelegateType(ITypeSymbol type)
        => type is INamedTypeSymbol named && named.DelegateInvokeMethod != null;

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

    static bool DelegateTargetCapturesProgramLocalPayload(
        IMethodSymbol target,
        CaptureScopeAnalysis captureScope,
        TypeClassifierContext typeCtx)
    {
        if (target == null || captureScope == null) return false;
        if (!captureScope.ClosureScopes.TryGetValue(target.OriginalDefinition, out var closureScope)
            || closureScope?.BindingScope == null)
            return false;
        for (var s = closureScope.BindingScope; s != null; s = captureScope.EffectiveParent(s))
            foreach (var cap in s.OwnedCaptures)
                if (CapturedSymbolType(cap) is { } t && TypeClassifier.ContainsProgramLocalPayload(t, typeCtx))
                    return true;
        return false;
    }

    static ITypeSymbol CapturedSymbolType(ISymbol symbol)
        => symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
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
