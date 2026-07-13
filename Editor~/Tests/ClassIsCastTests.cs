using Xunit;

namespace USugar.Tests;

// CA-v2b-1: is/cast on v1 class hierarchies (typeobj identity). Compile pins; VM value gates in the
// harness (ClassIsCastV2bVmTests / ClassIsCastCharterVmTests).
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
    public void Downcast_And_As_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class BIc2 { public int b; }
public class DIc2 : BIc2 { public int d; }
public class IcT2 : UdonSharpBehaviour { public int result;
  void Start(){ BIc2 x = new DIc2(); ((DIc2)x).d = 5; DIc2 a = x as DIc2; result = (a != null ? a.d : -1) + ((DIc2)x).d; } }", "IcT2");
}
