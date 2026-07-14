using System;
using Xunit;

namespace USugar.Tests;

// TRANSIENT VERIFICATION PROBE - delete after run.
public class DelegateArrayBoundaryProbeTests
{
    [Fact]
    public void Probe_PublicDelegateArrayField_WholeArrayAssign_ClassCapturingLambda()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class Foo { public int v; }
public class ArrWholeHost : UdonSharpBehaviour {
    public Action[] handlers;
    void Start() { var f = new Foo(); handlers = new Action[] { () => { f.v++; } }; }
}", "ArrWholeHost");
        Assert.NotNull(uasm);
        Assert.Contains("handlers", uasm);
        Assert.Contains(".export handlers", uasm);
    }

    [Fact]
    public void Probe_PublicDelegateArrayField_ElementAssign_ClassCapturingLambda()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class Foo { public int v; }
public class ArrElemHost : UdonSharpBehaviour {
    public Action[] handlers;
    void Start() { var f = new Foo(); handlers = new Action[1]; handlers[0] = () => { f.v++; }; }
}", "ArrElemHost");
        Assert.NotNull(uasm);
        Assert.Contains(".export handlers", uasm);
    }

    [Fact]
    public void Probe_ScalarTwin_Control_Rejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class Foo { public int v; }
public class ScalarCtl : UdonSharpBehaviour {
    public Action cb;
    void Start() { var f = new Foo(); cb = () => { f.v++; }; }
}", "ScalarCtl"));
        Assert.Contains("cross-program field 'cb'", ex.Message);
    }

    [Fact]
    public void Probe_CrossBehaviourDelegateArrayFieldWrite_Probe()
    {
        // Cross-behaviour whole-array write: other.handlers = local array holding class-capturing lambda.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class Foo { public int v; }
public class ArrOther : UdonSharpBehaviour { public Action[] handlers; }
public class ArrWriter : UdonSharpBehaviour {
    public ArrOther o;
    void Start() { var f = new Foo(); o.handlers = new Action[] { () => { f.v++; } }; }
}", "ArrWriter");
        Assert.NotNull(uasm);
    }
}
