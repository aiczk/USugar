using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Wave-9 fix round 2 — tracked pins for the six findings (DiffFuzz refs cited per test; real-VM
/// value pins live in the local harness wave corpus fcd_wave9_fixround2.json, 20/20 Match, and the
/// previously host-crashing fcd_wave9_inherit_r2_abort.json, 3/3 Match):
///
/// [W5] Interface implemented by a base-class VIRTUAL with an override anywhere in the chain:
///      FindImplementationForInterfaceMember returns the chain ROOT, which the round-1 [W4]
///      one-function-per-virtual-slot folding removes from the layout — ComputeBridges' direct
///      lookup missed and the __iface_* bridge was silently never exported (call site dispatches a
///      nonexistent event: silent no-op + stale 0 on a real client, unbounded SendCustomEvent
///      self-reentry in the harness). Fixed by resolving impl to the chain member that owns the
///      slot (DiffFuzz ref=36 / 76 / 11 across method, 3-level, and property-accessor flavors).
/// [W1] Named arguments on a DELEGATE invocation bound positionally in textual order — the conv
///      stores indexed by call-site position instead of parameter ordinal (DiffFuzz ref=902 vs
///      VM 209; evaluation order itself was correct). Method calls already slotted by ordinal.
/// [W2] Recursion threaded through manual property/indexer accessors was invisible to
///      BuildRecursionInfo (property references were not call-graph edges), so accessor frames
///      were never spilled (DiffFuzz ref=11 vs VM 5 — every frame's locals clobbered to the
///      deepest value). NOT delegate-specific.
/// [W4] goto out of a using block skipped Dispose() — the using lowering covered break/continue/
///      return but not the goto exit edge (DiffFuzz ref total=14/mark=1 vs VM 4/0; nested usings
///      skipped BOTH disposes, ref=214 vs 4 on the order-sensitive flavor).
/// [W6] User indexer accessed through ANY variable receiver (even an own-typed copy of this) fell
///      through to extern resolution and emitted a nonexistent IUdonEventReceiver.__get_Item /
///      __set_Item (validator/assembler crash on legal C#; only the literal this[i] form worked).
///      Now dispatched cross-program via the chain-root accessor exports (DiffFuzz ref acc=15).
/// [W7] Method-group conversion of a GENERIC method to a delegate ICEd with
///      InvalidOperationException 'No delegate bridge' — the planner never plans bridges for
///      generic definitions. Now the monomorphized specialization is bridged via
///      PendingDelegateBridges like a local function (DiffFuzz ref result=9); unresolved type
///      args / variable receivers / foreign declaring classes reject loudly.
/// </summary>
public class Wave9Round2RegressionTests
{
    // ── [W5] interface bridge through an overridden base virtual ──

    [Fact]
    public void IfaceBridge_BaseVirtualImpl_LeafOverride_ExportsBridge()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public interface IMinIf { int F(int x); }
public class MinIfBase : UdonSharpBehaviour, IMinIf { public virtual int F(int x) { return x + 1; } }
public class MinIf_LeafOverride : MinIfBase {
    public int seed; public int result;
    public override int F(int x) { return x * 4; }
    void Start() { IMinIf h = this; result = h.F(seed); }
}", "MinIf_LeafOverride");
        Assert.Contains(".export __iface_IMinIf___0_F", uasm);
    }

    [Fact]
    public void IfaceBridge_ThreeLevel_MidOnlyOverride_ExportsBridge()
    {
        // The compiled LEAF has no own override — the chain leaf visible from it is the MID
        // override, inherited into the layout; the chain ROOT (the implementing virtual) is folded.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public interface IMinIf2 { int F(int x); }
public class MinIf2Root : UdonSharpBehaviour, IMinIf2 { public virtual int F(int x) { return x + 1; } }
public class MinIf2Mid : MinIf2Root { public override int F(int x) { return x * 8; } }
public class MinIf2Leaf : MinIf2Mid {
    public int seed; public int result;
    void Start() { IMinIf2 h = this; result = h.F(seed); }
}", "MinIf2Leaf");
        Assert.Contains(".export __iface_IMinIf2___0_F", uasm);
    }

    [Fact]
    public void IfaceBridge_PropertyAccessor_LeafOverride_ExportsBridge()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public interface IMinIp { int P { get; } }
public class MinIpBase : UdonSharpBehaviour, IMinIp { public virtual int P { get { return 3; } } }
public class MinIp_LeafOverride : MinIpBase {
    public int result;
    public override int P { get { return 11; } }
    void Start() { IMinIp h = this; result = h.P; }
}", "MinIp_LeafOverride");
        Assert.Contains(".export __iface_IMinIp_get_P", uasm);
    }

    [Fact]
    public void IfaceBridge_NoOverride_Control_StillExportsBridge()
    {
        // The pre-fix-green control: no override relation → the planner's direct lookup hits.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public interface IMinIf3 { int F(int x); }
public class MinIf3Base : UdonSharpBehaviour, IMinIf3 { public virtual int F(int x) { return x + 1; } }
public class MinIf3Leaf : MinIf3Base {
    public int seed; public int result;
    void Start() { IMinIf3 h = this; result = h.F(seed); }
}", "MinIf3Leaf");
        Assert.Contains(".export __iface_IMinIf3___0_F", uasm);
    }

    // ── [W1] named arguments on delegate invocations ──

    [Fact]
    public void DelegateInvoke_NamedArgsReordered_EmitsSameUasmAsDeclarationOrder()
    {
        // Post-fix the reversed-named-args call is byte-identical to the declaration-order call
        // (same evaluation, conv stores slotted by parameter ordinal). Pre-fix the conv stores
        // were swapped (a0 received the textual-first arg).
        const string template = @"
using System;
using UdonSharp;
public delegate int W9TSubM(int left, int right);
public class W9T05Min : UdonSharpBehaviour {{
    public int k; public int r1;
    void Start() {{ W9TSubM s = Sub; r1 = s({0}); }}
    public int Sub(int left, int right) {{ return left * 100 + right; }}
}}";
        var reordered = TestHelper.CompileToUasm(string.Format(template, "right: k, left: 9"), "W9T05Min");
        var declOrder = TestHelper.CompileToUasm(string.Format(template, "left: 9, right: k"), "W9T05Min");
        Assert.Equal(declOrder, reordered);
    }

    // ── [W2] accessor recursion spills ──

    [Fact]
    public void GetterSelfRecursion_SpillsFrame()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9RR38MinSelfGetter : UdonSharpBehaviour {
    int cur; public int n; public int result;
    void Start() { cur = n; result = P; }
    int P { get { if (cur <= 0) return 1; int saved = cur; cur -= 1; int below = P; return below + saved; } }
}", "W9RR38MinSelfGetter");
        Assert.Contains("__recurStack", uasm);
    }

    [Fact]
    public void IndexerSelfRecursion_SpillsFrame()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R2IdxRec : UdonSharpBehaviour {
    int cur; public int n; public int result;
    void Start() { cur = n; result = this[0]; }
    int this[int i] { get { if (cur <= 0) return 1; int saved = cur; cur -= 1; int below = this[i + 1]; return below + saved; } }
}", "W9R2IdxRec");
        Assert.Contains("__recurStack", uasm);
    }

    [Fact]
    public void MethodGetterMutualRecursion_SpillsFrame()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R2MutualAccessor : UdonSharpBehaviour {
    int cur; public int n; public int result;
    void Start() { cur = n; result = F(); }
    int F() { if (cur <= 0) return 1; int saved = cur; cur -= 1; int below = P; return below + saved; }
    int P { get { return F(); } }
}", "W9R2MutualAccessor");
        Assert.Contains("__recurStack", uasm);
    }

    [Fact]
    public void NonRecursiveGetterChain_Control_DoesNotSpill()
    {
        // Accessor edges must not over-mark acyclic accessor use (no SCC → no spill machinery).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class W9R2GetterChain : UdonSharpBehaviour {
    public int n; public int result;
    void Start() { result = G; }
    int G { get { return H + 1; } }
    int H { get { return n * 2; } }
}", "W9R2GetterChain");
        Assert.DoesNotContain("__recurStack", uasm);
    }

    // ── [W4] goto out of using disposes ──

    [Fact]
    public void Using_GotoOut_EmitsDispose()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
