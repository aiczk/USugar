using System.Collections.Generic;
using System.Linq;

namespace USugar.Tests;

/// <summary>
/// Curated, stable corpus of (name, className, C# source) used as the full-text UASM
/// snapshot oracle. Covers the representative feature set + the highest Phi-sensitivity
/// control-flow shapes (design spec §5 / §6 gate 3). Adding a case is additive; never
/// reorder or mutate an existing case's source without regenerating its baseline intentionally.
/// Only currently-supported features belong here — the snapshot oracle pins the behavior the
/// Core IR migration must preserve byte-for-byte.
/// </summary>
public static class GoldenCorpus
{
    public static readonly IReadOnlyList<(string Name, string ClassName, string Source)> Cases =
        new (string, string, string)[]
    {
        ("if_else", "IfElse",
@"using UdonSharp; public class IfElse : UdonSharpBehaviour {
  public int x;
  void Start(){ if (x > 0) x = 1; else x = 2; }
}"),
        ("while_count", "WhileCount",
@"using UdonSharp; public class WhileCount : UdonSharpBehaviour {
  public int n;
  void Start(){ int i = 0; while (i < n) { i = i + 1; } }
}"),
        ("for_sum", "ForSum",
@"using UdonSharp; public class ForSum : UdonSharpBehaviour {
  public int total;
  void Start(){ for (int i = 0; i < 10; i++) total = total + i; }
}"),
        ("ternary", "Ternary",
@"using UdonSharp; public class Ternary : UdonSharpBehaviour {
  public int x; public int y;
  void Start(){ y = x > 0 ? 1 : -1; }
}"),
        ("lambda_local", "LambdaLocal",
@"using System; using UdonSharp; public class LambdaLocal : UdonSharpBehaviour {
  public int v;
  void Start(){ Action a = () => v = 5; a(); }
}"),
        // Stage 2 M2 canonical capturing-closure form (design §10): a per-iteration loop local
        // captured by a body lambda stored into an array. The byte gate for env alloc / __Get-__Set
        // access / __envp chain / capturing-bridge null-env guard on all later Stage-2 changes.
        ("capturing_lambda_loop", "CapturingLambdaLoop",
@"using System; using UdonSharp; public class CapturingLambdaLoop : UdonSharpBehaviour {
  public int a; public int b;
  void Start(){
    Func<int>[] fs = new Func<int>[2];
    for (int i = 0; i < 2; i++){ int v = i * 10; fs[i] = () => v; }
    a = fs[0](); b = fs[1]();
  }
}"),
        // B45 canonical baseline (design §4 M1): a RECURSIVE user-struct method that mints a per-frame
        // capturing closure and escapes it into an array, all fired after the full unwind — the struct
        // analogue of capturing_lambda_loop. The byte gate proving struct-member closures join the same
        // Stage-2 env chain (env alloc / __Get-__Set / __envp / capturing bridge) as class closures, per
        // CaptureScopeAnalysis's struct-member root expansion. An ADDED baseline, not a regeneration.
        ("struct_recursion_per_frame_closure", "StructRecursionPerFrameClosure",
@"using System; using UdonSharp;
public struct StructPerFrameRecBox {
  public void Rec(Func<int>[] arr, int depth){
    int captured = depth * 10;
    arr[depth] = () => captured;
    if (depth <= 0) return;
    Rec(arr, depth - 1);
  }
}
public class StructRecursionPerFrameClosure : UdonSharpBehaviour {
  public int sum;
  void Start(){
    Func<int>[] fs = new Func<int>[3];
    StructPerFrameRecBox box = new StructPerFrameRecBox();
    box.Rec(fs, 2);
    sum = fs[0]() + fs[1]() + fs[2]();
  }
}"),
        ("tuple_deconstruct", "TupleDeconstruct",
@"using UdonSharp; public class TupleDeconstruct : UdonSharpBehaviour {
  public int a; public int b;
  void Start(){ (a, b) = Stats(); }
  (int, int) Stats() => (1, 2);
}"),
        ("nullable_coalesce", "NullableCoalesce",
@"using UnityEngine; using UdonSharp; public class NullableCoalesce : UdonSharpBehaviour {
  public Transform target; public Vector3 p;
  void Start(){ p = target != null ? target.position : Vector3.zero; }
}"),
        ("switch_pattern", "SwitchPattern",
@"using UdonSharp; public class SwitchPattern : UdonSharpBehaviour {
  public int score; public int grade;
  void Start(){ grade = score switch { > 90 => 1, > 70 => 2, _ => 3 }; }
}"),
        ("foreach_array", "ForeachArray",
@"using UdonSharp; public class ForeachArray : UdonSharpBehaviour {
  public int[] arr; public int sum;
  void Start(){ foreach (var e in arr) sum = sum + e; }
}"),
        ("local_function", "LocalFunction",
@"using UdonSharp; public class LocalFunction : UdonSharpBehaviour {
  public int r;
  void Start(){ int Square(int n) => n * n; r = Square(5); }
}"),
        // Cross-behaviour call exercises CoreBuilder's direct Set/Send/Get expansion.
        ("cross_behaviour_call", "CrossBehaviour",
@"using UdonSharp; public class CrossBehaviour : UdonSharpBehaviour {
  public TestStubs.BaseEnemy enemy; public int hp;
  void Start(){ enemy.TakeDamage(5); hp = enemy.GetHp(); }
}"),
        // ── high-sensitivity Phi / CondBlock shapes (spec §6 gate 3) ──
        ("do_while", "DoWhile",
@"using UdonSharp; public class DoWhile : UdonSharpBehaviour {
  public int n; public int i;
  void Start(){ i = 0; do { i = i + 1; } while (i < n); }
}"),
        ("nested_loop_switch", "NestedLoopSwitch",
@"using UdonSharp; public class NestedLoopSwitch : UdonSharpBehaviour {
  public int acc;
  void Start(){ for (int i = 0; i < 3; i++) { switch (i) { case 0: acc += 1; break; case 1: acc += 10; break; default: acc += 100; break; } } }
}"),
        ("goto_out_of_loop", "GotoOutOfLoop",
@"using UdonSharp; public class GotoOutOfLoop : UdonSharpBehaviour {
  public int found;
  void Start(){ for (int i = 0; i < 10; i++) { if (i == 5) { found = i; goto done; } } done: found = found; }
}"),
        ("shortcircuit_in_condition", "ShortCircuitInCondition",
@"using UdonSharp; public class ShortCircuitInCondition : UdonSharpBehaviour {
  public int[] data; public int hits;
  void Start(){ int i = 0; while (i < data.Length && data[i] > 0) { hits = hits + 1; i = i + 1; } }
}"),
        ("ternary_in_loop_body", "TernaryInLoopBody",
@"using UdonSharp; public class TernaryInLoopBody : UdonSharpBehaviour {
  public int acc;
  void Start(){ for (int i = 0; i < 5; i++) acc += (i % 2 == 0 ? 1 : -1); }
}"),
        // ── ??= write-back across non-this-field lvalue forms (H-1 regression lock) ──
        // Pin the conditional store's operand wiring byte-exact: the null branch must write back through the
        // captured lvalue (SetProgramVariable for cross-behaviour, SystemObjectArray.__Set__ at the right index
        // for an aggregate member, the user setter for an auto-property) — not just copy into a dead scratch.
        ("coalesce_crossfield", "CoalesceCrossField",
@"using UdonSharp; public class CoalesceTarget : UdonSharpBehaviour { public string F; }
public class CoalesceCrossField : UdonSharpBehaviour {
  public CoalesceTarget other;
  void Start(){ other.F ??= ""x""; }
}"),
        ("coalesce_tuplemember", "CoalesceTupleMember",
@"using UdonSharp; public class CoalesceTupleMember : UdonSharpBehaviour {
  void Start(){ (string a, string b) t = (null, null); t.a ??= ""x""; UnityEngine.Debug.Log(t.a); }
}"),
        ("coalesce_autoprop", "CoalesceAutoProp",
@"using UdonSharp; public class CoalesceAutoProp : UdonSharpBehaviour {
  public string Name { get; set; }
  void Start(){ Name ??= ""x""; }
}"),
        // ── ABI-risk operand wiring (H-2/H-3): the highest-risk invented ABIs were tested only by extern
        // EXISTENCE, which cannot catch operand miswiring (a clone that swaps indices, a spill that saves the
        // wrong slot still emits the same extern set). These byte-exact snapshots freeze the wiring (index→element,
        // copy-in/copy-out, spill/reload, field-by-field ==) so a silent value-corruption regression fails loudly,
        // VM-free. Codegen is already real-world + harness validated, so these pin known-good output.
        ("tuple_value_copy", "TupleValueCopy",
@"using UdonSharp; public class TupleValueCopy : UdonSharpBehaviour {
  public int outa;
  void Start(){ (int a, int b) t = (1, 2); var u = t; u.a = 9; outa = t.a; }
}"),
        ("struct_value_copy", "StructValueCopy",
@"using UdonSharp;
public struct StructValueCopyPt { public int x; public int y; }
public class StructValueCopy : UdonSharpBehaviour {
  public int outx;
  void Start(){ StructValueCopyPt a = new StructValueCopyPt(); a.x = 1; StructValueCopyPt b = a; b.x = 9; outx = a.x; }
}"),
        ("struct_ref_param", "StructRefParam",
@"using UdonSharp;
public struct StructRefBox { public int v; }
public class StructRefParam : UdonSharpBehaviour {
  public int outv;
  void Start(){ StructRefBox b = new StructRefBox(); b.v = 1; Bump(ref b); outv = b.v; }
  void Bump(ref StructRefBox x){ x.v = x.v + 1; }
}"),
        ("nontail_recursion", "NonTailRecursion",
@"using UdonSharp; public class NonTailRecursion : UdonSharpBehaviour {
  public int result;
  void Start(){ result = Fact(5); }
  int Fact(int n){ if (n <= 1) return 1; return n * Fact(n - 1); }
}"),
        ("tuple_equality", "TupleEquality",
@"using UdonSharp; public class TupleEquality : UdonSharpBehaviour {
  public bool eq;
  void Start(){ (int a, int b) x = (1, 2); (int a, int b) y = (1, 2); eq = x == y; }
}"),
        // Static readonly materialization (design §3, feature B): a non-const static readonly array
        // table declared+read, plus an instance field initialized FROM it — pins the static-tier
        // DeclareField/_start-init/LoadField codegen and the static-before-instance init order (§3.6)
        // byte-exact, the canonical baseline the S-M1 gate requires (§6).
        ("static_readonly_array_table", "StaticReadonlyArrayTable",
@"using UdonSharp; public class StaticReadonlyArrayTable : UdonSharpBehaviour {
  static readonly int[] Table = { 10, 20, 30 };
  public int fromTable = Table[1];
  public int result;
  void Start(){ result = Table[0] + fromTable; }
}"),
        // Multicast combine + fan-out (design 2026-07-03 §1, feature A A-M1): a loop-built `+=`
        // (three handlers subscribed) followed by a single fire — pins the __dlg_combine_/__dlg_remove_/
        // __dlg_fanout_{sig} synthesis and the CompoundAssignmentHandler lowering byte-exact, the
        // canonical baseline the A-M1 gate requires (§6). Single-cast golden stays untouched — this is
        // an ADDED baseline, not a regeneration of an existing one.
        ("multicast_loop_combine_fanout", "MulticastLoopCombineFanout",
@"using System; using UdonSharp; public class MulticastLoopCombineFanout : UdonSharpBehaviour {
  public int[] trace;
  public int n;
  public int lastRet;
  Func<int> d;
  void Start(){
    for (int i = 0; i < 3; i++){
      int captured = i;
      d += () => { trace[n++] = captured; return captured; };
    }
    lastRet = d();
  }
}"),
        // Field-like event subscribe + fire (design §2, feature A A-M2): backing-field materialize,
        // combine helper via `+=`, and invoke via the this-receiver event reference resolving to the
        // SAME dispatch path a plain delegate field uses — the canonical baseline the A-M2 gate
        // requires (§6). An ADDED baseline, not a regeneration of an existing one.
        ("event_subscribe_fire", "EventSubscribeFire",
@"using System; using UdonSharp; public class EventSubscribeFire : UdonSharpBehaviour {
  public int[] trace;
  public int n;
  public event Action<int> Foo;
  void Start(){
    Foo += x => trace[n++] = x;
    Foo += x => trace[n++] = x * 10;
    Foo(3);
  }
}"),
        // Tuple-return delegate: invoke + deconstruction (Stage 1.75 design 2026-07-04 §1 canonical
        // baseline, §5). A method-group bound to a tuple-returning method, invoked through the
        // delegate bundle, then deconstructed — the bridge InternalCalls the real method and stores
        // its already-SystemObjectArray result straight into conv-ret (no pack adapter: a tuple return
        // is already the same single aggregate slot a struct return uses), and the dispatch's conv-ret
        // read feeds DeconstructionAssignmentHandler's delegate-invocation arm directly.
        ("tuple_return_delegate", "TupleReturnDelegate",
@"using System; using UdonSharp; public class TupleReturnDelegate : UdonSharpBehaviour {
  public int x; public int y;
  (int, int) Callee(int p, int q) => (p * 10 + 1, q * 10 + 2);
  void Start(){
    Func<int, int, (int, int)> f = Callee;
    var (a, b) = f(3, 4);
    x = a; y = b;
  }
}"),
        // Variant method-group binding + invoke (Stage 1.75 design 2026-07-04 §2 canonical baseline,
        // §5). A covariant-return method group mints a sig adapter under the delegate's OWN sig
        // (design §2.2, B-1) — the adapter InternalCalls the real target with zero conversion
        // (reference-variance only, P2-verified) and stores its result into the sig-S conv-ret.
        ("variant_methodgroup_binding", "VariantMethodGroupBinding",
@"using System; using UdonSharp; public class VariantMethodGroupBinding : UdonSharpBehaviour {
  public object result;
  string MakeTag() => ""tag"";
  void Start(){
    Func<object> f = MakeTag;
    result = f();
  }
}"),
        // Cross-behaviour delegate property get/set (Stage 1.75 design 2026-07-04 §3 canonical
        // baseline, §5). The bundle is an opaque object[] reference transported through the EXISTING
        // cross property machinery (auto: direct SetProgramVariable/GetProgramVariable) — no new
        // mechanism, only the former setter-side reject removed.
        ("crossbehaviour_delegate_property", "CrossBehaviourDelegatePropertyCaller",
@"using System; using UdonSharp;
public class CrossBehaviourDelegatePropertyOwner : UdonSharpBehaviour {
  public Action Callback { get; set; }
}
public class CrossBehaviourDelegatePropertyCaller : UdonSharpBehaviour {
  public CrossBehaviourDelegatePropertyOwner owner;
  public int result;
  void Start(){
    owner.Callback = () => { result = 1; };
    Action a = owner.Callback;
    a();
  }
}"),
        // Feature G canonical baseline (2026-07-04 design §6): Box<int> and Box<string> coexisting in
        // one program, each calling its OWN per-spec method/indexer body — the byte gate for the
        // constructed-symbol collector + per-spec registration replacing roadmap B36's reject.
        ("genstruct_two_instantiations", "GenStructTwoInstantiations",
@"using UdonSharp;
public struct Box<T> {
  public T[] items;
  public T Get(int i) { return items[i]; }
  public T this[int i] { get { return items[i]; } set { items[i] = value; } }
}
public class GenStructTwoInstantiations : UdonSharpBehaviour {
  public int outInt;
  public int outStrLen;
  void Start(){
    Box<int> a = new Box<int>();
    a.items = new int[1];
    a[0] = 5;
    outInt = a.Get(0);

    Box<string> b = new Box<string>();
    b.items = new string[1];
    b[0] = ""hi"";
    outStrLen = b.Get(0).Length;
  }
}"),
        // N-dim array design (2026-07-04) canonical baseline: rank-2 creation + initializer + element
        // read/write + GetLength — the ONE new golden snapshot the design's §5 gate calls for.
        ("ndim_rank2_creation_initializer_readwrite_getlength", "NdimRank2Canonical",
@"using UdonSharp; public class NdimRank2Canonical : UdonSharpBehaviour {
  public int result;
  void Start(){
    int[,] a = new int[2,3] { {1,2,3}, {4,5,6} };
    a[0,0] = a[1,2];
    int len0 = a.GetLength(0);
    int len1 = a.GetLength(1);
    result = a[0,0] + len0 + len1;
  }
}"),
        // ── Modernization phase-1 lattice (design §2-3): each of the four pending-bridge queues
        // exercised under a NON-null type-param map — the coverage hole the M1-3 snapshot→reference
        // change must not silently break. The resolved generic type is byte-load-bearing in each
        // (union: new U[]; adapter: new T[]; wrapper/multicast: Func<T>), so a mis-composed or
        // mis-carried map changes the UASM. All four are real-VM value-verified (DiffFuzz Match).
        //
        // (1) delegate bridge from inside a generic LOCAL FUNCTION spec body — the union-compose shape
        // (Inner<U>'s map = spec(U) ∪ closure(T) ∪ rekey): the body binds a method group (bridge queue)
        // and reads new U[] so the re-keyed U is byte-load-bearing.
        ("genlf_bridge_union", "LatticeGenLfBridge",
@"using System; using UdonSharp;
public class LatticeGenLfBridge : UdonSharpBehaviour {
  public int seed;
  public int result;
  int Helper() => 7;
  void Start(){ result = M<int>(seed); }
  int M<T>(int baseVal){
    int Inner<U>(){ Func<int> d = Helper; U[] ua = new U[1]; return baseVal + d() + ua.Length; }
    return Inner<int>();
  }
}"),
        // (2) sig adapter under a generic method's map: a variant method-group binding (Func<object> ← a
        // string-returning method) mints a sig adapter; new T[] keeps the map content byte-load-bearing.
        ("genctx_sig_adapter", "LatticeGenAdapter",
@"using System; using UdonSharp;
public class LatticeGenAdapter : UdonSharpBehaviour {
  public int result;
  string MakeTag() => ""abc"";
  int M<T>(){ Func<object> d = MakeTag; T[] ta = new T[1]; return ((string)d()).Length + ta.Length; }
  void Start(){ result = M<int>(); }
}"),
        // (3) variance wrapper under a generic method's map: a Func<T> value converted to Func<object>
        // mints a wrapper whose INNER sig is T resolved (Void__SystemString), byte-load-bearing on T.
        ("genctx_variance_wrapper", "LatticeGenWrapper",
@"using System; using UdonSharp;
public class LatticeGenWrapper : UdonSharpBehaviour {
  public int result;
  string Make() => ""wxyz"";
  int M<T>(Func<T> src) where T : class { Func<object> o = src; return ((string)o()).Length; }
  void Start(){ Func<string> s = Make; result = M<string>(s); }
}"),
        // (4) multicast under a generic method's map: `+=` on a Func<T> registers the combine/remove/
        // fan-out helpers keyed by the T-resolved sig (Void__SystemInt32), byte-load-bearing on T.
        ("genctx_multicast", "LatticeGenMulticast",
@"using System; using UdonSharp;
public class LatticeGenMulticast : UdonSharpBehaviour {
  public int seed;
  public int result;
  T Run<T>(T a, T b){ Func<T> d = null; d += () => a; d += () => b; return d(); }
  void Start(){ result = Run<int>(seed, seed + 5); }
}"),
        // CA-M1 canonical baseline (class ABI v1, §2-2): a data class minted as a reference-semantics
        // object[1+F] bundle — new (no ctor) → field writes → ctor → field reads. Pins the reserved slot-0
        // layout, the mint sequence, and reference-semantics field r/w for a plain user class.
        ("class_data_crud", "ClassDataCrud",
@"using UdonSharp;
public class DataRec { public int A; public int B; public DataRec(int a){ A = a; } }
public class ClassDataCrud : UdonSharpBehaviour {
  public int seed;
  public int result;
  void Start(){ DataRec d = new DataRec(seed); d.B = seed * 2 + 1; result = d.A * 10 + d.B; }
}"),
        // CA-M1 canonical baseline: a self-referential linked list — the shape a struct cannot express (a
        // struct cannot hold a field of its own type). Pins reference-aliased chained mutation through
        // Next : Node and an instance method walking the chain.
        ("class_linked_list", "ClassLinkedList",
@"using UdonSharp;
public class LlNode { public int V; public LlNode Next; public LlNode(int v){ V = v; }
  public int Sum(){ int s = V; if (Next != null) s += Next.Sum(); return s; } }
public class ClassLinkedList : UdonSharpBehaviour {
  public int seed;
  public int result;
  void Start(){ LlNode a = new LlNode(seed); LlNode b = new LlNode(seed * 2); a.Next = b;
    result = a.Sum(); }
}"),
        // CA-M1 canonical baseline: a self-referential CONSTRUCTED GENERIC class Node<T> — monomorphized
        // per instantiation, self-reference walked by the AggregateLayout constructed-key cache without
        // recursion. Pins the generic class layout + mint + field r/w through a T-typed value.
        ("class_generic_node", "ClassGenericNode",
@"using UdonSharp;
public class GenNode<T> { public T Val; public GenNode<T> Next; public GenNode(T v){ Val = v; } }
public class ClassGenericNode : UdonSharpBehaviour {
  public int seed;
  public int result;
  void Start(){ GenNode<int> a = new GenNode<int>(seed); GenNode<int> b = new GenNode<int>(seed * 3);
    a.Next = b; result = a.Val + a.Next.Val; }
}"),
        // CA-v2b-2 canonical baseline (call-graph rewrite M5 prerequisite): the virtual-dispatch arm had
        // no byte gate. One case pins every lowering the arm owns: a base-typed 2-target typeobj-
        // ReferenceEquals chain including the no-match LogError guard, a singleton-devirt direct call
        // (derived-typed receiver), a this-receiver virtual call inside an inherited base method
        // (runtime-type dispatch from a base body), and the explicit `: base(...)` ctor chain writing a
        // base field. An ADDED baseline, not a regeneration.
        ("virtual_dispatch_chain", "VirtualDispatchChain",
@"using UdonSharp;
public abstract class VdShape {
  public int tag;
  public VdShape(int t){ tag = t; }
  public abstract int Area();
  public int Doubled(){ return Area() * 2; }
}
public class VdSq : VdShape {
  public int s;
  public VdSq(int side) : base(1) { s = side; }
  public override int Area(){ return s * s; }
}
public class VdRc : VdShape {
  public int w; public int h;
  public VdRc(int w0, int h0) : base(2) { w = w0; h = h0; }
  public override int Area(){ return w * h; }
}
public class VirtualDispatchChain : UdonSharpBehaviour {
  public int pick;
  public int result;
  void Start(){
    VdShape a = new VdSq(3);
    VdShape b = new VdRc(4, 5);
    VdShape c;
    if (pick > 0) c = a; else c = b;
    int viaChain = c.Area();
    VdSq d = new VdSq(6);
    int viaDevirt = d.Area();
    int viaBase = b.Doubled();
    result = viaChain + viaDevirt + viaBase + a.tag + b.tag + d.s;
  }
}"),
        // CW1 lift: the accessor twin of virtual_dispatch_chain — every dispatched accessor surface in
        // one snapshot: computed virtual property + virtual indexer + auto-prop/computed-override mixed
        // chain (two derived overrides ⇒ ≥2-target typeobj chains with no-match armor, reads AND
        // writes), compound assignment through a dispatched property, a this-receiver accessor read
        // inside an inherited base method body, and a singleton-implementor devirtualized accessor.
        ("virtual_accessor_chain", "VirtualAccessorChain",
@"using UdonSharp;
public class VacBase {
  public int v;
  protected int[] cells;
  public VacBase(){ cells = new int[4]; }
  public virtual int Score { get { return v; } set { v = value; } }
  public virtual int this[int i] { get { return cells[i]; } set { cells[i] = value; } }
  public virtual int Tag { get; set; }
  public int Snap(){ return Score + 100; }
}
public class VacA : VacBase {
  public override int Score { get { return v * 10; } set { v = value + 1; } }
  public override int this[int i] { get { return cells[i] * 2; } set { cells[i] = value + 3; } }
  public override int Tag { get { return v + 7; } set { v = value * 2; } }
}
public class VacB : VacBase {
  public override int Score { get { return v * 100; } set { v = value + 2; } }
  public override int this[int i] { get { return cells[i] * 3; } set { cells[i] = value + 4; } }
}
public class VdvBase { public virtual int K { get { return 1; } } }
public class VdvOnly : VdvBase { public override int K { get { return 7; } } }
public class VirtualAccessorChain : UdonSharpBehaviour {
  public int pick;
  public int result;
  void Start(){
    VacBase a = new VacA();
    VacBase b = new VacB();
    VacBase c;
    if (pick > 0) c = a; else c = b;
    c.Score = pick + 3;
    int viaRead = c.Score;
    a[1] = pick + 5;
    b[2] = pick + 6;
    int viaIdx = a[1] + b[2];
    a.Tag = pick + 2;
    int viaTag = a.Tag + b.Tag;
    c.Score += 4;
    int viaThis = a.Snap() + b.Snap();
    VdvBase s = new VdvOnly();
    int viaDevirt = s.K;
    result = viaRead + viaIdx + viaTag + viaThis + viaDevirt + c.Score;
  }
}"),
    };

    public static (string Name, string ClassName, string Source) ByName(string name)
        => Cases.First(c => c.Name == name);
}
