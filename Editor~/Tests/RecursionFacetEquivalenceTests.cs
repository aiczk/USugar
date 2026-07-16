using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// CA call-graph rewrite — the permanent facet census oracle (C4 retirement, 2026-07-16: the live
/// legacy-vs-production differential seam is gone with the deleted legacy walks; these committed
/// fixtures are now the sole oracle for the six RecursionInfo facets). For every battery source,
/// serialize the production facets (RecursionNodeWalk via DebugRecursionInfo) canonically, gate
/// determinism across two compiles, and compare against the committed fixture under
/// Editor~/Tests/Golden/FacetCensus. Battery = every GoldenCorpus source +
/// targeted recursion shapes (non-tail, mutual, local-function, lambda-in-loop capture,
/// struct-member, virtual-dispatch polymorphic incl. the CW5 generic-T-receiver form, base-ctor
/// chain, reentrant-dispatch, tail-spared mixed, foreign-static LF, variant-leaf reentry,
/// virtual-accessor recursion in three polarities: mutual getter cycle, method-mediated cycle,
/// this-receiver getter recursion).
/// Regenerate fixtures with UPDATE_SNAPSHOTS=1 — never silently (a diff here is an analysis-facet
/// regression until proven otherwise).
/// </summary>
public class RecursionFacetEquivalenceTests
{
    static bool UpdateMode =>
        Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1";

    static string Facets(UasmEmitter emitter) => Serialize(emitter.DebugRecursionInfo);

    public static IEnumerable<object[]> Battery()
        => AllCases().Select(c => new object[] { c.Name });

    static IEnumerable<(string Name, string ClassName, string Source)> AllCases()
        => GoldenCorpus.Cases.Concat(TargetedShapes);

    static (string Name, string ClassName, string Source) ByName(string name)
        => AllCases().First(c => c.Name == name);

    [Theory]
    [MemberData(nameof(Battery))]
    public void FacetCensus_MatchesFixture(string name)
    {
        var c = ByName(name);
        TestHelper.CompileToUasm(c.Source, c.ClassName, out var emitter);

        // Determinism gate (mirrors the snapshot oracle): the census must be byte-stable across
        // compiles, or the fixture compare below would flake instead of gating facet changes.
        TestHelper.CompileToUasm(c.Source, c.ClassName, out var emitter2);

        var census = Facets(emitter);
        Assert.True(census == Facets(emitter2),
            $"Nondeterministic facet census for '{name}': two compiles differ.");

        var path = Path.Combine(TestPaths.FacetCensusDir, name + ".facets");
        if (UpdateMode)
        {
            Directory.CreateDirectory(TestPaths.FacetCensusDir);
            File.WriteAllText(path, census);
            return;
        }

        Assert.True(File.Exists(path),
            $"Missing facet census '{path}'. Run with UPDATE_SNAPSHOTS=1 to capture.");
        Assert.Equal(Lf(File.ReadAllText(path)), census);
    }

