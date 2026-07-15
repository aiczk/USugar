using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// CA call-graph rewrite, M5b differential harness (the M5a-analogue oracle deleted in 640fe51):
/// for every battery source, compute the six RecursionInfo facets BOTH ways — the production
/// resolver-driven pass (RecursionNodeWalk, via <see cref="NewFacets"/>) and the retained legacy
/// private walks (<see cref="LegacyFacets"/>, via DebugComputeLegacyRecursionInfo) — assert
/// set-equality, then compare the canonical serialization against a committed fixture (the facet
/// census pinned BEFORE the swap). Battery = every GoldenCorpus source + targeted recursion shapes
/// (non-tail, mutual, local-function, lambda-in-loop capture, struct-member, virtual-dispatch
/// polymorphic incl. the CW5 generic-T-receiver form, base-ctor chain, reentrant-dispatch,
/// tail-spared mixed). Regenerate fixtures with UPDATE_SNAPSHOTS=1. Delete this file with the
/// legacy walks after C2/C4 prove the remaining arms.
/// </summary>
public class RecursionFacetEquivalenceTests
{
    static bool UpdateMode =>
        Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1";

    // ── the two "ways" (the M5b seam) ──
    // Stage 2 (swap landed): production BuildRecursionInfo consumes the shared resolver-driven pass
    // (RecursionNodeWalk — NewFacets reads it via DebugRecursionInfo); LegacyFacets recomputes the
    // facets from the retained legacy private walks on demand, so the equality below is the LIVE
    // old-vs-new differential guarding the swap until C2/C4 delete the legacy arms.
    static string LegacyFacets(UasmEmitter emitter) => Serialize(emitter.DebugComputeLegacyRecursionInfo());
    static string NewFacets(UasmEmitter emitter) => Serialize(emitter.DebugRecursionInfo);

    public static IEnumerable<object[]> Battery()
        => AllCases().Select(c => new object[] { c.Name });

    static IEnumerable<(string Name, string ClassName, string Source)> AllCases()
        => GoldenCorpus.Cases.Concat(TargetedShapes);

    static (string Name, string ClassName, string Source) ByName(string name)
        => AllCases().First(c => c.Name == name);

    [Theory]
    [MemberData(nameof(Battery))]
    public void FacetCensus_BothWaysEqual_AndMatchFixture(string name)
    {
        var c = ByName(name);
        TestHelper.CompileToUasm(c.Source, c.ClassName, out var emitter);

        // Determinism gate (mirrors the snapshot oracle): the census must be byte-stable across
        // compiles, or the fixture compare below would flake instead of gating the M5b swap.
        TestHelper.CompileToUasm(c.Source, c.ClassName, out var emitter2);
        Assert.True(LegacyFacets(emitter) == LegacyFacets(emitter2),
            $"Nondeterministic facet census for '{name}': two compiles differ.");

        var legacy = LegacyFacets(emitter);
        var fused = NewFacets(emitter);
        Assert.True(legacy == fused,
            $"Recursion facet sets diverge between the legacy builder and the worklist for '{name}':\n"
            + FirstDiffLine(legacy, fused));

        var path = Path.Combine(TestPaths.FacetCensusDir, name + ".facets");
        if (UpdateMode)
        {
            Directory.CreateDirectory(TestPaths.FacetCensusDir);
            File.WriteAllText(path, legacy);
            return;
        }

        Assert.True(File.Exists(path),
            $"Missing facet census '{path}'. Run with UPDATE_SNAPSHOTS=1 to capture.");
        Assert.Equal(Lf(File.ReadAllText(path)), legacy);
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

    static string FirstDiffLine(string a, string b)
    {
        var la = a.Split('\n');
        var lb = b.Split('\n');
        for (int i = 0; i < Math.Max(la.Length, lb.Length); i++)
        {
            var x = i < la.Length ? la[i] : "<missing>";
            var y = i < lb.Length ? lb[i] : "<missing>";
            if (x != y) return $"line {i + 1}: legacy '{x}' vs new '{y}'";
        }
        return "<identical>";
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
