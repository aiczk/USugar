using System;
using Xunit;

namespace USugar.Tests;

// TEMPORARY verification probe for the SS2(a) unclassifiable-carrier asymmetry finding.
// DELETE after verification.
public class Ss2aProbeTempTests
{
    const string Shared = @"
using System;
using UdonSharp;
public class SmglPayload { public int X; }
public struct SmglBox { public object o; }
public struct SmglDBox { public Action a; }
public class SmglOther : UdonSharpBehaviour { public Action pub; }
";

    // Twin A: struct-wrapped object capture. Finding claims this SHIPS (classifies direct-safe).
    [Fact]
    public void BoxWrappedObjectCapture_CrossProgramStore()
    {
        var uasm = TestHelper.CompileToUasm(Shared + @"
public class SmglMainA : UdonSharpBehaviour {
    public SmglOther other;
    void Start() {
        var p = new SmglPayload();
        Action inner = () => { p.X++; };
        SmglBox b = new SmglBox();
        b.o = inner;
        other.pub = () => { var q = b.o; };
    }
}", "SmglMainA");
        Assert.Contains(".code_start", uasm);
    }

    // Twin B: raw object capture — the unwrapped twin. Should REJECT via SS2(a) object leg.
    [Fact]
    public void RawObjectCapture_CrossProgramStore_Rejects()
    {
        var ex = Record.Exception(() => TestHelper.CompileToUasm(Shared + @"
public class SmglMainB : UdonSharpBehaviour {
    public SmglOther other;
    void Start() {
        var p = new SmglPayload();
        Action inner = () => { p.X++; };
        object o2 = inner;
        other.pub = () => { var q = o2; };
    }
}", "SmglMainB"));
        Assert.NotNull(ex);
    }

    // Twin C: struct-wrapped DELEGATE capture — the guarded aggregate twin (delegate leg recurses).
    [Fact]
    public void BoxWrappedDelegateCapture_CrossProgramStore_Rejects()
    {
        var ex = Record.Exception(() => TestHelper.CompileToUasm(Shared + @"
public class SmglMainC : UdonSharpBehaviour {
    public SmglOther other;
    void Start() {
        var p = new SmglPayload();
        Action inner = () => { p.X++; };
        SmglDBox d = new SmglDBox();
        d.a = inner;
        other.pub = () => { var q = d.a; };
    }
}", "SmglMainC"));
        Assert.NotNull(ex);
    }
}
