using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>CA-v2b-2 (inline typeobj-dispatch): the single authority for lowering a virtual call. Given a
/// call's receiver STATIC type and the slot method, it enumerates the closed-world set of minted concrete
/// subtypes and each one's most-derived override (resolved through the <c>OverriddenMethod</c> chain — so
/// member hiding via <c>new</c>/<c>new virtual</c> is correct without extra rules). Emission
/// (InvocationHandler) and recursion analysis (BuildRecursionInfo) both read this one enumeration, so the
/// dispatch chain and the call-graph edges can never disagree. typeobj shape is unchanged; this only
/// CONSUMES the v2b-1 typeobj registry.</summary>
public readonly struct VDispatchTarget
{
    public readonly INamedTypeSymbol Concrete;
    public readonly string TypeObjVar;
    public readonly IMethodSymbol Impl;
    public VDispatchTarget(INamedTypeSymbol c, string v, IMethodSymbol impl) { Concrete = c; TypeObjVar = v; Impl = impl; }
}

public sealed class VirtualDispatch
{
    readonly ClassTypeObjectContext _typeObjs;

    public VirtualDispatch(ClassTypeObjectContext typeObjs) { _typeObjs = typeObjs; }

    /// <summary>A runtime-polymorphic ordinary call OR property/indexer accessor call. Generic virtual
    /// methods use the same slot after closing the selected implementation with call-site type args.</summary>
    public static bool IsVirtualCall(IMethodSymbol target)
        => (target.IsVirtual || target.IsAbstract || target.IsOverride)
           && target.MethodKind is MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.PropertySet;

    /// <summary>The SINGLE predicate for "this invocation OR property/indexer accessor reference is a
    /// runtime-polymorphic dispatch site on a v1 user-class receiver": a virtual call whose receiver is a base-typed variable OR <c>this</c> — NOT
    /// <c>base</c> (a non-virtual direct call to a specific base impl). The receiver's static type is passed
    /// in because the caller resolves it differently by phase (through the monomorphization map at emit, or
    /// the declared type in Phase-1 reach). Both the emission branch (InvocationHandler) and the
    /// recursion-graph enumerator (EnumerateInternalCallTargets) call this, so the two can never drift — a
    /// drift silently mis-spills polymorphic recursion.</summary>
    public static bool IsDispatchSite(IMethodSymbol target, IOperation instance, INamedTypeSymbol receiverType)
        => IsVirtualCall(target)
           && !(instance is IInstanceReferenceOperation ir
                && ir.Syntax is Microsoft.CodeAnalysis.CSharp.Syntax.BaseExpressionSyntax)
           && receiverType != null && TypeClassifier.IsUserClass(receiverType);

    /// <summary>CW1 lift: the accessor a property reference dispatches — the property's own accessor,
    /// or the nearest base declaration's when a partial-accessor override omits it (C#: `override int P
    /// { set … }` inherits the base getter; Roslyn leaves the override's GetMethod null).</summary>
    public static IMethodSymbol FindAccessor(IPropertySymbol prop, bool getter)
    {
        for (var cur = prop; cur != null; cur = cur.OverriddenProperty)
            if ((getter ? cur.GetMethod : cur.SetMethod) is { } acc)
                return acc;
        return null;
    }

    /// <summary>The root virtual declaration that owns the dispatch slot for m (the deepest
    /// <c>OverriddenMethod</c>). A non-override virtual (incl. <c>new virtual</c>) is its own root, so it
    /// forms a distinct slot from any base method it hides. Accessor symbols chain through
    /// <c>OverriddenMethod</c> exactly like ordinary methods, so property/indexer slots need no extra
    /// rules (a `new`/`new virtual` property's accessors root their own distinct slots).</summary>
    public static IMethodSymbol SlotIntroducer(IMethodSymbol m)
    {
        var cur = m.OriginalDefinition;
        while (cur.OverriddenMethod != null) cur = cur.OverriddenMethod.OriginalDefinition;
        return cur;
    }

    /// <summary>The closed-world set of (minted concrete subtype of staticType, its most-derived impl of the
    /// slot). Empty if no minted subtype implements it; singleton ⇒ devirtualizable; ≥2 ⇒ ReferenceEquals-chain.</summary>
    public List<VDispatchTarget> ResolveTargets(INamedTypeSymbol staticType, IMethodSymbol slotMethod)
    {
        var slotDef = SlotIntroducer(slotMethod);
        var outp = new List<VDispatchTarget>();
        foreach (var concrete in _typeObjs.MintedClasses)
        {
            if (!IsAssignable(concrete, staticType)) continue;
            var impl = MostDerivedImpl(concrete, slotDef);
            if (impl == null)
            {
                if (slotDef.IsAbstract) continue; // a concrete type with an abstract, unimplemented slot cannot occur
                impl = slotDef;                    // non-abstract slot not overridden in this type
            }
            if (slotMethod.IsGenericMethod && impl.IsGenericMethod)
                impl = impl.Construct(slotMethod.TypeArguments.ToArray());
            var v = _typeObjs.TryGetTypeObjVar(concrete);
            if (v != null) outp.Add(new VDispatchTarget(concrete, v, impl));
        }
        return outp;
    }

    public List<VDispatchTarget> ResolveInterfaceTargets(INamedTypeSymbol interfaceType, IMethodSymbol member)
    {
        var outp = new List<VDispatchTarget>();
        foreach (var concrete in _typeObjs.MintedClasses)
        {
            if (!concrete.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, interfaceType)))
                continue;
            var impl = concrete.FindImplementationForInterfaceMember(member) as IMethodSymbol;
            if (impl == null || impl.IsAbstract) continue;
            if (impl.IsVirtual || impl.IsOverride)
                impl = MostDerivedImpl(concrete, SlotIntroducer(impl)) ?? impl;
            var typeObj = _typeObjs.TryGetTypeObjVar(concrete);
            if (typeObj != null) outp.Add(new VDispatchTarget(concrete, typeObj, impl));
        }
        return outp;
    }

    /// <summary>Walk `concrete` and its base chain from most-derived up; return the first NON-abstract method
    /// that participates in `slotDef`'s virtual slot (its <c>SlotIntroducer</c> is slotDef). A <c>new</c> /
    /// <c>new virtual</c> method roots a different slot, so it is skipped for this slot (member hiding).</summary>
    public static IMethodSymbol MostDerivedImpl(INamedTypeSymbol concrete, IMethodSymbol slotDef)
    {
        for (var t = concrete; t != null; t = t.BaseType)
            foreach (var member in t.GetMembers(slotDef.Name))
                if (member is IMethodSymbol m && !m.IsAbstract
                    && SymbolEqualityComparer.Default.Equals(SlotIntroducer(m), slotDef))
                    return m;
        return null;
    }

    /// <summary>`concrete` is-or-derives-from `target` (base-chain walk). Also the CW2 cast arm's
    /// upcast/identity test (an assignable source needs no runtime typeobj check).</summary>
    public static bool IsAssignable(INamedTypeSymbol concrete, INamedTypeSymbol target)
    {
        for (var t = concrete; t != null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, target)) return true;
        return false;
    }
}
