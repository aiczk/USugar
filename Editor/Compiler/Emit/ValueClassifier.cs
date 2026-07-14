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
    // Receiver-capture design v2 SS2(a): the delegate captures a value whose TYPE can hide arbitrary
    // payload from the static walk (delegate / object / object[] / delegate-containing aggregate) -
    // such a capture defeats type-based classification, so the value cannot be proven class-free.
    public readonly bool CapturesUnclassifiablePayload;
    public readonly bool IsDirectDelegateValue;

    public ValueInfo(
        IOperation operation,
        ITypeSymbol staticType,
        ValueKind kind,
        ValueProvenance provenance,
        bool containsProgramLocalPayload,
        bool delegateCapturesProgramLocalPayload,
        bool capturesUnclassifiablePayload,
        bool isDirectDelegateValue)
    {
        Operation = operation;
        StaticType = staticType;
        Kind = kind;
        Provenance = provenance;
        ContainsProgramLocalPayload = containsProgramLocalPayload;
        DelegateCapturesProgramLocalPayload = delegateCapturesProgramLocalPayload;
        CapturesUnclassifiablePayload = capturesUnclassifiablePayload;
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
            return Create(null, staticType, ValueKind.Unknown, ValueProvenance.Unknown, false, false, false, false);

        if (unwrapped.ConstantValue.HasValue && unwrapped.ConstantValue.Value == null)
            return Create(unwrapped, staticType, ValueKind.Null, ValueProvenance.LiteralNull, false, false, false, false);

        if (TryGetDelegateTarget(unwrapped, out var target, out var provenance))
        {
            var (capturesPayload, capturesUnclassifiable) = DelegateTargetCaptureFlags(target, captureScope, typeCtx);
            return Create(
                unwrapped,
                staticType,
                ValueKind.Delegate,
                provenance,
                capturesPayload,
                capturesPayload,
                capturesUnclassifiable,
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
                false,
                false);

        if (staticType == null)
            return Create(unwrapped, staticType, ValueKind.Unknown, ProvenanceOf(unwrapped), false, false, false, false);
        if (TypeClassifier.ContainsProgramLocalPayload(staticType, typeCtx))
            return Create(unwrapped, staticType, ValueKind.ProgramLocalPayload, ProvenanceOf(unwrapped), true, false, false, false);
        if (TypeClassifier.IsAggregateValue(staticType))
            return Create(unwrapped, staticType, ValueKind.Aggregate, ProvenanceOf(unwrapped), false, false, false, false);
        if (TypeClassifier.IsObjectArrayEmulated(staticType))
            return Create(unwrapped, staticType, ValueKind.ObjectArray, ProvenanceOf(unwrapped), false, false, false, false);

        return Create(unwrapped, staticType, ValueKind.Native, ProvenanceOf(unwrapped), false, false, false, false);
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

    static ValueInfo Create(
        IOperation operation,
        ITypeSymbol staticType,
        ValueKind kind,
        ValueProvenance provenance,
        bool containsProgramLocalPayload,
        bool delegateCapturesProgramLocalPayload,
        bool capturesUnclassifiablePayload,
        bool isDirectDelegateValue)
        => new ValueInfo(
            operation,
            staticType,
            kind,
            provenance,
            containsProgramLocalPayload,
            delegateCapturesProgramLocalPayload,
            capturesUnclassifiablePayload,
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

    static (bool Payload, bool Unclassifiable) DelegateTargetCaptureFlags(
        IMethodSymbol target,
        CaptureScopeAnalysis captureScope,
        TypeClassifierContext typeCtx)
    {
        if (target == null || captureScope == null) return (false, false);
        // MG auto-wrap (design 2026-07-11 v2, FATAL amendment): a class/struct-INSTANCE method group
        // carries its RECEIVER object[] in DelegateAbi.Env — program-local payload the capture walk
        // below cannot see (the target is a named member, not a hoisted closure with a receiver key).
        // Without this arm the bundle classifies direct-safe and the receiver silently crosses the
        // program boundary. Conservative for structs too (widening is a separate decision).
        if (!target.IsStatic
            && target.MethodKind is not (MethodKind.LambdaMethod or MethodKind.LocalFunction)
            && target.ContainingType is INamedTypeSymbol recvCt && EmitPolicy.IsObjectArrayEmulated(recvCt))
            return (true, true);
        if (!captureScope.ClosureScopes.TryGetValue(target.OriginalDefinition, out var closureScope)
            || closureScope?.BindingScope == null)
            return (false, false);
        bool payload = false, unclassifiable = false;
        for (var s = closureScope.BindingScope; s != null; s = captureScope.EffectiveParent(s))
            foreach (var cap in s.OwnedCaptures)
            {
                if (CapturedSymbolType(cap) is not { } t) continue;
                if (TypeClassifier.ContainsProgramLocalPayload(t, typeCtx)) payload = true;
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
        => EmitPolicy.ContainsDelegateType(t) || EmitPolicy.ContainsOpaqueObjectType(t);

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
