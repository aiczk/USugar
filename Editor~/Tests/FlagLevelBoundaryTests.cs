using Xunit;

namespace USugar.Tests;

// Census companions (2026-07-11): boundaries the KIND-level census cannot see (ref-ness is a flag,
// iterators reject at the return type) — pinned here so they stay loud.
public class FlagLevelBoundaryTests
{
    [Fact]
    public void RefLocal_RejectsLoudly()
    {
        var ex = Record.Exception(() => TestHelper.CompileToUasm(@"using UdonSharp;
public class RefL : UdonSharpBehaviour {
    public int seed; public int result;
    void Start(){ int x = seed; ref int r = ref x; r = 99; result = x; }
}", "RefL"));
        Assert.NotNull(ex);
        Assert.Contains("ref local", ex.Message);
    }
}