public class UsingGotoTest : UdonSharpBehaviour {
    public int n; public int mark;
    void Start() {
        using (var r = new DisposableResource()) {
            if (n > 0) { goto AFTER; }
            mark = mark + 100;
        }
    AFTER:
        mark = mark * 10 + n;
    }
}", "UsingGotoTest");
        // goto exit edge + normal exit — pre-fix only the normal exit disposed.
        var disposeCount = System.Text.RegularExpressions.Regex.Matches(uasm, "__Dispose").Count;
        Assert.True(disposeCount >= 2, $"Expected at least 2 Dispose calls, got {disposeCount}. UASM:\n{uasm}");
    }

    [Fact]
    public void Using_GotoOutOfNested_DisposesBoth()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
public class UsingGotoNestedTest : UdonSharpBehaviour {
    public int n; public int mark;
    void Start() {
        using (var a = new DisposableResource()) {
            using (var b = new DisposableResource()) {
                if (n > 0) { goto AFTER; }
                mark = mark + 100;
            }
        }
    AFTER:
        mark = mark * 10 + n;
    }
}", "UsingGotoNestedTest");
        // goto edge disposes b then a; normal exits dispose b and a — 4 sites total.
        var disposeCount = System.Text.RegularExpressions.Regex.Matches(uasm, "__Dispose").Count;
        Assert.True(disposeCount >= 4, $"Expected at least 4 Dispose calls, got {disposeCount}. UASM:\n{uasm}");
    }

    [Fact]
    public void Using_GotoWithinSameUsing_Control_DisposesOnceOnly()
    {
        // A goto whose label lives INSIDE every enclosing using exits nothing — exactly the one
        // normal-exit Dispose (an extra one would double-dispose on the fall-through path).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
public class UsingGotoInsideTest : UdonSharpBehaviour {
    public int n; public int mark;
    void Start() {
        using (var r = new DisposableResource()) {
            if (n > 0) { goto SKIP; }
            mark = mark + 100;
        SKIP:
            mark = mark + 5;
        }
    }
}", "UsingGotoInsideTest");
        var disposeCount = System.Text.RegularExpressions.Regex.Matches(uasm, "__Dispose").Count;
        Assert.Equal(1, disposeCount);
    }

    // ── [W6] indexer through a variable receiver ──

    [Fact]
    public void Indexer_VariableReceiver_Read_DispatchesCrossProgram()
    {
        // Pre-fix: UasmValidationException 'Unknown extern: ...__get_Item...' (CompileToUasm
        // always validates). Post-fix: cross-program SendCustomEvent of the accessor export.
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class IdxV2_OwnRef : UdonSharpBehaviour {
    public int seed; public int acc;
    public int this[int i] { get { return i * 3; } }
    void Start() { IdxV2_OwnRef s = this; acc = s[seed]; }
}", "IdxV2_OwnRef");
        Assert.DoesNotContain("__get_Item", uasm);
        Assert.Contains("SendCustomEvent", uasm);
        Assert.Contains(".export __0_get_Item", uasm);
    }

    [Fact]
    public void Indexer_VariableReceiver_Write_DispatchesCrossProgram()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R2IdxWrite : UdonSharpBehaviour {
    public int seed; public int acc; public int store;
    public int this[int i] { get { return store + i; } set { store = value * 2 + i; } }
    void Start() { W9R2IdxWrite s = this; s[seed] = 3; acc = s[1]; }
}", "W9R2IdxWrite");
        Assert.DoesNotContain("__set_Item", uasm);
        Assert.DoesNotContain("__get_Item", uasm);
    }

    [Fact]
    public void Indexer_VariableReceiver_Compound_DispatchesCrossProgram()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R2IdxCompound : UdonSharpBehaviour {
    public int seed; public int acc; public int store;
    public int this[int i] { get { return store + i; } set { store = value * 2 + i; } }
    void Start() { W9R2IdxCompound s = this; s[seed] += 2; acc = s[1]; }
}", "W9R2IdxCompound");
        Assert.DoesNotContain("__set_Item", uasm);
        Assert.DoesNotContain("__get_Item", uasm);
    }

    [Fact]
    public void Indexer_VariableReceiver_BaseTypedWithOverride_DispatchesChainRoot()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class IdxBase9 : UdonSharpBehaviour { public virtual int this[int i] { get { return i + 1; } } }
