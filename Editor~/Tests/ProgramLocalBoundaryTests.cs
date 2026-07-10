using System;
using Xunit;

namespace USugar.Tests;

public class ProgramLocalBoundaryTests
{
    [Fact]
    public void ClassReceiverCapturedByHostedLambda_Compiles()
    {
        // B84 ledger, superseded by the class-receiver-capture feature (design 2026-07-10 v2): the
        // receiver rides the closure env as a synthetic capture keyed by the owning member method.
        // Runtime value correctness is pinned in the harness (ClassReceiverCaptureTests, real-VM
        // DiffFuzz with divergent seeds); this pin keeps the shape compiling.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class C {
    public int v;
    public int M() { Func<int> f = () => v + 1; return f(); }
}
public class ClassReceiverCaptureHost : UdonSharpBehaviour {
    public int result;
    void Start() { var c = new C(); result = c.M(); }
}", "ClassReceiverCaptureHost");
        Assert.NotNull(uasm);
    }
}
