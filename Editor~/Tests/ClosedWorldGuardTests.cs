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
    public void VirtualDispatch_OpenGenericFamily_UsesClosedSpec()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class GBaseVd<T> { public virtual int M() => 1; }
public class GDerVd<T> : GBaseVd<T> { public override int M() => 2; }
public class VdOpen : UdonSharpBehaviour {
    public int r;
    int Make<T>() { GBaseVd<T> g = new GDerVd<T>(); return g.M(); }
    void Start() { r = Make<int>(); }
}", "VdOpen");
        Assert.Contains("__typeobj_GDerVd_Int32", uasm);
        Assert.Matches(@"__\d+_M", uasm);
    }

    [Fact]
    public void VirtualDispatch_OpenMintClosedReceiver_UsesClosedSpec()
    {
        // The mint syntax is open, but the census observes Make<int> and registers GDerVd<int>, so the
        // fully closed receiver resolves the derived implementation without an open-family fallback.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class GBaseVd<T> { public virtual int M() => 1; }
public class GDerVd<T> : GBaseVd<T> { public override int M() => 2; }
public class VdOpenClosedRecv : UdonSharpBehaviour {
    public int r;
    GBaseVd<T> Make<T>() { return new GDerVd<T>(); }
    void Start() { GBaseVd<int> g = Make<int>(); r = g.M(); }
}", "VdOpenClosedRecv");
        Assert.Contains("__typeobj_GDerVd_Int32", uasm);
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
    public void VirtualProperty_BaseTypedRead_DispatchesOverride()  // CW1 lift (flipped 1c72d4b pin)
    {
        // Pre-lift this loud-rejected at layout: every accessor site bound the receiver's STATIC
        // property symbol, so `s.Area` on a base-typed receiver silently ran the base getter
        // (USugar 1, C# 42). Now the accessor rides the v2b-2 typeobj chain: both minted classes'
        // getters are emitted, dispatched through a typeobj-ReferenceEquals chain, and the override's
        // const 42 is reachable through its arm.
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class ShapeVp { public virtual int Area { get { return 1; } } }
public class CircleVp : ShapeVp { public override int Area { get { return 42; } } }
public class CwVProp : UdonSharpBehaviour {
    public int r;
    void Start() { ShapeVp s = new CircleVp(); ShapeVp t = new ShapeVp(); r = s.Area + t.Area; }
}", "CwVProp");
        Assert.Contains("__typeobj_ShapeVp", uasm);
        Assert.Contains("__typeobj_CircleVp", uasm);
        Assert.Contains(consts, c => c.UdonType == "SystemInt32" && Equals(c.Value, 42)); // override arm
        Assert.Contains(consts, c => c.UdonType == "SystemInt32" && Equals(c.Value, 1));  // base arm
        Assert.True(Regex.Matches(uasm, @"__\d+_get_Area").Count >= 2,
            "both slot impls' getters must be emitted and dispatched");
        // The chain carries the no-match armor (null/laundered receiver → LogError, never silent).
        Assert.Contains("UnityEngineDebug.__LogError__SystemObject__SystemVoid", uasm);
    }

    [Fact]
    public void VirtualIndexer_BaseTypedRead_DispatchesOverride()  // CW1 lift (flipped 1c72d4b pin)
    {
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class IdxVBase { public virtual int this[int i] { get { return 1; } } }
public class IdxVDer : IdxVBase { public override int this[int i] { get { return 42; } } }
public class CwVIdx : UdonSharpBehaviour {
    public int r;
    void Start() { IdxVBase b = new IdxVDer(); IdxVBase c = new IdxVBase(); r = b[0] + c[0]; }
}", "CwVIdx");
        Assert.Contains("__typeobj_IdxVBase", uasm);
        Assert.Contains("__typeobj_IdxVDer", uasm);
        Assert.Contains(consts, c => c.UdonType == "SystemInt32" && Equals(c.Value, 42));
        Assert.True(Regex.Matches(uasm, @"__\d+_get_Item").Count >= 2,
            "both slot impls' indexer getters must be emitted and dispatched");
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
    public void NewT_MintRegistersClosedTypeObj()
    {
        // `new T()` contributes its call-site concrete class to the closed census even when no direct
        // `new NewTOnly()` occurs, so the mint receives a valid bundle[0] typeobj.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NewTOnly { public int V; }
public class VdNewT : UdonSharpBehaviour {
    public int r;
    T Make<T>() where T : class, new() { return new T(); }
    void Start() { NewTOnly c = Make<NewTOnly>(); r = c.V; }
}", "VdNewT");
        Assert.Contains("__typeobj_NewTOnly", uasm);
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
    public void GenericReceiverDispatch_SpillsRecursionFrame()  // CW5
    {
        // Emission resolves the receiver through the monomorphization map, so `n.Visit(...)` on a
        // generic-T receiver IS a typeobj dispatch — but the recursion enumerator pattern-matched
        // INamedTypeSymbol only, so the T-receiver site yielded ZERO virtual edges: the Go ↔ Visit
        // cycle was missed and Go's locals never spilled around the dispatch (VM-proven 37 vs CLR 67,
        // `keep` clobbered on re-entry; the named-receiver twin returns 67). The def-keyed walk has no
        // type-param map, so it must over-approximate: every minted impl of the slot (over-spill only).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NodG { public virtual int Visit(int d) { return 0; } }
public class LfG : NodG { public override int Visit(int d) { if (d <= 0) return 7; return HelpG.Go(this, d); } }
public static class HelpG {
    public static int Go<T>(T n, int d) where T : NodG { int keep = d * 10; int r = n.Visit(d - 1); return keep + r; }
}
public class CwGenRecv : UdonSharpBehaviour {
    public int result;
    void Start() { result = HelpG.Go(new LfG(), 3); }
}", "CwGenRecv");
        Assert.Contains("__recurStack", uasm);
    }

    [Fact]
    public void ClassProperty_CompoundAssign_CallsSetter()  // CW6
    {
        // EmitWriteBack's aggregate property arms gated on IsAggregateValue (deliberately FALSE for a
        // class), so `c.P += 1` on a class receiver fell through to the generic extern arm and minted
        // a nonexistent SystemObjectArray.__set_P__ extern (loud validator crash on legal C#, with an
        // extern-registry diagnosis instead of the collector-drift one). The compound write-back must
        // route through the same setter-call path the simple set (PreparePropertySet) already takes.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CpBox { int v; public int P { get { return v; } set { v = value; } } }
public class CwClassPropCompound : UdonSharpBehaviour {
    public int result;
    void Start() { CpBox c = new CpBox(); c.P += 1; result = c.P; }
}", "CwClassPropCompound");
        Assert.DoesNotContain("SystemObjectArray.__set_", uasm);
        Assert.Matches(@"__\d+_set_P", uasm);
    }

    [Fact]
    public void ClassAutoProperty_CompoundAssign_WritesLayoutSlot()  // CW6 auto-prop leg
    {
        // The auto-prop slot-write sub-arm sits inside the same IsAggregateValue-gated case, so
        // `c.AutoP += 1` broke identically (SystemObjectArray.__set_AutoP__ extern).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CaBox { public int AutoP { get; set; } }
public class CwClassAutoCompound : UdonSharpBehaviour {
    public int result;
    void Start() { CaBox c = new CaBox(); c.AutoP += 1; result = c.AutoP; }
}", "CwClassAutoCompound");
        Assert.DoesNotContain("SystemObjectArray.__set_", uasm);
    }

    [Fact]
    public void ClassProperty_IncrementDecrement_CallsSetter()  // CW6 inc-dec leg
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CiBox { int v; public int P { get { return v; } set { v = value; } } }
public class CwClassPropIncDec : UdonSharpBehaviour {
    public int result;
    void Start() { CiBox c = new CiBox(); c.P++; result = c.P; }
}", "CwClassPropIncDec");
        Assert.DoesNotContain("SystemObjectArray.__set_", uasm);
        Assert.Matches(@"__\d+_set_P", uasm);
    }

    [Fact]
    public void CrossBehaviourCallArg_ClassCapturingLambda_LoudRejects()  // CW7
    {
        // CrossCallArgPairs checked only the STATIC type (ContainsUserClassType sees a clean Action
        // signature), so the exact value the store twin `o.Handler = ...` loudly rejects crossed via
        // the argument syntax and SPV'd the class-carrying env bundle into the foreign program —
        // then laundered back into the guarded field via the callee's unclassifiable param provenance
        // (CW23). The argument IS a store: the pair becomes a SetProgramVariable into the callee's
        // param var, so it must run the same value-classification ladder as the store surfaces.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CwFoo7 { public int v; }
public class CwReg7 : UdonSharpBehaviour {
    public Action Handler;
    public void Register(Action h) { Handler = h; }
}
public class CwArgEscape : UdonSharpBehaviour {
    public CwReg7 o;
    void Start() { var f = new CwFoo7(); o.Register(() => { f.v++; }); }
}", "CwArgEscape"));
        Assert.Contains("cross-program argument 'h'", ex.Message);
    }

    [Fact]
    public void CrossBehaviourCallArg_ClassReceiverMethodGroup_LoudRejects()  // CW7 receiver-bridge leg
    {
        // MG autowrap carries the class receiver in DelegateAbi.Env (the FATAL-amendment shape the
        // scalar store surface rejects) — the argument surface must not hand it a bypass.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CwFoo7b { public int v; public void M() { v++; } }
public class CwReg7b : UdonSharpBehaviour {
    public Action Handler;
    public void Register(Action h) { Handler = h; }
}
public class CwArgEscapeMg : UdonSharpBehaviour {
    public CwReg7b o;
    void Start() { var f = new CwFoo7b(); o.Register(f.M); }
}", "CwArgEscapeMg"));
        Assert.Contains("cross-program argument 'h'", ex.Message);
    }

    [Fact]
    public void InterfaceCallArg_ClassCapturingLambda_LoudRejects()  // CW7/CW23 interface-dispatch leg
    {
        // EmitInterfaceCall shares the CrossCallArgPairs choke — the interface flavor of the same
        // cross-program SPV store must reject with the same polarity.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public interface ICwReg { void Register(Action h); }
public class CwFoo7i { public int v; }
public class CwReg7i : UdonSharpBehaviour, ICwReg {
    public Action Handler;
    public void Register(Action h) { Handler = h; }
}
public class CwArgEscapeIf : UdonSharpBehaviour {
    public ICwReg o;
    void Start() { var f = new CwFoo7i(); o.Register(() => { f.v++; }); }
}", "CwArgEscapeIf"));
        Assert.Contains("cross-program argument 'h'", ex.Message);
    }

    [Fact]
    public void CrossBehaviourCallArg_DirectClassFreeLambda_Compiles()  // CW7/CW23 accept control
    {
        // The designed feature stays open: a direct capture-safe lambda (and null) pass the same
        // ladder legs the store surfaces accept — dispatch executes in the minter's program.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CwReg7c : UdonSharpBehaviour {
    public Action Handler;
    public void Register(Action h) { Handler = h; }
}
public class CwArgClean : UdonSharpBehaviour {
    public CwReg7c o;
    void Start() { int x = 1; o.Register(() => { x++; }); o.Register(null); }
}", "CwArgClean");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void PublicDelegateArrayField_ElementWrite_LoudRejects()  // CW8
    {
        // The array-element assignment arm never reported to the boundary checker, and the
        // field-target test bailed on IArrayTypeSymbol — `handlers[0] = classCapturingLambda` into
        // a public Action[] (declared as an ordinary EXPORTED var: DeclareDelegateField intercepts
        // only direct delegate types) compiled clean where the scalar twin rejects.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CwFoo8 { public int v; }
public class CwDlgArrElem : UdonSharpBehaviour {
    public Action[] handlers;
    void Init() { handlers = new Action[1]; }
    void Start() { Init(); var f = new CwFoo8(); handlers[0] = () => { f.v++; }; }
}", "CwDlgArrElem"));
        Assert.Contains("cross-program array field 'handlers'", ex.Message);
    }

    [Fact]
    public void PublicDelegateArrayField_WholeArrayAssign_LoudRejects()  // CW8 whole-array leg
    {
        // The whole-array assign DID report, but IsCrossProgramDelegateFieldTarget returned false
        // for Action[] (`is not INamedTypeSymbol` bail) and the switch silently ate it.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CwFoo8b { public int v; }
public class CwDlgArrWhole : UdonSharpBehaviour {
    public Action[] handlers;
    void Start() { var f = new CwFoo8b(); handlers = new Action[] { () => { f.v++; } }; }
}", "CwDlgArrWhole"));
        Assert.Contains("cross-program field 'handlers'", ex.Message);
    }

    [Fact]
    public void CrossBehaviourDelegateArrayField_Assign_LoudRejects()  // CW8 cross-behaviour leg
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CwFoo8c { public int v; }
public class CwArrOther : UdonSharpBehaviour { public Action[] handlers; }
public class CwDlgArrCross : UdonSharpBehaviour {
    public CwArrOther o;
    void Start() { var f = new CwFoo8c(); o.handlers = new Action[] { () => { f.v++; } }; }
}", "CwDlgArrCross"));
        Assert.Contains("cross-program field 'handlers'", ex.Message);
    }

    [Fact]
    public void PrivateDelegateArrayField_SameValues_Compiles()  // CW8 accept control
    {
        // A private delegate array is never exported — program-local storage stays legal for the
        // identical class-capturing values (the scalar private twin is pinned the same way).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class CwFoo8p { public int v; }
public class CwDlgArrPriv : UdonSharpBehaviour {
    Action[] handlers;
    void Start() { var f = new CwFoo8p(); handlers = new Action[] { () => { f.v++; } }; handlers[0] = () => { f.v++; }; }
}", "CwDlgArrPriv");
        Assert.NotNull(uasm);
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

    const string MulticastSrc = @"
using UdonSharp;
using System;
public class CwMulti9 : UdonSharpBehaviour {
    Action d;
    public int n;
    void A() { n += 1; }
    void B() { n += 10; }
    void Start() { d = A; d += B; d(); }
}";

    [Fact]
    public void MulticastFanout_NullInvocationList_LogsAndReturnsDefault()  // CW9/CW21
    {
        // __dlg_fanout_{sig} is an EXPORTED entry: a direct SendCustomEvent (foreign program, or
        // by-name) reaches it with the env global at its initial null, and the unguarded get_Length
        // faulted the VM — the only Env-payload consumer of four with zero armor, despite its own
        // doc comment (and the 2026-07-03 design §1.2) promising "empty/null list → default".
        // Mirror the closure bridge's env-null arm: LogError + conv-ret default.
        var (uasm, consts) = TestHelper.CompileWithConsts(MulticastSrc, "CwMulti9");
        Assert.Contains(consts, c => c.Value is string s && s.Contains("missing invocation list"));
        // The null test must sit BEFORE the length read inside the fan-out body.
        var fanout = uasm.Substring(uasm.IndexOf(".export __dlg_fanout_", StringComparison.Ordinal));
        var nullCmp = fanout.IndexOf("SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean", StringComparison.Ordinal);
        var lenRead = fanout.IndexOf("SystemArray.__get_Length__SystemInt32", StringComparison.Ordinal);
        Assert.True(nullCmp >= 0 && nullCmp < lenRead,
            "fan-out reads the invocation list length before any null guard");
    }

    [Fact]
    public void MulticastFlattenOperand_TagAndNullListGuards()  // CW10 flatten leg + CW9/CW21 corrupted-list leg
    {
        // The combine/remove flatten read Method (slot 2) with neither tag nor null-list guard: an
        // untagged foreign object[] `+=` operand faulted the slot read, and a fan-out-named bundle
        // whose Env is null faulted get_Length. Untagged / null-list operands must fall back to the
        // single-cast wrap — their own dispatch LogErrors downstream.
        var (uasm, consts) = TestHelper.CompileWithConsts(MulticastSrc, "CwMulti9");
        var kindTag = consts.First(c => Equals(c.Value, DelegateAbi.KindTag)).Id;
        var combineStart = uasm.IndexOf("____dlg_combine_", StringComparison.Ordinal);
        var removeStart = uasm.IndexOf("____dlg_remove_", StringComparison.Ordinal);
        var combine = uasm.Substring(combineStart, removeStart - combineStart);
        // 2 flatten tag guards + the re-minted multicast bundle's own tag write.
        Assert.Equal(3, Regex.Matches(combine, $@"PUSH, {Regex.Escape(kindTag)}\b").Count);
        // One env-null inequality guard per flatten arm.
        Assert.Equal(2, Regex.Matches(combine,
            @"SystemObject\.__op_Inequality__SystemObject_SystemObject__SystemBoolean").Count);
    }

    [Fact]
    public void DelegateEquality_UntaggedOperand_ReferenceEqualityFallback()  // CW10
    {
        // CompareDelegates read slots 1/2/4 behind only null checks while its dispatch twin reads
        // them only inside IsTaggedBundle: `d == handler` on a foreign-written untagged object[]
        // faulted the VM (OOB __Get / string-equality cast) where invoking the same value LogErrors
        // and continues. Both operands now tag-check first; either untagged → plain reference
        // equality of the two bundle refs (C#-consistent for the identical laundered ref).
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
using System;
public class CwDlgEq10 : UdonSharpBehaviour {
    Action a; Action b;
    public bool eq;
    void M1() { }
    void Start() { a = M1; b = M1; eq = (a == b); }
}", "CwDlgEq10");
        var kindTag = consts.First(c => Equals(c.Value, DelegateAbi.KindTag)).Id;
        // 2 mints write the tag; the equality's both-non-null leg reads it on BOTH operands.
        Assert.Equal(4, Regex.Matches(uasm, $@"PUSH, {Regex.Escape(kindTag)}\b").Count);
    }

    [Fact]
    public void NullableToString_NullSafeWithEmptyFallback()  // CW17
    {
        // Nullable<T> OVERRIDES ToString, so the bound target's ContainingType is Nullable<int> —
        // escaping the [V3]/B59/B60 Object-method re-routes (keyed on SpecialType) — and the erased
        // SystemObject INSTANCE extern ran on the raw box: a null int? NRE'd inside the extern and
        // halted the program where C# defines "". The lowering must gate on HasValue with an
        // empty-string fallback.
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class CwNblToStr : UdonSharpBehaviour {
    public int seed;
    public string s;
    void Start() { int? x = seed > 0 ? (int?)seed : null; s = x.ToString(); }
}", "CwNblToStr");
        Assert.Contains(consts, c => c.Value is string str && str.Length == 0);
        var nullCmp = uasm.IndexOf("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean", StringComparison.Ordinal);
        var toStr = uasm.IndexOf("SystemObject.__ToString__SystemString", StringComparison.Ordinal);
        Assert.True(nullCmp >= 0 && nullCmp < toStr,
            "ToString extern runs on the raw box before any null guard");
    }

    [Fact]
    public void NullableEquals_NullSafeStaticForm()  // CW17
    {
        // x.Equals(5) emitted the INSTANCE SystemObject.__Equals extern on the raw box (NRE on a
        // null int?, where C# defines false); Nullable<T>.Equals(object) is exactly the null-safe
        // STATIC object.Equals(box, arg): both-null true, one-null false, else boxed value equality.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CwNblEq : UdonSharpBehaviour {
    public int seed;
    public bool eq;
    void Start() { int? x = seed > 0 ? (int?)seed : null; eq = x.Equals(5); }
}", "CwNblEq");
        Assert.Contains("SystemObject.__Equals__SystemObject_SystemObject__SystemBoolean", uasm);
        Assert.DoesNotContain("SystemObject.__Equals__SystemObject__SystemBoolean", uasm);
    }

    [Fact]
    public void NullableGetHashCode_NullSafeWithZeroFallback()  // CW17
    {
        // Same fall-through as ToString: the instance GetHashCode extern NRE'd on the null
        // representation where C# defines 0. HasValue-gated underlying hash, 0 fallback.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CwNblHash : UdonSharpBehaviour {
    public int seed;
    public int r;
    void Start() { int? x = seed > 0 ? (int?)seed : null; r = x.GetHashCode(); }
}", "CwNblHash");
        var nullCmp = uasm.IndexOf("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean", StringComparison.Ordinal);
        var hash = uasm.IndexOf("SystemObject.__GetHashCode__SystemInt32", StringComparison.Ordinal);
        Assert.True(nullCmp >= 0 && nullCmp < hash,
            "GetHashCode extern runs on the raw box before any null guard");
    }

    [Fact]
    public void NullableUserEnumToString_UsesNameHelper()  // CW17 user-enum leg
    {
        // A user enum prints its member NAME in C# (the B67 rule); the nullable receiver must route
        // the present value through the same synthesized helper — the boxed number's ToString would
        // silently print the underlying integer.
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public enum EcName : byte { Alpha, Beta }
public class CwNblEnumStr : UdonSharpBehaviour {
    public int seed;
    public string s;
    void Start() { EcName? e = seed > 0 ? (EcName?)EcName.Beta : null; s = e.ToString(); }
}", "CwNblEnumStr");
        Assert.Contains(consts, c => c.Value is string str && str == "Beta");
        Assert.Contains(consts, c => c.Value is string str && str.Length == 0);
        Assert.NotNull(uasm);
    }

    [Fact]
    public void NullableAggregate_ObjectMethod_LoudRejects()  // CW17 aggregate leg
    {
        // An aggregate underlying boxes as its object[] bundle: the SystemObject extern would print/
        // hash/compare the ARRAY REFERENCE, not the value (C#: the struct's own semantics) — loud
        // reject, mirroring the bare user-struct receiver's object-method polarity.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class CwNblAgg : UdonSharpBehaviour {
    public string s;
    void Start() { (int, int)? t = (1, 2); s = t.ToString(); }
}", "CwNblAgg"));
        Assert.Contains("HasValue", ex.Message);
    }

    [Fact]
    public void NullableEnumConversion_NarrowsToUnderlyingTag()  // CW18 producer leg
    {
        // (Ec?)intExpr fell through BOTH lifted numeric arms (IsNumericType excludes enums) to the
        // identity passthrough, minting a plain-int-tagged box inside a byte-underlying Ec? — the
        // drifted box every strict-typed accessor read HeapTypeMismatch-faults on (VM-proven). A
        // user enum is STORED as its bare underlying tag, so the mint must narrow+rebox exactly
        // like (byte?)intExpr does.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EcCv : byte { A, B }
public class CwNblEnumCv : UdonSharpBehaviour {
    public int seed;
    public int r;
    void Start() { EcCv? e = (EcCv?)seed; r = e.HasValue ? 1 : 0; }
}", "CwNblEnumCv");
        Assert.Contains("SystemConvert.__ToByte__SystemInt32__SystemByte", uasm);
    }

    [Fact]
    public void NullableEnumAccessors_TolerateDriftedBoxTag()  // CW18
    {
        // .Value / GetValueOrDefault / ?? copied the raw box into a strict underlying-typed slot, so
        // the small-underlying-enum tag drift the lifted-operator/pattern consumers already tolerate
        // (PromoteBoxedToInt32) faulted at exactly these three accessors while `e == Ec.B` on the
        // SAME box passed. Mirror the tolerance: promote via ToInt32(SystemObject), then narrow back
        // to the underlying tag.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EcAc : byte { A, B }
public class CwNblEnumAcc : UdonSharpBehaviour {
    public int seed;
    public int r;
    void Start() {
        EcAc? e = (EcAc?)seed;
        int a = (int)e.Value;
        EcAc k = e ?? EcAc.A;
        int c = (int)e.GetValueOrDefault();
        r = a + (int)k + c;
    }
}", "CwNblEnumAcc");
        Assert.True(Regex.Matches(uasm, @"SystemConvert\.__ToInt32__SystemObject__SystemInt32").Count >= 3,
            "value accessors copy the raw box without the sibling consumers' tag tolerance");
    }

    [Fact]
    public void NullablePattern_NotConstant_SeedsNullMatchTrue()  // CW16
    {
        // NullableAbi.EmitPatternCheck preset matchSlot to FALSE and answered every non-`is null`
        // shape only inside the HasValue gate, so shapes whose C# semantics MATCH null silently
        // flipped: `((int?)null) is not 5` is TRUE in C#, USugar answered false. The seed must be
        // the pattern's statically-computed null answer (not: !inner; or: either; and: both).
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class CwNblNot : UdonSharpBehaviour {
    public int seed;
    public bool r;
    void Start() { int? x = seed > 0 ? (int?)seed : null; r = x is not 5; }
}", "CwNblNot");
        Assert.Contains(consts, c => c.UdonType == "SystemBoolean" && Equals(c.Value, true));
        var trueId = consts.First(c => c.UdonType == "SystemBoolean" && Equals(c.Value, true)).Id;
        // The TRUE seed is written before the HasValue gate's null compare — the null path keeps it.
        var seedWrite = Regex.Match(uasm, $@"PUSH, {Regex.Escape(trueId)}\b");
        var nullCmp = uasm.IndexOf("SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean", StringComparison.Ordinal);
        Assert.True(seedWrite.Success && seedWrite.Index < nullCmp,
            "null-matching pattern seeds FALSE — a null scrutinee answers false where C# says true");
    }

    [Fact]
    public void NullablePattern_OrNull_SeedsNullMatchTrue()  // CW16 or-combinator leg
    {
        // `x is null or 7` wraps the null constant in a binary pattern, so the bare-`is null`
        // special case never fired and the null scrutinee fell to the false seed (C#: true).
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class CwNblOrNull : UdonSharpBehaviour {
    public int seed;
    public bool r;
    void Start() { int? x = seed > 0 ? (int?)seed : null; r = x is null or 7; }
}", "CwNblOrNull");
        Assert.Contains(consts, c => c.UdonType == "SystemBoolean" && Equals(c.Value, true));
        Assert.NotNull(uasm);
    }

    [Fact]
    public void NullablePattern_VarPattern_BindsUngated()  // CW16 var leg
    {
        // `x is var y` matches ANY value including null (C#: true, y = null) — the HasValue-gated
        // lowering answered false on null and left y's binding stale. A top-level var/discard runs
        // UNGATED: no HasValue gate at all, unconditional bind of the raw (possibly null) box.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CwNblVar : UdonSharpBehaviour {
    public int seed;
    public bool r;
    void Start() { int? x = seed > 0 ? (int?)seed : null; r = x is var y; }
}", "CwNblVar");
        Assert.DoesNotContain("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean", uasm);
    }

    [Fact]
    public void NullableSwitch_PatternCaseNot_SeedsNullMatchTrue()  // CW16 switch-statement leg
    {
        // The same false seed was reachable from a switch STATEMENT's pattern clause
        // (SwitchHandler → EmitPatternCheck): `case not 5:` must take a null scrutinee.
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class CwNblSwNot : UdonSharpBehaviour {
    public int seed;
    public int r;
    void Start() { int? x = seed > 0 ? (int?)seed : null; switch (x) { case not 5: r = 1; break; default: r = 2; break; } }
}", "CwNblSwNot");
        Assert.Contains(consts, c => c.UdonType == "SystemBoolean" && Equals(c.Value, true));
        Assert.NotNull(uasm);
    }

    [Fact]
    public void NullableSwitch_SingleValueClause_ComparesUnderlying()  // CW19
    {
        // The single-value clause set eqType = GetUdonType(Nullable<T>) = SystemObject and compared
        // the raw scrutinee box against a boxed label const — but SystemObject.__op_Equality is
        // REFERENCE equality (runtime differential harness), so EVERY non-null constant case on ANY
        // nullable scrutinee silently fell to default while the pattern twin `x is 5` answered true.
        // Mirror the pattern clause: HasValue gate + underlying-typed equality; `case null:` keeps
        // the object null check.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CwNblSwitch : UdonSharpBehaviour {
    public int seed;
    public int r;
    void Start() { int? x = seed > 0 ? (int?)seed : null; switch (x) { case 5: r = 1; break; case null: r = 2; break; default: r = 3; break; } }
}", "CwNblSwitch");
        Assert.Contains("SystemInt32.__op_Equality__SystemInt32_SystemInt32__SystemBoolean", uasm);
        // The HasValue gate (IsNull + negation) must guard the typed compare.
        Assert.Contains("SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean", uasm);
    }

    [Fact]
    public void NullableSwitch_SmallUnderlying_PromotesBothSides()  // CW19 small-int leg
    {
        // A small-int underlying additionally needs the sibling pattern clause's box-tag tolerance:
        // promote scrutinee AND label const to int32 (ToInt32(SystemObject)) before the typed extern,
        // exactly like the constant-pattern arm — a strict SystemByte equality would InvalidCast on
        // the plain-int tag the ABI admits.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CwNblSwByte : UdonSharpBehaviour {
    public int seed;
    public int r;
    void Start() { byte? b = (byte?)seed; switch (b) { case 5: r = 1; break; default: r = 2; break; } }
}", "CwNblSwByte");
        Assert.True(Regex.Matches(uasm, @"SystemConvert\.__ToInt32__SystemObject__SystemInt32").Count >= 2,
            "nullable switch label compare skips the pattern arm's two-sided int32 promotion");
        Assert.Contains("SystemInt32.__op_Equality__SystemInt32_SystemInt32__SystemBoolean", uasm);
    }

    [Fact]
    public void HardCast_ObjectToNullable_LoudRejects()  // CW20
    {
        // `(byte?)o` from object matched no VisitConversion arm and fell to the identity passthrough
        // with no unbox check, minting a drifted-tag box (C#: InvalidCastException) that mis-compares
        // on the tolerant lifted paths and faults on the strict accessors — while the SAME shape as
        // `o as byte?` loud-rejects through the EmitTypeCheck choke. Mirror the as-form's polarity.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class CwUnboxNbl : UdonSharpBehaviour {
    public int seed;
    public int r;
    void Start() { object o = seed; byte? b = (byte?)o; r = b == 5 ? 1 : 0; }
}", "CwUnboxNbl"));
        Assert.Contains("unbox", ex.Message);
    }

    [Fact]
    public void HardCast_StaticNullToNullable_Compiles()  // CW20 accept control
    {
        // C#: unboxing null into T? is legal and yields null — a statically-null operand stays a
        // passthrough (same null carve-out as the delegate and class cast arms).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CwUnboxNblNull : UdonSharpBehaviour {
    public int r;
    void Start() { int? x = (int?)(object)null; r = x.HasValue ? 1 : 0; }
}", "CwUnboxNblNull");
        Assert.NotNull(uasm);
    }

    // ── CW22: cross-behaviour property/indexer/interface-accessor surfaces skipped the payload reject ──

    [Fact]
    public void CrossBehaviourProperty_ClassPayloadSet_LoudRejects()  // CW22
    {
        // The field twin `o.node = n` loud-rejects through RequireCanWriteCrossBehaviourField, but
        // PreparePropertySet's cross arms SPV'd the same bundle with no payload test — the foreign
        // program's is/cast/virtual dispatch then compares bundle[0] against ITS OWN typeobj
        // instances (silent base-impl call / false `is`). Same choke, property syntax.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class CwNode22 { public int v; }
public class CwHolder22 : UdonSharpBehaviour {
    public CwNode22 NodeProp { get; set; }
}
public class CwPropEscape : UdonSharpBehaviour {
    public CwHolder22 o;
    void Start() { var n = new CwNode22(); o.NodeProp = n; }
}", "CwPropEscape"));
        Assert.Contains("property 'NodeProp'", ex.Message);
    }

    [Fact]
    public void CrossBehaviourProperty_ClassPayloadGet_LoudRejects()  // CW22 read leg
    {
        // The read direction of the same surface: the field twin runs
        // RequireCanReadCrossBehaviourField, the property GET arm ran nothing.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class CwNode22g { public int v; }
public class CwHolder22g : UdonSharpBehaviour {
    public CwNode22g NodeProp { get; set; }
}
public class CwPropRead : UdonSharpBehaviour {
    public CwHolder22g o;
    public int r;
    void Start() { CwNode22g n = o.NodeProp; r = n != null ? 1 : 0; }
}", "CwPropRead"));
        Assert.Contains("property 'NodeProp'", ex.Message);
    }

    [Fact]
    public void CrossBehaviourIndexer_ClassIndexArg_LoudRejects()  // CW22 indexer leg
    {
        // EmitCrossIndexerCall SPVs every index arg (and the setter value) into the foreign
        // program — the same transport CrossCallArgPairs fences per-arg for method calls.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class CwNode22i { public int v; }
public class CwIdxHost22 : UdonSharpBehaviour {
    public int this[CwNode22i k] { get { return 1; } set { } }
}
public class CwIdxEscape : UdonSharpBehaviour {
    public CwIdxHost22 o;
    void Start() { var n = new CwNode22i(); o[n] = 5; }
}", "CwIdxEscape"));
        Assert.Contains("cannot cross a program boundary", ex.Message);
    }

    [Fact]
    public void InterfaceProperty_ClassPayloadSet_LoudRejects()  // CW22 interface-accessor leg
    {
        // The interface set arm SPVs the value straight into whichever behaviour implements the
        // interface — same boundary, one dispatch layer over.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class CwNode22f { public int v; }
public interface ICwProp22 { CwNode22f NodeProp { get; set; } }
public class CwIfaceHost22 : UdonSharpBehaviour, ICwProp22 {
    public CwNode22f NodeProp { get; set; }
}
public class CwIfacePropEscape : UdonSharpBehaviour {
    public ICwProp22 o;
    void Start() { var n = new CwNode22f(); o.NodeProp = n; }
}", "CwIfacePropEscape"));
        Assert.Contains("property 'NodeProp'", ex.Message);
    }

    [Fact]
    public void CrossBehaviourProperty_ScalarAndSdkTyped_Compiles()  // CW22 accept control
    {
        // The designed cross-property feature stays open: scalar and SDK-typed values carry no
        // program-local bundle, so set/get through another behaviour's property stays legal.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class CwHolder22c : UdonSharpBehaviour {
    public int Count { get; set; }
    public GameObject Go { get; set; }
}
public class CwPropClean : UdonSharpBehaviour {
    public CwHolder22c o;
    public int r;
    void Start() { o.Count = 5; o.Go = gameObject; r = o.Count; }
}", "CwPropClean");
        Assert.NotNull(uasm);
    }

    // A source-element read whose bundle is deep-cloned before any further extern runs: the element
    // __Get__ is followed (PUSH/COPY only, same block) by a fresh-bundle allocation. The alias bugs
    // (CW25/CW26) copied the raw element reference, so no allocation sat between the read and the write.
    const string GetThenCloneCtor =
        @"__Get__SystemInt32__SystemObject""((?!EXTERN|JUMP)[^:])*EXTERN, ""SystemObjectArray\.__ctor__";

    [Fact]
    public void StructThis_ReturnAsValue_Clones()  // CW24
    {
        // The IInstanceReferenceOperation value-read arm returned the receiver bundle RAW while every
        // sibling value-read arm (locals/env locals/params/env params) DeepClones aggregates: the
        // receiver is the caller's LIVE storage, so `var t = s.Copy(); t.x = 1;` mutated s.x
        // (VM-proven 1 vs CLR: s.x stays 3). The struct method must clone `this` escaping as a value.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct SThisRet { public int x; public SThisRet Copy() { return this; } }
public class CwThisRet : UdonSharpBehaviour {
    public int result;
    void Start() { var s = new SThisRet(); s.x = 3; var t = s.Copy(); t.x = 1; result = s.x; }
}", "CwThisRet");
        Assert.Matches(@"____\d+_Copy:((?!JUMP_INDIRECT)[\s\S])*SystemObjectArray\.__ctor__", uasm);
    }

    [Fact]
    public void StructThis_LocalCopy_Clones()  // CW24 local-init leg
    {
        // `var c = this; c.x = 5;` inside a struct method mutated the caller's variable (VM 5 vs
        // CLR 2): aggregate local init relies on VisitExpression cloning, and the `this` arm didn't.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct SThisLoc { public int x; public int Probe() { var c = this; c.x = 5; return x; } }
public class CwThisLoc : UdonSharpBehaviour {
    public int result;
    void Start() { var s = new SThisLoc(); s.x = 2; result = s.Probe(); }
}", "CwThisLoc");
        // The local-decl default init already allocates an (empty) bundle, so a bare __ctor__ pin
        // false-greens — the clone's signature is the per-slot copy: a __Get__ from the receiver
        // followed (PUSH/COPY only) by a __Set__ into the fresh bundle, inside Probe's body.
        Assert.Matches(@"____\d+_Probe:((?!JUMP_INDIRECT)[\s\S])*__Get__SystemInt32__SystemObject""((?!EXTERN|JUMP)[^:])*EXTERN, ""SystemObjectArray\.__Set__", uasm);
    }

    [Fact]
    public void StructThis_PassAsArgument_Clones()  // CW24 argument leg
    {
        // `Take(this)` handed the callee the live receiver bundle, so the callee's param writes landed
        // in the caller's struct (VM 9 vs CLR 3). A non-ref argument is a plain value read — clone.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct SThisArg {
    public int x;
    public static int Take(SThisArg s) { s.x = 9; return s.x; }
    public int Go() { int r = Take(this); return x + r; }
}
public class CwThisArg : UdonSharpBehaviour {
    public int result;
    void Start() { var s = new SThisArg(); s.x = 3; result = s.Go(); }
}", "CwThisArg");
        Assert.Matches(@"____\d+_Go:((?!JUMP_INDIRECT)[\s\S])*SystemObjectArray\.__ctor__", uasm);
    }

    [Fact]
    public void ClassThis_ReturnAsValue_StaysReference()  // CW24 accept control
    {
        // CA-M1: a v1 CLASS shares the same receiver param0 convention but has reference semantics —
        // `return this` must stay a raw bundle passthrough (no clone); only the struct half clones.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CSelfRef { public int x; public CSelfRef Self() { return this; } }
public class CwClassSelf : UdonSharpBehaviour {
    public int result;
    void Start() { CSelfRef c = new CSelfRef(); CSelfRef d = c.Self(); d.x = 7; result = c.x; }
}", "CwClassSelf");
        Assert.DoesNotMatch(@"____\d+_Self:((?!JUMP_INDIRECT)[\s\S])*SystemObjectArray\.__ctor__", uasm);
    }

    [Fact]
    public void StructArraySlice_ElementsCloned()  // CW25
    {
        // VisitRangeSlice's copy loop did raw __Get__/__Set__ while the single-element read of the SAME
        // array clones (ArrayHandler's value-semantics rule): `var sl = arr[0..2]; sl[0].x = 9;` wrote
        // through the shared element bundle into arr[0] (C# GetSubArray copies struct values).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct SSl { public int x; }
public class CwSliceStruct : UdonSharpBehaviour {
    public int result;
    void Start() { SSl[] arr = new SSl[3]; var sl = arr[0..2]; sl[0].x = 9; result = arr[0].x; }
}", "CwSliceStruct");
        Assert.Matches(GetThenCloneCtor, uasm);
    }

    [Fact]
    public void TupleArraySlice_ElementsCloned()  // CW25 tuple leg
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CwSliceTuple : UdonSharpBehaviour {
    public int result;
    void Start() { (int a, int b)[] arr = new (int, int)[3]; var sl = arr[1..3]; result = sl.Length; }
}", "CwSliceTuple");
        Assert.Matches(GetThenCloneCtor, uasm);
    }

    [Fact]
    public void StructArray_CopyTo_DeepClonesElements()  // CW26
    {
        // Rank-1 aggregate-element arrays fell past the N-R4 intercept (Rank>1 only) to the registry-
        // valid shallow externs: CopyTo copied the element bundle REFERENCES, so b[0] and a[0] were one
        // bundle and `b[0].x = 1` silently became a[0].x = 1 (C# copies struct VALUES). Must lower to a
        // per-element DeepClone loop; the shallow extern must be gone.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct SCt { public int x; }
public class CwAggCopyTo : UdonSharpBehaviour {
    public int result;
    void Start() { SCt[] a = new SCt[3]; SCt[] b = new SCt[3]; a.CopyTo(b, 0); b[0].x = 1; result = a[0].x; }
}", "CwAggCopyTo");
        Assert.DoesNotContain("SystemArray.__CopyTo__", uasm);
        Assert.Matches(GetThenCloneCtor, uasm);
    }

    [Fact]
    public void StructArray_Clone_DeepClonesElements()  // CW26 Clone leg
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct SCl { public int x; }
public class CwAggClone : UdonSharpBehaviour {
    public int result;
    void Start() { SCl[] a = new SCl[2]; SCl[] c = (SCl[])a.Clone(); c[0].x = 2; result = a[0].x; }
}", "CwAggClone");
        Assert.DoesNotContain("SystemArray.__Clone__", uasm);
        Assert.Matches(GetThenCloneCtor, uasm);
    }

    [Fact]
    public void StructArray_ArrayCopy_DeepClonesElements()  // CW26 static Copy leg (3-arg + ranged)
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct SAc { public int x; }
public class CwAggArrayCopy : UdonSharpBehaviour {
    public int result;
    void Start() {
        SAc[] a = new SAc[3]; SAc[] b = new SAc[3];
        System.Array.Copy(a, b, 2);
        System.Array.Copy(a, 1, b, 0, 2);
        b[1].x = 4; result = a[1].x;
    }
}", "CwAggArrayCopy");
        Assert.DoesNotContain("SystemArray.__Copy__", uasm);
        Assert.Matches(GetThenCloneCtor, uasm);
    }

    [Fact]
    public void TupleArray_CopyTo_DeepClonesElements()  // CW26 tuple leg
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CwAggCopyToTuple : UdonSharpBehaviour {
    public int result;
    void Start() {
        (int a, int b)[] x = new (int, int)[2]; (int a, int b)[] y = new (int, int)[2];
        x.CopyTo(y, 0); result = y[0].a;
    }
}", "CwAggCopyToTuple");
        Assert.DoesNotContain("SystemArray.__CopyTo__", uasm);
        Assert.Matches(GetThenCloneCtor, uasm);
    }

    [Fact]
    public void ScalarAndClassElementArray_Clone_StaysExtern()  // CW26 accept controls
    {
        // Shallow IS C# semantics for these: int elements are values inside a real SystemInt32Array,
        // and class elements ARE references (C# Clone copies the references) — both stay on the extern.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CElCtl { public int v; }
public class CwAggCloneCtl : UdonSharpBehaviour {
    public int result;
    void Start() {
        int[] a = new int[3];
        int[] b = (int[])a.Clone();
        CElCtl[] c = new CElCtl[2];
        CElCtl[] d = (CElCtl[])c.Clone();
        result = b[0] + (d[0] == null ? 1 : 0);
    }
}", "CwAggCloneCtl");
        Assert.Equal(2, Regex.Matches(uasm, @"SystemArray\.__Clone__SystemObject").Count);
    }

    [Fact]
    public void MixedElementArrayCopy_LoudRejects()  // CW26 reject leg
    {
        // object[] → S[] is an unboxing Array.Copy on the CLR; the bundle representation cannot type
        // the per-element value copy, so the shape rejects loudly instead of aliasing or faulting.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public struct SMx { public int x; }
public class CwAggCopyMixed : UdonSharpBehaviour {
    public int result;
    void Start() { object[] o = new object[2]; SMx[] b = new SMx[2]; System.Array.Copy(o, b, 2); result = b[0].x; }
}", "CwAggCopyMixed"));
        Assert.Contains("struct/tuple-element array", ex.Message);
    }

    [Fact]
    public void AggregateArrayCopy_LongOverload_LoudRejects()  // CW26 reject leg
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public struct SLg { public int x; }
public class CwAggCopyLong : UdonSharpBehaviour {
    public int result;
    void Start() { SLg[] a = new SLg[2]; SLg[] b = new SLg[2]; System.Array.Copy(a, b, 2L); result = b[0].x; }
}", "CwAggCopyLong"));
        Assert.Contains("Int32 overload", ex.Message);
    }

    // ── CW27: EmitObjectInitializer silently dropped members it could not lower ──

    [Fact]
    public void ObjectInitializer_NestedMemberInit_WritesNestedField()  // CW27
    {
        // `Inner = { X = 7 }` is an IMemberInitializerOperation — the `is not ISimpleAssignment`
        // filter skipped it, so the program compiled clean with Inner.X never written (the constant
        // 7 existed nowhere). C# writes 7 into the fresh value's nested field.
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public struct InnerNi { public int X; }
public struct OuterNi { public InnerNi Inner; public int Y; }
public class CwNestedInit : UdonSharpBehaviour {
    public int result;
    void Start() { var s = new OuterNi { Inner = { X = 7 }, Y = 2 }; result = s.Inner.X * 100 + s.Y; }
}", "CwNestedInit");
        Assert.NotNull(uasm);
        Assert.Contains(consts, c => c.UdonType == "SystemInt32" && Equals(c.Value, 7));
    }

    [Fact]
    public void ObjectInitializer_ClassNestedMemberInit_WritesNestedField()  // CW27 class leg
    {
        // Same skip on a v1-class member: the ctor-initialized Inner bundle is live in the slot, so
        // the nested initializer is a read-slot + recurse (C#: read the member, assign into it).
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class InnerNc { public int X; }
public class WrapNc { public InnerNc Inner = new InnerNc(); }
public class CwNestedInitCls : UdonSharpBehaviour {
    public int result;
    void Start() { var w = new WrapNc { Inner = { X = 37 } }; result = w.Inner.X; }
}", "CwNestedInitCls");
        Assert.NotNull(uasm);
        Assert.Contains(consts, c => c.UdonType == "SystemInt32" && Equals(c.Value, 37));
    }

    [Fact]
    public void ObjectInitializer_ComputedProperty_CallsSetter()  // CW27
    {
        // A computed property never gets a layout slot, so `Comp = 5` resolved no index and was
        // silently dropped — though PLAIN assignment to the same property runs the user setter
        // (PreparePropertySet's IsObjectArrayEmulated arm). The initializer must take the same path.
        // (The accessor label ____N_set_Comp exists regardless — the Phase-1 collector registers it —
        // so the pin is the VALUE: the dropped member never evaluated `5` into the const pool.)
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public struct CompIn { public int back; public int Comp { get { return back; } set { back = value * 2; } } }
public class CwCompInit : UdonSharpBehaviour {
    public int result;
    void Start() { var s = new CompIn { Comp = 5 }; result = s.back; }
}", "CwCompInit");
        Assert.Contains("set_Comp", uasm);
        Assert.Contains(consts, c => c.UdonType == "SystemInt32" && Equals(c.Value, 5));
    }

    [Fact]
    public void ObjectInitializer_IndexInitializer_CallsSetter()  // CW27 indexer leg
    {
        // `new S { [1] = 5 }` targets the indexer ("this[]" resolves no layout slot) — silently
        // dropped; must call the user indexer setter (index args by ordinal, then the value).
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public struct IdxIn { public int a0; public int a1;
    public int this[int i] { get { return i == 0 ? a0 : a1; } set { if (i == 0) a0 = value; else a1 = value; } } }
public class CwIdxInit : UdonSharpBehaviour {
    public int result;
    void Start() { var s = new IdxIn { [1] = 5 }; result = s.a1; }
}", "CwIdxInit");
        Assert.Contains("set_Item", uasm);
        Assert.Contains(consts, c => c.UdonType == "SystemInt32" && Equals(c.Value, 5));
    }

    [Fact]
    public void ObjectInitializer_SdkTypedMemberNestedInit_LoudRejects()  // CW27 reject leg
    {
        // A nested member initializer on an SDK value-type member (boxed scalar slot, no object[]
        // layout to recurse into) cannot be lowered in place — loud reject, never a silent drop.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public struct HoldV { public Vector3 V; }
public class CwSdkNested : UdonSharpBehaviour {
    public float result;
    void Start() { var h = new HoldV { V = { x = 1f } }; result = h.V.x; }
}", "CwSdkNested"));
        Assert.Contains("nested member initializer", ex.Message);
    }

    [Fact]
    public void LocalDecl_CtorWithObjectInitializer_AppliesInitializer()  // CW27 ctor+init leg
    {
        // The local-decl in-place ctor fast arm never applied op.Initializer, silently dropping
        // `{ Y = 31 }` after `new CtInit(4)` (the VisitObjectCreation path applies it post-ctor).
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public struct CtInit { public int a; public int Y; public CtInit(int a) { this.a = a; Y = 0; } }
public class CwCtorInit : UdonSharpBehaviour {
    public int result;
    void Start() { var s = new CtInit(4) { Y = 31 }; result = s.a * 100 + s.Y; }
}", "CwCtorInit");
        Assert.NotNull(uasm);
        Assert.Contains(consts, c => c.UdonType == "SystemInt32" && Equals(c.Value, 31));
    }

    // ── CW28: SS2(a) unclassifiable-carrier gate must recurse into aggregate object/object[] fields ──

    [Fact]
    public void CrossProgramStore_StructWrappedObjectCapture_LoudRejects()  // CW28
    {
        // `struct Box { object o; }` hides the same arbitrary payload its raw `object` twin declares:
        // the delegate leg of the carrier gate recurses aggregate fields (ContainsDelegateType) but
        // the object/object[] legs tested only the capture's top-level type, so wrapping the object
        // in a struct laundered a class-capturing delegate past the cross-program store guard.
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class PayCls28 { public int v; }
public struct ObjBox28 { public object o; }
public class Recv28 : UdonSharpBehaviour { public System.Action cb; }
public class CwObjCarrier : UdonSharpBehaviour {
    public Recv28 other;
    void Start() {
        ObjBox28 b = default;
        {
            PayCls28 f = new PayCls28();
            System.Action inner = () => { f.v = f.v + 1; };
            b.o = inner;
        }
        other.cb = () => { b.o = null; };
    }
}", "CwObjCarrier"));
        Assert.Contains("cross-program field 'cb'", ex.Message);
    }

    [Fact]
    public void CrossProgramStore_StructWrappedObjectArrayCapture_LoudRejects()  // CW28 object[] leg
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class PayCls28b { public int v; }
public struct CellBox28 { public object[] cells; }
public class Recv28b : UdonSharpBehaviour { public System.Action cb; }
public class CwObjArrCarrier : UdonSharpBehaviour {
    public Recv28b other;
    void Start() {
        CellBox28 b = default;
        PayCls28b f = new PayCls28b(); f.v = 1;
        other.cb = () => { b.cells = null; };
    }
}", "CwObjArrCarrier"));
        Assert.Contains("cross-program field 'cb'", ex.Message);
    }

    [Fact]
    public void CrossProgramStore_CleanStructCapture_Compiles()  // CW28 accept control
    {
        // A capture whose type walk proves it payload- and carrier-free stays direct-safe even when
        // the surrounding body mentions program-local payload — the widening must not over-reject.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class PayCls28c { public int v; }
public struct CleanBox28 { public int n; }
public class Recv28c : UdonSharpBehaviour { public System.Action cb; }
public class CwCleanCapture : UdonSharpBehaviour {
    public Recv28c other;
    void Start() {
        CleanBox28 b = default; b.n = 1;
        PayCls28c f = new PayCls28c(); f.v = 2;
        other.cb = () => { b.n = b.n + 1; };
    }
}", "CwCleanCapture");
        Assert.NotNull(uasm);
    }

    // ── CW29: ONE deconstruction element-read clone rule (was 5 divergent sibling arms) ──

    [Fact]
    public void DeconFromSameClassCall_AggregateElement_Clones()  // CW29
    {
        // The same-class call-return arm assigned the return-array element RAW while its siblings
        // (delegate-invoke return, aggregate-value RHS) DeepClone aggregate elements — its safety
        // rested on the unstated every-return-materialization-is-fresh invariant. One rule: a
        // deconstruction element read is a VALUE read — clone every struct/tuple element.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct SDe { public int x; public (int, SDe) Pair() { return (1, this); } }
public class CwDeconCall : UdonSharpBehaviour {
    public int result;
    void Start() { var s = new SDe(); s.x = 10; var (n, sv) = s.Pair(); sv.x = 99; result = s.x + n; }
}", "CwDeconCall");
        Assert.Matches(GetThenCloneCtor, uasm);
    }

    [Fact]
    public void DeconFromCrossBehaviourCall_AggregateElement_Clones()  // CW29 cross-behaviour leg
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct SXd { public int x; }
public class XdCallee : UdonSharpBehaviour {
    public (int, SXd) Make() { SXd s = new SXd(); s.x = 5; return (1, s); }
}
public class CwDeconXb : UdonSharpBehaviour {
    public XdCallee other;
    public int result;
    void Start() { var (n, sv) = other.Make(); result = n + sv.x; }
}", "CwDeconXb");
        Assert.Matches(GetThenCloneCtor, uasm);
    }
}
