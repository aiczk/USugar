using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// WaveJoint round-2 regression pins (2026-07-16).
/// A02: `base.ToString()` in a class whose parent IS System.Object never reached the M4b dispatch —
/// the intercept read the receiver's static type (`base` carries object there), so the call fell
/// through to the raw SystemObject extern and printed VM junk instead of the runtime type name. The
/// intercept now resolves the dispatch family from the ENCLOSING class when `base` is object-typed.
/// D10: an object[]-emulated value type (user struct / tuple / anonymous type) ERASED to object
/// laundered past every stringify/cast choke that keys on the static type and silently printed
/// "System.Object[]" — RequireCanEraseProgramLocalPayload gains an aggregate arm (the ndim/class
/// twins' polarity), keeping equality/Equals positions and T? wrapping legal.
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

    // ── D10: aggregate erased to object is a loud reject at the erasure choke ──

    [Fact]
    public void StructErasedToObject_IsRejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Wj2S1 { public int v; public override string ToString(){ return ""s"" + v; } }
public class Wj2Box : UdonSharpBehaviour {
    public int seed; public string s;
    void Start(){ Wj2S1 t; t.v = seed; object o = t; s = """" + o; }
}", "Wj2Box"));
        Assert.Contains("Erasing", ex.Message);
        Assert.Contains("System.Object[]", ex.Message);
    }

    [Fact]
    public void TupleErasedToObject_IsRejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class Wj2TupleBox : UdonSharpBehaviour {
    public int seed; public string s;
    void Start(){ (int, int) t = (seed, seed + 1); object o = t; s = """" + o; }
}", "Wj2TupleBox"));
        Assert.Contains("Erasing", ex.Message);
    }

    [Fact]
    public void StructObjectRoundtrip_IsRejectedAtTheErasure()
    {
        // Category flip (triage t07/t10, was Match): the pure box-and-cast-back roundtrip is contained
        // at the erasure choke like its ndim/class twins — a laundered bundle cannot be told from a
        // real object[] downstream, so the conversion itself is the only sound reject point.
        Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Wj2S3 { public int v; }
public class Wj2Round : UdonSharpBehaviour {
    public int seed; public int r;
    void Start(){ Wj2S3 t; t.v = seed; object o = t; Wj2S3 u = (Wj2S3)o; r = u.v; }
}", "Wj2Round"));
    }

    [Fact]
    public void StructBoxInEqualityPosition_StaysLegal()
    {
        // The equality carve-out (IsProgramLocalEqualityPosition) the class arm grants applies to the
        // aggregate arm too: a box compared with == is a reference comparison of a fresh clone —
        // C#-consistent — and never flows onward. (A struct's own Equals(object) override roots its own
        // slot, not the object/ValueType one, so THAT boxing stays contained.)
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Wj2S4 { public int v; }
public class Wj2Eq : UdonSharpBehaviour {
    public int seed; public bool r;
    void Start(){ Wj2S4 t; t.v = seed; r = (object)t == null; }
}", "Wj2Eq");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void StructToNullableOfSameStruct_StaysLegal()
    {
        // T → T? is a wrap, not an erasure — the static type still names the aggregate.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct Wj2S5 { public int v; }
public class Wj2Nul : UdonSharpBehaviour {
    public int seed; public int r;
    void Start(){ Wj2S5 t; t.v = seed; Wj2S5? n = t; r = n.HasValue ? n.Value.v : -1; }
}", "Wj2Nul");
        Assert.NotNull(uasm);
    }
}
