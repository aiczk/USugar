using System;
using Xunit;

namespace USugar.Tests;

// Phase-A closed-world armor (2026-07-14): the class-ABI perimeter enforced its closed-world assumptions
// asymmetrically — is/cast rejected open-generic families (HandlerBase.EmitTypeCheck) and guarded laundered
// values, while the sibling dispatch/mint sites silently fell through. Four guards, one polarity:
//   1. Virtual dispatch whose receiver/target family involves an open construction site (or a receiver whose
//      static type still carries a type parameter) → compile-time loud reject. Was: exact-symbol assignability
//      silently missed cross-context mints and fell through to a DIRECT CALL TO THE BASE IMPL (silent wrong),
//      and ≥2-target chains keyed on a spec-shared typeobj (cross-spec confusion).
//   2. Virtual dispatch with an EMPTY minted-implementor set on a fully closed receiver → runtime LogError +
//      default (closed-world: no instance can exist, receiver is null → CLR NREs). Was: silent base-impl call.
//   3. EmitVirtualChain typeobj no-match → runtime LogError (was: silent fall-through, dest slot left default).
//   4. TypeObjWrite at a mint site with no registered typeobj → compile-time reject (census-hole sensor;
//      was: silent bundle[0] skip = the GenBoxFactoryIdentity silent-false family, reachable via `new T()`).
public class ClosedWorldGuardTests
{
    [Fact]
    public void VirtualDispatch_OpenGenericFamily_LoudRejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class GBaseVd<T> { public virtual int M() => 1; }
public class GDerVd<T> : GBaseVd<T> { public override int M() => 2; }
public class VdOpen : UdonSharpBehaviour {
    public int r;
    int Make<T>() { GBaseVd<T> g = new GDerVd<T>(); return g.M(); }
    void Start() { r = Make<int>(); }
}", "VdOpen"));
        Assert.Contains("generic method", ex.Message);
    }

    [Fact]
    public void VirtualDispatch_OpenMintClosedReceiver_LoudRejects()
    {
        // The mint is open (inside Make<T>) but the dispatch receiver is fully closed (GBaseVd<int>):
        // exact-symbol assignability cannot see the open mint, so the target set is empty — silently
        // direct-calling the base impl today. Must reject: the family is open-minted.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class GBaseVd<T> { public virtual int M() => 1; }
public class GDerVd<T> : GBaseVd<T> { public override int M() => 2; }
public class VdOpenClosedRecv : UdonSharpBehaviour {
    public int r;
    GBaseVd<T> Make<T>() { return new GDerVd<T>(); }
    void Start() { GBaseVd<int> g = Make<int>(); r = g.M(); }
}", "VdOpenClosedRecv"));
        Assert.Contains("generic method", ex.Message);
    }

    [Fact]
    public void VirtualDispatch_NoMintedImplementor_EmitsRuntimeGuard()
    {
        // BaseNoMint is never minted anywhere: closed-world says no instance can exist, so the receiver
        // must be null (CLR: NRE). The lowering must be LogError + default — never a silent call to the
        // base impl on a null bundle.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class BaseNoMint { public virtual int M() => 1; }
public class VdEmpty : UdonSharpBehaviour {
    BaseNoMint f;
    public int r;
    void Start() { if (f != null) r = f.M(); }
}", "VdEmpty");
        Assert.Contains("UnityEngineDebug.__LogError__SystemObject__SystemVoid", uasm);
    }

    [Fact]
    public void VirtualChain_NoMatch_EmitsRuntimeGuard()
    {
        // A ≥2-target chain must carry a no-match arm: a laundered non-bundle/foreign value whose slot 0
        // matches no typeobj previously fell through every arm silently, returning the dest slot's default.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class BaseCh { public virtual int M() => 0; }
public class DerCh1 : BaseCh { public override int M() => 1; }
public class DerCh2 : BaseCh { public override int M() => 2; }
public class VdChain : UdonSharpBehaviour {
    public int r;
    void Start() { BaseCh a = new DerCh1(); BaseCh b = new DerCh2(); r = a.M() + b.M(); }
}", "VdChain");
        Assert.Contains("UnityEngineDebug.__LogError__SystemObject__SystemVoid", uasm);
    }

    [Fact]
    public void NdimArray_ErasingConversion_LoudRejects()
    {
        // N-R1 checks the extern ARGUMENT's unwrapped static type, so `object o = a;` laundered the
        // bundle past the choke the direct `Debug.Log(a)` form loudly rejects (B82 mirror: contain at
        // the erasure). Cross-behaviour transport is compiler-generated typed member access and the
        // cast-BACK direction (object → T[,]) stays legal.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class NdLaunder : UdonSharpBehaviour {
    public int r;
    void Start() { int[,] a = new int[2,3]; a[0,0] = 4; object o = a; Debug.Log(o); r = 1; }
}", "NdLaunder"));
        Assert.Contains("multi-dimensional", ex.Message);
    }

    [Fact]
    public void NdimArray_ParamsExpansionSmuggle_LoudRejects()
    {
        // N-R1 checked only the params ARRAY argument (static type object[]), so a T[,] element rode the
        // expansion past the extern choke (audit finding, 2026-07-14): 4-arg string.Format has no per-arity
        // extern, so only the params overload applies. Both the per-element N-R1 re-check and the erasure
        // choke must stop it.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class NdSmuggle : UdonSharpBehaviour {
    public string s;
    void Start() { int[,] a = new int[2,2]; s = string.Format(""{0}{1}{2}{3}"", 1, 2, 3, a); }
}", "NdSmuggle"));
        Assert.Contains("multi-dimensional", ex.Message);
    }

    [Fact]
    public void NdimArray_ParamsPerArityExpansionSmuggle_LoudRejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.Udon.Common.Interfaces;
