using System.Linq;
using Xunit;

namespace USugar.Tests;

public class RecursivePatternTests
{
    [Fact]
    public void PositionalAndPropertyPattern_LowersEveryFacetAndBindsDesignator()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;

public struct PatternPoint
{
    public int X;
    public int Y;
    public void Deconstruct(out int x, out int y) { x = X; y = Y; }
}

public class RecursivePatternTest : UdonSharpBehaviour
{
    public int result;

    void Start()
    {
        var point = new PatternPoint { X = 4, Y = 7 };
        if (point is PatternPoint(4, > 5) { X: > 0 } bound)
            result = bound.Y;
    }
}", "RecursivePatternTest");

        // The positional Y check and the property X check must both survive lowering. Before the
        // unified recursive-pattern arm only the first comparison was emitted, and `bound` was never
        // registered (using bound.Y then failed later instead of exposing the omitted binding directly).
        var greaterThanChecks = uasm.Split('\n')
            .Count(line => line.Contains(
                "SystemInt32.__op_GreaterThan__SystemInt32_SystemInt32__SystemBoolean"));
        Assert.True(greaterThanChecks >= 2,
            $"Expected both positional and property comparisons, found {greaterThanChecks}.");
        Assert.Contains("result: %SystemInt32", uasm);
    }
}
