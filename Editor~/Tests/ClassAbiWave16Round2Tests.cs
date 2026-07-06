using System;
using Xunit;

namespace USugar.Tests;

public class ClassAbiWave16Round2Tests
{
    [Fact]
    public void B84_ClassHostedLambdaTouchingThis_Rejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class C {
    public int v;
    public int M() { Func<int> f = () => v + 1; return f(); }
}
public class B84Host : UdonSharpBehaviour {
    public int result;
    void Start() { var c = new C(); result = c.M(); }
}", "B84Host"));
        Assert.Contains("class receiver capture", ex.Message);
    }

    [Fact]
    public void B85_GenericArrayClassErasure_Rejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class Foo { public int v; }
public class B85ArrayHost : UdonSharpBehaviour {
    object[] Erase<T>(T[] xs) where T : class { object[] o = xs; return o; }
    void Start() { var xs = new Foo[1]; Erase(xs); }
}", "B85ArrayHost"));
        Assert.Contains("Erasing the v1 user class", ex.Message);
    }

    [Fact]
    public void B85_GenericStructFieldClassErasure_Rejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class Foo { public int v; }
public struct Box<T> { public T value; }
public class B85StructHost : UdonSharpBehaviour {
    object Erase<T>(Box<T> box) { object o = box; return o; }
    void Start() { Box<Foo> box = default; Erase(box); }
}", "B85StructHost"));
        Assert.Contains("Erasing the v1 user class", ex.Message);
    }

    [Fact]
    public void B86_PublicDelegatePlusEqualsClassCapturingLambda_Rejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class Foo { public int v; }
public class B86PublicHost : UdonSharpBehaviour {
    public Action cb;
    void Start() { var f = new Foo(); cb += () => { f.v++; }; }
}", "B86PublicHost"));
        Assert.Contains("cross-program field 'cb'", ex.Message);
    }

    [Fact]
    public void B86_PrivateDelegatePlusEqualsClassCapturingLambda_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class Foo { public int v; }
public class B86PrivateHost : UdonSharpBehaviour {
    Action cb;
    void Start() { var f = new Foo(); cb += () => { f.v++; }; }
}", "B86PrivateHost");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void B86_PublicEventPlusEqualsClassCapturingLambda_Rejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class Foo { public int v; }
public class B86EventHost : UdonSharpBehaviour {
    public event Action Tick;
    void Start() { var f = new Foo(); Tick += () => { f.v++; }; }
}", "B86EventHost"));
        Assert.Contains("public event 'Tick'", ex.Message);
    }

    [Fact]
    public void B87_PublicDelegateCopyFromPrivateField_Rejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class Foo { public int v; }
public class B87Host : UdonSharpBehaviour {
    public Action pub;
    Action priv;
    void Start() { var f = new Foo(); priv = () => { f.v++; }; pub = priv; }
}", "B87Host"));
        Assert.Contains("must be created directly", ex.Message);
    }

    [Fact]
    public void B87_PublicDelegateDirectCleanLambda_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class B87CleanHost : UdonSharpBehaviour {
    public Action pub;
    public int n;
    void Start() { int x = 1; pub = () => { n = x; }; }
}", "B87CleanHost");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void B87_PrivateDelegateCopy_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class Foo { public int v; }
public class B87PrivateHost : UdonSharpBehaviour {
    Action a;
    Action b;
    void Start() { var f = new Foo(); a = () => { f.v++; }; b = a; }
}", "B87PrivateHost");
        Assert.NotNull(uasm);
    }
}
