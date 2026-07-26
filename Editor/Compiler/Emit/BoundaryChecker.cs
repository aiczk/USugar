using System;
using System.Collections.Generic;
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
    ExternArgument,
    ExternReturn,
}

/// <summary>
/// Emit-time boundary policy. Handlers should identify boundary sites and delegate the semantic decision
/// here, instead of open-coding class/delegate/env escape checks per syntax shape.
/// </summary>
internal sealed class BoundaryChecker
{
    readonly LoweringState _ctx;

    public BoundaryChecker(LoweringState ctx) => _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    RuntimeShape ShapeOf(ITypeSymbol type)
        => _ctx.Types.Describe(type, _ctx.Generics.TypeParamMap).SourceShape
           ?? throw new InvalidOperationException(
               $"Source type '{type}' has no semantic runtime shape.");

    public ValueInfo ClassifyValue(IOperation value)
        => _ctx.Program.Values.Require(
            value, _ctx.CurrentBindingScope);

    public bool CurrentMethodBodyMentionsUserClassPayload()
    {
        // Receiver-capture design v2 SS2(b): inside a v1-class instance member the receiver itself is
        // a program-local payload in scope - an unclassifiable delegate store from here cannot be
        // proven class-free (bounded conservative polarity; over-reject is the accepted trade).
        return _ctx.Program.Values.MethodMentionsProgramLocalPayload(
            _ctx.CurrentBindingScope);
    }

    /// <summary>Report any delegate store target here; non-cross-program targets (locals, private
    /// this-fields, struct/class members, declarations) are ignored. The handler only names the
    /// syntax — whether the surface crosses a program boundary is decided here.</summary>
    public void RequireCanStoreCrossProgramDelegate(IOperation target, ValueInfo info)
    {
        switch (target)
        {
            case IFieldReferenceOperation f when IsCrossProgramDelegateFieldTarget(f):
                if (IsEmptyDelegateArrayCreation(info)) break;
                RequireDelegateValueSafeForCrossProgramStore(info,
                    $"the cross-program field '{f.Field.Name}'", "the write site",
                    "Keep the delegate field private, or assign a direct class-free lambda/method group.");
                break;
            case IPropertyReferenceOperation p when IsCrossProgramDelegatePropertyTarget(p):
                if (IsEmptyDelegateArrayCreation(info)) break;
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
            case IArrayElementReferenceOperation e when ArrayRootPropertyReference(e) is { } rootP
                && IsCrossProgramDelegatePropertyTarget(rootP):
                RequireDelegateValueSafeForCrossProgramStore(info,
                    $"an element of the cross-program array property '{rootP.Property.Name}'", "the write site",
                    "Keep the delegate array property private, or assign a direct class-free lambda/method group.");
                break;
        }
    }

    static bool IsEmptyDelegateArrayCreation(ValueInfo info)
        => ValueClassifier.UnwrapConversions(info.Operation) is IArrayCreationOperation array
           && (array.Initializer == null || array.Initializer.ElementValues.Length == 0);

    /// <summary>CW7/CW23: a cross-program call argument IS a delegate store — the pair becomes a
    /// SetProgramVariable into the foreign program's param var — so the argument surface runs the
    /// same value-classification ladder as the store surfaces (the MG-autowrap design pins "class
    /// payload cross-program stays rejected"). Type-only checking saw a clean Action signature,
    /// let a class-capturing env cross, and the callee-side re-store classified Parameter/
    /// unclassifiable — laundering the bundle back into the guarded fields one hop later.</summary>
    public void RequireCanPassCrossProgramDelegateArgument(IArgumentOperation arg)
    {
        var argType = arg.Value?.Type ?? arg.Parameter?.Type;
        if (argType == null
            || !ShapeOf(argType).ContainsDelegate) return;
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
        // Stable locals have already been traced to their initializer by ClassifyValue. Anything still
        // indirect here is mutable or originates outside this method, so its capture payload is unknown.
        if (!info.IsDirectDelegateValue)
            throw new NotSupportedException(
                $"A delegate stored in {surface} must be created directly from a capture-safe lambda or "
                + $"method group at {site}. The copied {info.Provenance.ToString().ToLowerInvariant()} value "
                + "has no creation-site capture proof and may carry a program-local payload."
                + (advice == null ? "" : " " + advice));
        if (!info.DelegateCapturesProgramLocalPayload && !CurrentMethodBodyMentionsUserClassPayload()) return;
        throw new NotSupportedException(
            $"A delegate stored in {surface} must be created directly from a capture-safe lambda or "
            + $"method group at {site}. Delegate values copied from locals, parameters, fields, calls, "
            + "or other unclassified sources may carry a v1 user class through their closure environment, "
            + "and cannot cross a program boundary."
            + (advice == null ? "" : " " + advice));
    }