public class NdSmuggle2 : UdonSharpBehaviour {
    void Start() { int[,] a = new int[2,2]; SendCustomNetworkEvent(NetworkEventTarget.All, ""Evt"", a); }
}", "NdSmuggle2"));
        Assert.Contains("multi-dimensional", ex.Message);
    }

    [Fact]
    public void NdimArray_ArrayTypedAlias_LoudRejects()  // CW11
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class NdAlias : UdonSharpBehaviour {
    public int r;
    void Start() { int[,] a = new int[2,10]; System.Array x = a; r = x.Length; }
}", "NdAlias"));
        Assert.Contains("multi-dimensional", ex.Message);
    }

    [Fact]
    public void NdimArray_ExplicitObjectArrayElement_LoudRejects()  // CW12 array-form leg
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class NdArrForm : UdonSharpBehaviour {
    public string s;
    void Start() { int[,] a = new int[2,2]; s = string.Format(""{0}{1}"", new object[] { 1, a }); }
}", "NdArrForm"));
        Assert.Contains("multi-dimensional", ex.Message);
    }

    [Fact]
    public void NdimArray_InterpolationHole_LoudRejects()  // CW14
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class NdInterp : UdonSharpBehaviour {
    public string s;
    void Start() { int[,] a = new int[2,2]; s = $""grid={a}""; }
}", "NdInterp"));
        Assert.Contains("multi-dimensional", ex.Message);
    }

    [Fact]
    public void NdimArray_StringConcatOperand_LoudRejects()  // CW15
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class NdConcat : UdonSharpBehaviour {
    public string s;
    void Start() { int[,] a = new int[2,2]; s = ""grid="" + a; }
}", "NdConcat"));
        Assert.Contains("multi-dimensional", ex.Message);
    }

    [Fact]
    public void NewT_MintWithoutRegisteredTypeObj_LoudRejects()
    {
        // `new T()` monomorphizes to a concrete class the Phase-1 reach census never saw minted (no direct
        // `new NewTOnly()` anywhere), so the typeobj registry has no entry. Writing no bundle[0] would make
        // every downstream `is`/`as`/virtual dispatch silently mis-answer — reject at the mint instead.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class NewTOnly { public int V; }
public class VdNewT : UdonSharpBehaviour {
    public int r;
    T Make<T>() where T : class, new() { return new T(); }
    void Start() { NewTOnly c = Make<NewTOnly>(); r = c.V; }
}", "VdNewT"));
        Assert.Contains("minted", ex.Message);
    }
}
