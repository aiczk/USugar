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
        // Cross-behaviour call exercises LowerCrossBehaviourCall (Set/Send/Get expansion) —
        // the one LIVE semantic lowering embedded in HirToLir that CoreFlatten copies verbatim.
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
    };

    public static (string Name, string ClassName, string Source) ByName(string name)
        => Cases.First(c => c.Name == name);
}
