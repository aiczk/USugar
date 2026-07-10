using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Owns per-method emission bookkeeping for one class emission.
/// </summary>
public sealed class MethodContext
{
    public readonly Dictionary<IMethodSymbol, CFunction> Functions = new(SymbolEqualityComparer.Default);

    public readonly Dictionary<IMethodSymbol, EmitContext.MethodSlot> Slots = new(SymbolEqualityComparer.Default);

    public readonly Dictionary<IMethodSymbol, ReturnSlot[]> Returns = new(SymbolEqualityComparer.Default);

    public readonly Dictionary<IMethodSymbol, string[]> ParamVarIds = new(SymbolEqualityComparer.Default);

    public IMethodSymbol CurrentMethod;

    // When emitting a user-struct method/ctor, the receiver object[] param var id; otherwise null.
    // Makes this / this.field resolve to the receiver array instead of the Behaviour.
    public string CurrentStructReceiverParamId;

    public int NextMethodIndex;

    public EmitContext.MethodSlot Register(IMethodSymbol method, Func<int, string> prefixFactory)
    {
        var idx = NextMethodIndex++;
        var slot = new EmitContext.MethodSlot(idx, prefixFactory(idx));
        Slots[method] = slot;
        return slot;
    }

    // ── Per-spec hoisted closures (design 2026-07-10 v3 §2B, B64/B70 root fix) ──
    //
    // A lambda / non-generic local function's IMethodSymbol is DEFINITION-EQUAL across the enclosing
    // generic's instantiations ([Y8]), so a bare symbol key shares ONE hoisted CFunction across specs —
    // the closure body is then baked with the first spec's type arguments and its flat bookkeeping
    // aliases (VM-proven B64/B70/B89). Every hoisted closure therefore registers under a composite
    // (definition, enclosing-spec type-args) key. The args component uses CLR SYMBOL identity
    // (element-wise SymbolEqualityComparer) — NEVER Udon type-name strings, which launder distinct
    // CLR types onto one tag (B66/B76) and would unsoundly share closures across e.g. Run<IFoo>/Run<IBar>.
    // A closure in a non-generic context has an EMPTY args vector — the key degenerates 1:1 to the
    // definition and behavior is byte-identical to the old symbol keying.

    /// <summary>Everything a per-spec hoisted closure owns: its CFunction, flat param/return field
    /// ids, its hidden __envp field id (null for capture-free), the enclosing constructed specs it
    /// was registered under (drives the type-param compose at its own emission — replacing the
    /// first-wins FirstSpecByDefinition read), and the composite key args.</summary>
    public sealed class ClosureSpec
    {
        public IMethodSymbol Def;
        public ImmutableArray<ITypeSymbol> KeyArgs;
        public ImmutableArray<IMethodSymbol> OwnerSpecs;
        public CFunction Func;
        public EmitContext.MethodSlot Slot;
        public string[] ParamVarIds;
        public ReturnSlot[] ReturnSlots;
        public string EnvpFieldId;
    }

    readonly Dictionary<IMethodSymbol, List<ClosureSpec>> _closureSpecs = new(SymbolEqualityComparer.Default);

    public readonly List<ClosureSpec> PendingClosures = new();

    /// <summary>The closure spec currently being emitted (set by EmitMethod for pending-closure
    /// drains), or null when emitting a named method. Consumers that used to read the bare
    /// definition-keyed maps for the CURRENT method (spill collection, self-__envp) must prefer
    /// this — a bare read under multi-spec is the silent under-spill / wrong-env class (F3/F4).</summary>
    public ClosureSpec CurrentClosureSpec;

    /// <summary>The flattened enclosing-spec type arguments of the CURRENT emission context:
    /// a named spec's own (method + containing-type) args, an emitting closure's KeyArgs, else
    /// empty. This is the ambient key-args component every closure registration and closure
    /// lookup uses.</summary>
    public ImmutableArray<ITypeSymbol> CurrentSpecArgs = ImmutableArray<ITypeSymbol>.Empty;

    /// <summary>The enclosing constructed specs of the current emission context (a named spec =
    /// itself; an emitting closure = its record's chain; else empty).</summary>
    public ImmutableArray<IMethodSymbol> CurrentOwnerSpecs = ImmutableArray<IMethodSymbol>.Empty;

    static bool ArgsEqual(ImmutableArray<ITypeSymbol> a, ImmutableArray<ITypeSymbol> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!SymbolEqualityComparer.Default.Equals(a[i], b[i])) return false;
        return true;
    }

    public bool TryGetClosureSpec(IMethodSymbol def, ImmutableArray<ITypeSymbol> keyArgs, out ClosureSpec spec)
    {
        spec = null;
        if (def == null) return false;
        if (!_closureSpecs.TryGetValue(def.OriginalDefinition, out var list)) return false;
        foreach (var s in list)
            if (ArgsEqual(s.KeyArgs, keyArgs)) { spec = s; return true; }
        return false;
    }

    /// <summary>Throw-on-miss twin (design v3 §2B: a multi-spec context must never silently fall
    /// back to another spec's closure — the pre-fix failure mode was exactly that, silent).</summary>
    public ClosureSpec GetClosureSpec(IMethodSymbol def, ImmutableArray<ITypeSymbol> keyArgs)
        => TryGetClosureSpec(def, keyArgs, out var spec)
            ? spec
            : throw new InvalidOperationException(
                $"Hoisted closure '{def?.Name}' has no registration for the current enclosing spec "
                + $"args [{string.Join(", ", keyArgs.Select(a => a?.ToDisplayString() ?? "?"))}] — a "
                + "per-spec closure lookup fell outside its registration context (per-spec keying bug).");

    public void AddClosureSpec(ClosureSpec spec)
    {
        if (!_closureSpecs.TryGetValue(spec.Def.OriginalDefinition, out var list))
            _closureSpecs[spec.Def.OriginalDefinition] = list = new List<ClosureSpec>();
        list.Add(spec);
    }
}
