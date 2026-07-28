using Xunit;

namespace USugar.Tests;

// CA-v2b-1: runtime class tests use typeobj identity. Potentially-failing
// hard casts reject because Udon cannot implement InvalidCastException.
public class ClassIsCastTests
{
    [Fact]
    public void IsTest_ClassHierarchy_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class BIc { public int b; }
public class DIc : BIc { public int d; }
public class IcT1 : UdonSharpBehaviour { public int result;
  void Start(){ BIc x = new DIc(); result = (x is DIc ? 1 : 0) + (x is BIc ? 2 : 0); } }", "IcT1");

    [Fact]
    public void HardDowncast_Rejects()
    {
        var ex = Assert.Throws<System.NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"using UdonSharp;
public class BIc2 { public int b; }
public class DIc2 : BIc2 { public int d; }
public class IcT2 : UdonSharpBehaviour { public int result;
  void Start(){ BIc2 x = new DIc2(); result = ((DIc2)x).d; } }", "IcT2"));
        Assert.Contains("InvalidCastException", ex.Message);
    }

    [Fact]
    public void AsUpcastAndStaticNull_Compile()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class BIc3 { public int b; }
public class DIc3 : BIc3 { public int d; }
public class IcT3 : UdonSharpBehaviour { public int result;
  void Start(){ BIc3 x = (BIc3)new DIc3(); DIc3 a = x as DIc3; DIc3 n = (DIc3)null; result = a != null && n == null ? 1 : 0; } }", "IcT3");
}
