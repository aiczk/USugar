## v0.1.0

First release.

USugar is an alternative UdonSharp compiler that adds user-defined classes and structs,
generics, delegates, closures, tuples, and pattern matching. It hooks into UdonSharp through
Harmony patches and needs no SDK modifications.

Import the `.unitypackage`, enable **USugar > Override Compiler** in the Unity menu bar, then
run **USugar > Compile > USugar**.

Experimental: expect breaking changes and unsupported edge cases. See the
[README](https://github.com/aiczk/USugar#readme) for the supported C# surface and the current
limitations.
