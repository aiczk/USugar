using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace USugar.Tests;

// CW1 lift (2026-07-16): virtual/override/abstract PROPERTY and INDEXER accessors on v1 user-class
// receivers dispatch at runtime through the SAME single-sourced v2b-2 machinery as method dispatch
// (VirtualDispatch.IsDispatchSite shared with the recursion enumerator, ResolveTargets closed-world
// set, GuardOpenVirtualDispatch armor). These pin the dispatched STRUCTURE per lowering; runtime
// values are pinned by the local DiffFuzz harness (CLR-oracle probes). The two 1c72d4b reject pins
// were flipped in ClosedWorldGuardTests (VirtualProperty/VirtualIndexer_BaseTypedRead_DispatchesOverride).
public class AccessorDispatchTests
{
    [Fact]
    public void VirtualPropertySetter_BaseTypedWrite_DispatchesBothImpls()
    {
        // `s.P = 5` on a base-typed receiver must run the RUNTIME type's setter — both minted
        // classes' set accessors are emitted and reached through the typeobj chain.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class StB { protected int v; public virtual int P { get { return v; } set { v = value + 1; } } }
public class StD : StB { public override int P { get { return v; } set { v = value + 10; } } }
public class CwVSet : UdonSharpBehaviour {
    public int seed;
    public int r;
    void Start() { StB s = seed > 0 ? (StB)new StD() : new StB(); s.P = 5; r = s.P; }
}", "CwVSet");
        Assert.True(Regex.Matches(uasm, @"__\d+_set_P").Count >= 2,
            "both slot impls' setters must be emitted and dispatched");
        Assert.True(Regex.Matches(uasm, @"__\d+_get_P").Count >= 2,
            "both slot impls' getters must be emitted and dispatched");
        Assert.DoesNotContain("SystemObjectArray.__set_", uasm);
    }

    [Fact]
    public void AbstractPropertyAccessor_ImplsOnlyChain()
    {
        // An abstract accessor has no base impl and the abstract base is never minted: the chain is
        // built from the override impls only (no base typeobj, no base arm).
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public abstract class AbBase { public abstract int Area { get; } }
public class AbOne : AbBase { public override int Area { get { return 11; } } }
public class AbTwo : AbBase { public override int Area { get { return 22; } } }
public class CwVAbs : UdonSharpBehaviour {
    public int r;
    void Start() { AbBase x = new AbOne(); AbBase y = new AbTwo(); r = x.Area + y.Area; }
}", "CwVAbs");
        Assert.Contains("__typeobj_AbOne", uasm);
        Assert.Contains("__typeobj_AbTwo", uasm);
        Assert.DoesNotContain("__typeobj_AbBase", uasm);
        Assert.Contains(consts, c => c.UdonType == "SystemInt32" && Equals(c.Value, 11));
        Assert.Contains(consts, c => c.UdonType == "SystemInt32" && Equals(c.Value, 22));
        Assert.True(Regex.Matches(uasm, @"__\d+_get_Area").Count >= 2);
    }

    [Fact]
    public void SingletonImplementor_DevirtualizesToDirectCall()
    {
        // Only the sealed derived class is minted: the accessor devirtualizes to a direct call —
        // no typeobj chain, no no-match armor (the chain's LogError const never appears).
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class SvB { public virtual int P { get { return 1; } } }
public sealed class SvD : SvB { public override int P { get { return 7; } } }
public class CwVDevirt : UdonSharpBehaviour {
    public int r;
    void Start() { SvB s = new SvD(); r = s.P; }
}", "CwVDevirt");
        Assert.Contains(consts, c => c.UdonType == "SystemInt32" && Equals(c.Value, 7));
        Assert.DoesNotContain(consts, c => c.Value is string m && m.Contains("null or non-class receiver"));
        Assert.Matches(@"__\d+_get_P", uasm);
    }

    [Fact]
    public void OpenGenericFamilyAccessor_StillRejectedByGuard()
    {
        // GuardOpenVirtualDispatch is shared with the method arm: an accessor dispatch whose family
        // is minted through an OPEN construction site has no spec-distinct runtime identity — loud.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class GAVp<T> { public virtual int P { get { return 1; } } }
public class GBVp<T> : GAVp<T> { public override int P { get { return 2; } } }
public class CwVOpenGen : UdonSharpBehaviour {
    public int r;
    int Make<T>() { GAVp<T> g = new GBVp<T>(); return g.P; }
    void Start() { r = Make<int>(); }
}", "CwVOpenGen"));
        Assert.Contains("generic method", ex.Message);
    }

    [Fact]
    public void NewHiddenProperty_RootsDistinctSlot()
    {
        // `new virtual int P` roots its OWN dispatch slot: an NhA-typed read of an NhC instance must
        // resolve NhA's impl (const 1), an NhB-typed read NhC's override (const 3) — C# gives 13.
        // NhB's hidden body (const 2) is dispatched by NEITHER slot.
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class NhA { public virtual int P { get { return 1; } } }
public class NhB : NhA { public new virtual int P { get { return 2; } } }
public class NhC : NhB { public override int P { get { return 3; } } }
public class CwVNewHide : UdonSharpBehaviour {
    public int r;
    void Start() { NhA a = new NhC(); NhB b = new NhC(); r = a.P * 10 + b.P; }
}", "CwVNewHide");
        Assert.Contains(consts, c => c.UdonType == "SystemInt32" && Equals(c.Value, 1)); // NhA slot impl
        Assert.Contains(consts, c => c.UdonType == "SystemInt32" && Equals(c.Value, 3)); // NhB slot impl
        Assert.True(Regex.Matches(uasm, @"__\d+_get_P").Count >= 2,
            "the two distinct slots' impls must both be emitted");
    }

