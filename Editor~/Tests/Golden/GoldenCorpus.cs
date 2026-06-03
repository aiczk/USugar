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
    };

    public static (string Name, string ClassName, string Source) ByName(string name)
        => Cases.First(c => c.Name == name);
}