public class W9R2IdxBaseRecv : IdxBase9 {
    public int seed; public int acc;
    public override int this[int i] { get { return i * 4; } }
    void Start() { IdxBase9 b = this; acc = b[seed]; }
}", "W9R2IdxBaseRecv");
        Assert.DoesNotContain("__get_Item", uasm);
        // One exported accessor per virtual slot — the chain-root export name.
        Assert.Contains(".export __0_get_Item", uasm);
    }

    [Fact]
    public void Indexer_VariableReceiver_NonPublicAccessor_Rejects()
    {
        var ex = Record.Exception(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class IdxPrivate : UdonSharpBehaviour {
    public int seed; public int acc;
    int this[int i] { get { return i * 3; } }
    void Start() { IdxPrivate s = this; acc = s[seed]; }
}", "IdxPrivate"));
        Assert.NotNull(ex);
        Assert.Contains("public accessor", ex.ToString());
    }

    // ── [W7] generic method groups ──

    [Fact]
    public void GenericMethodGroup_BridgesMonomorphizedSpecialization()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class MinGenMG : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() { Func<int, int> f = Echo; result = f(seed); }
    T Echo<T>(T v) { return v; }
}", "MinGenMG");
        Assert.Contains("_Echo_SystemInt32", uasm);                  // the specialization body
        Assert.Contains(".export __dlg_", uasm);                     // its exported pending bridge
    }

    [Fact]
    public void GenericMethodGroup_ExplicitTypeArgs_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R2GenMGExplicit : UdonSharpBehaviour {
    public int seed; public int result;
    void Start() { Func<int, int> f = Echo<int>; result = f(seed); }
    T Echo<T>(T v) { return v; }
}", "W9R2GenMGExplicit");
        Assert.Contains("_Echo_SystemInt32", uasm);
    }

    [Fact]
    public void GenericMethodGroup_VariableReceiver_Rejects()
    {
        var ex = Record.Exception(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class W9R2GenMGRecv : UdonSharpBehaviour {
    public W9R2GenMGRecv other; public int seed; public int result;
    void Start() { Func<int, int> f = other.Echo; result = f(seed); }
    public T Echo<T>(T v) { return v; }
}", "W9R2GenMGRecv"));
        Assert.NotNull(ex);
        Assert.Contains("generic method", ex.ToString());
    }

    [Fact]
    public void GenericMethodGroup_InheritedFromBase_Rejects()
    {
        var ex = Record.Exception(() => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class GenMGBase : UdonSharpBehaviour { protected T Echo<T>(T v) { return v; } }
public class W9R2GenMGInherited : GenMGBase {
    public int seed; public int result;
    void Start() { Func<int, int> f = Echo; result = f(seed); }
}", "W9R2GenMGInherited"));
        Assert.NotNull(ex);
        Assert.Contains("generic method", ex.ToString());
    }
}
