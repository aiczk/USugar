# USugar

An alternative UdonSharp compiler that handles C# features the standard compiler rejects.

> **Status: Experimental.** This works on my projects, but it might not work on yours. Expect rough edges and breaking changes. If something breaks, [open an issue](https://github.com/aiczk/USugar/issues).

## What this gets you

Standard UdonSharp throws `NotSupportedException` on a surprising amount of valid C#. USugar compiles it. Every feature below compiles today — this whole behaviour goes through USugar as-is:

```csharp
using System;
using UdonSharp;
using UnityEngine;

public class Showcase : UdonSharpBehaviour
{
    [UdonSynced] public int score;     // networked field
    public Action onTick;              // public delegate field: assign / invoke / ?.Invoke / compare
    public Transform target;
    int[] _data = { 3, 1, 4, 1, 5 };
    string _cache;

    struct Vec2 { public float x, y; public float LengthSq() => x * x + y * y; }   // struct with methods
    enum State { Idle, Running }
    [Flags] enum Opt { None = 0, A = 1, B = 2 }

    void Start()
    {
        // Lambdas + higher-order functions
        Action greet = () => Debug.Log("Hello"); greet();
        ForEach(_data, x => Debug.Log(x));

        // Enums, [Flags], foreach / while / do-while
        State s = State.Running;
        if (s == State.Running) Debug.Log("go");
        Opt opt = Opt.A | Opt.B;
        if ((opt & Opt.A) != 0) Debug.Log("hasA");
        int total = 0;
        foreach (int x in _data) total += x;
        int k = 0; while (k < 3) k++;
        do { k--; } while (k > 0);

        // Local functions + recursion (tail calls become loops)
        int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);
        Debug.Log(Fib(10));

        // Switch expressions + pattern matching (type-narrowing, relational, combined with and/or)
        string grade = score switch { >= 90 => "A", >= 70 => "B", _ => "C" };
        object boxed = score;
        if (boxed is int n and > 0) Debug.Log(n + grade.Length);

        // Null operators + Nullable<T>
        Vector3 pos = target?.position ?? Vector3.zero;
        _cache ??= "ready";
        int? maybe = null;
        Debug.Log(maybe ?? -1);

        // Tuples: deconstruction, == (field-by-field), value semantics
        var (sum, count) = Stats();
        Debug.Log((sum, count) == (100, 5));

        // Structs by value + ref / out parameters
        Vec2 v = new Vec2 { x = 3, y = 4 };
        Vec2 copy = v; copy.x = 9;          // v.x is still 3
        Debug.Log(v.LengthSq());
        int a = 1, b = 2; Swap(ref a, ref b);

        // Operators: bitwise, shift, compound, ~, conversions, string interpolation
        int flags = (1 << 3) | 0b0010; flags ^= ~0;
        int truncated = (int)3.9f;          // C# truncation → 3
        Debug.Log($"{v.LengthSq():F2} {nameof(Stats)} {truncated} {flags}");

        // Arrays: Length, index-from-end, ranges
        Debug.Log(_data[^1]);
        int[] middle = _data[1..^1];

        // Generics, monomorphized at compile time
        Debug.Log(Last(_data));
    }

    public override void Interact() => onTick?.Invoke();   // Udon event + delegate invoke

    void ForEach(int[] xs, Action<int> body) { for (int i = 0; i < xs.Length; i++) body(xs[i]); }
    (int sum, int count) Stats() => (100, 5);
    void Swap(ref int a, ref int b) { int t = a; a = b; b = t; }
    T Last<T>(T[] xs) => xs[xs.Length - 1];

    // Also supported: interfaces & explicit implementation, inheritance / override,
    // cross-behaviour calls, [FieldChangeCallback], more Udon events, goto, and using.
}
```

None of this requires SDK modifications. USugar hooks into the existing UdonSharp pipeline as an Editor-only package.

## How it works

```
C# source → Roslyn IOperation tree → Core IR → flatten → slot coalescing → UASM
```

The compiler builds a single intermediate representation, the **Core IR** (`CModule`):

- Handlers translate Roslyn `IOperation` directly into structured Core IR (control flow as nodes, slot-based values) via `CoreBuilder` — they never emit UASM themselves.
- The module is verified, flattened in place into a flat CFG of basic blocks, verified again, and run through **slot coalescing** (merges scratch/frame variables with non-overlapping lifetimes to cut heap-variable count). `CoreToUasm` then lowers it to UASM.

Slot coalescing is the only optimization pass. Udon's runtime cost is dominated by external calls, so speed-oriented passes (constant folding, dead-code elimination, copy propagation, CFG simplification) changed neither extern count nor runtime and were removed; coalescing is kept because it is slot allocation — it keeps the serialized program's variable count sane — not a speed optimization.

Tail-recursive calls are automatically converted to loops. Compile errors include source file location for clickable Unity Console output.

## Requirements

- Unity 2022.3.x
- VRChat Worlds SDK 3.x
- UdonSharp (included in VRC SDK)

## Install

Download the latest `.unitypackage` from [Releases](https://github.com/aiczk/USugar/releases) and import it into your project.

_VPM listing coming soon._

## Usage

1. In the Unity menu bar, enable **USugar > Override Compiler**.
2. Run **USugar > Compile > USugar** to recompile all scripts.

That's it. Your existing UdonSharp scripts will be compiled through USugar instead of the standard compiler. To switch back, disable **Override Compiler** and run **USugar > Compile > UdonSharp**.

### Debugging

Enable **USugar > Dump IR** to write UASM output on every compile to `Library/USugarCache/{ClassName}/`:
- `3_uasm.txt` / `3_uasm_annotated.txt` — UASM output (annotated version has PC addresses)

## Limitations

USugar rejects constructs whose C# semantics cannot be preserved by the Udon VM or by its
serialized program boundary. These rejects are intentional; they do not silently compile to a
different result.

- **VM limits** — no `try`/`catch`/`finally`/`throw`, `async`/`await`, iterator `yield`, `lock`,
  `unsafe` pointers, `stackalloc`, function pointers, or dynamic dispatch. `checked` overflow is
  also rejected because Udon has no overflow trap.
- **Alias limits** — `ref`/`out` parameters work, but ref locals and `in` parameters do not preserve
  C# aliasing semantics and are rejected.
- **Collections** — `foreach` is array-only. `List<T>`/`IEnumerable` cannot be iterated with
  `foreach` because Udon exposes no compatible enumerator protocol.
- **Types** — records and runtime `new` of an `UdonSharpBehaviour` are unsupported. User classes
  support allocation, inheritance, virtual and interface dispatch, generics, runtime type tests,
  generic virtual methods, user-defined operators and conversions, and per-program static storage.
  `GetHashCode()` and `GetType()` remain unsupported.
- **Program boundaries** — user classes have a portable tagged `object[]` ABI and can cross behaviour
  program boundaries through typed fields, calls, and interfaces. Structs/tuples, delegates, and
  multidimensional arrays also use `object[]`, but have different identity and transport rules.
  Erasure to `object`, network sync, `[NetworkCallable]`, and delegates whose signatures contain a
  user class remain restricted where the VM cannot preserve the source semantics.
- **Delegates and events** — closures, delegate values, multicast (`+=`/`-=`), delegate
  fields/properties, tuple returns, and field-like events (including static field-like events) are
  supported. Delegate signatures with `ref`/`out`, direct method-group binding to a user-class
  instance method, custom-accessor events, and cross-behaviour event subscription are not. Wrap a
  user-class method call in a lambda when a delegate is required.
- **Multidimensional arrays** — creation, indexing, `Length`, `Rank`, `GetLength`, and
  `GetUpperBound` are supported. They use an `object[]` bundle rather than a native Udon array, so
  general `Array` APIs, erasure to `object`/`Array`, and implicit string formatting are restricted.

Not tested against every UdonSharp-compatible C# pattern. If something compiles with standard UdonSharp but fails with USugar, that's a bug — [open an issue](https://github.com/aiczk/USugar/issues).

## License

MIT
