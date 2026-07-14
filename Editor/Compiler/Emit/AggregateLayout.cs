using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class AggregateLayout
{
    public readonly struct FieldInfo
    {
        public readonly string Name;
        public readonly int Index;
        public readonly ITypeSymbol Type;
        public FieldInfo(string name, int index, ITypeSymbol type)
        { Name = name; Index = index; Type = type; }
    }

    public readonly IReadOnlyList<FieldInfo> Fields;
    readonly Dictionary<string, int> _nameToIndex;

    // Class ABI v1: a user class reserves object[] slot 0 (null placeholder for the future type-object
    // reference); its fields live at 1..F. A struct/tuple reserves nothing (fields at 0..F-1). SlotCount is
    // the backing array size; Count stays the field count. Each FieldInfo.Index already carries the reserved
    // offset, so field-slot resolution (TryGetIndex) is correct without a per-site +1.
    readonly int _reservedLeadingSlots;

    public int Count => Fields.Count;
    public int SlotCount => _reservedLeadingSlots + Fields.Count;

    public bool TryGetIndex(string fieldName, out int index)
        => _nameToIndex.TryGetValue(fieldName, out index);

    public bool TryGetIndex(IFieldSymbol field, out int index)
    {
        if (_nameToIndex.TryGetValue(field.Name, out index)) return true;
        if (field.CorrespondingTupleField != null
            && _nameToIndex.TryGetValue(field.CorrespondingTupleField.Name, out index)) return true;
        // Reverse: check if any layout field's CorrespondingTupleField matches
        return false;
    }

    AggregateLayout(IReadOnlyList<FieldInfo> fields, Dictionary<string, int> nameToIndex, int reservedLeadingSlots)
    { Fields = fields; _nameToIndex = nameToIndex; _reservedLeadingSlots = reservedLeadingSlots; }

    public static AggregateLayout Build(INamedTypeSymbol type)
    {
        var fields = new List<FieldInfo>();
        var nameToIndex = new Dictionary<string, int>();
        // Class ABI v1: reserve slot 0, fields start at index 1. Struct/tuple: no reservation.
        int reserved = EmitPolicy.IsUserClassType(type) ? 1 : 0;

        if (type.IsTupleType)
        {
            var elements = type.TupleElements;
            for (int i = 0; i < elements.Length; i++)
            {
                var name = elements[i].Name;
                fields.Add(new FieldInfo(name, i, elements[i].Type));
                nameToIndex[name] = i;
                var itemName = $"Item{i + 1}";
                if (name != itemName) nameToIndex[itemName] = i;
                if (elements[i].CorrespondingTupleField != null)
                {
                    var corrName = elements[i].CorrespondingTupleField.Name;
                    if (!nameToIndex.ContainsKey(corrName)) nameToIndex[corrName] = i;
                }
            }
        }
        else if (type.TypeKind == TypeKind.Struct || reserved > 0)
        {
            // User struct / v1 user class → instance fields mapped to indices in declaration order (a class
            // starts at `reserved`=1, slot 0 held for the future type-object reference). Auto-property backing
            // fields are implicitly declared but carry the property as AssociatedSymbol; map them by the
            // property name so `get`/`set`/`init` resolve to the same object[] element.
            // CA-v2 M1 (inheritance): a v1 class walks its BASE CHAIN root→derived so a base field owns the
            // SAME index in the derived layout — the derived object[] is usable AS the base with no
            // conversion (up-conversion = no-op). Structs have no user base (System.ValueType), so the
            // chain is a single frame for them.
            int i = reserved;
            var chain = new List<INamedTypeSymbol>();
            for (var t = type; t is { } && (t.TypeKind == TypeKind.Struct
                     ? SymbolEqualityComparer.Default.Equals(t, type)
                     : EmitPolicy.IsUserClassType(t)); t = t.BaseType)
                chain.Add(t);
            chain.Reverse(); // root base first, most-derived last
            foreach (var frame in chain)
                foreach (var member in frame.GetMembers())
                {
                    if (member is not IFieldSymbol { IsStatic: false, IsConst: false } f) continue;
                    var logicalName = f.IsImplicitlyDeclared
                        ? (f.AssociatedSymbol as IPropertySymbol)?.Name
                        : f.Name;
                    if (logicalName == null) continue;
                    // charter/M1: a `new`-hidden field (same logical name in base AND derived) needs
                    // distinct slots + declaring-type-qualified resolution the flat map cannot hold —
                    // loud reject in M1 (qualified keying is a later v2 step).
                    if (nameToIndex.ContainsKey(logicalName))
                        throw new NotSupportedException(
                            $"Field '{logicalName}' on '{type.Name}' hides a same-named base field (`new`): "
                            + "class ABI v2 M1 cannot give two same-named fields distinct storage yet. Rename "
                            + "one of the fields.");
                    fields.Add(new FieldInfo(logicalName, i, f.Type));
                    nameToIndex[logicalName] = i++;
                }
        }
        else if (type.IsAnonymousType)
        {
            // Anonymous type: its read-only properties map to slots in declaration order (no reserved
            // slot). Member access (p.X) resolves through this map exactly like a tuple element.
            int i = 0;
            foreach (var prop in type.GetMembers().OfType<IPropertySymbol>())
            {
                fields.Add(new FieldInfo(prop.Name, i, prop.Type));
                nameToIndex[prop.Name] = i++;
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"AggregateLayout.Build called on non-aggregate type '{type.Name}'");
        }

        return new AggregateLayout(fields.AsReadOnly(), nameToIndex, reserved);
    }
}