    public void RequireCanEraseProgramLocalPayload(IConversionOperation conversion,
        ITypeSymbol sourceType, ITypeSymbol destinationType,
        bool allowsProgramLocalClassErasure)
    {
        var sourceShape = ShapeOf(sourceType);
        var destinationShape = ShapeOf(destinationType);
        if (destinationType is INamedTypeSymbol { TypeKind: TypeKind.Interface } mixedInterface
            && _ctx.Planner.InterfaceHasMixedRuntimeRepresentations(mixedInterface))
            RequireInterfaceDispatchRepresentation(mixedInterface, "conversion");
        if (TypeClassifier.IsUserClass(sourceType)
            && destinationType is INamedTypeSymbol { TypeKind: TypeKind.Interface } localInterface
            && _ctx.Planner.InterfaceIsLocalUserClassOnly(localInterface))
            return;
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

        if (!sourceShape.ContainsUserClassPayload
            || destinationShape.ContainsUserClassPayload
            || IsProgramLocalEqualityPosition(conversion)
            || allowsProgramLocalClassErasure)
            return;
        throw new NotSupportedException(
            $"Erasing the v1 user class '{sourceType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' "
            + $"to '{destinationType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' is not supported: "
            + "a class value is a program-local object[] bundle with no runtime type identity, so once boxed "
            + "to object it launders past the cross-program / cast / ToString boundary checks. Compare class "
            + "references directly, or keep the value class-typed / use Foo[].");
    }

    public void RequireInterfaceDispatchRepresentation(INamedTypeSymbol ifaceType, string memberName)
    {
        if (ifaceType == null || !_ctx.Planner.InterfaceHasMixedRuntimeRepresentations(ifaceType)) return;
        throw new NotSupportedException(
            $"Interface member '{ifaceType.Name}.{memberName}' uses an interface with different runtime "
            + "representations in this compilation (a UdonBehaviour reference, a user-class object[] "
            + "bundle, or a user struct bundle). Keep one representation per interface; USugar cannot "
            + "safely dispatch a single interface value across mixed representations.");
    }

    public void RequireCanDeclareDelegateSurface(ISymbol member, INamedTypeSymbol delegateType)
    {
        if (!DelegateAbi.IsProgramLocalSignature(delegateType.DelegateInvokeMethod)) return;
        var externallyVisible = member.DeclaredAccessibility == Accessibility.Public
            || member.GetAttributes().Any(a => a.AttributeClass?.Name is
                "SerializeField" or "SerializeFieldAttribute");
        if (externallyVisible)
            throw new NotSupportedException(
                $"Delegate '{member.Name}' has a user-class parameter or return type and must remain "
                + "private: its convention is valid only inside this Udon program.");
    }

    /// <summary>Allows the useful class -> object -> class pattern without reopening object laundering.
    /// The erased value must be a single-declaration local and every reference must immediately perform
    /// a runtime class test/cast or reference equality. This is deliberately a whole-body proof: one
    /// opaque use makes the originating erasure loud again.</summary>
    internal static bool IsProvablyLocalClassErasure(
        IConversionOperation conversion,
        ITypeSymbol sourceType, ITypeSymbol destinationType)
    {
        if (destinationType == null
            || !TypeClassifier.IsUserClass(sourceType)
            || destinationType.SpecialType != SpecialType.System_Object)
            return false;

        IOperation cursor = conversion;
        while (cursor.Parent is IConversionOperation parentConversion) cursor = parentConversion;
        if (cursor.Parent is not IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator })
            return false;

