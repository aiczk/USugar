using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Stateless C#-shape policy and type-fact checks shared across handlers/emitter — no
/// EmitContext instance state, only Compilation/symbol/operation inputs. Split out of EmitContext
/// (which otherwise mixed this with per-class emission state) so a fact/policy lookup never implies
/// an EmitContext instance is needed.</summary>
public static class EmitPolicy
{
    /// <summary>True if <paramref name="t"/> is <c>Nullable&lt;T&gt;</c>; yields the underlying T.
    /// Nullable is emulated as a boxed object (null | boxed T) — see ExternResolver type mapping.</summary>
    public static bool IsNullableT(ITypeSymbol t, out ITypeSymbol underlying)
    {
        if (t is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            underlying = n.TypeArguments[0];
            return true;
        }
        underlying = null;
        return false;
    }

    // ── Tail-call analysis (shared by named-method and recursive-lambda recursion detection) ──
    // A self-recursive call only needs spilling when it is NOT in tail position: a tail call reads nothing
    // of its frame afterwards, so the flat-heap clobber is harmless and deep tail recursion must not spill.
    // The walk itself lives in TailCallAnalysis (shared with UasmEmitter.HasNonTailCallTo); this is the
    // delegate-dispatch-site matcher's parameterization of it.

    /// <summary>Generalized delegate-dispatch matcher: ANY-receiver delegate Invoke (design §4.2;
    /// the pre-§4 local-variable-only matcher was removed per deletion #12).</summary>
    public static bool IsDelegateDispatch(IOperation op)
        => op is IInvocationOperation inv && inv.TargetMethod?.MethodKind == MethodKind.DelegateInvoke;

    /// <summary>True when THIS specific dispatch operation occurs in NON-tail position within
    /// <paramref name="body"/> (per-site tail sparing, design §4.3/§4.4: tail dispatches are never
    /// marked Reentrant so bundle-driven deep tail recursion stays spill-free). Reference-equality
    /// matcher — body and site must come from the SAME operation tree. A dispatch site is always a
    /// bare IInvocationOperation (never a property accessor), so the receiver leg is not checked on
    /// return (never reachable — see TailCallAnalysis's file header) and ternary return branches keep
    /// their precise tail treatment.</summary>
    public static bool IsNonTailDispatchSite(IOperation body, IOperation site)
        => TailCallAnalysis.HasNonTailCall(body,
            (IOperation op, out IOperation matched) =>
            {
                matched = op;
                return ReferenceEquals(op, site) && op is IInvocationOperation;
            },
            matchesAccessor: (_, _) => false,
            checkReturnInstanceLeg: false,
            ternaryPreciseReturn: true);

    /// <summary>Round-7 follow-up [Q3]: `in` parameters (RefKind.In) are a loud declaration-side
    /// reject. The flat-heap calling convention copies arguments by value with no copy-back, so an
    /// `in` param is neither a readonly ALIAS of the caller's storage (VM-proven: a callee observing
    /// a caller field write through the param read 1 vs CLR 5) nor protected by the readonly
    /// DEFENSIVE COPY (a mutating struct method on the param wrote the param storage, 11 vs CLR 1).
    /// Called at every user-method registration point (class/base/struct/foreign-static methods,
    /// generic specializations, local functions); delegates with `in` params already reject via
    /// DelegateAbi.ValidateNoRefOutParams (RefKind != None).</summary>
    public static void RejectInParameters(IMethodSymbol method)
    {
        foreach (var p in method.Parameters)
            if (p.RefKind == RefKind.In)
                throw new System.NotSupportedException(
                    $"'in' parameter '{p.Name}' on '{method.Name}' is not supported: the flat-heap "
                    + "calling convention copies by value, so 'in' would silently lose its readonly-"
                    + "alias and defensive-copy semantics. Use a by-value parameter, or ref if "
                    + "write-back is intended.");
    }

