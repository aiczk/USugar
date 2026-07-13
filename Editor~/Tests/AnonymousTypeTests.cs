using Xunit;

namespace USugar.Tests;

// Anonymous types (final kind-level census gap closed 2026-07-11): `new { X = a }` mints as an
// object[] aggregate (properties -> slots, like a tuple); member reads route through the aggregate
// slot path. This closes LanguageSurfaceTests.KnownGaps to empty. VM value gates in the harness.
public class AnonymousTypeTests
{
    [Fact]
    public void Anon_CreateRead_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class AtP1 : UdonSharpBehaviour { public int result;
  void Start(){ var p = new { X = 1, Y = 2 }; result = p.X + p.Y; } }", "AtP1");

    [Fact]
    public void Anon_MixedFields_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class AtP2 : UdonSharpBehaviour { public int result;
  void Start(){ var p = new { A = 1, B = true, C = 3 }; result = p.A + (p.B ? 10 : 0) + p.C; } }", "AtP2");
}