        var local = declarator.Symbol;
        IOperation root = declarator;
        while (root.Parent != null) root = root.Parent;
        foreach (var operation in root.DescendantsAndSelf())
        {
            if (operation is not ILocalReferenceOperation localRef
                || !SymbolEqualityComparer.Default.Equals(localRef.Local, local))
                continue;
            if (!IsSafeErasedClassUse(localRef)) return false;
        }
        return true;
    }

    static bool IsSafeErasedClassUse(ILocalReferenceOperation localRef)
    {
        switch (localRef.Parent)
        {
            case IConversionOperation conversion
                when TypeClassifier.IsUserClass(conversion.Type):
                return true;
            case IIsTypeOperation isType when TypeClassifier.IsUserClass(isType.TypeOperand):
                return true;
            case IIsPatternOperation { Pattern: IDeclarationPatternOperation declaration }
                when TypeClassifier.IsUserClass(declaration.MatchedType):
                return true;
            case IIsPatternOperation { Pattern: ITypePatternOperation typePattern }
                when TypeClassifier.IsUserClass(typePattern.MatchedType):
                return true;
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals }:
                return true;
            case IArgumentOperation { Parent: IInvocationOperation invocation }
                when invocation.TargetMethod.Name == "Equals"
                     && invocation.TargetMethod.ContainingType.SpecialType
                         is SpecialType.System_Object or SpecialType.System_ValueType:
                return true;
            default:
                return false;
        }
    }

    static IPropertyReferenceOperation ArrayRootPropertyReference(IArrayElementReferenceOperation elem)
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
                case IPropertyReferenceOperation pr:
                    return pr;
                default:
                    return null;
            }
        }
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
        if (!IsDelegateCarryingStorageType(propRef.Property.Type)) return false;
        var containing = propRef.Property.ContainingType;
        if (containing == null
            || ShapeOf(containing).Bundle
                is RuntimeBundleKind.Aggregate or RuntimeBundleKind.Class)
            return false;
        if (propRef.Instance is not null and not IInstanceReferenceOperation)
            return ExternResolver.IsUdonSharpBehaviour(containing)
                || ExternResolver.IsUserInterface(containing);
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
        => RequireTransport(BoundarySite.CrossBehaviourFieldWrite, field.Type, field.Name,
            TransportCapabilities.TypedProgramChannel);

    public void RequireCanReadCrossBehaviourField(IFieldSymbol field)
        => RequireTransport(BoundarySite.CrossBehaviourFieldRead, field.Type, field.Name,
            TransportCapabilities.TypedProgramChannel);

    public void RequireCanPassCrossBehaviourArgument(ITypeSymbol argType)
        => RequireTransport(BoundarySite.CrossBehaviourArgument, argType, null,
            TransportCapabilities.TypedProgramChannel);

    public void RequireCanPassExternArgument(ITypeSymbol type, string name,
        bool allowClassReferenceIdentity = false, bool deferAggregateReceiverPolicy = false)
    {
        var shape = ShapeOf(type);
        if (allowClassReferenceIdentity && shape.Bundle == RuntimeBundleKind.Class) return;
        if (deferAggregateReceiverPolicy && shape.Bundle == RuntimeBundleKind.Aggregate) return;
        RequireTransport(BoundarySite.ExternArgument, type, name,
            TransportCapabilities.ExternCall);
    }

    public void RequireCanReturnFromExtern(ITypeSymbol type, string name)
        => RequireTransport(BoundarySite.ExternReturn, type, name,
            TransportCapabilities.ExternCall);

    /// <summary>CW22: a cross-behaviour property SET/GET is the same SetProgramVariable /
    /// GetProgramVariable transport as the field twin one syntax over — same payload polarity.
    /// (The delegate axis stays with IsCrossProgramDelegatePropertyTarget: a clean-signature
    /// delegate type carries no payload and passes here untouched.)</summary>
    public void RequireCanWriteCrossBehaviourProperty(IPropertySymbol prop)
        => RequireTransport(BoundarySite.CrossBehaviourPropertyWrite, prop.Type, prop.Name,
            TransportCapabilities.TypedProgramChannel);

    public void RequireCanReadCrossBehaviourProperty(IPropertySymbol prop)
        => RequireTransport(BoundarySite.CrossBehaviourPropertyRead, prop.Type, prop.Name,
            TransportCapabilities.TypedProgramChannel);

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
            RequireTransport(
                isSetterValue ? BoundarySite.CrossBehaviourPropertyWrite : BoundarySite.CrossBehaviourArgument,
                accessor.Parameters[i].Type, propName, TransportCapabilities.TypedProgramChannel);
        }
        if (!accessor.ReturnsVoid)
            RequireTransport(BoundarySite.CrossBehaviourPropertyRead, accessor.ReturnType, propName,
                TransportCapabilities.TypedProgramChannel);
    }

    void RequireTransport(BoundarySite site, ITypeSymbol type, string memberName,
        TransportCapabilities capability)
    {
        if (ShapeOf(type).Supports(capability)) return;
        throw new NotSupportedException(UnsupportedTransportMessage(site, memberName));
    }

    static string UnsupportedTransportMessage(BoundarySite site, string memberName)
    {
        switch (site)
        {
            case BoundarySite.CrossBehaviourFieldWrite:
                return $"Field '{memberName}' cannot be written across behaviours because its type contains "
                       + "a value representation without a portable typed-program ABI.";
            case BoundarySite.CrossBehaviourFieldRead:
                return $"Field '{memberName}' cannot be read across behaviours because its type contains "
                       + "a value representation without a portable typed-program ABI.";
            case BoundarySite.CrossBehaviourArgument:
                return "The argument cannot be passed to a cross-behaviour (SendCustomEvent) call because "
                       + "its type contains a value representation without a portable typed-program ABI.";
            case BoundarySite.CrossBehaviourPropertyWrite:
                return $"Property '{memberName}' cannot be written across behaviours because its type contains "
                       + "a value representation without a portable typed-program ABI.";
            case BoundarySite.CrossBehaviourPropertyRead:
                return $"Property '{memberName}' cannot be read across behaviours because its type contains "
                       + "a value representation without a portable typed-program ABI.";
            case BoundarySite.ExternArgument:
                return $"'{memberName}' cannot cross an extern call boundary because its type has "
                       + "no Udon extern-call ABI.";
            case BoundarySite.ExternReturn:
                return $"The return value of '{memberName}' cannot cross an Udon extern boundary because "
                       + "its type has no extern-call ABI.";
            default:
                throw new ArgumentOutOfRangeException(nameof(site), site, null);
        }
    }
}