    [Fact]
    public void VirtualProperty_CompoundAssign_DispatchesBothAccessors()
    {
        // `c.P += 5` on a base-typed receiver: the compound READ (CaptureLValue) and WRITE-BACK
        // (EmitWriteBack) both route through the dispatch core — never a bogus extern, and both
        // impls' accessor pairs are live.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CvB { protected int v; public virtual int P { get { return v; } set { v = value; } } }
public class CvD : CvB { public override int P { get { return v * 10; } set { v = value + 1; } } }
public class CwVCompound : UdonSharpBehaviour {
    public int seed;
    public int r;
    void Start() { CvB c = seed > 0 ? (CvB)new CvD() : new CvB(); c.P += 5; r = c.P; }
}", "CwVCompound");
        Assert.DoesNotContain("SystemObjectArray.__set_", uasm);
        Assert.DoesNotContain("SystemObjectArray.__get_", uasm);
        Assert.True(Regex.Matches(uasm, @"__\d+_set_P").Count >= 2);
        Assert.True(Regex.Matches(uasm, @"__\d+_get_P").Count >= 2);
    }

    [Fact]
    public void VirtualIndexerSetter_BaseTypedWrite_Dispatches()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class IsB { protected int[] a; public IsB() { a = new int[4]; } public virtual int this[int i] { get { return a[i]; } set { a[i] = value; } } }
public class IsD : IsB { public override int this[int i] { get { return a[i] * 2; } set { a[i] = value + 1; } } }
public class CwVIdxSet : UdonSharpBehaviour {
    public int seed;
    public int r;
    void Start() { IsB b = seed > 0 ? (IsB)new IsD() : new IsB(); b[1] = 5; r = b[1]; }
}", "CwVIdxSet");
        Assert.True(Regex.Matches(uasm, @"__\d+_set_Item").Count >= 2);
        Assert.True(Regex.Matches(uasm, @"__\d+_get_Item").Count >= 2);
        Assert.DoesNotContain("SystemObjectArray.__set_", uasm);
    }

    [Fact]
    public void VirtualAutoProperty_MixedImpls_Compiles()
    {
        // A virtual AUTO property's impl arm is a layout-slot access on the CONCRETE target's layout;
        // a computed override's arm is a CFunction call — one chain carries both arm kinds.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class AmB { public virtual int P { get; set; } }
public class AmD : AmB { int w; public override int P { get { return w + 100; } set { w = value; } } }
public class CwVAutoMix : UdonSharpBehaviour {
    public int r;
    void Start() { AmB a = new AmB(); AmB d = new AmD(); a.P = 5; d.P = 6; r = a.P + d.P; }
}", "CwVAutoMix");
        Assert.Matches(@"__\d+_get_P", uasm); // the computed override's getter is emitted and dispatched
        Assert.Matches(@"__\d+_set_P", uasm);
        Assert.Contains("__typeobj_AmB", uasm);
        Assert.Contains("__typeobj_AmD", uasm);
    }

    [Fact]
    public void ThisReceiverRead_InInheritedBaseBody_Dispatches()
    {
        // A `P` read inside a base method body dispatches on the RUNTIME type (bundle[0] is written
        // before the ctor chain): with both classes minted the base body carries the typeobj chain,
        // so the override's const is reachable from Snap's function.
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class TrB { public virtual int P { get { return 1; } } public int Snap() { return P + 100; } }
public class TrD : TrB { public override int P { get { return 2; } } }
public class CwVThisBase : UdonSharpBehaviour {
    public int r;
    void Start() { TrB t = new TrD(); TrB u = new TrB(); r = t.Snap() + u.Snap(); }
}", "CwVThisBase");
        Assert.Contains(consts, c => c.UdonType == "SystemInt32" && Equals(c.Value, 2));
        Assert.True(Regex.Matches(uasm, @"__\d+_get_P").Count >= 2);
        Assert.Contains("__typeobj_TrD", uasm);
    }

    [Fact]
    public void VirtualAccessor_NoMintedImplementor_EmitsRuntimeGuards()
    {
        // Closed-world empty-target lowering: reads LogError + default, writes LogError + skip —
        // never a silent static-accessor call on the necessarily-null receiver.
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class EvB { public virtual int Q { get { return 1; } set { } } }
public class CwVEmpty : UdonSharpBehaviour {
    EvB f;
    public int r;
    void Start() { if (f != null) { f.Q = 3; r = f.Q; } }
}", "CwVEmpty");
        Assert.Contains("UnityEngineDebug.__LogError__SystemObject__SystemVoid", uasm);
        Assert.Contains(consts, c => c.Value is string m && m.Contains("Skipping the write."));
        Assert.Contains(consts, c => c.Value is string m && m.Contains("Returning default."));
    }

    [Fact]
    public void VirtualProperty_Subpattern_Dispatches()
    {
        // `x is TrPB { P: 42 }` reads P via the dispatch core on the narrowed scrutinee — the
        // override's const 42 body must be emitted (the static slot/getter arms would bind the base).
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class TrPB { public virtual int P { get { return 1; } } }
public class TrPD : TrPB { public override int P { get { return 42; } } }
public class CwVSubpat : UdonSharpBehaviour {
    public int seed;
    public int r;
    void Start() { TrPB x = seed > 0 ? (TrPB)new TrPD() : new TrPB(); r = x is TrPB { P: 42 } ? 1 : 0; }
}", "CwVSubpat");
        Assert.Contains(consts, c => c.UdonType == "SystemInt32" && Equals(c.Value, 42));
        Assert.True(Regex.Matches(uasm, @"__\d+_get_P").Count >= 2,
            "both impls' getters must be reachable from the subpattern read");
    }
}
