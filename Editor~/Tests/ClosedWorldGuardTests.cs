using System;
using System.Linq;
using System.Text.RegularExpressions;
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
    public void VirtualProperty_BaseTypedRead_LoudRejects()  // CW1
    {
        // The v2b-2 dispatch chain fires only for MethodKind.Ordinary, so every accessor site binds the
        // receiver's STATIC property symbol: `s.Area` on a base-typed receiver silently ran the base
        // getter (USugar 1, C# 42). Layout-level loud reject; a virtual METHOD wrapper does dispatch.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class ShapeVp { public virtual int Area { get { return 1; } } }
public class CircleVp : ShapeVp { public override int Area { get { return 42; } } }
public class CwVProp : UdonSharpBehaviour {
    public int r;
    void Start() { ShapeVp s = new CircleVp(); r = s.Area; }
}", "CwVProp"));
        Assert.Contains("property", ex.Message);
    }

    [Fact]
    public void VirtualIndexer_BaseTypedRead_LoudRejects()  // CW1 indexer leg
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class IdxVBase { public virtual int this[int i] { get { return 1; } } }
public class IdxVDer : IdxVBase { public override int this[int i] { get { return 42; } } }
public class CwVIdx : UdonSharpBehaviour {
    public int r;
    void Start() { IdxVBase b = new IdxVDer(); r = b[0]; }
}", "CwVIdx"));
        Assert.Contains("indexer", ex.Message);
    }

    [Fact]
    public void ClassDowncast_SiblingReinterpret_EmitsTypeObjGuard()  // CW2
    {
        // A direct `(T)o` cast was an identity passthrough while `is`/`as` ran the typeobj check, so a
        // base-held sibling value reinterpreted the bundle (b.bx read PA's ax slot, silently 42). Design
        // step-4 (Q1 house deviation): is-test ? passthrough : LogError + null.
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class PBase { public int p; }
public class PA : PBase { public int ax; }
public class PB : PBase { public int bx; }
public class CwCast : UdonSharpBehaviour {
    public int result;
    void Start() { PBase o = new PA(); ((PA)o).ax = 42; PB b = (PB)o; result = b != null ? b.bx : -1; }
}", "CwCast");
        Assert.Contains("UnityEngineDebug.__LogError__SystemObject__SystemVoid", uasm);
        Assert.Contains(consts, c => c.Value is string s && s.Contains("InvalidCastException"));
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

    [Fact]
    public void VirtualChain_RefArg_CopiesBackPerArm()  // CW3
    {
        // A ≥2-target polymorphic call staged ref/out args into scratch slots and never ran
        // EmitRefOutCopyBack — `r.M(ref local)` left local at its pre-call value (result=seed where
        // C# gives seed+1/+10), and adding a second minted subclass silently flipped a correct
        // 1-subclass program (the devirt sibling EmitStructInstanceCall copies back). The executed
        // arm must copy the callee's param var back into the argument's storage.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class VrB { public virtual void M(ref int x) { x = x + 1; } }
public class VrD : VrB { public override void M(ref int x) { x = x + 10; } }
public class CwVChainRef : UdonSharpBehaviour {
    public int seed;
    public int result;
    void Start() { VrB r = seed > 0 ? (VrB)new VrD() : new VrB(); int local = seed; r.M(ref local); result = local; }
}", "CwVChainRef");
        // Copy-back starts at the arm's call return — the callee's param var is the COPY source — and
        // the argument's local is rewritten per arm (1 init + 2 arms).
        Assert.True(Regex.Matches(uasm, @"___start__callret_\d+:\s+PUSH, __\d+_x__param").Count >= 2,
            "no per-arm ref/out copy-back after the chain's call returns");
        Assert.True(Regex.Matches(uasm, @"PUSH, __lcl_local_SystemInt32_0\s+COPY").Count >= 3,
            "ref argument's local is never rewritten after the polymorphic call");
    }

    [Fact]
    public void VirtualChain_AliasedRefArgs_LoudRejects()  // CW3 guard leg
    {
        // Same-storage ref/out aliasing must reject at a polymorphic site exactly as the devirt path
        // does ([R4]): each param is an independent heap var, so the callee never observes the alias
        // and the last copy-back silently wins.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class VaB { public virtual void M(ref int x, ref int y) { x = x + 1; } }
public class VaD : VaB { public override void M(ref int x, ref int y) { y = y + 1; } }
public class CwVChainAlias : UdonSharpBehaviour {
    public int seed;
    public int result;
    void Start() { VaB r = seed > 0 ? (VaB)new VaD() : new VaB(); int a = seed; r.M(ref a, ref a); result = a; }
}", "CwVChainAlias"));
        Assert.Contains("same storage", ex.Message);
    }

    [Fact]
    public void ClassCtor_NamedArgs_BindByParameterOrdinal()  // CW4
    {
        // IObjectCreationOperation.Arguments arrives in SOURCE order for named args (the same Roslyn
        // fact behind the w4 invocation fix), and the mint arm staged them positionally: new NCo(b: 2,
        // a: 1) copied 2 into a's param var and 1 into b's — fields silently swapped (21 vs C# 12).
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class NCo { public int A; public int B; public NCo(int a, int b) { A = a; B = b; } }
public class CwCtorNamed : UdonSharpBehaviour {
    public int result;
    void Start() { NCo c = new NCo(b: 2, a: 1); result = c.A * 10 + c.B; }
}", "CwCtorNamed");
        var one = consts.First(c => c.UdonType == "SystemInt32" && Equals(c.Value, 1)).Id;
        var two = consts.First(c => c.UdonType == "SystemInt32" && Equals(c.Value, 2)).Id;
        Assert.Matches($@"PUSH, {Regex.Escape(one)}\s+PUSH, __\d+_a__param\s+COPY", uasm);
        Assert.Matches($@"PUSH, {Regex.Escape(two)}\s+PUSH, __\d+_b__param\s+COPY", uasm);
    }

    [Fact]
    public void ClassCtor_OutParam_CopiesBack()  // CW4
    {
        // `new OCo(out y)` never copied the callee's param var back to y — result read 0 where C#
        // gives 5. The mint arm must run the same by-ordinal copy-back as EmitUserMethodCall.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class OCo { public int V; public OCo(out int x) { x = 5; V = 1; } }
public class CwCtorOut : UdonSharpBehaviour {
    public int result;
    void Start() { int y = 0; OCo c = new OCo(out y); result = y; }
}", "CwCtorOut");
        // Copy-back starts at the ctor's call return (param var as COPY source) and rewrites y (init + copy-back).
        Assert.Matches(@"___start__callret_\d+:\s+PUSH, __\d+_x__param", uasm);
        Assert.True(Regex.Matches(uasm, @"PUSH, __lcl_y_SystemInt32_0\s+COPY").Count >= 2,
            "out argument's local is never rewritten after the ctor call");
    }

    [Fact]
    public void BaseCtorChain_NamedArgs_BindByParameterOrdinal()  // CW4 chain leg
    {
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class BCh { public int A; public int B; public BCh(int a, int b) { A = a; B = b; } }
public class DCh : BCh { public DCh() : base(b: 2, a: 1) { } }
public class CwCtorChain : UdonSharpBehaviour {
    public int result;
    void Start() { DCh d = new DCh(); result = d.A * 10 + d.B; }
}", "CwCtorChain");
        var one = consts.First(c => c.UdonType == "SystemInt32" && Equals(c.Value, 1)).Id;
        var two = consts.First(c => c.UdonType == "SystemInt32" && Equals(c.Value, 2)).Id;
        Assert.Matches($@"PUSH, {Regex.Escape(one)}\s+PUSH, __\d+_a__param\s+COPY", uasm);
        Assert.Matches($@"PUSH, {Regex.Escape(two)}\s+PUSH, __\d+_b__param\s+COPY", uasm);
    }

    [Fact]
    public void StructCtor_NamedArgs_BindByParameterOrdinal()  // CW4 user-struct leg
    {
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public struct SCo { public int A; public int B; public SCo(int a, int b) { A = a; B = b; } }
public class CwStructCtor : UdonSharpBehaviour {
    public int result;
    int Take(SCo s) { return s.A * 10 + s.B; }
    void Start() { result = Take(new SCo(b: 2, a: 1)); }
}", "CwStructCtor");
        var one = consts.First(c => c.UdonType == "SystemInt32" && Equals(c.Value, 1)).Id;
        var two = consts.First(c => c.UdonType == "SystemInt32" && Equals(c.Value, 2)).Id;
        Assert.Matches($@"PUSH, {Regex.Escape(one)}\s+PUSH, __\d+_a__param\s+COPY", uasm);
        Assert.Matches($@"PUSH, {Regex.Escape(two)}\s+PUSH, __\d+_b__param\s+COPY", uasm);
    }

    [Fact]
    public void StructCtorLocalDecl_NamedArgs_BindByParameterOrdinal()  // CW4 local-decl leg
    {
        // The StatementHandler in-place fast arm staged positionally too (a 4th arm the audit's three
        // sat beside); named/reordered or ref/out ctor args now route through the fixed
        // VisitObjectCreation arm instead of the fast arm.
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public struct SLo { public int A; public int B; public SLo(int a, int b) { A = a; B = b; } }
public class CwStructCtorDecl : UdonSharpBehaviour {
    public int result;
    void Start() { SLo s = new SLo(b: 2, a: 1); result = s.A * 10 + s.B; }
}", "CwStructCtorDecl");
        var one = consts.First(c => c.UdonType == "SystemInt32" && Equals(c.Value, 1)).Id;
        var two = consts.First(c => c.UdonType == "SystemInt32" && Equals(c.Value, 2)).Id;
        Assert.Matches($@"PUSH, {Regex.Escape(one)}\s+PUSH, __\d+_a__param\s+COPY", uasm);
        Assert.Matches($@"PUSH, {Regex.Escape(two)}\s+PUSH, __\d+_b__param\s+COPY", uasm);
    }
}