    /// <summary>M4 [T1]: a [NetworkCallable] method's parameters cross the network, but a delegate
    /// value is a program-local object[] bundle — its target reference and funcaddr are meaningless
    /// in any other client's program, so it can never be marshalled. Pre-fix (probed at 931a9ab)
    /// this compiled CLEAN: the method exported unmangled with a SystemObjectArray param var, a
    /// silent runtime miscompile. The delegate-typed RETURN flavor also compiled clean, even though
    /// stock UdonSharp forbids ANY return type on [NetworkCallable] ("cannot have a return type") —
    /// rejected here for the same bundle reason. Called from the class first-pass registration loop
    /// (own + inherited behaviour methods, before the generic skip), so every compile of a class
    /// hits it exactly once per method.</summary>
    public static void RejectNetworkCallableDelegates(IMethodSymbol method)
    {
        if (!LayoutPlanner.IsNetworkCallable(method)) return;
        foreach (var p in method.Parameters)
            if (ContainsDelegateType(p.Type))
                throw new System.NotSupportedException(
                    $"[NetworkCallable] method '{method.Name}' cannot take delegate-typed parameter "
                    + $"'{p.Name}': a delegate value is a program-local object[] bundle and cannot "
                    + "cross a network call. Pass plain data instead and re-create the delegate "
                    + "locally on the receiving side.");
        if (ContainsDelegateType(method.ReturnType))
            throw new System.NotSupportedException(
                $"[NetworkCallable] method '{method.Name}' cannot return a delegate-typed value: "
                + "a delegate value is a program-local object[] bundle and cannot cross a network "
                + "call. Return plain data instead and re-create the delegate locally on the "
                + "receiving side.");
    }

    /// <summary>Delegate proper, an array (of arrays…) of delegates, or a delegate reachable through
    /// a struct/tuple field. A delegate smuggled inside a struct/tuple param is the same program-local
    /// object[] bundle and equally cannot cross the network — recurse aggregate fields so it rejects too
    /// (visited set guards a struct that transitively contains itself). Still NARROW on object / bare
    /// type params: [NetworkCallable] methods with plain object params stay outside this policy.</summary>
    static bool ContainsDelegateType(ITypeSymbol type)
        => ContainsDelegateType(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

    static bool ContainsDelegateType(ITypeSymbol type, HashSet<ITypeSymbol> visited)
    {
        if (type is INamedTypeSymbol n && n.DelegateInvokeMethod != null) return true;
        if (type is IArrayTypeSymbol a) return ContainsDelegateType(a.ElementType, visited);
        if (type is INamedTypeSymbol agg && IsAggregateType(agg) && visited.Add(agg))
            foreach (var m in agg.GetMembers())
                if (m is IFieldSymbol f && !f.IsStatic && ContainsDelegateType(f.Type, visited))
                    return true;
        return false;
    }

    // Aggregate type support — tuples and user-defined structs share the object[] emulation.
    public static bool IsAggregateType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return false;
        // Armor (design §1.2): a delegate value is an object[] BUNDLE copied by reference — it must never
        // ride the aggregate clone-on-read machinery (a clone would break reference identity and
        // (target, method) equality). Single choke point for every clone path.
        if (named.TypeKind == TypeKind.Delegate) return false;
        return named.IsTupleType || IsUserStruct(named);
    }