/// <summary>
/// Object[] ABI for class, struct, and tuple values. Layout decides which slot a logical member owns;
/// this type owns the backing array representation and element access protocol.
/// </summary>
public static class AggregateAbi
{
    public const string ArrayType = "SystemObjectArray";
    public const string ElementType = "SystemObject";

    public static CLeaf Allocate(CoreBuilder builder, int slotCount)
        => builder.ExternCall(
            ExternResolver.BuildArrayCtorSignature(ArrayType),
            new List<CLeaf> { builder.Const(slotCount, "SystemInt32") },
            ArrayType);

    public static CLeaf ReadSlot(CoreBuilder builder, CLeaf instance, int index, string udonType)
        => builder.ExternCall(
            ExternResolver.BuildArrayGetSignature(ArrayType, ElementType),
            new List<CLeaf> { instance, builder.Const(index, "SystemInt32") },
            udonType);

    public static void WriteSlot(CoreBuilder builder, CLeaf instance, int index, CLeaf value)
        => builder.EmitExternVoid(
            ExternResolver.BuildArraySetSignature(ArrayType, ElementType),
            new List<CLeaf> { instance, builder.Const(index, "SystemInt32"), value });

    public static CLeaf MintTupleLiteral(CoreBuilder builder, ITupleOperation tuple,
        Func<IOperation, CLeaf> emitValue)
    {
        var instance = Allocate(builder, tuple.Elements.Length);
        for (int i = 0; i < tuple.Elements.Length; i++)
            WriteSlot(builder, instance, i, emitValue(tuple.Elements[i]));
        return instance;
    }

