# USugar

An alternative UdonSharp compiler that handles C# features the standard compiler rejects.

> **Status: Experimental.** This works on my projects, but it might not work on yours. Expect rough edges and breaking changes. If something breaks, [open an issue](https://github.com/aiczk/USugar/issues).

## What this gets you

Standard UdonSharp throws `NotSupportedException` on a surprising amount of valid C#. USugar compiles it.

```csharp
// Lambdas
Action greet = () => Debug.Log("Hello");
greet();

// Higher-order functions with delegates
void ForEach(int[] arr, Action<int> callback)
{
    for (int i = 0; i < arr.Length; i++)
        callback(arr[i]);
}
ForEach(new[] { 1, 2, 3 }, x => Debug.Log(x));

// Local functions
int Square(int n) => n * n;
Debug.Log(Square(5));

// Switch expressions + pattern matching
string grade = score switch
{
    > 90 => "A",
    > 70 => "B",
    _    => "C"
};

// Null operators
var pos = target?.position ?? Vector3.zero;
_cache ??= "initialized";

// Tuple types
(int sum, int count) GetStats() => (100, 5);
var (x, y) = GetStats();

// ref / out parameters
void Swap(ref int a, ref int b)
{
    int tmp = a; a = b; b = tmp;
}

// Default parameter values
int Add(int a, int b = 10) => a + b;

// Generic methods (monomorphized at compile time)
T Max<T>(T a, T b) where T : IComparable<T>
    => a.CompareTo(b) > 0 ? a : b;

// goto, using statements, do-while, ~bitwise NOT, and more
```

None of this requires SDK modifications. USugar hooks into the existing UdonSharp pipeline as an Editor-only package.

## How it works

```
C# source → Roslyn IOperation tree → HIR → optimize → LIR → optimize → UASM
```

The compiler uses a two-layer intermediate representation:

- **HIR (High-level IR)**: Structured control flow (if/while/for as nodes). Optimization passes run constant folding, dead code elimination, and copy propagation on the expression tree.
- **LIR (Low-level IR)**: Flat CFG with basic blocks. Further optimized with CFG simplification, copy propagation, dead code elimination, and slot coalescing (variable count reduction).

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

- **USugar > Compile > USugar (with IR dump)** — Outputs HIR, LIR, and UASM (with PC annotations) to `Temp/USugar/{ClassName}/` for each compiled class.

## Limitations

- No try/catch/finally — Udon VM has no exception support.
- Delegate variables work within a single UdonSharpBehaviour. Cross-behaviour delegate calls are not supported.
- Not tested against every UdonSharp-compatible C# pattern. If you find something that compiles with UdonSharp but fails with USugar, that's a bug.

## License

MIT
