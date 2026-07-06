using System;
using Xunit;

namespace USugar.Tests;

public class DelegateStorageBoundaryTests
{
    [Fact]
    public void PublicDelegateField_CompoundAssigningClassCapturingLambda_Rejects()
    {
        // B86 ledger: public delegate storage is a cross-program surface, and a closure env can
        // carry a program-local class even when the delegate signature is clean.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class Foo { public int v; }
public class PublicDelegateClassCaptureHost : UdonSharpBehaviour {
    public Action cb;
    void Start() { var f = new Foo(); cb += () => { f.v++; }; }
}", "PublicDelegateClassCaptureHost"));
        Assert.Contains("cross-program field 'cb'", ex.Message);
    }

    [Fact]
    public void PrivateDelegateField_CompoundAssigningClassCapturingLambda_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class Foo { public int v; }
public class PrivateDelegateClassCaptureHost : UdonSharpBehaviour {
    Action cb;
    void Start() { var f = new Foo(); cb += () => { f.v++; }; }
}", "PrivateDelegateClassCaptureHost");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void PublicEvent_CompoundAssigningClassCapturingLambda_Rejects()
    {
        // B86 ledger: event backing storage follows the same public delegate boundary policy.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class Foo { public int v; }
public class PublicEventClassCaptureHost : UdonSharpBehaviour {
    public event Action Tick;
    void Start() { var f = new Foo(); Tick += () => { f.v++; }; }
}", "PublicEventClassCaptureHost"));
        Assert.Contains("public event 'Tick'", ex.Message);
    }

    [Fact]
    public void PublicDelegateField_CopyingUnclassifiedDelegateValue_Rejects()
    {
        // B87 ledger: copying a delegate from another storage location loses the creation-site proof.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class Foo { public int v; }
public class PublicDelegateCopyHost : UdonSharpBehaviour {
    public Action pub;
    Action priv;
    void Start() { var f = new Foo(); priv = () => { f.v++; }; pub = priv; }
}", "PublicDelegateCopyHost"));
        Assert.Contains("must be created directly", ex.Message);
    }

    [Fact]
    public void PublicDelegateField_DirectClassFreeLambda_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class PublicDelegateCleanLambdaHost : UdonSharpBehaviour {
    public Action pub;
    public int n;
    void Start() { int x = 1; pub = () => { n = x; }; }
}", "PublicDelegateCleanLambdaHost");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void PrivateDelegateField_CopyingClassCapturingDelegate_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class Foo { public int v; }
public class PrivateDelegateCopyHost : UdonSharpBehaviour {
    Action a;
    Action b;
    void Start() { var f = new Foo(); a = () => { f.v++; }; b = a; }
}", "PrivateDelegateCopyHost");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void DelegateBundleMint_WritesAbiProvenanceTag()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DelegateAbiTagHost : UdonSharpBehaviour {
    public Action cb;
    void M() { }
    void Start() { cb = M; }
}", "DelegateAbiTagHost");
        Assert.Contains("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray", uasm);
        Assert.True(
            Count(uasm, "SystemObjectArray.__Set__SystemInt32_SystemObject__SystemVoid") >= 5,
            "A delegate ABI mint writes Kind, Target, Method, Addr, and Env slots.");
    }

    static int Count(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
