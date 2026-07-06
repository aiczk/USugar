using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Emit-time boundary policy. Handlers should identify boundary sites and delegate the semantic decision
/// here, instead of open-coding class/delegate/env escape checks per syntax shape.
/// </summary>
public sealed class BoundaryChecker
{
    readonly EmitContext _ctx;

    public BoundaryChecker(EmitContext ctx) => _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    TypeClassifierContext TypeCtx => new TypeClassifierContext(_ctx.TypeParamMap);

    public bool DelegateValueCapturesProgramLocalPayload(IOperation value)
    {
        var v = UnwrapConversions(value);
        var closure = v switch
        {
            IDelegateCreationOperation dc => (dc.Target as IAnonymousFunctionOperation)?.Symbol
                                             ?? (dc.Target as IMethodReferenceOperation)?.Method,
            IAnonymousFunctionOperation af => af.Symbol,
            _ => null,
        };
        if (closure == null || _ctx.CaptureScope == null) return false;
        if (!_ctx.CaptureScope.ClosureScopes.TryGetValue(closure.OriginalDefinition, out var closureScope)
            || closureScope?.BindingScope == null)
            return false;
        for (var s = closureScope.BindingScope; s != null; s = _ctx.CaptureScope.EffectiveParent(s))
            foreach (var cap in s.OwnedCaptures)
            {
                var t = (cap as ILocalSymbol)?.Type ?? (cap as IParameterSymbol)?.Type;
                if (t != null && TypeClassifier.ContainsProgramLocalPayload(t, TypeCtx)) return true;
            }
        return false;
    }

    public bool IsNullDelegateValue(IOperation value)
    {
        var v = UnwrapConversions(value);
        return v.ConstantValue.HasValue && v.ConstantValue.Value == null;
    }

    public bool IsDirectProgramLocalSafeDelegateValue(IOperation value)
    {
        var v = UnwrapConversions(value);
        return v switch
        {
            IAnonymousFunctionOperation => !DelegateValueCapturesProgramLocalPayload(v),
            IDelegateCreationOperation dc when dc.Target is IAnonymousFunctionOperation
                => !DelegateValueCapturesProgramLocalPayload(dc),
            IDelegateCreationOperation { Target: IMethodReferenceOperation } => true,
            IMethodReferenceOperation => true,
            _ => false,
        };
    }

    public bool CurrentMethodBodyMentionsProgramLocalPayload()
    {
        var syntaxRef = _ctx.CurrentMethod?.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return false;
        var syntax = syntaxRef.GetSyntax();
        var model = _ctx.Compilation.GetSemanticModel(syntax.SyntaxTree);
        var bodyOp = model.GetOperation(syntax);
        if (bodyOp == null) return false;
        foreach (var op in bodyOp.DescendantsAndSelf())
        {
            ITypeSymbol type = op switch
            {
                ILocalReferenceOperation lr => lr.Local.Type,
                IParameterReferenceOperation pr => pr.Parameter.Type,
                IFieldReferenceOperation fr => fr.Field.Type,
                IVariableDeclaratorOperation vd => vd.Symbol.Type,
                _ => null,
            };
            if (type != null && TypeClassifier.ContainsProgramLocalPayload(type, TypeCtx))
                return true;
        }
        return false;
    }

    public void RequireCanStoreCrossProgramDelegate(IFieldReferenceOperation target, IOperation value)
    {
        if (!IsCrossProgramDelegateFieldTarget(target)) return;
        if (IsNullDelegateValue(value) || IsDirectProgramLocalSafeDelegateValue(value)) return;
        if (!DelegateValueCapturesProgramLocalPayload(value) && !CurrentMethodBodyMentionsProgramLocalPayload())
            return;
        throw new NotSupportedException(
            $"A delegate stored in the cross-program field '{target.Field.Name}' must be created directly "
            + "from a capture-safe lambda or method group at the write site. Delegate values copied from "
            + "locals, parameters, fields, calls, or other unclassified sources may carry a v1 user class "
            + "through their closure environment, and cannot cross a program boundary. Keep the delegate "
            + "field private, or assign a direct class-free lambda/method group.");
    }

    public void RequireCanStorePublicEventHandler(IEventSymbol evt, IOperation value)
    {
        if (evt.DeclaredAccessibility != Accessibility.Public || IsNullDelegateValue(value)
            || IsDirectProgramLocalSafeDelegateValue(value))
            return;
        if (!DelegateValueCapturesProgramLocalPayload(value) && !CurrentMethodBodyMentionsProgramLocalPayload())
            return;
        throw new NotSupportedException(
            $"A handler stored in the public event '{evt.Name}' must be created directly from a capture-safe "
            + "lambda or method group at the add/remove site. Delegate values copied from locals, parameters, "
            + "fields, calls, or other unclassified sources may carry a v1 user class through their closure "
            + "environment, and cannot cross a program boundary.");
    }

    public bool IsCrossProgramDelegateFieldTarget(IFieldReferenceOperation fieldRef)
    {
        if (fieldRef.Field.Type is not INamedTypeSymbol dft || dft.DelegateInvokeMethod == null) return false;
        if (fieldRef.Instance is not null and not IInstanceReferenceOperation)
            return ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType);
        return fieldRef.Field.DeclaredAccessibility == Accessibility.Public
            || fieldRef.Field.GetAttributes().Any(a =>
                a.AttributeClass?.Name is "SerializeField" or "SerializeFieldAttribute" or "UdonSyncedAttribute");
    }

    static IOperation UnwrapConversions(IOperation value)
    {
        var v = value;
        while (v is IConversionOperation c) v = c.Operand;
        return v;
    }
}