    /// <summary>Default-initialize an allocated aggregate bundle. Nested aggregate fields are allocated
    /// recursively; class-typed fields stay null by default.</summary>
    public static void DefaultInitialize(CoreBuilder builder, CValue arrayVal, AggregateLayout layout,
        Func<INamedTypeSymbol, AggregateLayout> getLayout, Func<ITypeSymbol, string> getUdonType)
    {
        var slot = builder.AllocScratch(ArrayType);
        builder.EmitAssign(slot, arrayVal);
        foreach (var fi in layout.Fields)
        {
            int i = fi.Index;
            var fieldType = fi.Type;
            if (fieldType is INamedTypeSymbol nested && EmitPolicy.IsAggregateType(nested))
            {
                var nestedLayout = getLayout(nested);
                var subSlot = builder.AllocScratch(ArrayType);
                builder.EmitAssign(subSlot, Allocate(builder, nestedLayout.SlotCount));
                WriteSlot(builder, builder.SlotRef(slot), i, builder.SlotRef(subSlot));
                DefaultInitialize(builder, builder.SlotRef(subSlot), nestedLayout, getLayout, getUdonType);
                continue;
            }

            var defVal = DefaultScalarValue(fieldType);
            if (defVal != null)
                WriteSlot(builder, builder.SlotRef(slot), i, builder.Const(defVal, getUdonType(fieldType)));
        }
    }

    public static void AllocateField(CoreBuilder builder, string fieldName, AggregateLayout layout)
        => builder.EmitStoreField(fieldName, Allocate(builder, layout.Count));

    public static void DefaultInitializeField(CoreBuilder builder, string fieldName, AggregateLayout layout,
        Func<INamedTypeSymbol, AggregateLayout> getLayout, Func<ITypeSymbol, string> getUdonType)
        => DefaultInitialize(builder, builder.LoadField(fieldName, ArrayType), layout, getLayout, getUdonType);

    /// <summary>Deep value-copy of an object[]-backed struct/tuple aggregate. Nested aggregate elements
    /// are recursively cloned; scalar boxed elements are copied by reference.</summary>
    public static CLeaf DeepClone(CoreBuilder builder, CLeaf source, AggregateLayout layout,
        Func<INamedTypeSymbol, AggregateLayout> getLayout)
    {
        var dstSlot = builder.AllocScratch(ArrayType);
        builder.EmitAssign(dstSlot, Allocate(builder, layout.Count));
        for (int i = 0; i < layout.Count; i++)
        {
            var elem = ReadSlot(builder, source, i, ElementType);
            CLeaf copy = layout.Fields[i].Type is INamedTypeSymbol nested && EmitPolicy.IsAggregateType(nested)
                ? DeepClone(builder, elem, getLayout(nested), getLayout)
                : elem;
            WriteSlot(builder, builder.SlotRef(dstSlot), i, copy);
        }
        return builder.SlotRef(dstSlot);
    }

    public static CLeaf DeepClone(CoreBuilder builder, CLeaf source, INamedTypeSymbol aggregateType,
        Func<INamedTypeSymbol, AggregateLayout> getLayout)
        => DeepClone(builder, source, getLayout(aggregateType), getLayout);

    /// <summary>Allocate and default-initialize a fresh aggregate bundle.</summary>
    public static CLeaf MintDefault(CoreBuilder builder, AggregateLayout layout,
        Func<INamedTypeSymbol, AggregateLayout> getLayout, Func<ITypeSymbol, string> getUdonType)
    {
        var slot = builder.AllocScratch(ArrayType);
        builder.EmitAssign(slot, Allocate(builder, layout.SlotCount));
        DefaultInitialize(builder, builder.SlotRef(slot), layout, getLayout, getUdonType);
        return builder.SlotRef(slot);
    }

    public static void EmitObjectInitializer(CoreBuilder builder, CLeaf instanceValue, AggregateLayout layout,
        IObjectOrCollectionInitializerOperation initializer, Func<IOperation, CLeaf> emitValue)
    {
        if (initializer == null) return;
        foreach (var member in initializer.Initializers)
        {
            if (member is not ISimpleAssignmentOperation assignment) continue;
            var memberName = assignment.Target switch
            {
                IFieldReferenceOperation fieldRef => fieldRef.Field.Name,
                IPropertyReferenceOperation propertyRef => propertyRef.Property.Name,
                _ => null,
            };
            if (memberName != null && layout.TryGetIndex(memberName, out var idx))
                WriteSlot(builder, instanceValue, idx, emitValue(assignment.Value));
        }
    }