    [Fact]
    public void BatteryNames_AreUnique()
    {
        var names = AllCases().Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    // ── canonical serialization (facet → sorted lines; equality of the text IS set-equality) ──

    static string Serialize(RecursionInfo info)
    {
        Assert.NotNull(info?.RecursionGraphNodes); // BuildRecursionInfo must have populated
        var sb = new StringBuilder();
        Section(sb, "nodes", info.RecursionGraphNodes.Select(MethodKey));
        Section(sb, "recursive-edges", EdgeLines(info.RecursiveCallees));
        Section(sb, "cycle-edges", EdgeLines(info.CycleCallees));
        Section(sb, "this-field-touches", info.ThisFieldTouches
            .SelectMany(kv => kv.Value.Select(f => MethodKey(kv.Key) + " -> " + f.ToDisplayString())));
        Section(sb, "reentrant-dispatch-sites", info.ReentrantDispatchSites.Select(SiteKey));
        Section(sb, "tail-spared-direct-call-sites", info.TailSparedDirectCallSites.Select(SiteKey));
        return sb.ToString();
    }

    static IEnumerable<string> EdgeLines(Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> edges)
        => edges.SelectMany(kv => kv.Value.Select(callee => MethodKey(kv.Key) + " -> " + MethodKey(callee)));

    static void Section(StringBuilder sb, string title, IEnumerable<string> lines)
    {
        sb.Append('[').Append(title).Append("]\n");
        foreach (var line in lines.Distinct().OrderBy(s => s, StringComparer.Ordinal))
            sb.Append(line).Append('\n');
    }

    // Definition-keyed method identity: display string disambiguated by the declaring span (a lambda's
    // display string is not unique; spans are byte-stable because battery sources are fixed strings).
    static string MethodKey(IMethodSymbol m)
    {
        var d = m.OriginalDefinition;
        var span = d.DeclaringSyntaxReferences.Length > 0
            ? d.DeclaringSyntaxReferences[0].Span
            : default;
        return d.ToDisplayString() + " @" + span.Start + ".." + span.End;
    }

    static string SiteKey(SyntaxNode node)
    {
        var text = node.ToString().Replace("\r", " ").Replace("\n", " ");
        if (text.Length > 80) text = text.Substring(0, 80) + "…";
        return text + " @" + node.Span.Start + ".." + node.Span.End;
    }

    static string Lf(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    // ── targeted recursion shapes (each exercises a distinct facet-producing arm) ──

    static readonly (string Name, string ClassName, string Source)[] TargetedShapes =
    {
        ("facet_nontail_recursion", "FacetNonTailRec",
@"using UdonSharp; public class FacetNonTailRec : UdonSharpBehaviour {
  public int result;
  void Start(){ result = F(5); }
  int F(int n){ if (n <= 1) return 1; int r = F(n - 1); return r + n; }
}"),
        ("facet_mutual_recursion", "FacetMutualRec",
@"using UdonSharp; public class FacetMutualRec : UdonSharpBehaviour {
  public int result;
  void Start(){ result = Ev(6); }
  int Ev(int n){ if (n <= 0) return 1; return Od(n - 1) + 1; }
  int Od(int n){ if (n <= 0) return 0; return Ev(n - 1) + 1; }
}"),
        ("facet_local_function_recursion", "FacetLocalFuncRec",
@"using UdonSharp; public class FacetLocalFuncRec : UdonSharpBehaviour {
  public int result;
  void Start(){
    int Sum(int n){ if (n <= 0) return 0; return Sum(n - 1) + n; }
    result = Sum(4);
  }
}"),
        ("facet_lambda_in_loop_capture", "FacetLambdaLoopCapture",
@"using System; using UdonSharp; public class FacetLambdaLoopCapture : UdonSharpBehaviour {
  public int a; public int b;
  void Start(){
    Func<int>[] fs = new Func<int>[2];
    for (int i = 0; i < 2; i++){ int v = i * 7; fs[i] = () => v; }
    a = fs[0](); b = fs[1]();
  }
}"),
        ("facet_struct_member_recursion", "FacetStructMemberRec",
@"using UdonSharp;
public struct FacetRecBox {
  public int Rec(int n){ if (n <= 0) return 0; int r = Rec(n - 1); return r + n; }
}
public class FacetStructMemberRec : UdonSharpBehaviour {
  public int result;
  void Start(){ FacetRecBox b = new FacetRecBox(); result = b.Rec(4); }
}"),
        // Polymorphic recursion through a base-typed field: FvA.Step ↔ FvB.Step cycle exists only via
        // the v2b-2 dispatch edges (each override calls next.Step non-tail).
        ("facet_virtual_polymorphic_recursion", "FacetVirtualPolyRec",
@"using UdonSharp;
public class FvBase { public FvBase next; public virtual int Step(int n){ return n; } }
public class FvA : FvBase { public override int Step(int n){ if (n <= 0) return 1; int r = next.Step(n - 1); return r + 1; } }
public class FvB : FvBase { public override int Step(int n){ if (n <= 0) return 2; int r = next.Step(n - 1); return r + 2; } }
public class FacetVirtualPolyRec : UdonSharpBehaviour {
  public int result;
  void Start(){ FvA a = new FvA(); FvB b = new FvB(); a.next = b; b.next = a; result = a.Step(5); }
}"),
        // CW5 shape (ClosedWorldGuardTests.GenericReceiverDispatch_SpillsRecursionFrame): the dispatch
        // receiver's DECLARED type is a type parameter, so the def-keyed edge walk must over-approximate
        // to every minted impl — the Go ↔ Visit cycle exists only through that arm.
        ("facet_cw5_generic_receiver_recursion", "FacetCw5GenRecv",
@"using UdonSharp;
public class FcwNode { public virtual int Visit(int d){ return 0; } }
public class FcwLeaf : FcwNode { public override int Visit(int d){ if (d <= 0) return 7; return FcwHelp.Go(this, d); } }
public static class FcwHelp {
  public static int Go<T>(T n, int d) where T : FcwNode { int keep = d * 10; int r = n.Visit(d - 1); return keep + r; }
}
public class FacetCw5GenRecv : UdonSharpBehaviour {
  public int result;
  void Start(){ result = FcwHelp.Go(new FcwLeaf(), 3); }
}"),
        // Non-tail delegate dispatch inside a cycle member whose escaped lambda re-enters the SCC —
        // the only battery shape producing a non-empty ReentrantDispatchSites (§4.3 per-site marking);
        // without it that facet would be pinned vacuously empty.
        ("facet_reentrant_dispatch", "FacetReentrantDispatch",
@"using System; using UdonSharp;
public class FacetReentrantDispatch : UdonSharpBehaviour {
  public int result;
  Func<int, int> hook;
  int Walk(int n){
    if (n <= 0) return 0;
    int r = hook(n - 1);
    return r + 1;
  }
  void Start(){
    hook = x => Walk(x);
    result = Walk(3);
  }
}"),
        // Mixed tail/non-tail direct recursion: the non-tail site puts Mix in RecursiveCallees, the
        // tail-position sibling site must land in TailSparedDirectCallSites ([Y3] per-site sparing) —
        // the only battery shape producing a non-empty pin for that facet.
        ("facet_tail_spared_mixed_recursion", "FacetTailSparedMix",
@"using UdonSharp;
public class FacetTailSparedMix : UdonSharpBehaviour {
  public int result;
  int Mix(int n){
    if (n <= 0) return 0;
    if (n % 2 == 0) { int r = Mix(n - 1); return r + 1; }
    return Mix(n - 1);
  }
  void Start(){ result = Mix(5); }
}"),
        // C2 kept-arm pin: a STATIC local function declared inside a FOREIGN static rides the
        // ForeignStatics reach leg into BodyByDef with an ILocalFunctionOperation body — the
        // RecursionNodeWalk seed-loop unwrap is what keeps its edges (removal silently dropped the
        // self-recursive spill edge while every other gate stayed green).
        ("facet_foreign_static_local_function", "FacetForeignStaticLf",
@"using UdonSharp;
public static class FfsHelp {
  public static int Run(int n) {
    static int Twice(int x) { if (x <= 0) return 0; return Twice(x - 1) + 2; }
    return Twice(n) + 1;
  }
}
public class FacetForeignStaticLf : UdonSharpBehaviour {
  public int result;
  void Start(){ result = FfsHelp.Run(5); }
}"),
        // C2 kept-arm pin, generic leg: a generic STATIC local function in a foreign static lands in
        // GenericForeignStaticBodies as an ILocalFunctionOperation; declared AFTER its use so the
        // walk's supp-discovery arm (not the in-tree LF arm) seeds it and must unwrap.
        ("facet_foreign_generic_static_local_function", "FacetForeignGenStaticLf",
@"using UdonSharp;
public static class FgsHelp {
  public static int Run(int n) {
    int r = Twice<int>(n);
    return r + 1;
    static int Twice<T>(int x) { if (x <= 0) return 0; return Twice<T>(x - 1) + 2; }
  }
}
public class FacetForeignGenStaticLf : UdonSharpBehaviour {
  public int result;
  void Start(){ result = FgsHelp.Run(5); }
}"),
        // C3 stage 2 shape (closed [X1] variant-leaf omission; probe twin: local harness
        // C3VariantLeafOverrideProbes, VM staircase reject → 205-vs-715 under-spill → Match): a
        // VARIANT method-group binding (`Action<string> hop = Step(object)`) created in the BASE
        // behaviour body statically binds the base def, the bridge/adapter runs the LEAF override,
        // and the leaf re-enters the dispatching cycle — the variant escape-sig facet must carry
        // (leafDef, sig-S) or the `hop("xy")` dispatch in Go is never marked Reentrant. The fixture
        // pins the gap-closed census (the Go↔Step synthetic cycle + the reentrant hop site).
        ("facet_variant_leaf_override_reentry", "FacetVariantLeafReentry",
@"using System; using UdonSharp;
public class FvlBase : UdonSharpBehaviour {
  protected Action<string> hop;
  public int acc; public int n; public int result;
  public void Seed() { hop = Step; }
  public virtual void Step(object o) { acc = acc + 1000; }
}
public class FacetVariantLeafReentry : FvlBase {
  public override void Step(object o) {
    acc = acc + ((string)o).Length;
    if (acc < 4) Go(1);
  }
  void Go(int d) {
    if (d <= 0) return;
    int keep = d * 100;
    hop(""xy"");
    acc = acc + keep + d;
    Go(d - 1);
  }
  void Start() { Seed(); Go(n); result = acc; }
}"),
        // CW1 lift: polymorphic recursion through a virtual PROPERTY accessor on a base-typed field —
        // the FvaA.get_Depth ↔ FvaB.get_Depth cycle exists only via the accessor dispatch fan-out
        // (AccessorDispatchImplDefs, the accessor twin of the v2b-2 invocation arm); without it the
        // getters' frames are never spilled around the mutual re-entry.
        ("facet_virtual_accessor_recursion", "FacetVAccRec",
@"using UdonSharp;
public class FvaBase { public FvaBase next; public int budget; public virtual int Depth { get { return 0; } } }
public class FvaA : FvaBase { public override int Depth { get { if (budget <= 0) return 1; budget = budget - 1; int keep = budget * 10; return next.Depth + keep + 1; } } }
public class FvaB : FvaBase { public override int Depth { get { if (budget <= 0) return 2; budget = budget - 1; int keep = budget * 100; return next.Depth + keep + 2; } } }
public class FacetVAccRec : UdonSharpBehaviour {
  public int result;
  void Start(){ FvaA a = new FvaA(); FvaB b = new FvaB(); a.next = b; b.next = a; a.budget = 2; b.budget = 3; result = a.Depth; }
}"),
        // CW1 instruments: ACCESSOR-MEDIATED cycle — the override getter calls back into an ordinary
        // method whose base-typed `peer.Level` read re-enters the getter, so the Probe↔get_Level SCC
        // exists only through the accessor fan-out (the re-entrant read site surfaces as the
        // Probe → FamDer.get_Level cycle edge; [reentrant-dispatch-sites] stays a delegate-only facet
        // and is pinned empty here). Both classes minted keeps the chain ≥2-target; `keep` is live
        // across the dispatched read, so Probe's frame must spill around the re-entry.
        ("facet_accessor_mediated_cycle", "FacetAccMedCycle",
@"using UdonSharp;
public class FamBase { public FamBase peer; public int budget; public virtual int Level { get { return 0; } }
  public int Probe(){ int keep = budget * 10; return peer.Level + keep + 1; } }
public class FamDer : FamBase { public override int Level { get { if (budget <= 0) return 3; budget = budget - 1; return Probe() + 2; } } }
public class FacetAccMedCycle : UdonSharpBehaviour {
  public int result;
  void Start(){ FamBase b = new FamBase(); FamDer d = new FamDer(); FamDer e = new FamDer(); d.peer = e; e.peer = d; b.peer = d; d.budget = 2; e.budget = 3; b.budget = 4; result = d.Probe() + b.Probe(); }
}"),
        // CW1 instruments: THIS-receiver accessor recursion — `Depth` inside each getter body is a
        // this-receiver dispatch site (IsDispatchSite includes `this`, excludes `base` syntax), so the
        // getters are self-recursive THROUGH the dispatch fan-out: the base getter's read fans out to
        // both minted impls (base self-loop + base→derived cross edge) while the derived getter's
        // fans out to the derived impl only (its `this` is declared FthDer).
        ("facet_this_accessor_recursion", "FacetThisAccRec",
@"using UdonSharp;
public class FthBase { public int budget; public virtual int Depth { get { if (budget <= 0) return 5; budget = budget - 1; int keep = budget * 10; return Depth + keep + 1; } } }
public class FthDer : FthBase { public override int Depth { get { if (budget <= 0) return 9; budget = budget - 1; int keep = budget * 100; return Depth + keep + 2; } } }
public class FacetThisAccRec : UdonSharpBehaviour {
  public int result;
  void Start(){ FthBase b = new FthBase(); FthDer d = new FthDer(); b.budget = 2; d.budget = 3; result = b.Depth + d.Depth; }
}"),
        // Base-ctor chain recursion (the v2b-2 comment's Rb..ctor -> Rd.Make -> new Rd -> Rb..ctor
        // form): the cycle runs through the explicit ctor chain and a this-receiver virtual call
        // inside the base ctor; a ctor inside a cycle is always non-tail.
        ("facet_base_ctor_chain_recursion", "FacetBaseCtorRec",
@"using UdonSharp;
public class FbcBase { public int v; public FbcBase(int n){ v = Make(n); } public virtual int Make(int n){ return n; } }
public class FbcDer : FbcBase {
  public FbcDer(int n) : base(n) { }
  public override int Make(int n){ if (n <= 0) return 0; FbcDer d = new FbcDer(n - 1); return d.v + 1; }
}
public class FacetBaseCtorRec : UdonSharpBehaviour {
  public int result;
  void Start(){ FbcDer d = new FbcDer(3); result = d.v; }
}"),
    };
}
