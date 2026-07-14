using Xunit;

namespace USugar.Tests;

public class ResolvedEdgeResolverTests
{
    [Fact]
    public void DirectCall_YieldsOneCallEdge()
    {
        var src = @"using UdonSharp;
public class A : UdonSharpBehaviour { public int seed; public int result;
  int Helper(int x) => x + 1;
  void Start(){ result = Helper(seed); } }";
        var targets = TestHelper.ResolveEdgesForFirstCall(src, "A", callInMethod: "Start", calleeName: "Helper");
        Assert.Contains(targets, t => t.Method.Name == "Helper" && t.Role == TargetRole.CallEdge);
    }
}