    public static bool TryGetMemberTarget(IOperation target, out IOperation instance, out string memberName)
    {
        switch (target)
        {
            case IFieldReferenceOperation { Instance: not null } fieldRef:
                instance = fieldRef.Instance;
                memberName = fieldRef.Field.Name;
                return true;
            case IPropertyReferenceOperation { Instance: not null } propertyRef:
                instance = propertyRef.Instance;
                memberName = propertyRef.Property.Name;
                return true;
            default:
                instance = null;
                memberName = null;
                return false;
        }
    }

    static object DefaultScalarValue(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return false;
            case SpecialType.System_Int32: return 0;
            case SpecialType.System_Single: return 0f;
            case SpecialType.System_Double: return 0d;
            case SpecialType.System_Int64: return 0L;
            case SpecialType.System_Byte: return (byte)0;
            case SpecialType.System_UInt32: return 0u;
            case SpecialType.System_UInt64: return 0UL;
            case SpecialType.System_Int16: return (short)0;
            case SpecialType.System_UInt16: return (ushort)0;
            case SpecialType.System_Char: return '\0';
            case SpecialType.System_SByte: return (sbyte)0;
            default: return null;
        }
    }
}

/// <summary>
/// User class ABI policy and class-specific object[] initialization. AggregateAbi owns the backing array;
/// ClassAbi owns the user-class restrictions and initializer ordering layered on top of that storage.
/// </summary>
public static class ClassAbi
{
    /// <summary>Emit the complete class instance mint sequence for a supported v1 user class.</summary>
    public static CLeaf EmitMint(CoreBuilder builder, Compilation compilation,
        INamedTypeSymbol classTy, AggregateLayout layout,
        Func<IOperation, CLeaf> emitValue, Action<CLeaf> emitDefaultInitialize,
        Action<CLeaf> emitConstructor, Action<CLeaf> emitObjectInitializer,
        Action<CLeaf> emitTypeObj = null)
    {
        RejectUnsupportedMembers(classTy);
        var slot = builder.AllocScratch(AggregateAbi.ArrayType);
        builder.EmitAssign(slot, AggregateAbi.Allocate(builder, layout.SlotCount));
        var instance = builder.SlotRef(slot);
        emitDefaultInitialize(instance);
        // CA-v2b-1 (charter #6): write bundle[0]=typeobj BEFORE the ctor chain so a stored reference is
        // type-identifiable even during partial initialization (matches C# base-ctor virtual-dispatch timing).
        emitTypeObj?.Invoke(instance);
        // CA-v2 M1: field initializers moved INTO the ctor chain (charter #6: each class runs its own
        // field inits at ctor entry, derived->base, before the base call; bodies run base->derived).
        // emitConstructor now runs either the explicit ctor function or the implicit chain.
        emitConstructor(instance);
        emitObjectInitializer(instance);
        return instance;
    }

    /// <summary>CA-v2 M1: the implicit (compiler-generated parameterless) ctor chain — run this class's
    /// field initializers, then recurse into a user-class base (derived->base init order, empty bodies).
    /// Used by the mint for a class with no explicit ctor, and by an explicit ctor whose base ctor is
    /// itself implicit.</summary>
    public static void EmitImplicitCtorChain(CoreBuilder builder, Compilation compilation,
        CLeaf instance, INamedTypeSymbol classTy, Func<INamedTypeSymbol, AggregateLayout> getLayout,
        Func<IOperation, CLeaf> emitValue, Action<IMethodSymbol, CLeaf> callBaseCtor)
    {
        // If this class has an EXPLICIT parameterless ctor, its BODY must run (its own field inits, base
        // chain, and statements — e.g. a base ctor calling a virtual method, charter #6). CALL it rather than
        // inlining field inits, which would skip the body. A class with only an implicit ctor has no body:
        // inline its field inits and chain to the base (which applies the same rule).
        var ownCtor = classTy.InstanceConstructors.FirstOrDefault(
            c => c.Parameters.Length == 0 && !c.IsImplicitlyDeclared);
        if (ownCtor != null && callBaseCtor != null)
        {
            callBaseCtor(ownCtor, instance);
            return;
        }
        EmitInstanceFieldInitializers(builder, compilation, instance, classTy, getLayout(classTy), emitValue);
        if (classTy.BaseType is { } bt && EmitPolicy.IsUserClassType(bt))
            EmitImplicitCtorChain(builder, compilation, instance, bt, getLayout, emitValue, callBaseCtor);
    }

