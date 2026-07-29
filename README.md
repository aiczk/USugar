# USugar

An alternative UdonSharp compiler for writing Udon behaviours, adding user-defined classes and structs, generics, delegates, closures, tuples, and pattern matching.

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

It also supports common language constructs such as `ref` and `out` parameters, `using`, user-defined operators and conversions, and string interpolation.

Ranges (`arr[1..3]`) and index-from-end expressions (`arr[^1]`) are supported on arrays, for reads, writes, compound assignment, and `ref` arguments. They are not supported on `string` or on other types with a `Count`/`Length` indexer. Range slicing copies into a new array.

A single interface must have one runtime representation across a compilation. Implementing the same interface with both an `UdonSharpBehaviour` and a user class or struct is a compile error.

Your code is parsed as C# 9 regardless of the language version configured for the assembly. Newer syntax (`record struct`, list patterns, file-scoped namespaces, UTF-8 string literals) fails as a syntax error before USugar sees it.

Closed generics are specialized at compile time.
Direct self tail calls written as `return Self(...)` are converted to loops and cost no stack depth. Mutual tail recursion, a tail call through another instance, and a tail call inside a conditional expression stay real calls.

## Requirements

- Unity 2022.3.x, developed against 2022.3.22f1
- VRChat Worlds SDK 3.x, developed against `com.vrchat.worlds` 3.10.1
- UdonSharp (included in VRC SDK)

## Installation

