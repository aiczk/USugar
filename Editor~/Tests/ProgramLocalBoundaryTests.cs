using System;
using Xunit;

namespace USugar.Tests;

public class ProgramLocalBoundaryTests
{
    [Fact]
    public void ClassReceiverCapturedByHostedLambda_Rejects()
    {
        // B84 ledger: a lambda hosted inside a v1 class would need to capture the class receiver
        // through closure env state. Until class receiver capture is a formal ABI feature, reject it.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class C {
    public int v;
    public int M() { Func<int> f = () => v + 1; return f(); }
}
public class ClassReceiverCaptureHost : UdonSharpBehaviour {
    public int result;
    void Start() { var c = new C(); result = c.M(); }
}", "ClassReceiverCaptureHost"));
        Assert.Contains("class receiver capture", ex.Message);
    }
}
