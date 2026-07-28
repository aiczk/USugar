# USugar

An alternative UdonSharp compiler for writing Udon behaviours with a broader subset of C#.

> **Status: Experimental.** Expect breaking changes and unsupported edge cases.

## Example

This example uses a user-defined class, a lambda, a delegate, string interpolation, and a switch expression:

```csharp
using System;
using UdonSharp;
using UnityEngine;

class Counter
{
    public int Value;

    public int Add(int amount) => Value += amount;
}

public class Scoreboard : UdonSharpBehaviour
{
    Counter counter = new Counter();
    Action<int> changed;

    void Start() =>
        changed = score => Debug.Log($"Score: {score}");

    public override void Interact()
    {
        var score = counter.Add(1);
        changed?.Invoke(score);

        Debug.Log(score switch
        {
            >= 10 => "Gold",
            >= 5 => "Silver",
            _ => "Bronze",
        });
    }
}
```

USugar integrates with the existing UdonSharp pipeline and requires no SDK modifications.

## Supported C#

USugar supports user-defined classes and structs, anonymous types, interfaces, inheritance, virtual dispatch, generic types and methods, delegates, closures, events, tuples, nullable values, pattern matching, switch expressions, local functions, and recursion.

It also supports common language constructs such as `ref` and `out` parameters, `using`, user-defined operators and conversions, ranges, index-from-end expressions, and string interpolation.

Closed generics are specialized at compile time.
Tail-recursive calls are converted to loops.

## Requirements

- Unity 2022.3.x
- VRChat Worlds SDK 3.x
- UdonSharp (included in VRC SDK)

## Installation

Download the latest `.unitypackage` from [Releases](https://github.com/aiczk/USugar/releases) and import it into your project.

## Usage

1. In the Unity menu bar, enable **USugar > Override Compiler**.
2. Run **USugar > Compile > USugar** to recompile all scripts.

Existing UdonSharp scripts are then compiled through USugar.

Disable **USugar > Override Compiler** to return to the standard compiler.

## Limitations

USugar reports a compile error when the Udon VM cannot preserve the source program's C# semantics.

- Exceptions, `async` and `await`, iterator `yield`, `lock`, unsafe code, function pointers, C# `dynamic`, and checked overflow are unsupported.
- A value-dependent failure exposed by a real Udon extern or compiler-owned representation access follows the normal Udon VM halt path. USugar does not synthesize exception handlers or trap operations.
- Exception-dependent constructs that cannot be represented honestly are compile errors. This includes non-exhaustive switch expressions, potentially failing hard casts into compiler-owned bundles, `Nullable<T>.Value`, and explicit nullable-to-non-nullable unwraps. Use exhaustive arms, `is`/`as`/patterns, `??`, or `GetValueOrDefault()` instead.
- `ref` and `out` parameters are supported, but ref locals and `in` parameters are not.
- `foreach` supports arrays, not `IEnumerable` or `List<T>`.
- Multidimensional arrays are unsupported; use one-dimensional or jagged arrays.
- Runtime construction of `UdonSharpBehaviour` instances is unsupported.
- Records are unsupported. Their CLR contracts combine runtime type identity, equality, hashing, cloning, and printing; USugar rejects them instead of implementing only a subset. Use a class for reference semantics or a struct with explicit equality and copy operations for value semantics.
- Anonymous types retain C# reference semantics even though their physical carrier is `object[]`: assignment aliases, `==` compares identity, and the generated `Equals`, `GetHashCode`, and `ToString` members use the read-only property values.
- Static constructors, module initializers, destructors, and explicit constructors on `UdonSharpBehaviour` types are unsupported because Udon has no matching CLR lifetime hooks.
- `GetType()` is unsupported when a value can contain a USugar class, struct, anonymous type, or delegate bundle.
- Delegates use a callable-only model: creation, typed storage, invocation, null checks, combination, removal, events, closures, variance adapters, and `ref`/`out` signatures are supported. Delegate-to-delegate equality, object erasure, `Equals`, `GetHashCode`, `GetType`, `ToString`, `Target`, `Method`, and `GetInvocationList` are rejected; generated aggregate members that would observe a contained delegate are rejected for the same reason.
- User classes can cross program boundaries through typed fields, methods, and interfaces, but erasure to `object`, network sync, and `[NetworkCallable]` are restricted.
- Delegates whose signatures contain user classes are limited to private, same-program use.
- Mutable static fields, auto-properties, and events are unsupported. Constants, compile-time-foldable `static readonly` values, static methods, and computed static properties are supported.
- Non-tail recursion uses a shared software stack with 8,192 object slots. Effective recursion
  depth depends on the number of values live across each recursive call.
- User classes, delegates, tuples, anonymous types, and other `object[]`-backed values cannot round-trip through
  the standard UdonSharp proxy/Inspector. USugar rejects serialized fields with those
  representations; mark runtime-only fields `[NonSerialized]` and initialize them in code.
- Each emitted concrete `UdonSharpBehaviour` must resolve to exactly one
  `UdonSharpProgramAsset` through its source `MonoScript`. Generic, abstract, and other
  helper behaviours are parsed for semantic context but are not emitted and do not need a
  program asset. Missing, duplicate, renamed, or orphaned root bindings fail compilation
  instead of reusing a program by class name.
- A behaviour, its user base classes, and runtime helper types must belong to the same asmdef.
  Metadata-only attributes, enums, and registered Udon extern types may be referenced across asmdefs.

## Compiler design

```text
C# source
  -> Roslyn semantic model
  -> compilation plan
  -> structured Core IR
  -> flat Core IR
  -> UASM
```

Each runtime Unity assembly is compiled independently with its own asmdef references and
preprocessor symbols. Source files reported by Unity's compilation pipeline are accepted from
both `Assets/` and resolved `Packages/` locations.

USugar first builds one compilation plan per concrete, program-asset-backed behaviour root.
The plan records reachable bodies, callable definitions, generic specializations, user-class type objects, and field initializers.

The same reachability result feeds method registration, closure capture analysis, and recursion analysis.
Call sites use shared dispatch and transport plans so analysis and emitted calls resolve the same runtime targets.

Roslyn operations are then lowered directly into one structured Core IR.
The backend verifies the IR, flattens it in place, verifies the flat form, coalesces non-overlapping slots, inserts liveness-based recursion spills, verifies it again, and emits UASM.

Core IR is the only compiler IR.
Slot coalescing is the only optimization pass and primarily reduces the generated program's heap-variable count.

Compiler errors include source locations that link back to the offending code in the Unity Console.

USugar has not been tested against every UdonSharp-compatible C# program.
If standard UdonSharp accepts a program that USugar rejects, [open an issue](https://github.com/aiczk/USugar/issues).

## License

MIT