Download the latest `.unitypackage` from [Releases](https://github.com/aiczk/USugar/releases) and import it into your project.

## Usage

1. In the Unity menu bar, enable **USugar > Override Compiler**.
2. Run **USugar > Compile > USugar** to recompile all scripts.

Existing UdonSharp scripts are then compiled through USugar.

Disable **USugar > Override Compiler** to return to the standard compiler. The setting is remembered and re-applied on every domain reload.

Recompiles are skipped when nothing relevant changed. The change fingerprint covers source contents, preprocessor symbols, compiler options, response files, referenced assemblies, program-asset bindings, and USugar's own build, so upgrading USugar invalidates it. **USugar > Compile > USugar** always bypasses it.

USugar binds to UdonSharp internals by reflection. If an SDK update moves or renames them, the override refuses to install, disables itself, and logs `CRITICAL reflection target not found`. Check the Console after a domain reload following an SDK upgrade.

## Limitations

USugar reports a compile error when the Udon VM cannot preserve the source program's C# semantics at compile time. Where only the runtime can decide, it either follows the Udon VM halt path or logs a `USugar:` error and continues with a default value.

- Exceptions, `async` and `await`, iterator `yield`, `lock`, unsafe code, function pointers, C# `dynamic`, and checked overflow are unsupported. LINQ query syntax (`from ... select`) is a known gap. Except for `yield`, these fail with a generic `Unsupported operation` message rather than a tailored one.
- `foreach` supports arrays, not `IEnumerable` or `List<T>`.
- Exception-dependent constructs that cannot be represented honestly are compile errors. This includes non-exhaustive switch expressions, potentially failing hard casts into compiler-owned bundles, `Nullable<T>.Value`, and explicit nullable-to-non-nullable unwraps. Use exhaustive arms, `is`/`as`/patterns, `??`, or `GetValueOrDefault()` instead. A non-exhaustive switch expression cannot be silenced with `#pragma warning disable CS8509` or `NoWarn`, because USugar re-runs the diagnostics with suppressions stripped. Add a discard (`_`) arm.
- `ref` and `out` parameters are supported, but ref locals and `in` parameters are not.
- Multidimensional arrays are unsupported; use one-dimensional or jagged arrays.
- Mutable static fields, auto-properties, and events are unsupported. Constants, compile-time-foldable `static readonly` values, static methods, and computed static properties are supported.
- Records are unsupported. Their CLR contracts combine runtime type identity, equality, hashing, cloning, and printing; USugar rejects them instead of implementing only a subset. Use a class for reference semantics or a struct with explicit equality and copy operations for value semantics.
- Runtime construction of `UdonSharpBehaviour` instances is unsupported.
- Static constructors, module initializers, destructors, and explicit constructors on `UdonSharpBehaviour` types are unsupported because Udon has no matching CLR lifetime hooks.
- Serialized fields whose runtime representation is an `object[]` bundle are round-tripped through USugar's own tagged bundle serializer, so plain-data user classes, structs, and tuples serialize normally. A field that cannot round-trip, because it contains a delegate, an open or unmanaged type, or a multidimensional array, is a compile error; mark it `[NonSerialized]` and initialize it in code.
- `GetType()` is unsupported when a value can contain a USugar class, struct, anonymous type, or delegate bundle. `typeof(SomeUserClass)` is likewise unsupported, and `typeof(T)` for a type Udon folds onto a shared runtime tag may only be passed directly to a component query such as `GetComponent(typeof(T))`.
- Delegates use a callable-only model: creation, typed storage, invocation, null checks, combination, removal, events, closures, variance adapters, and `ref`/`out` signatures are supported. Delegate-to-delegate equality, object erasure, `Equals`, `GetHashCode`, `GetType`, `ToString`, `Target`, `Method`, and `GetInvocationList` are rejected; generated aggregate members that would observe a contained delegate are rejected for the same reason.
- Anonymous types retain C# reference semantics even though their physical carrier is `object[]`: assignment aliases, `==` compares identity, and the generated `Equals`, `GetHashCode`, and `ToString` members use the read-only property values.
- Lifted (nullable-operand) user-defined operators and conversions on your own classes and structs are unsupported; test `HasValue` and apply the operator to the non-null value. User-defined `&&` and `||` through `operator true`/`false` are also unsupported.
- `[UdonSynced]` is limited to Udon's own syncable primitives and arrays of them; delegate-typed and user-class-typed fields cannot be synced. Continuous sync rejects array fields, and Manual sync rejects linear and smooth interpolation. `[NetworkCallable]` methods cannot take or return a delegate or a user class.
- User classes can cross program boundaries through typed fields, methods, and interfaces, but erasure to `object`, network sync, and `[NetworkCallable]` are restricted.
- Delegates whose signatures contain user classes are limited to private, same-program use. A delegate stored into any cross-program surface (a public or `[SerializeField]` field, a public property or event, another behaviour's member, or a cross-behaviour call argument) must be written at that site as a lambda or method group; copying one through a local, parameter, or field first is rejected.
- A value-dependent failure exposed by a real Udon extern or compiler-owned representation access follows the normal Udon VM halt path, so invoking a null delegate and calling through a null user-class reference both fault the VM. Some sites instead log a `USugar:` error and continue with a default value, among them `ToString()` on a null class reference, an untagged or environment-less delegate bundle, a null or non-local method-group receiver, a virtual call or accessor that matches no compiled class, `Equals` against a foreign compiler bundle, and a multicast fan-out with no invocation list. USugar does not synthesize exception handlers or trap operations.
- Non-tail recursion uses a shared software stack that starts at 64 object slots and doubles at runtime whenever a frame does not fit, so USugar imposes no fixed recursion-depth cap. Per-frame cost is the number of spilled fields plus the number of values live across the recursive call. Every call that was not converted to a loop also pushes a return address onto the Udon VM's own stack, which USugar neither sizes nor bounds.
- Each emitted concrete `UdonSharpBehaviour` must resolve to exactly one `UdonSharpProgramAsset` through its source `MonoScript`. Generic, abstract, and other helper behaviours are parsed for semantic context but are not emitted and do not need a program asset. Missing, duplicate, renamed, or orphaned root bindings fail compilation instead of reusing a program by class name.
- A behaviour's assembly must be in the USugar source domain: `Assembly-CSharp`, or an assembly covered by a U# Assembly Definition asset. Its user base classes and helper types must live in that assembly or in another in-domain assembly it references; a user type reachable only as a compiled reference is treated as an extern and rejected. Attributes, enums, and registered Udon extern types may be referenced across any assembly boundary.
- `Runtime/IsExternalInit.cs` sits outside any asmdef so it compiles into `Assembly-CSharp`. To use `init` accessors from behaviours in your own asmdef, add a copy of that file to it.

## Compiler design

```text
C# source (one Unity assembly)
  -> Roslyn compilation + semantic model
  -> frozen layout plan
  -> bound program
  -> Core IR (flat control-flow graph)
  -> UASM
```

Each runtime Unity assembly is compiled independently with its own asmdef references and
preprocessor symbols. Source files reported by Unity's compilation pipeline are accepted from
both `Assets/` and resolved `Packages/` locations.

USugar first freezes one layout plan per compilation unit, then builds one bound program per
concrete, program-asset-backed behaviour root. The bound program records reachable bodies, callable
definitions, generic specializations, user-class type objects, and field initializers. It is a set
of resolved lookup tables, not an expression tree.

The same reachability result feeds method registration, closure capture analysis, and recursion
analysis. Call sites use shared dispatch and transport plans so analysis and emitted calls resolve
the same runtime targets.

Roslyn operations are then lowered directly into the flat Core IR: functions, basic blocks, and a
closed vocabulary of five instruction kinds, three terminators, and six value kinds. There is no
structured or tree-shaped IR stage and no flattening pass.

The backend closes open terminators, verifies the IR, coalesces non-overlapping slots, inserts
liveness-based recursion spills, then verifies and freezes the module before generating UASM text.
The IR is verified twice, once after construction and once at the freeze that gates code
generation. Code generation accepts only a frozen, verified module, so that handoff is enforced by
the type system.

Core IR is the only compiler IR. Slot coalescing is the only optimization pass and primarily
reduces the generated program's heap-variable count; recursion spill insertion is a correctness
pass, not an optimization.

Behaviours are compiled in parallel, one emitter per behaviour, and a failure is reported against
that behaviour rather than aborting the compile.

Compiler errors are reported with the offending file, line, and column.

USugar has not been tested against every UdonSharp-compatible C# program. If standard UdonSharp
accepts a program that USugar rejects, [open an issue](https://github.com/aiczk/USugar/issues).
Some rejects are deliberate design decisions rather than defects, including `checked` contexts,
non-exhaustive switch expressions, `in` parameters, records, and mutable static state.

## License

MIT
