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

/// <summary>One cross-program dispatch's possible landing in the program currently being compiled.
/// Emission consumes the constructed symbol; reachability and recursion consume its definition.</summary>
public readonly struct CrossDispatchPlan
{
    public readonly IMethodSymbol LocalTarget;
    public IMethodSymbol LocalTargetDefinition => LocalTarget?.OriginalDefinition;
    public bool HasLocalTarget => LocalTarget != null;

    public CrossDispatchPlan(IMethodSymbol localTarget) => LocalTarget = localTarget;
}

internal enum DispatchPrecision
{
    Static,
    ClosedWorld,
}

/// <summary>One normalized callable site's dispatch targets. Emission consumes RuntimeTargets;
/// recursion/reachability consume the same implementations and the optional local cross landing.</summary>
internal readonly struct DispatchPlan
{
    public readonly CallableSite Site;
    public readonly IReadOnlyList<VDispatchTarget> RuntimeTargets;
    public readonly CrossDispatchPlan Cross;
    public readonly DispatchPrecision Precision;
    public readonly IReadOnlyList<IMethodSymbol> LocalTargets;

    public DispatchPlan(CallableSite site, IReadOnlyList<VDispatchTarget> runtimeTargets,
        CrossDispatchPlan cross, DispatchPrecision precision)
    {
        Site = site;
        RuntimeTargets = runtimeTargets;
        Cross = cross;
        Precision = precision;
        LocalTargets = runtimeTargets.Select(target => target.Impl.OriginalDefinition)
            .Concat(cross.HasLocalTarget
                ? new[] { cross.LocalTargetDefinition }
                : System.Array.Empty<IMethodSymbol>())
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
            .ToArray();
    }
}

public sealed class VirtualDispatch
{
    readonly ClassTypeObjectContext _typeObjs;

    public VirtualDispatch(ClassTypeObjectContext typeObjs) { _typeObjs = typeObjs; }

    /// <summary>Single target-resolution entry for normalized callable sites. The caller states whether
    /// the receiver uses interface dispatch; every consumer then receives the same closed-world target
    /// list instead of selecting one of two resolver APIs independently.</summary>
    internal DispatchPlan Resolve(CallableSite site, INamedTypeSymbol receiverType,
        INamedTypeSymbol compiledClass = null)
    {
        var interfaceDispatch = receiverType?.TypeKind == TypeKind.Interface;
        var runtimeTargets = interfaceDispatch
            ? ResolveInterfaceTargets(receiverType, site.Target)
            : IsDispatchSite(site.Target, site.Receiver, receiverType)
                ? ResolveTargets(receiverType, site.Target)
                : new List<VDispatchTarget>();
        var cross = compiledClass == null
            ? default : ResolveCrossProgramLocalTarget(compiledClass, site.Target);
        return new DispatchPlan(site, runtimeTargets, cross,
            runtimeTargets.Count == 0 ? DispatchPrecision.Static : DispatchPrecision.ClosedWorld);
    }

    /// <summary>Resolve where a variable/interface receiver dispatch lands if it points back to the
    /// program currently being compiled. Shared by emission re-entry marking and SCC analysis.</summary>
    static CrossDispatchPlan ResolveCrossProgramLocalTarget(
        INamedTypeSymbol compiledClass, IMethodSymbol target)
    {
        if (compiledClass == null || target == null || target.IsStatic)
            return default;

        IMethodSymbol localTarget;
        if (target.ContainingType?.TypeKind == TypeKind.Interface)
        {
            localTarget = (compiledClass.FindImplementationForInterfaceMember(target)
                           ?? compiledClass.FindImplementationForInterfaceMember(target.OriginalDefinition))
                          as IMethodSymbol;
            if (localTarget == null) return default;
        }
        else
        {
            bool belongsToFamily = false;
            for (var type = compiledClass; type != null; type = type.BaseType)
                if (SymbolEqualityComparer.Default.Equals(type, target.ContainingType))
                {
                    belongsToFamily = true;
                    break;
                }
            if (!belongsToFamily) return default;
            localTarget = target;
        }

        var leaf = LoweringServices.FindOverrideMethodInChain(
            compiledClass, localTarget.OriginalDefinition, localTarget.Name) ?? localTarget;
        if (target.IsGenericMethod && leaf.IsGenericMethod)
            leaf = leaf.OriginalDefinition.Construct(target.TypeArguments.ToArray());
        return new CrossDispatchPlan(leaf);
    }

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
    List<VDispatchTarget> ResolveTargets(INamedTypeSymbol staticType, IMethodSymbol slotMethod)
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

    List<VDispatchTarget> ResolveInterfaceTargets(INamedTypeSymbol interfaceType, IMethodSymbol member)
    {
        var outp = new List<VDispatchTarget>();
        foreach (var concrete in _typeObjs.MintedClasses)
        {
            if (!concrete.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, interfaceType)))
                continue;
            var impl = FindInterfaceMethodImplementation(concrete, member);
            if (impl == null || impl.IsAbstract) continue;
            if (impl.IsVirtual || impl.IsOverride)
                impl = MostDerivedImpl(concrete, SlotIntroducer(impl)) ?? impl;
            var typeObj = _typeObjs.TryGetTypeObjVar(concrete);
            if (typeObj != null) outp.Add(new VDispatchTarget(concrete, typeObj, impl));
        }
        return outp;
    }

    /// <summary>Resolve an interface method/accessor on a concrete class. Roslyn does not reliably
    /// accept a constructed generic method in FindImplementationForInterfaceMember, so lookup uses
    /// the definition and reapplies the call-site method arguments exactly once.</summary>
    public static IMethodSymbol FindInterfaceMethodImplementation(
        INamedTypeSymbol concrete, IMethodSymbol target)
    {
        if (concrete == null || target == null) return null;
        var member = concrete.FindImplementationForInterfaceMember(target)
            ?? concrete.FindImplementationForInterfaceMember(target.OriginalDefinition);
        if (member == null && target.AssociatedSymbol != null)
            member = concrete.FindImplementationForInterfaceMember(target.AssociatedSymbol)
                ?? concrete.FindImplementationForInterfaceMember(target.AssociatedSymbol.OriginalDefinition);
        var method = member switch
        {
            IMethodSymbol m => m,
            IPropertySymbol property when target.MethodKind == MethodKind.PropertyGet => property.GetMethod,
            IPropertySymbol property when target.MethodKind == MethodKind.PropertySet => property.SetMethod,
            IEventSymbol evt when target.MethodKind == MethodKind.EventAdd => evt.AddMethod,
            IEventSymbol evt when target.MethodKind == MethodKind.EventRemove => evt.RemoveMethod,
            _ => null,
        };
        if (method?.IsGenericMethod == true && target.IsGenericMethod
            && !target.TypeArguments.Any(ClassTypeObjectContext.ContainsTypeParameter))
            method = method.OriginalDefinition.Construct(target.TypeArguments.ToArray());
        return method;
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
