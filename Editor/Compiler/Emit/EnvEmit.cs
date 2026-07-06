using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

// ============================================================================
// Stage 2 M2 (design §3/§4.1): closure environment-record emission.
// - Alloc: env record creation at a capture-bearing scope's entry. INV-1: this is ordinary
//   sequential codegen — emitted once, RE-EXECUTED on every dynamic re-entry (per loop iteration),
//   which is exactly what gives per-iteration freshness. Never hoist.
// - Leaf: resolve the live env-record reference for a scope — the current frame's slot when the
//   scope was allocated here, else a __envp + parent-slot walk (EffectiveParent hops, never raw
//   .Parent).
// - Read/Write: captured-variable access through SystemObjectArray __Get/__Set on the owning
//   scope's env. Typed __Get destinations are safe for every value type (M1 gating probe P-A:
//   UdonHeap retypes the dest StrongBox and typed reads match on boxed runtime type).
// Static + stateless: all state lives on EmitContext, so HandlerBase-derived handlers and
// UasmEmitter's method-entry hook share one implementation.
// (C# 9.0 only — Unity compiles Editor/ at the 2022.3 language level.)
// ============================================================================
static class EnvEmit
{
    public const string EnvType = "SystemObjectArray";
    static readonly string CtorSig = ExternResolver.BuildArrayCtorSignature(EnvType);
    static readonly string SetSig = ExternResolver.BuildArraySetSignature(EnvType, "SystemObject");
    static readonly string GetSig = ExternResolver.BuildArrayGetSignature(EnvType, "SystemObject");

    /// <summary>The hoisted closure whose body is being emitted right now, or null when emission is
    /// inside an ordinary method / field initializer (then every env scope must be frame-local).</summary>
    static IMethodSymbol CurrentClosure(EmitContext ctx)
        => ctx.CurrentMethod is IMethodSymbol m
           && ctx.CaptureScope != null
           && ctx.CaptureScope.ClosureScopes.ContainsKey(m.OriginalDefinition)
            ? m : null;

    /// <summary>Allocate the env record for a capture-bearing scope at its entry point and register
    /// its frame slot. Slot 0 is the env ABI tag and slot 1 is always the EffectiveParent scope's
    /// live env (null when the scope has no capture-bearing ancestor). No-op for null /
    /// non-capture-bearing scopes.</summary>
    public static void Alloc(CoreBuilder b, EmitContext ctx, CaptureScope scope)
    {
        if (scope == null || !scope.IsCaptureBearing) return;
        var env = b.ExternCall(CtorSig,
            new List<CLeaf> { new CConst(EnvAbi.RecordSize(scope.OwnedCaptures.Count), "SystemInt32") }, EnvType);
        var parent = ctx.CaptureScope.EffectiveParent(scope);
        var parentLeaf = parent != null
            ? Leaf(b, ctx, parent)
            : (CLeaf)new CConst(null, "SystemObject");
        b.EmitExternVoid(SetSig, new List<CLeaf> { env, new CConst(EnvAbi.Kind, "SystemInt32"), new CConst(EnvAbi.KindTag, "SystemString") });
        b.EmitExternVoid(SetSig, new List<CLeaf> { env, new CConst(EnvAbi.Parent, "SystemInt32"), parentLeaf });
        ctx.ScopeEnvSlots[(b.CurrentFunction, scope.Id)] = env.SlotId;
    }

    /// <summary>The live env-record reference for <paramref name="scope"/> at the current emission
    /// point: the frame slot when this function allocated it, else a chain walk from the current
    /// closure's __envp (hop 0 = the closure's BindingScope, each hop = one parent __Get).</summary>
    public static CLeaf Leaf(CoreBuilder b, EmitContext ctx, CaptureScope scope)
    {
        if (ctx.ScopeEnvSlots.TryGetValue((b.CurrentFunction, scope.Id), out var slotId))
            return new CSlotRef(slotId, EnvType);

        var currentClosure = CurrentClosure(ctx);
        if (currentClosure == null)
            throw new InvalidOperationException(
                $"Env scope #{scope.Id} ({scope.Kind}) is not live in this frame and there is no enclosing closure to chain from.");
        var def = currentClosure.OriginalDefinition;
        if (!ctx.TryGetEnvpField(currentClosure, out var envpField))
            throw new InvalidOperationException(
                $"Closure '{currentClosure.Name}' reads captured state but has no __envp parameter registered.");
        if (!ctx.CaptureScope.ClosureScopes.TryGetValue(def, out var ownScope) || ownScope.BindingScope == null)
            throw new InvalidOperationException(
                $"Closure '{currentClosure.Name}' has no binding scope to chain env scope #{scope.Id} from.");

        var hops = ctx.CaptureScope.HopDistance(ownScope.BindingScope, scope);
        CLeaf cur = b.LoadField(envpField, EnvType);
        for (int i = 0; i < hops; i++)
            cur = b.ExternCall(GetSig, new List<CLeaf> { cur, new CConst(EnvAbi.Parent, "SystemInt32") }, EnvType);
        return cur;
    }

    /// <summary>Read a captured variable out of its owning scope's env record into a typed slot.</summary>
    public static CLeaf Read(CoreBuilder b, EmitContext ctx, ISymbol symbol, string udonType)
    {
        if (!ctx.TryGetEnvBinding(symbol, out var binding))
            throw new InvalidOperationException($"'{symbol.Name}' has no env binding.");
        var env = Leaf(b, ctx, binding.Scope);
        return b.ExternCall(GetSig,
            new List<CLeaf> { env, new CConst(EnvAbi.CaptureSlot(binding.Slot), "SystemInt32") }, udonType);
    }

    /// <summary>Write a value into a captured variable's env cell.</summary>
    public static void Write(CoreBuilder b, EmitContext ctx, ISymbol symbol, CLeaf value)
    {
        if (!ctx.TryGetEnvBinding(symbol, out var binding))
            throw new InvalidOperationException($"'{symbol.Name}' has no env binding.");
        var env = Leaf(b, ctx, binding.Scope);
        b.EmitExternVoid(SetSig,
            new List<CLeaf> { env, new CConst(EnvAbi.CaptureSlot(binding.Slot), "SystemInt32"), value });
    }
}

static class EnvAbi
{
    public const int Kind = 0;
    public const int Parent = 1;
    public const int FirstCapture = 2;
    public const string KindTag = "__usugar_env";

    public static int RecordSize(int captureCount) => FirstCapture + captureCount;

    public static int CaptureSlot(int logicalCaptureSlot)
        => FirstCapture + logicalCaptureSlot - 1;
}