    // Walks the OverriddenMethod chain to confirm the root is a System.Object virtual (not a user-class
    // virtual that merely happens to be named ToString/Equals/GetHashCode).
    static bool IsObjectMethodOverride(IMethodSymbol m)
    {
        for (var o = m.OverriddenMethod; o != null; o = o.OverriddenMethod)
            if (o.ContainingType?.SpecialType == SpecialType.System_Object) return true;
        return false;
    }

    /// <summary>CA-M1: user classes have reference semantics and no interface dispatch. CA-v2 M1 adds
    /// non-virtual inheritance; M3 adds a SEALED-class Object-method override (ToString/Equals/GetHashCode).</summary>
    public static void RejectUnsupportedMembers(INamedTypeSymbol classTy)
    {
        if (classTy.Interfaces.Length > 0)
            throw new NotSupportedException(
                $"Class '{classTy.Name}' implements interface '{classTy.Interfaces[0].Name}': class ABI v1 "
                + "does not support interface implementation on a user class. Call the method directly, or "
                + "use a UdonSharpBehaviour interface for dispatch.");
        foreach (var m in classTy.GetMembers())
        {
            if (m.IsImplicitlyDeclared) continue;
            if (m is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.PropertySet }
                || m is IPropertySymbol)
            {
                // CA-v2b-2: non-generic virtual/abstract/override METHODS dispatch at runtime through the
                // inline typeobj-ReferenceEquals chain (a sealed/singleton receiver devirtualizes to a direct
                // call). A GENERIC virtual method would need per-call monomorphized dispatch slots, which the
                // inline scheme does not model — reject it loudly (backlog).
                if (m is IMethodSymbol vm0 && VirtualDispatch.IsGenericVirtual(vm0))
                    throw new NotSupportedException(
                        $"Member '{classTy.Name}.{m.Name}' is a generic virtual method: v2b-2 inline "
                        + "typeobj-dispatch does not support per-call monomorphized virtual slots. Make the "
                        + "method non-generic, or call a named method directly.");
                // CW1: the dispatch chain exists ONLY for MethodKind.Ordinary — every property/indexer
                // accessor site binds the receiver's STATIC symbol, so a virtual/override/abstract accessor
                // on a base-typed receiver would silently run the base accessor. Same polarity as the
                // generic-virtual reject: loud over silent-wrong (accessor dispatch is backlog).
                if (m is IPropertySymbol vp && (vp.IsVirtual || vp.IsAbstract || vp.IsOverride))
                    throw new NotSupportedException(
                        $"Member '{classTy.Name}.{vp.Name}' is a virtual {(vp.IsIndexer ? "indexer" : "property")}: "
                        + "v2b-2 inline typeobj-dispatch covers ordinary methods only, so an accessor on a "
                        + "base-typed receiver would silently run the base accessor. Wrap the accessor in a "
                        + "virtual method (methods DO dispatch), or make the "
                        + (vp.IsIndexer ? "indexer" : "property") + " non-virtual.");
            }
        }
    }

    /// <summary>Reject static field storage on a v1 user class, except consts folded before this point.</summary>
    public static void RejectStaticField(IFieldSymbol field)
    {
        if (field.IsStatic && field.ContainingType is INamedTypeSymbol classTy && EmitPolicy.IsUserClassType(classTy))
            throw new NotSupportedException(
                $"Static field '{classTy.Name}.{field.Name}' on a v1 user class is not "
                + "supported (only `const` is): a class has no per-type static storage yet. Move the data "
                + "to a field on the UdonSharpBehaviour class, or make it a `const`.");
    }

    /// <summary>Reject static properties on a v1 user class.</summary>
    public static void RejectStaticProperty(IPropertySymbol property)
    {
        if (property.IsStatic && property.ContainingType is INamedTypeSymbol classTy && EmitPolicy.IsUserClassType(classTy))
            throw new NotSupportedException(
                $"Static property '{classTy.Name}.{property.Name}' on a v1 user class is not "
                + "supported (only `const` and static methods are): move it to a static method, or to "
                + "a field on the UdonSharpBehaviour class.");
    }

    /// <summary>CA-v2 M3: the user ToString() override callable for a v1 class value, or null. Only a
    /// SEALED class qualifies (no derived type -> static dispatch is exact); a non-sealed override needs
    /// virtual dispatch (M4b) and falls through to RejectImplicitToString.</summary>
    public static IMethodSymbol TryGetUserToString(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol ct || !EmitPolicy.IsUserClassType(ct) || !ct.IsSealed) return null;
        for (var t = ct; t != null && EmitPolicy.IsUserClassType(t); t = t.BaseType)
            foreach (var m in t.GetMembers("ToString"))
                if (m is IMethodSymbol { IsStatic: false } ts && ts.Parameters.Length == 0 && IsObjectMethodOverride(ts))
                    return ts;
        return null;
    }

    /// <summary>Reject implicit stringification of a v1 class reference bundle — and of a multi-dimensional
    /// array bundle (CW14/CW15): both stringify to "System.Object[]" instead of the C# type name, and the
    /// interpolation/concat Format externs bypass the N-R1 argument choke.</summary>
    public static void RejectImplicitToString(ITypeSymbol type)
    {
        if (type == null) return;
        if (EmitPolicy.IsUserClassType(type))
            throw new NotSupportedException(
                $"A v1 user class '{type.Name}' cannot be converted to a string (interpolation / concat): a "
                + "class ABI v1 reference bundle has no member-name synthesis, so it would stringify to "
                + "\"System.Object[]\". Format the class's fields directly instead.");
        if (NdimArrayAbi.IsNdimArray(type))
            throw new NotSupportedException(
                $"A multi-dimensional array ('{type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}') cannot be "
                + "converted to a string (interpolation / concat): its runtime value is an object[] bundle, so it would "
                + "stringify to \"System.Object[]\". Format the elements directly instead.");
    }

    /// <summary>Reject user-defined operators and conversions on v1 user classes.</summary>
    public static void RejectUserOperator(IMethodSymbol method)
    {
        if (method is { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion }
            && method.ContainingType is INamedTypeSymbol classTy && EmitPolicy.IsUserClassType(classTy))
            throw new NotSupportedException(
                $"User-defined operator '{classTy.Name}.{method.Name}' on a v1 user class is not supported: "
                + "a class has reference semantics (== / != compare object identity) and no user operator or "
                + "conversion is emitted. Call a named method instead.");
    }

    public static bool IsReferenceEquality(BinaryOperatorKind kind, ITypeSymbol leftType, ITypeSymbol rightType)
        => kind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals
           && (EmitPolicy.IsUserClassType(leftType) || EmitPolicy.IsUserClassType(rightType));

    public static CLeaf EmitReferenceEquality(CoreBuilder builder, BinaryOperatorKind kind, CLeaf left, CLeaf right)
    {
        var signature = kind == BinaryOperatorKind.NotEquals
            ? "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean"
            : "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean";
        return builder.ExternCall(signature, new List<CLeaf> { left, right }, "SystemBoolean");
    }

    public static bool IsObjectMethodOnUserClass(IMethodSymbol method, ITypeSymbol receiverType)
        => !method.IsStatic
           && receiverType is INamedTypeSymbol classTy
           && EmitPolicy.IsUserClassType(classTy)
           && method.ContainingType.SpecialType == SpecialType.System_Object;

    public static CLeaf EmitObjectEquals(CoreBuilder builder, CLeaf left, CLeaf right)
        => EmitReferenceEquality(builder, BinaryOperatorKind.Equals, left, right);

    public static string UnsupportedObjectMethodMessage(INamedTypeSymbol classTy, IMethodSymbol method)
        => $"'{classTy.Name}.{method.Name}()' is not supported on a v1 user class: class ABI v1 gives a "
           + "reference bundle no stable hash, no member-name synthesis, and no runtime type identity. "
           + "Use reference equality (== / Equals) or format the class's fields directly.";

    public static void RejectRuntimeTypeTest(ITypeSymbol targetType)
    {
        if (ExternResolver.IsUnsupportedUserClass(targetType))
            throw new NotSupportedException(
                $"Runtime type tests (is / as / switch) against the user-defined class "
                + $"'{targetType.Name}' are not supported: class ABI v1 gives a user class no "
                + "runtime type identity yet. Keep the value typed as its static type instead of recovering "
                + "it with a type test.");
    }

    public static void RejectTypeofToken(ITypeSymbol type)
    {
        if (ExternResolver.IsUnsupportedUserClass(type))
            throw new NotSupportedException(
                $"typeof(user-defined class '{type.Name}') is not supported: class ABI v1 gives "
                + "a user class no runtime type identity yet, so its System.Type token cannot be resolved.");
    }

    public static void RejectDelegateBindingToInstanceMethod(IMethodSymbol targetMethod)
    {
        // A lambda / local function hosted INSIDE a class member reports the class as its
        // ContainingType too (Roslyn resolves up to the nearest named type), but it is a hoisted
        // closure dispatched via its own bridge + env — not a receiver-dispatch target. Only a real
        // named instance method is the unsupported B54-class shape.
        if (targetMethod.MethodKind is MethodKind.LambdaMethod or MethodKind.LocalFunction) return;
        if (!targetMethod.IsStatic
            && targetMethod.ContainingType is INamedTypeSymbol classTy
            && EmitPolicy.IsUserClassType(classTy))
            throw new NotSupportedException(
                $"A delegate cannot be created from v1 class instance method '{classTy.Name}.{targetMethod.Name}': "
                + "a user class is not a dispatch target for the delegate ABI. Wrap the call in a lambda instead "
                + $"('() => {targetMethod.Name}(...)' inside the class, '() => receiver.{targetMethod.Name}(...)' outside).");
    }

    /// <summary>Run instance field / auto-property initializers on an already allocated class bundle.</summary>
    public static void EmitInstanceFieldInitializers(CoreBuilder builder, Compilation compilation,
        CLeaf instance, INamedTypeSymbol classTy, AggregateLayout layout, Func<IOperation, CLeaf> emitValue)
    {
        foreach (var member in classTy.GetMembers())
        {
            if (member is not IFieldSymbol { IsStatic: false, IsConst: false } f) continue;
            var (slotName, initHolder) = f.IsImplicitlyDeclared && f.AssociatedSymbol is IPropertySymbol prop
                ? (prop.Name, (ISymbol)prop)
                : (f.Name, f);
            if (!layout.TryGetIndex(slotName, out var idx)) continue;
            var syntax = initHolder.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            var initValue = syntax switch
            {
                Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax vd => vd.Initializer?.Value,
                Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax pd => pd.Initializer?.Value,
                _ => null,
            };
            if (initValue == null) continue;
            var initOp = compilation.GetSemanticModel(initValue.SyntaxTree).GetOperation(initValue);
            if (initOp == null) continue;
            AggregateAbi.WriteSlot(builder, instance, idx, emitValue(initOp));
        }
    }
}
