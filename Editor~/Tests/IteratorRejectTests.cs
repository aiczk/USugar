using Xunit;

namespace USugar.Tests;

// Language-surface census companion (2026-07-11): iterator methods (yield) are NOT silently
// miscompiled — the IEnumerator/IEnumerable return type rejects loudly at extern resolution
// before the yield-as-return arm could ever run. This pin holds that door shut; a friendlier
// dedicated message is a LOW cleanup.
public class IteratorRejectTests
{
    [Fact]
    public void IteratorMethod_RejectsLoudly()
    {
        var ex = Record.Exception(() => TestHelper.CompileToUasm(@"using UdonSharp;
public class Yld : UdonSharpBehaviour {
    public int result;
    System.Collections.IEnumerator Steps(){ yield return null; }
    void Start(){ Steps(); result = 1; }
}", "Yld"));
        Assert.NotNull(ex);
        Assert.Contains("IEnumerator", ex.Message);
    }
}
