using Xunit;

namespace USugar.Tests;

public class PatternCaptureTests
{
    // Regression: a recursive-pattern designator captured in a closure was missed by CaptureScopeAnalysis's
    // ownership walker (no IRecursivePatternOperation arm) → "Capturing closure has no binding scope" throw.
    [Fact]
    public void RecursivePatternDesignator_CapturedInClosure_Compiles()
    {
        var src = @"using UdonSharp;
using System;
public struct P { public int X; public int Y; }
public class A : UdonSharpBehaviour { public int result;
  void Start(){
    P p = new P{ X = 5, Y = 7 };
    Func<int> f = () => 0;
    if (p is { X: 5 } q) f = () => q.Y;
    result = f();
  } }";
        var uasm = TestHelper.CompileToUasm(src, "A");
        Assert.NotNull(uasm);
    }

    // A nested designator inside a positional/property subpattern must also be captured.
    [Fact]
    public void NestedRecursivePatternDesignator_CapturedInClosure_Compiles()
    {
        var src = @"using UdonSharp;
using System;
public struct P { public int X; public int Y; }
public class A : UdonSharpBehaviour { public int result;
  void Start(){
    P p = new P{ X = 5, Y = 7 };
    Func<int> f = () => 0;
    if (p is { Y: var y }) f = () => y;
    result = f();
  } }";
        var uasm = TestHelper.CompileToUasm(src, "A");
        Assert.NotNull(uasm);
    }
}
