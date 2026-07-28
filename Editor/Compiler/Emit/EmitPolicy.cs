using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>Stateless C#-shape policy and type-fact checks shared across handlers/emitter — no
/// LoweringState instance state, only Compilation/symbol/operation inputs. Split out of LoweringState
/// (which otherwise mixed this with per-class emission state) so a fact/policy lookup never implies
/// an LoweringState instance is needed.</summary>
public static class EmitPolicy
{
    public static int GetBehaviourSyncMode(INamedTypeSymbol type)
        => GetBehaviourSyncModeArgument(type)?.Value is int mode
            ? mode
            : -1;

    public static string GetBehaviourSyncModeName(
        INamedTypeSymbol type)
    {
        var argument = GetBehaviourSyncModeArgument(type);
        if (argument?.Type is not INamedTypeSymbol enumType
            || argument.Value.Value is not int value)
            return null;
        return enumType.GetMembers()
            .OfType<IFieldSymbol>()
            .FirstOrDefault(field =>
                field.HasConstantValue
                && field.ConstantValue is int fieldValue
                && fieldValue == value)
            ?.Name;
    }

    static TypedConstant? GetBehaviourSyncModeArgument(
        INamedTypeSymbol type)
    {
        for (var current = type;
             current != null && current.Name != "UdonSharpBehaviour";
             current = current.BaseType)
        {
            var attribute = current.GetAttributes()
                .FirstOrDefault(candidate =>
                    candidate.AttributeClass?.Name
                    == "UdonBehaviourSyncModeAttribute");
            if (attribute != null
                && attribute.ConstructorArguments.Length > 0)
                return attribute.ConstructorArguments[0];
        }
        return null;
    }

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
    /// Called by the single callable registrar and by delegate surface declaration, so named methods,
    /// specializations, closures, and delegate-only declarations share one rejection contract.</summary>
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
    internal static void RejectNetworkCallableDelegates(
        IMethodSymbol method, IUdonTypeSystem types)
    {
        if (types == null) throw new ArgumentNullException(nameof(types));
        if (!LayoutPlanBuilder.IsNetworkCallable(method)) return;
        foreach (var p in method.Parameters)
        {
            if (types.SourceShape(p.Type).ContainsDelegate)
                throw new System.NotSupportedException(
                    $"[NetworkCallable] method '{method.Name}' cannot take delegate-typed parameter "
                    + $"'{p.Name}': a delegate value is a program-local object[] bundle and cannot "
                    + "cross a network call. Pass plain data instead and re-create the delegate "
                    + "locally on the receiving side.");
            // CA-M1 §2-1: a v1 class parameter is the same program-local object[] bundle — cannot cross.
            if (types.SourceShape(p.Type).ContainsUserClassPayload)
                throw new System.NotSupportedException(
                    $"[NetworkCallable] method '{method.Name}' cannot take v1-class-typed parameter "
                    + $"'{p.Name}': a class value is a program-local object[] bundle and cannot cross a "
                    + "network call. Pass plain data instead and rebuild the object on the receiving side.");
        }
        if (types.SourceShape(method.ReturnType).ContainsDelegate)
            throw new System.NotSupportedException(
                $"[NetworkCallable] method '{method.Name}' cannot return a delegate-typed value: "
                + "a delegate value is a program-local object[] bundle and cannot cross a network "
                + "call. Return plain data instead and re-create the delegate locally on the "
                + "receiving side.");
        if (types.SourceShape(method.ReturnType).ContainsUserClassPayload)
            throw new System.NotSupportedException(
                $"[NetworkCallable] method '{method.Name}' cannot return a v1-class-typed value: a class "
                + "value is a program-local object[] bundle and cannot cross a network call.");
    }

    /// <summary>A public behaviour method is callable by another Udon program. A delegate whose
    /// signature carries a user class is deliberately self-dispatch-only, so exposing such a bundle
    /// as a parameter or return would create an unusable cross-program surface.</summary>
    internal static void RejectPublicProgramLocalDelegateSignature(
        IMethodSymbol method, IUdonTypeSystem types)
    {
        if (types == null) throw new ArgumentNullException(nameof(types));
        if (method.DeclaredAccessibility != Accessibility.Public) return;
        foreach (var p in method.Parameters)
            if (p.Type is INamedTypeSymbol d && d.DelegateInvokeMethod is { } invoke
                && DelegateAbi.IsProgramLocalSignature(invoke, types))
                throw new NotSupportedException(
                    $"Public method '{method.Name}' cannot expose delegate parameter '{p.Name}' because "
                    + "its user-class signature is valid only inside this Udon program.");
        if (method.ReturnType is INamedTypeSymbol rd && rd.DelegateInvokeMethod is { } returnInvoke
            && DelegateAbi.IsProgramLocalSignature(returnInvoke, types))
            throw new NotSupportedException(
                $"Public method '{method.Name}' cannot expose a delegate return with a user-class "
                + "signature because it is valid only inside this Udon program.");
    }

    /// <summary>Evaluates a field's initializer syntax to a compile-time constant (primitives/enums/
    /// string). A `static readonly` field has no ConstantValue of its own (only `const` does), so this
    /// folds `static readonly int X = 1 + 2;`-style initializers that ARE compile-time-constant
    /// expressions. Shared by ExpressionHandler's read-time fold and UasmEmitter's field-declaration
    /// walk — the two must agree on the only accepted static-field shape: a value that needs
    /// no runtime storage.</summary>
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

    /// <summary>
    /// The implicit user callable introduced by a using resource declaration. Roslyn does not
    /// represent this as an invocation operation, so reachability and closed-specialization census
    /// consume this shared classifier rather than independently walking declaration shapes.
    /// </summary>
    internal static IEnumerable<IMethodSymbol> UsingDisposeMethods(IOperation operation)
    {
        var resources = operation is IUsingOperation usingOperation
            ? usingOperation.Resources
            : operation is IUsingDeclarationOperation usingDeclaration
                ? usingDeclaration.DeclarationGroup
                : null;
        if (resources is IVariableDeclarationGroupOperation group)
        {
            foreach (var declaration in group.Declarations)
                foreach (var declarator in declaration.Declarators)
                    if (declarator.Symbol.Type is INamedTypeSymbol named
                        && FindStructDisposeMethod(named) is { } dispose)
                        yield return dispose;
            yield break;
        }
        if (resources?.Type is INamedTypeSymbol resourceType
            && FindStructDisposeMethod(resourceType) is { } resourceDispose)
            yield return resourceDispose;
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
            // WjR3 C07: without this arm a const-folded decimal (`-12.5m` folds through unary minus,
            // `2m + 3m` through the binary fold) fell to the integer default arm and became null → 0.
            "SystemDecimal" => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            "SystemBoolean" => bool.Parse(value),
            "SystemString" => value,
            "SystemByte" => byte.Parse(value),
            "SystemChar" => value[0],
            "SystemType" => value, // Udon type name, resolved to CLR Type at apply time
            // Default arm: an SDK enum constant reaches here under its registered Udon type name with
            // an integral raw value. Anything that fails BOTH integer parses has no arm at all — loud,
            // not null (a silent null reads back as 0 at apply time, the be04dd6 SystemDecimal bug).
            _ => long.TryParse(value, out var longVal)
                ? (longVal is >= int.MinValue and <= int.MaxValue ? (object)(int)longVal : longVal)
                : ulong.TryParse(value, out var ulongVal)
                    ? (object)ulongVal
                    : throw new NotSupportedException(
                        $"ParseConstValue has no parse arm for Udon type '{udonType}' "
                        + $"(raw constant '{value}') — a silent null would read back as 0."),
        };
    }
}
