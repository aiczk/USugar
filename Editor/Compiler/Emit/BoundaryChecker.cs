using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public enum BoundarySite
{
    CrossBehaviourFieldWrite,
    CrossBehaviourFieldRead,
    CrossBehaviourArgument,
    CrossBehaviourPropertyWrite,
    CrossBehaviourPropertyRead,
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
        // Receiver-capture design v2 SS2(b): inside a v1-class instance member the receiver itself is
        // a program-local payload in scope - an unclassifiable delegate store from here cannot be
        // proven class-free (bounded conservative polarity; over-reject is the accepted trade).
        if (LambdaCaptureAnalyzer.ReceiverCaptureKey(_ctx.Methods.CurrentMethod) != null) return true;
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
            // CW8: an element write stores through the array reference into the root field's exported
            // storage — same surface as assigning the scalar twin, one index deeper.
            case IArrayElementReferenceOperation e when ArrayRootFieldReference(e) is { } rootF
                && IsCrossProgramDelegateFieldTarget(rootF):
                RequireDelegateValueSafeForCrossProgramStore(info,
                    $"an element of the cross-program array field '{rootF.Field.Name}'", "the write site",
                    "Keep the delegate array private, or assign a direct class-free lambda/method group.");
                break;
        }
    }

    /// <summary>CW7/CW23: a cross-program call argument IS a delegate store — the pair becomes a
    /// SetProgramVariable into the foreign program's param var — so the argument surface runs the
    /// same value-classification ladder as the store surfaces (the MG-autowrap design pins "class
    /// payload cross-program stays rejected"). Type-only checking saw a clean Action signature,
    /// let a class-capturing env cross, and the callee-side re-store classified Parameter/
    /// unclassifiable — laundering the bundle back into the guarded fields one hop later.</summary>
    public void RequireCanPassCrossProgramDelegateArgument(IArgumentOperation arg)
    {
        var argType = arg.Value?.Type ?? arg.Parameter?.Type;
        if (argType == null || !EmitPolicy.ContainsDelegateType(argType)) return;
        RequireDelegateValueSafeForCrossProgramStore(ClassifyValue(arg.Value),
            $"the cross-program argument '{arg.Parameter?.Name ?? "?"}'", "the call site",
            "Pass a direct class-free lambda/method group, or keep the call within this behaviour.");
    }

    /// <summary>Walks nested element/conversion links to the array's root field (the
    /// TryGetStaticReadonlyWriteThroughRoot walk, field-reference flavor); null when the array is
    /// not rooted at a field (local/param/fresh value — program-local storage, no hazard).</summary>
    static IFieldReferenceOperation ArrayRootFieldReference(IArrayElementReferenceOperation elem)
    {
        IOperation op = elem.ArrayReference;
        while (true)
        {
            switch (op)
            {
                case IConversionOperation c:
                    op = c.Operand; continue;
                case IArrayElementReferenceOperation ae:
                    op = ae.ArrayReference; continue;
                case IFieldReferenceOperation fr:
                    return fr;
                default:
                    return null;
            }
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
        // A copied delegate has lost its creation-site capture proof. Inspecting only this method's body
        // is unsound: a caller can pass a class-capturing lambda through a clean Action parameter and the
        // helper can then publish it. Until value-flow carries capture taint across calls, only a direct
        // creation at the boundary is provably transport-safe.
        if (!info.IsDirectDelegateValue)
            throw new NotSupportedException(
                $"A delegate stored in {surface} must be created directly from a capture-safe lambda or "
                + $"method group at {site}. The copied {info.Provenance.ToString().ToLowerInvariant()} value "
                + "has no creation-site capture proof and may carry a program-local payload."
                + (advice == null ? "" : " " + advice));
        if (!info.DelegateCapturesProgramLocalPayload && !CurrentMethodBodyMentionsProgramLocalPayload()) return;
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
        var sourceShape = TypeClassifier.ShapeOf(sourceType, TypeCtx);
        var destinationShape = TypeClassifier.ShapeOf(destinationType, TypeCtx);
        // Phase-A armor (B82 mirror for N-R1): a Rank>1 array's runtime value is an object[] bundle, and
        // the extern-boundary choke keys on the ARGUMENT's unwrapped static type — erasing the T[,] to
        // object/Array first launders the bundle past externs the direct form loudly rejects. Contain at
        // the erasure. Cross-behaviour transport is compiler-generated typed member access (never a user
        // conversion), cast-BACK (object → T[,]) does not erase, and the equality position compares
        // bundle references exactly like array references — all stay legal.
        if (sourceShape.Bundle == RuntimeBundleKind.MultiDimensionalArray
            && destinationShape.Bundle != RuntimeBundleKind.MultiDimensionalArray
            && !IsProgramLocalEqualityPosition(conversion))
            throw new NotSupportedException(
                $"Erasing the multi-dimensional array '{sourceType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' "
                + $"to '{destinationType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' is not supported: "
                + "its runtime value is an object[] bundle, not a real array, so once widened to object/Array it "
                + "reaches extern calls (Debug.Log, string.Format, Array statics, …) that would silently receive "
                + "the wrong shape. Keep the value typed as its T[,] type, or use a jagged array.");

        // WaveJoint R2 [D10]: an object[]-emulated VALUE type (user struct / tuple / anonymous type)
        // erased to object launders exactly like its ndim and v1-class twins — the box's runtime tag is
        // a plain object[], so a later stringify silently prints "System.Object[]" and a cast back to a
        // DIFFERENT bundle type reinterprets silently. No downstream surface can tell the laundered
        // bundle from a real object[], so the erasure conversion is the only sound reject point. The
        // equality/Equals positions stay legal (the class arm's carve-out), and T → T? is a WRAP whose
        // static type still names the aggregate, not an erasure.
        if (sourceShape.Bundle == RuntimeBundleKind.Aggregate
            && !destinationShape.IsBundle
            && !(EmitPolicy.IsNullableT(destinationType, out var wrapped) && TypeClassifier.IsObjectArrayEmulated(wrapped))
            && !IsProgramLocalEqualityPosition(conversion))
            throw new NotSupportedException(
                $"Erasing the value type '{sourceType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' "
                + $"to '{destinationType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' is not supported: "
                + "its runtime value is an object[] bundle with no runtime type identity, so once boxed to object it "
                + "launders past the cast / ToString / extern boundary checks and would stringify as \"System.Object[]\" "
                + "or silently reinterpret when cast back. Keep the value typed as its struct/tuple type.");

        if (!sourceShape.ContainsProgramLocalPayload
            || destinationShape.ContainsProgramLocalPayload
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
        // CW8: an ARRAY of delegates is the same exported SystemObjectArray surface as a scalar
        // delegate field (DeclareDelegateField intercepts only direct delegate types, so Action[]
        // declares as an ordinary exported var a foreign program GetProgramVariable-reads and
        // invokes element-wise) — walk the element chain so the array flavor fences like its twin.
        if (!IsDelegateCarryingStorageType(fieldRef.Field.Type)) return false;
        // Only a BEHAVIOUR's storage is a program surface — a v1 class / struct field is program-local
        // regardless of accessibility (a class is not a program; its bundle has no exported symbols).
        // Without this gate, `F = lambda` inside a class member treats the class's own public delegate
        // field as cross-program and over-rejects (receiver-capture M2).
        if (!ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType)) return false;
        if (fieldRef.Instance is not null and not IInstanceReferenceOperation)
            return true;
        return fieldRef.Field.DeclaredAccessibility == Accessibility.Public
            || fieldRef.Field.GetAttributes().Any(a =>
                a.AttributeClass?.Name is "SerializeField" or "SerializeFieldAttribute" or "UdonSyncedAttribute");
    }

    static bool IsDelegateCarryingStorageType(ITypeSymbol type)
    {
        var t = type;
        while (t is IArrayTypeSymbol arr) t = arr.ElementType;
        return t is INamedTypeSymbol named && named.DelegateInvokeMethod != null;
    }

    /// <summary>A delegate-typed property whose storage is cross-program addressable: any property on
    /// another behaviour/interface instance (the set lands via SetProgramVariable / SendCustomEvent),
    /// or a public property on this behaviour (exported accessors + name-addressable backing symbol).
    /// Struct/class (object[]-emulated) containers are program-local slot writes and never match.</summary>
    public bool IsCrossProgramDelegatePropertyTarget(IPropertyReferenceOperation propRef)
    {
        if (propRef.Property.Type is not INamedTypeSymbol dpt || dpt.DelegateInvokeMethod == null) return false;
        var containing = propRef.Property.ContainingType;
        if (containing == null || TypeClassifier.IsObjectArrayEmulated(containing)) return false;
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

    /// <summary>CW22: a cross-behaviour property SET/GET is the same SetProgramVariable /
    /// GetProgramVariable transport as the field twin one syntax over — same payload polarity.
    /// (The delegate axis stays with IsCrossProgramDelegatePropertyTarget: a clean-signature
    /// delegate type carries no payload and passes here untouched.)</summary>
    public void RequireCanWriteCrossBehaviourProperty(IPropertySymbol prop)
        => RequireNoProgramLocalPayload(BoundarySite.CrossBehaviourPropertyWrite, prop.Type, prop.Name);

    public void RequireCanReadCrossBehaviourProperty(IPropertySymbol prop)
        => RequireNoProgramLocalPayload(BoundarySite.CrossBehaviourPropertyRead, prop.Type, prop.Name);

    /// <summary>CW22: a cross-program accessor dispatch (variable-receiver behaviour indexer,
    /// interface property/indexer accessor) SPVs every parameter and GPVs the return — the same
    /// transport as a cross method call, so each parameter and the returned value run the payload
    /// check CrossCallArgPairs applies per-arg. The setter's trailing parameter IS the stored
    /// value, so it reports as a property write.</summary>
    public void RequireCanDispatchCrossBehaviourAccessor(IMethodSymbol accessor)
    {
        var propName = (accessor.AssociatedSymbol as IPropertySymbol)?.Name ?? accessor.Name;
        for (int i = 0; i < accessor.Parameters.Length; i++)
        {
            var isSetterValue = accessor.MethodKind == MethodKind.PropertySet
                && i == accessor.Parameters.Length - 1;
            RequireNoProgramLocalPayload(
                isSetterValue ? BoundarySite.CrossBehaviourPropertyWrite : BoundarySite.CrossBehaviourArgument,
                accessor.Parameters[i].Type, propName);
        }
        if (!accessor.ReturnsVoid)
            RequireNoProgramLocalPayload(BoundarySite.CrossBehaviourPropertyRead, accessor.ReturnType, propName);
    }

    void RequireNoProgramLocalPayload(BoundarySite site, ITypeSymbol type, string memberName)
    {
        if (TypeClassifier.ShapeOf(type, TypeCtx).CanCrossProgram) return;
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
            case BoundarySite.CrossBehaviourPropertyWrite:
                return $"A v1 user class cannot be written to another behaviour's property '{memberName}': a class "
                       + "value is a program-local object[] bundle and cannot cross a program boundary.";
            case BoundarySite.CrossBehaviourPropertyRead:
                return $"Reading another behaviour's property '{memberName}' that carries a v1 user class "
                       + "is not supported: a class value is a program-local object[] bundle and cannot cross a "
                       + "program boundary.";
            default:
                throw new ArgumentOutOfRangeException(nameof(site), site, null);
        }
    }
}
