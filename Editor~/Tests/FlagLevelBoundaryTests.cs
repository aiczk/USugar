using Xunit;

namespace USugar.Tests;

// Census companion for flag-level shapes the operation-kind table cannot
// distinguish. Ref locals use selector-based storage lowering.
public class FlagLevelBoundaryTests
{
    [Fact]
    public void RefLocal_IsHandled()
    {
        var uasm = TestHelper.CompileToUasm(@"using UdonSharp;
public class RefL : UdonSharpBehaviour {
    public int seed; public int result;
    void Start(){ int x = seed; ref int r = ref x; r = 99; result = x; }
}", "RefL");
        Assert.NotNull(uasm);
    }
}
