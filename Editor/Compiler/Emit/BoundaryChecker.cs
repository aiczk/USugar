using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public enum BoundarySite
{
    CrossBehaviourFieldWrite,
    CrossBehaviourFieldRead,
    CrossBehaviourArgument,
}

/// <summary>
/// Emit-time boundary policy. Handlers should identify boundary sites and delegate the semantic decision
/// here, instead of open-coding class/delegate/env escape checks per syntax shape.
/// </summary>
public sealed class BoundaryChecker
{
    readonly EmitContext _ctx;

    public BoundaryChecker(EmitContext ctx) => _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    TypeClassifierContext TypeCtx => new TypeClassifierContext(_ctx.Generics.TypeParamMap);

    public ValueInfo ClassifyValue(IOperation value)
        => ValueClassifier.Classify(value, TypeCtx, _ctx.Closures.CaptureScope);

    public bool CurrentMethodBodyMentionsProgramLocalPayload()
    {
        var syntaxRef = _ctx.Methods.CurrentMethod?.DeclaringSyntaxReferences.FirstOrDefault();
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

    /// <summary>Report any delegate store target here; non-cross-program targets (locals, private
    /// this-fields, struct/class members, declarations) are ignored. The handler only names the
    /// syntax — whether the surface crosses a program boundary is decided here.</summary>
    public void RequireCanStoreCrossProgramDelegate(IOperation target, ValueInfo info)
    {
        switch (target)
        {
            case IFieldReferenceOperation f when IsCrossProgramDelegateFieldTarget(f):
                RequireDelegateValueSafeForCrossProgramStore(info,
                    $"the cross-program field '{f.Field.Name}'", "the write site",
                    "Keep the delegate field private, or assign a direct class-free lambda/method group.");
                break;
            case IPropertyReferenceOperation p when IsCrossProgramDelegatePropertyTarget(p):
                RequireDelegateValueSafeForCrossProgramStore(info,
                    $"the cross-program property '{p.Property.Name}'", "the write site",
                    "Keep the property non-public, or assign a direct class-free lambda/method group.");
                break;
        }
    }

    public void RequireCanStorePublicEventHandler(IEventSymbol evt, ValueInfo info)
    {
        if (evt.DeclaredAccessibility != Accessibility.Public) return;
        RequireDelegateValueSafeForCrossProgramStore(info,
            $"the public event '{evt.Name}'", "the add/remove site", null);
    }

    void RequireDelegateValueSafeForCrossProgramStore(ValueInfo info, string surface, string site, string advice)
    {
        if (info.Kind == ValueKind.Null || ValueClassifier.IsDirectProgramLocalSafeDelegate(info)) return;
        if (!info.DelegateCapturesProgramLocalPayload && !CurrentMethodBodyMentionsProgramLocalPayload())
            return;
        throw new NotSupportedException(
            $"A delegate stored in {surface} must be created directly from a capture-safe lambda or "
            + $"method group at {site}. Delegate values copied from locals, parameters, fields, calls, "
            + "or other unclassified sources may carry a v1 user class through their closure environment, "
            + "and cannot cross a program boundary."
            + (advice == null ? "" : " " + advice));
    }

    public void RequireCanEraseProgramLocalPayload(IConversionOperation conversion,
        ITypeSymbol sourceType, ITypeSymbol destinationType)
    {
        if (!TypeClassifier.ContainsProgramLocalPayload(sourceType, TypeCtx)
            || TypeClassifier.ContainsProgramLocalPayload(destinationType, TypeCtx)
            || IsProgramLocalEqualityPosition(conversion))
            return;
        throw new NotSupportedException(
            $"Erasing the v1 user class '{sourceType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' "
            + $"to '{destinationType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' is not supported: "
            + "a class value is a program-local object[] bundle with no runtime type identity, so once boxed "
            + "to object it launders past the cross-program / cast / ToString boundary checks. Compare class "
            + "references directly, or keep the value class-typed / use Foo[].");
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

    /// <summary>A delegate-typed property whose storage is cross-program addressable: any property on
    /// another behaviour/interface instance (the set lands via SetProgramVariable / SendCustomEvent),
    /// or a public property on this behaviour (exported accessors + name-addressable backing symbol).
    /// Struct/class (object[]-emulated) containers are program-local slot writes and never match.</summary>
    public bool IsCrossProgramDelegatePropertyTarget(IPropertyReferenceOperation propRef)
    {
        if (propRef.Property.Type is not INamedTypeSymbol dpt || dpt.DelegateInvokeMethod == null) return false;
        var containing = propRef.Property.ContainingType;
        if (containing == null || EmitPolicy.IsObjectArrayEmulated(containing)) return false;
        if (propRef.Instance is not null and not IInstanceReferenceOperation)
            return ExternResolver.IsUdonSharpBehaviour(containing)
                || (containing.TypeKind == TypeKind.Interface && containing.SpecialType == SpecialType.None);
        return ExternResolver.IsUdonSharpBehaviour(containing)
            && propRef.Property.DeclaredAccessibility == Accessibility.Public;
    }

    static bool IsProgramLocalEqualityPosition(IConversionOperation conv)
    {
        switch (conv.Parent)
        {
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals }:
                return true;
            case IArgumentOperation { Parent: IInvocationOperation inv }
                when inv.TargetMethod.Name == "Equals"
                    && inv.TargetMethod.ContainingType.SpecialType
                        is SpecialType.System_Object or SpecialType.System_ValueType:
                return true;
            default:
                return false;
        }
    }

    public void RequireCanWriteCrossBehaviourField(IFieldSymbol field)
        => RequireNoProgramLocalPayload(BoundarySite.CrossBehaviourFieldWrite, field.Type, field.Name);

    public void RequireCanReadCrossBehaviourField(IFieldSymbol field)
        => RequireNoProgramLocalPayload(BoundarySite.CrossBehaviourFieldRead, field.Type, field.Name);

    public void RequireCanPassCrossBehaviourArgument(ITypeSymbol argType)
        => RequireNoProgramLocalPayload(BoundarySite.CrossBehaviourArgument, argType, null);

    void RequireNoProgramLocalPayload(BoundarySite site, ITypeSymbol type, string memberName)
    {
        if (!TypeClassifier.ContainsProgramLocalPayload(type, TypeCtx)) return;
        throw new NotSupportedException(ProgramLocalPayloadMessage(site, memberName));
    }

    static string ProgramLocalPayloadMessage(BoundarySite site, string memberName)
    {
        switch (site)
        {
            case BoundarySite.CrossBehaviourFieldWrite:
                return $"A v1 user class cannot be written to another behaviour's field '{memberName}': a class "
                       + "value is a program-local object[] bundle and cannot cross a program boundary.";
            case BoundarySite.CrossBehaviourFieldRead:
                return $"Reading another behaviour's field '{memberName}' that carries a v1 user class "
                       + "is not supported: a class value is a program-local object[] bundle and cannot cross a "
                       + "program boundary.";
            case BoundarySite.CrossBehaviourArgument:
                return "A v1 user class cannot be passed to a cross-behaviour (SendCustomEvent) call: a class "
                       + "value is a program-local object[] bundle and cannot cross a program boundary. Pass "
                       + "plain data instead and rebuild the object on the receiving side.";
            default:
                throw new ArgumentOutOfRangeException(nameof(site), site, null);
        }
    }
}
