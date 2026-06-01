// Polyfill enabling C# 9 `init`-only auto-property accessors under Unity's C# 9 LCD, whose BCL predates
// .NET 5 and therefore lacks System.Runtime.CompilerServices.IsExternalInit. Without this type Unity's
// own Roslyn pass refuses to compile any `init` accessor in the assembly, regardless of USugar.
//
// `internal` scopes the type to the compiling assembly, avoiding CS0436 duplicate-type conflicts with
// other assemblies (or a future Unity BCL) that ship their own copy. This file lives outside any asmdef
// so it compiles into Assembly-CSharp, where most UdonSharp behaviours live; user code in a custom asmdef
// should add its own copy of this file (or reference one) to use `init` there.
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