    /// <summary>Source-defined value struct (object[]-emulated). Excludes SDK/native structs
    /// (Vector3, Color, …) — which have native Udon extern types — by namespace, since in the test
    /// environment SDK types are source stubs (so syntax-refs alone can't tell them apart).</summary>
    public static bool IsUserStruct(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Struct || type.SpecialType != SpecialType.None) return false;
        if (type.DeclaringSyntaxReferences.Length == 0) return false; // from a referenced assembly = native
        return !IsSdkNamespace(type.ContainingNamespace);
    }

    /// <summary>Class ABI v1 (CA-M1): a plain user class the compiler represents as a reference-semantics
    /// object[1+F] bundle (slot 0 reserved). The single semantic entry for class support — a supported v1
    /// class has no explicit base (System.Object only) and is not a record; those excluded shapes stay
    /// B79-rejected. Distinct from IsAggregateType (which stays FALSE for a class) so the ~30 clone-on-read
    /// sites keyed on IsAggregateType automatically give a class REFERENCE semantics.</summary>
    public static bool IsUserClassType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol n) return false;
        if (!ExternResolver.IsPlainUserClass(n)) return false;
        if (n.IsRecord) return false;
        if (n.BaseType != null && n.BaseType.SpecialType != SpecialType.System_Object) return false;
        // A foreign type modelled as a source stub (registered externs — DisposableResource, and real SDK
        // types) is NOT a v1 user class: it keeps its real Udon extern type, not the object[] bundle ABI.
        if (ExternResolver.ClassHasRegisteredExterns(n)) return false;
        return true;
    }

    /// <summary>A named type whose runtime value is an object[] the VM tags SystemObjectArray — a user
    /// struct/tuple OR a v1 user class. The "is this object[]-emulated?" test for Category-A sites (layout,
    /// mint, field-slot resolution, size); clone-on-read (Category-B) sites keep IsAggregateType alone so a
    /// class stays reference-semantics.</summary>
    public static bool IsObjectArrayEmulated(ITypeSymbol type)
        => IsAggregateType(type) || IsUserClassType(type);

    /// <summary>Evaluates a field's initializer syntax to a compile-time constant (primitives/enums/
    /// string). A `static readonly` field has no ConstantValue of its own (only `const` does), so this
    /// folds `static readonly int X = 1 + 2;`-style initializers that ARE compile-time-constant
    /// expressions. Shared by ExpressionHandler's read-time fold and UasmEmitter's field-declaration
    /// walk — the two must agree on which static readonly fields get no storage (const-fold path)
    /// vs. which get per-program materialized (design §3.1, feature B).</summary>
    public static bool TryGetConstFieldInitializer(Compilation compilation, IFieldSymbol field, out object value)
    {
        value = null;
        var refs = field.DeclaringSyntaxReferences;
        if (refs.Length > 0 && refs[0].GetSyntax()
            is Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax { Initializer: { } init })
        {
            var cv = compilation.GetSemanticModel(init.SyntaxTree).GetConstantValue(init.Value);
            if (cv.HasValue) { value = cv.Value; return true; }
        }
        return false;
    }

    /// <summary>True when a `static readonly` field initializer's operation tree is composed ONLY of
    /// side-effect-free, instance-invariant shapes: literals, other static readonly reads (const or
    /// materialized — cross-class non-const reads are independently loud-rejected at emit time, §3.5,
    /// not a purity concern), value-type construction, array/tuple initializers of pure elements,
    /// built-in (non-overloaded) arithmetic/conversion operators, and nameof/typeof/default (design
    /// §3.4, Q-S3). Per-program materialization runs the initializer once PER BEHAVIOUR INSTANCE
    /// (Udon has no shared static storage) — an impure initializer (method call, instance-state read,
    /// a Time/Random-style extern) would silently diverge per instance where C# runs it exactly once.
    /// Deliberately conservative: any operation kind not recognized here rejects rather than risk
    /// silently re-running a side effect N times (§8-4 self-critique — a stricter gate is the
    /// accepted trade against rejecting some pure-but-unrecognized shapes).</summary>
    public static bool IsPureStaticReadonlyInitializer(IOperation op)
    {
        switch (op)
        {
            case null:
            case ILiteralOperation:
            case IDefaultValueOperation:
            case ITypeOfOperation:
            case INameOfOperation:
                return true;
            case IFieldReferenceOperation fieldRef:
                return fieldRef.Field.IsStatic && (fieldRef.Field.IsConst || fieldRef.Field.IsReadOnly);
            // A delegate field's initializer is bound via the EqualsValueClause's conversion-STRIPPED
            // inner op (matching UasmEmitter.DeclareDelegateField's own binding — see its comment), so a
            // `static readonly Action a = SomeStaticMethod;` initializer arrives here as a bare
            // IMethodReferenceOperation, not IDelegateCreationOperation. Binding a static method group is
            // side-effect-free (no instance to read); an instance-bound method reference can't legally
            // appear in a static initializer in the first place, but is rejected defensively anyway.
            case IMethodReferenceOperation mref:
                return mref.Instance == null;
            // wave-13 staticro lens (2026-07-04): minting a delegate from a lambda literal or an
            // explicit delegate-creation expression is itself side-effect-free (the lambda BODY only
            // runs later, at invocation — never during the mint). A lambda appearing directly in a
            // static field initializer can never capture instance state (no `this`/locals exist in
            // that scope; C# itself rejects an instance-member reference there), so unlike an ordinary
            // method call this is unconditionally pure — same category as the method-group case above.
            case IAnonymousFunctionOperation:
                return true;
            case IDelegateCreationOperation dc:
                return IsPureStaticReadonlyInitializer(dc.Target);
            case IConversionOperation conv:
                return IsPureStaticReadonlyInitializer(conv.Operand);
            case IUnaryOperation { OperatorMethod: null } un:
                return IsPureStaticReadonlyInitializer(un.Operand);
            case IBinaryOperation { OperatorMethod: null } bin:
                return IsPureStaticReadonlyInitializer(bin.LeftOperand) && IsPureStaticReadonlyInitializer(bin.RightOperand);
            case IArrayCreationOperation arrCreate:
                return arrCreate.DimensionSizes.All(IsPureStaticReadonlyInitializer)
                    && (arrCreate.Initializer == null || IsPureStaticReadonlyInitializer(arrCreate.Initializer));
            case IArrayInitializerOperation arrInit:
                return arrInit.ElementValues.All(IsPureStaticReadonlyInitializer);
            case ITupleOperation tuple:
                return tuple.Elements.All(IsPureStaticReadonlyInitializer);
            case IObjectCreationOperation objCreate when objCreate.Type?.IsValueType == true:
                return objCreate.Arguments.All(a => IsPureStaticReadonlyInitializer(a.Value))
                    && (objCreate.Initializer == null
                        || objCreate.Initializer.Initializers.All(m =>
                            m is ISimpleAssignmentOperation asn && IsPureStaticReadonlyInitializer(asn.Value)));
            default:
                return false;
        }
    }

    /// <summary>The parameterless void Dispose() of a user type (public or explicit IDisposable impl),
    /// or null. Used to route a `using` resource's implicit Dispose through a real method call rather
    /// than a non-existent SystemObjectArray.__Dispose__ extern when the disposable is a user struct.</summary>
    public static IMethodSymbol FindStructDisposeMethod(ITypeSymbol type)
    {
        foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
            if (!m.IsStatic && m.Parameters.Length == 0 && m.ReturnsVoid
                && (m.Name == "Dispose"
                    || m.ExplicitInterfaceImplementations.Any(e => e.Name == "Dispose")))
                return m;
        return null;
    }

    static bool IsSdkNamespace(INamespaceSymbol ns)
    {
        for (var n = ns; n != null && !n.IsGlobalNamespace; n = n.ContainingNamespace)
        {
            if (n.Name is "System" or "UnityEngine" or "VRC" or "Cinemachine"
                or "TMPro" or "Unity" or "Microsoft")
                return true;
        }
        return false;
    }

    // ── Constant parsing (moved from VariableTable) ──

    /// <summary>Parse a string constant value to a typed CLR object.</summary>
    public static object ParseConstValue(string udonType, string value)
    {
        if (value == "null") return null;
        return udonType switch
        {
            "SystemInt32" => value.StartsWith("0x") ? Convert.ToInt32(value, 16) : int.Parse(value),
            "SystemUInt32" => value.StartsWith("0x") ? Convert.ToUInt32(value, 16) : uint.Parse(value),
            "SystemInt64" => long.Parse(value),
            "SystemUInt64" => ulong.Parse(value),
            "SystemInt16" => short.Parse(value),
            "SystemUInt16" => ushort.Parse(value),
            "SystemSByte" => sbyte.Parse(value),
            "SystemSingle" => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            "SystemDouble" => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            "SystemBoolean" => bool.Parse(value),
            "SystemString" => value,
            "SystemByte" => byte.Parse(value),
            "SystemChar" => value[0],
            "SystemType" => value, // Udon type name, resolved to CLR Type at apply time
            _ => long.TryParse(value, out var longVal)
                ? (longVal is >= int.MinValue and <= int.MaxValue ? (object)(int)longVal : longVal)
                : ulong.TryParse(value, out var ulongVal) ? (object)ulongVal : null,
        };
    }
}
