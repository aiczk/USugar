using System.Collections.Generic;

/// <summary>Representative C#9 forms whose lowering USugar currently hand-implements.
/// Each probe targets one handler-layer mechanism; the CFG dump shows whether Roslyn's
/// lowered form (FlowCapture / explicit blocks) would replace it.</summary>
public static class CfgProbes
{
    public static readonly List<(string Name, string Source)> All = new List<(string, string)>
    {
        ("compound_assign_sideeffect_legs", @"
public class C {
    int[] arr = new int[8];
    int k;
    int Idx() { k++; return k; }
    int Val() { return 7; }
    void M() { arr[Idx()] += Val(); }
}"),

        ("refout_complex_lvalue", @"
public struct S { public int v; }
public class C {
    S[] arr = new S[3];
    int k;
    int Idx() { k++; return k; }
    static void AddTo(ref int x) { x++; }
    void M() { AddTo(ref arr[Idx()].v); }
}"),

        ("deconstruction_assign", @"
public class C {
    int a;
    int[] arr = new int[8];
    int k;
    int Idx() { k++; return k; }
    (int, int) F() { return (1, 2); }
    void M() { (a, arr[Idx()]) = F(); }
}"),

        ("named_args_out_of_order", @"
public class C {
    int A() { return 1; }
    int B() { return 2; }
    void T(int a, int b) { }
    void M() { T(b: B(), a: A()); }
}"),

        ("conditional_access_coalesce", @"
public class C {
    string s;
    object o;
    void M() {
        int n = s?.Length ?? -1;
        o ??= new object();
        System.Action f = null;
        f?.Invoke();
    }
}"),

        ("loops", @"
public class C {
    int sum;
    void M(int[] xs) {
        for (int i = 0; i < xs.Length; i++) sum += xs[i];
        foreach (int x in xs) sum += x;
        int j = 0;
        while (j < 3) { sum++; j++; }
    }
}"),

        ("switch_statement_patterns", @"
public class C {
    int r;
    void M(object o) {
        switch (o) {
            case int i when i > 3: r = 1; break;
            case string s: r = s.Length; break;
            default: r = 0; break;
        }
    }
}"),

        ("switch_expression", @"
public class C {
    int M(int x) {
        return x switch { > 3 => 1, 0 => 2, _ => 0 };
    }
}"),

        ("is_pattern_designator", @"
public class C {
    int r;
    void M(object o) {
        if (o is int tv) r = tv;
    }
}"),

        ("lambda_and_local_function", @"
public class C {
    int M() {
        int v = 5;
        System.Func<int> f = () => v + 1;
        int L(int n) { return n <= 0 ? 0 : L(n - 1) + 1; }
        return f() + L(3);
    }
}"),

        ("using_with_goto", @"
public class D : System.IDisposable { public void Dispose() { } }
public class C {
    int x;
    bool cond;
    void M() {
        using (var d = new D()) {
            if (cond) goto done;
            x++;
        }
        done: x--;
    }
}"),

        ("incdec_property_and_indexer", @"
public class C {
    int P { get; set; }
    int k;
    int Idx() { k++; return k; }
    int Val() { return 7; }
    int this[int i] { get { return i; } set { k = value; } }
    void M() {
        P++;
        this[Idx()] = Val();
        this[1] += 2;
    }
}"),

        ("string_interpolation", @"
public class C {
    string M(int a) { return $""v={a:D2}!""; }
}"),

        ("tuple_equality", @"
public class C {
    bool M((int, int) a, (int, int) b) { return a == b; }
}"),

        ("index_from_end", @"
public class C {
    void M(int[] arr) { arr[^1] = 5; }
}"),

        ("try_finally_region", @"
public class C {
    int x;
    void M() {
        try { x++; }
        finally { x--; }
    }
}"),
    };
}
