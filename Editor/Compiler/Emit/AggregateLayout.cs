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
        public readonly ISymbol Symbol;
        public readonly int Index;
        public readonly ITypeSymbol Type;
        public FieldInfo(string name, ISymbol symbol, int index, ITypeSymbol type)
        { Name = name; Symbol = symbol; Index = index; Type = type; }
    }

    public readonly IReadOnlyList<FieldInfo> Fields;
    readonly Dictionary<string, int> _nameToIndex;
    readonly Dictionary<ISymbol, int> _symbolToIndex;

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
        if (_symbolToIndex.TryGetValue(field, out index)) return true;
        if (_symbolToIndex.TryGetValue(field.OriginalDefinition, out index)) return true;
        return field.CorrespondingTupleField != null
            && _symbolToIndex.TryGetValue(field.CorrespondingTupleField, out index);
    }

    public bool TryGetIndex(IPropertySymbol property, out int index)
        => _symbolToIndex.TryGetValue(property, out index)
           || _symbolToIndex.TryGetValue(property.OriginalDefinition, out index);

    public bool TryGetIndex(ISymbol member, out int index)
    {
        if (_symbolToIndex.TryGetValue(member, out index)) return true;
        return member switch
        {
            IFieldSymbol field => _symbolToIndex.TryGetValue(field.OriginalDefinition, out index),
            IPropertySymbol property => _symbolToIndex.TryGetValue(property.OriginalDefinition, out index),
            _ => false,
        };
    }

    AggregateLayout(IReadOnlyList<FieldInfo> fields, Dictionary<string, int> nameToIndex,
        Dictionary<ISymbol, int> symbolToIndex, int reservedLeadingSlots)
    { Fields = fields; _nameToIndex = nameToIndex; _symbolToIndex = symbolToIndex; _reservedLeadingSlots = reservedLeadingSlots; }

    public static AggregateLayout Build(INamedTypeSymbol type)
    {
        var fields = new List<FieldInfo>();
        var nameToIndex = new Dictionary<string, int>();
        var symbolToIndex = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        var ambiguousNames = new HashSet<string>();
        // Class ABI v1: reserve slot 0, fields start at index 1. Struct/tuple: no reservation.
        int reserved = TypeClassifier.IsUserClass(type) ? 1 : 0;

        if (type.IsTupleType)
        {
            var elements = type.TupleElements;
            for (int i = 0; i < elements.Length; i++)
            {
                var name = elements[i].Name;
                fields.Add(new FieldInfo(name, elements[i], i, elements[i].Type));
                symbolToIndex[elements[i]] = i;
                nameToIndex[name] = i;
                var itemName = $"Item{i + 1}";
                if (name != itemName) nameToIndex[itemName] = i;
                if (elements[i].CorrespondingTupleField != null)
                {
                    var corrName = elements[i].CorrespondingTupleField.Name;
                    if (!nameToIndex.ContainsKey(corrName)) nameToIndex[corrName] = i;
                    symbolToIndex[elements[i].CorrespondingTupleField] = i;
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
                     : TypeClassifier.IsUserClass(t)); t = t.BaseType)
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
                    var slot = i++;
                    fields.Add(new FieldInfo(logicalName, f, slot, f.Type));
                    symbolToIndex[f] = slot;
                    symbolToIndex[f.OriginalDefinition] = slot;
                    if (f.AssociatedSymbol is IPropertySymbol property)
                    {
                        symbolToIndex[property] = slot;
                        symbolToIndex[property.OriginalDefinition] = slot;
                    }
                    if (!ambiguousNames.Contains(logicalName) && !nameToIndex.TryAdd(logicalName, slot))
                    {
                        nameToIndex.Remove(logicalName);
                        ambiguousNames.Add(logicalName);
                    }
                }
        }
        else if (type.IsAnonymousType)
        {
            // Anonymous type: its read-only properties map to slots in declaration order (no reserved
            // slot). Member access (p.X) resolves through this map exactly like a tuple element.
            int i = 0;
            foreach (var prop in type.GetMembers().OfType<IPropertySymbol>())
            {
                fields.Add(new FieldInfo(prop.Name, prop, i, prop.Type));
                symbolToIndex[prop] = i;
                nameToIndex[prop.Name] = i++;
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"AggregateLayout.Build called on non-aggregate type '{type.Name}'");
        }

        return new AggregateLayout(fields.AsReadOnly(), nameToIndex, symbolToIndex, reserved);
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
            new List<CLeaf> { builder.Const(slotCount, StorageTypes.Int32) },
            new StorageType(ArrayType));

    public static CLeaf ReadSlot(CoreBuilder builder, CLeaf instance, int index, StorageType udonType)
        => builder.ExternCall(
            ExternResolver.BuildArrayGetSignature(ArrayType, ElementType),
            new List<CLeaf> { instance, builder.Const(index, StorageTypes.Int32) },
            udonType);

    public static void WriteSlot(CoreBuilder builder, CLeaf instance, int index, CLeaf value)
        => builder.EmitExternVoid(
            ExternResolver.BuildArraySetSignature(ArrayType, ElementType),
            new List<CLeaf> { instance, builder.Const(index, StorageTypes.Int32), value });

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
        var slot = builder.AllocScratch(new StorageType(ArrayType));
        builder.EmitAssign(slot, arrayVal);
        foreach (var fi in layout.Fields)
        {
            int i = fi.Index;
            var fieldType = fi.Type;
            if (fieldType is INamedTypeSymbol nested && TypeClassifier.IsAggregateValue(nested))
            {
                var nestedLayout = getLayout(nested);
                var subSlot = builder.AllocScratch(new StorageType(ArrayType));
                builder.EmitAssign(subSlot, Allocate(builder, nestedLayout.SlotCount));
                WriteSlot(builder, builder.SlotRef(slot), i, builder.SlotRef(subSlot));
                DefaultInitialize(builder, builder.SlotRef(subSlot), nestedLayout, getLayout, getUdonType);
                continue;
            }

            var defVal = DefaultScalarValue(fieldType);
            if (defVal != null)
                WriteSlot(builder, builder.SlotRef(slot), i, builder.Const(defVal, new StorageType(getUdonType(fieldType))));
        }
    }

    public static void AllocateField(CoreBuilder builder, string fieldName, AggregateLayout layout)
        => builder.EmitStoreField(fieldName, Allocate(builder, layout.Count));

    public static void DefaultInitializeField(CoreBuilder builder, string fieldName, AggregateLayout layout,
        Func<INamedTypeSymbol, AggregateLayout> getLayout, Func<ITypeSymbol, string> getUdonType)
        => DefaultInitialize(builder, builder.LoadField(fieldName, new StorageType(ArrayType)), layout, getLayout, getUdonType);

    /// <summary>Deep value-copy of an object[]-backed struct/tuple aggregate. Nested aggregate elements
    /// are recursively cloned; scalar boxed elements are copied by reference.</summary>
    public static CLeaf DeepClone(CoreBuilder builder, CLeaf source, AggregateLayout layout,
        Func<INamedTypeSymbol, AggregateLayout> getLayout)
    {
        var dstSlot = builder.AllocScratch(new StorageType(ArrayType));
        builder.EmitAssign(dstSlot, Allocate(builder, layout.Count));
        for (int i = 0; i < layout.Count; i++)
        {
            var elem = ReadSlot(builder, source, i, new StorageType(ElementType));
            CLeaf copy = layout.Fields[i].Type is INamedTypeSymbol nested && TypeClassifier.IsAggregateValue(nested)
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
        var slot = builder.AllocScratch(new StorageType(ArrayType));
        builder.EmitAssign(slot, Allocate(builder, layout.SlotCount));
        DefaultInitialize(builder, builder.SlotRef(slot), layout, getLayout, getUdonType);
        return builder.SlotRef(slot);
    }

    /// <summary>CW29: the single deconstruction element-read clone rule. An element read out of a
    /// tuple bundle that feeds an lvalue store is a VALUE read in C# — struct/tuple/anonymous
    /// elements are deep-copied, scalars are immutable boxes, and v1 classes keep reference
    /// semantics (IsAggregateValue is false for them). Every sibling deconstruction arm routes its
    /// element reads through this one gate instead of open-coding the predicate.</summary>
    public static CLeaf CloneIfAggregate(CoreBuilder builder, CLeaf value, ITypeSymbol elementType,
        Func<INamedTypeSymbol, AggregateLayout> getLayout)
        => elementType is INamedTypeSymbol agg && TypeClassifier.IsAggregateValue(agg)
            ? DeepClone(builder, value, agg, getLayout)
            : value;

    /// <summary>CW27: every initializer member is lowered or throws — the old loop silently skipped
    /// anything that was not a slot-resolvable simple assignment (nested member initializers,
    /// computed-property and indexer members), leaving fields at their defaults with no diagnostic.
    /// <paramref name="emitSetterAssignment"/> is the handler-side computed/indexer setter call
    /// (the same lowering PreparePropertySet gives plain assignment).</summary>
    public static void EmitObjectInitializer(CoreBuilder builder, CLeaf instanceValue, AggregateLayout layout,
        IObjectOrCollectionInitializerOperation initializer, Func<IOperation, CLeaf> emitValue,
        Func<INamedTypeSymbol, AggregateLayout> getLayout,
        Action<CLeaf, IPropertyReferenceOperation, IOperation> emitSetterAssignment)
    {
        if (initializer == null) return;
        foreach (var member in initializer.Initializers)
        {
            switch (member)
            {
                case ISimpleAssignmentOperation assignment:
                {
                    var memberSymbol = assignment.Target switch
                    {
                        IFieldReferenceOperation fieldRef => (ISymbol)fieldRef.Field,
                        IPropertyReferenceOperation { Property: { IsIndexer: false } } propertyRef
                            => propertyRef.Property,
                        _ => null,
                    };
                    if (memberSymbol != null && layout.TryGetIndex(memberSymbol, out var idx))
                    {
                        WriteSlot(builder, instanceValue, idx, emitValue(assignment.Value));
                        break;
                    }
                    // Computed property / indexer: no layout slot — call the user setter with the
                    // fresh instance as param0, exactly like plain assignment to the same member.
                    if (assignment.Target is IPropertyReferenceOperation { Property: { SetMethod: { } } } setterRef)
                    {
                        emitSetterAssignment(instanceValue, setterRef, assignment.Value);
                        break;
                    }
                    throw new NotSupportedException(
                        $"Object initializer member '{assignment.Target.Syntax}' cannot be lowered: it "
                        + "resolves neither a layout slot nor a callable user setter. Assign the member "
                        + "in a separate statement after construction instead.");
                }
                case IMemberInitializerOperation memberInit:
                {
                    // Nested member initializer (`Inner = { X = 1 }`): C# reads the member and assigns
                    // into it. For an object[]-emulated member the live nested bundle sits in the slot
                    // (structs/tuples are allocated by DefaultInitialize, a class bundle by its ctor
                    // chain), so read it raw and recurse — writes land in the fresh instance's storage.
                    var (memberSymbol, name, memberType) = memberInit.InitializedMember switch
                    {
                        IFieldReferenceOperation f => ((ISymbol)f.Field, f.Field.Name, f.Field.Type),
                        IPropertyReferenceOperation p => (p.Property, p.Property.Name, p.Property.Type),
                        _ => ((ISymbol)null, (string)null, (ITypeSymbol)null),
                    };
                    if (memberSymbol != null && layout.TryGetIndex(memberSymbol, out var slotIdx)
                        && memberType is INamedTypeSymbol nested && TypeClassifier.IsObjectArrayEmulated(nested))
                    {
                        var nestedVal = ReadSlot(builder, instanceValue, slotIdx, new StorageType(ElementType));
                        EmitObjectInitializer(builder, nestedVal, getLayout(nested), memberInit.Initializer,
                            emitValue, getLayout, emitSetterAssignment);
                        break;
                    }
                    throw new NotSupportedException(
                        $"A nested member initializer on '{name ?? memberInit.InitializedMember.Syntax.ToString()}' "
                        + "is not supported: only a struct/tuple/v1-class member backed by an object[] slot "
                        + "can be initialized in place. Assign the member a whole value, or set its members "
                        + "in separate statements after construction.");
                }
                default:
                    throw new NotSupportedException(
                        $"Object initializer member '{member.Syntax}' ({member.Kind}) is not supported on an "
                        + "object[]-emulated aggregate: only member assignments and nested member initializers "
                        + "can be lowered. Initialize via separate statements after construction.");
            }
        }
    }

    public static bool TryGetMemberTarget(IOperation target, out IOperation instance, out ISymbol member)
    {
        switch (target)
        {
            case IFieldReferenceOperation { Instance: not null } fieldRef:
                instance = fieldRef.Instance;
                member = fieldRef.Field;
                return true;
            case IPropertyReferenceOperation { Instance: not null } propertyRef:
                instance = propertyRef.Instance;
                member = propertyRef.Property;
                return true;
            default:
                instance = null;
                member = null;
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
        var slot = builder.AllocScratch(new StorageType(AggregateAbi.ArrayType));
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
        if (classTy.BaseType is { } bt && TypeClassifier.IsUserClass(bt))
            EmitImplicitCtorChain(builder, compilation, instance, bt, getLayout, emitValue, callBaseCtor);
    }

    /// <summary>M4b: `method` occupies the System.Object.ToString dispatch slot — object.ToString itself,
    /// or a user override whose OverriddenMethod chain roots there (a `new`/`new virtual` ToString roots
    /// its OWN slot and is excluded, so member hiding keeps C# semantics: the hidden method is never
    /// dispatched by an object-typed stringify).</summary>
    public static bool IsObjectToStringSlot(IMethodSymbol method)
    {
        if (method is not { Name: "ToString", IsStatic: false } || method.Parameters.Length != 0)
            return false;
        return VirtualDispatch.SlotIntroducer(method).ContainingType?.SpecialType == SpecialType.System_Object;
    }

    /// <summary>M4b: what CLR Object.ToString() prints for an instance of `t` — Type.ToString() format:
    /// namespace-qualified, nested types joined with '+', a generic type as backtick-arity plus the
    /// constructed arguments' own full names in brackets (args flattened outer-to-inner, the reflection
    /// convention). This is the no-override dispatch arm's constant.</summary>
    public static string RuntimeTypeName(ITypeSymbol t)
    {
        if (t is IArrayTypeSymbol arr) return RuntimeTypeName(arr.ElementType) + "[]";
        if (t is not INamedTypeSymbol n) return t.ToDisplayString();
        var args = new List<ITypeSymbol>();
        var skeleton = ClrTypeSkeleton(n, args);
        if (args.Count == 0) return skeleton;
        return skeleton + "[" + string.Join(",", args.Select(RuntimeTypeName)) + "]";
    }

    static string ClrTypeSkeleton(INamedTypeSymbol n, List<ITypeSymbol> args)
    {
        var prefix = n.ContainingType is { } outer
            ? ClrTypeSkeleton(outer, args) + "+"
            : n.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." : "";
        args.AddRange(n.TypeArguments);
        return prefix + n.Name + (n.Arity > 0 ? "`" + n.Arity : "");
    }

    /// <summary>Reject static field storage on a v1 user class, except consts folded before this point.</summary>
    public static void RejectStaticField(IFieldSymbol field)
    {
        if (field.IsStatic && field.ContainingType is INamedTypeSymbol classTy && TypeClassifier.IsUserClass(classTy))
            throw new NotSupportedException(
                $"Static field '{classTy.Name}.{field.Name}' on a v1 user class is not "
                + "supported (only `const` is): a class has no per-type static storage yet. Move the data "
                + "to a field on the UdonSharpBehaviour class, or make it a `const`.");
    }

    /// <summary>Reject static properties on a v1 user class.</summary>
    public static void RejectStaticProperty(IPropertySymbol property)
    {
        if (property.IsStatic && property.ContainingType is INamedTypeSymbol classTy && TypeClassifier.IsUserClass(classTy))
            throw new NotSupportedException(
                $"Static property '{classTy.Name}.{property.Name}' on a v1 user class is not "
                + "supported (only `const` and static methods are): move it to a static method, or to "
                + "a field on the UdonSharpBehaviour class.");
    }

    /// <summary>Reject implicit stringification of a multi-dimensional array bundle (CW14/CW15) or an
    /// object[]-emulated value type (user struct / tuple / anonymous type — WaveJoint R1 D02): both
    /// stringify to "System.Object[]" instead of running ToString / printing the C# form, and the
    /// interpolation/concat Format externs bypass the N-R1 argument choke. (The former v1-class arm was
    /// replaced by the M4b object.ToString-slot dispatch at the implicit consumers.)</summary>
    public static void RejectImplicitToString(ITypeSymbol type)
    {
        if (type == null) return;
        if (NdimArrayAbi.IsNdimArray(type))
            throw new NotSupportedException(
                $"A multi-dimensional array ('{type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}') cannot be "
                + "converted to a string (interpolation / concat): its runtime value is an object[] bundle, so it would "
                + "stringify to \"System.Object[]\". Format the elements directly instead.");
        if (TypeClassifier.IsAggregateValue(type))
            throw new NotSupportedException(
                $"'{type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}' cannot be converted to a string "
                + "implicitly (interpolation / concat): its runtime value is an object[] bundle, so it would stringify "
                + "to \"System.Object[]\" instead of running ToString. Call ToString() explicitly (a struct override "
                + "is a supported direct call), or format the fields/elements directly.");
    }

    /// <summary>Reject user-defined operators and conversions on v1 user classes.</summary>
    public static void RejectUserOperator(IMethodSymbol method)
    {
        if (method is { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion }
            && method.ContainingType is INamedTypeSymbol classTy && TypeClassifier.IsUserClass(classTy))
            throw new NotSupportedException(
                $"User-defined operator '{classTy.Name}.{method.Name}' on a v1 user class is not supported: "
                + "a class has reference semantics (== / != compare object identity) and no user operator or "
                + "conversion is emitted. Call a named method instead.");
    }

    public static bool IsReferenceEquality(BinaryOperatorKind kind, ITypeSymbol leftType, ITypeSymbol rightType)
        => kind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals
           && (TypeClassifier.IsUserClass(leftType) || TypeClassifier.IsUserClass(rightType));

    public static CLeaf EmitReferenceEquality(CoreBuilder builder, BinaryOperatorKind kind, CLeaf left, CLeaf right)
    {
        var signature = kind == BinaryOperatorKind.NotEquals
            ? "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean"
            : "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean";
        return builder.ExternCall(signature, new List<CLeaf> { left, right }, StorageTypes.Boolean);
    }

    public static bool IsObjectMethodOnUserClass(IMethodSymbol method, ITypeSymbol receiverType)
        => !method.IsStatic
           && receiverType is INamedTypeSymbol classTy
           && TypeClassifier.IsUserClass(classTy)
           && method.ContainingType.SpecialType == SpecialType.System_Object;

    public static CLeaf EmitObjectEquals(CoreBuilder builder, CLeaf left, CLeaf right)
        => EmitReferenceEquality(builder, BinaryOperatorKind.Equals, left, right);

    public static string UnsupportedObjectMethodMessage(INamedTypeSymbol classTy, IMethodSymbol method)
        => $"'{classTy.Name}.{method.Name}()' is not supported on a v1 user class: class ABI v1 gives a "
           + "reference bundle no stable hash and no System.Type identity. Use reference equality "
           + "(== / Equals), or ToString/interpolation for a printable form.";

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
            && TypeClassifier.IsUserClass(classTy))
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
            var initHolder = f.IsImplicitlyDeclared && f.AssociatedSymbol is IPropertySymbol prop
                ? (ISymbol)prop : f;
            if (!layout.TryGetIndex(f, out var idx)) continue;
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
