using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// WaveJoint round-2 regression pins (2026-07-16).
/// A02: `base.ToString()` in a class whose parent IS System.Object never reached the M4b dispatch —
/// the intercept read the receiver's static type (`base` carries object there), so the call fell
/// through to the raw SystemObject extern and printed VM junk instead of the runtime type name. The
/// intercept now resolves the dispatch family from the ENCLOSING class when `base` is object-typed.
/// </summary>
public class WaveJointRound2RegressionTests
{
    // ── A02: base.ToString() bound to object.ToString with a DIRECT object parent ──

    [Fact]
    public void BaseToString_DirectObjectParent_MintsRuntimeTypeNameConstant()
    {
        var (_, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class Wj2B1 { public int v; public override string ToString(){ return base.ToString(); } }
public class Wj2Base : UdonSharpBehaviour {
    public int seed; public string s;
    void Start(){ Wj2B1 x = new Wj2B1(); x.v = seed; s = x.ToString(); }
}", "Wj2Base");
        Assert.Contains(consts, c => c.UdonType == "SystemString" && Equals(c.Value, "Wj2B1"));
    }

    [Fact]
    public void BaseToString_DirectObjectParent_DispatchesDerivedRuntimeName()
    {
        // The inherited override's base.ToString() must print the RUNTIME name (object.ToString reads
        // GetType()) — the dispatch family is the enclosing class, so BOTH concrete names are minted.
        var (_, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class Wj2B2 { public int v; public override string ToString(){ return base.ToString() + ""#"" + v; } }
public class Wj2D2 : Wj2B2 { }
public class Wj2Derived : UdonSharpBehaviour {
    public int seed; public string s;
    void Start(){ Wj2B2 x = new Wj2D2(); x.v = seed; Wj2B2 y = new Wj2B2(); y.v = seed + 1; s = $""{x}|{y}""; }
}", "Wj2Derived");
        Assert.Contains(consts, c => c.UdonType == "SystemString" && Equals(c.Value, "Wj2B2"));
        Assert.Contains(consts, c => c.UdonType == "SystemString" && Equals(c.Value, "Wj2D2"));
    }
}
