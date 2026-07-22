using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Hand-enumeration audit 2026-07-17, Tier-2 (cross-matrices, Matrix 3): the Contains* walker family.
/// The three public predicates — EmitPolicy.ContainsUserClassType / ContainsDelegateType /
/// ContainsOpaqueObjectType — were three hand-copied walkers with DIVERGING descent: the delegate and
/// opaque-object twins were missing the generic-type-argument and delegate-signature arms the
/// user-class walker had (the f6ebdae/8cb0962 per-copy missing-arm family). Owner ruling: one
/// parameterized descent body (leaf predicate as the parameter), the three predicates as thin
/// wrappers. This battery IS the descent-structure contract: every composite shape the shared walk
/// must reach (array element, jagged, tuple element, aggregate field, generic type argument,
/// delegate parameter/return signature, Nullable wrapper, self-referential termination), each with
/// a hand-computed verdict per predicate. A row flipping means the shared descent changed — that is
/// a walker-contract break, not a fixture to regenerate.
/// </summary>
public class ContainsWalkerCensusTests
{
    const string CensusSource = @"
public delegate void PlainDel();
public delegate void ClassSigDel(PayloadCls p);
public delegate object ObjRetDel();
public class PayloadCls { public int v; }
public class NodeCls { public NodeCls next; public PlainDel d; }
public struct ObjArrBox { public object[] arr; }
public struct DelBox { public PlainDel d; }
public struct ClsBox { public PayloadCls p; }
public struct SelfArr { public SelfArr[] kids; }
public struct SelfArrDel { public SelfArrDel[] kids; public PlainDel d; }
public struct Wrap<T> { public int x; }
public class WalkerCensusCarrier
{
    public int cInt;
    public string cStr;
    public int[] cIntArr;
    public int[,] cNdim;
    public (int, string) cTup;
    public Wrap<int> cWrapInt;
    public Wrap<int>? cWrapIntN;
    public UnityEngine.Vector3 cVec;
    public PayloadCls dCls;
    public PlainDel dDel;
    public object dObj;
    public PayloadCls[] aCls;
    public PayloadCls[][] jCls;
    public PlainDel[] aDel;
    public PlainDel[][] jDel;
    public object[] aObj;
    public object[][] jObj;
    public (PlainDel, int) tDel;
    public (PayloadCls, int) tCls;
    public (object, int) tObj;
    public ObjArrBox sObjArr;
    public DelBox sDel;
    public ClsBox sCls;
    public Wrap<PayloadCls> gCls;
    public Wrap<PlainDel> gDel;
    public Wrap<object> gObj;
    public ClassSigDel dsCls;
    public ObjRetDel dsObj;
    public Wrap<PayloadCls>? nCls;
    public Wrap<PlainDel>? nDel;
    public Wrap<object>? nObj;
    public ObjArrBox? nObjArr;
    public ClsBox? nClsBox;
    public (PlainDel, int)? nTupDel;
    public ObjArrBox?[] anObjArr;
    public Wrap<PayloadCls>?[] anCls;
    public NodeCls dNode;
    public SelfArr sSelf;
    public SelfArrDel sSelfDel;
}";

    // field → (ContainsUserClassType, ContainsDelegateType, ContainsOpaqueObjectType), hand-computed.
    // Wrap<T> deliberately has NO T-typed field, so its rows exercise the generic-type-argument arm
    // alone (a T-typed field would let the aggregate-field arm mask a missing type-arg arm).
    static readonly (string Field, bool Cls, bool Del, bool Obj, string Shape)[] Battery =
    {
        // Negative controls — no descent path reaches any leaf.
        ("cInt",     false, false, false, "int"),
        ("cStr",     false, false, false, "string"),
        ("cIntArr",  false, false, false, "int[]"),
        ("cNdim",    false, false, false, "int[,]"),
        ("cTup",     false, false, false, "(int, string)"),
        ("cWrapInt", false, false, false, "Wrap<int>"),
        ("cWrapIntN",false, false, false, "Wrap<int>?"),
        ("cVec",     false, false, false, "UnityEngine.Vector3 (SDK struct)"),
        // Direct leaves.
        ("dCls", true,  false, false, "v1 class proper"),
        ("dDel", false, true,  false, "delegate proper"),
        ("dObj", false, false, true,  "object proper"),
        // Array-element arm, plain and jagged.
        ("aCls", true,  false, false, "class[]"),
        ("jCls", true,  false, false, "class[][]"),
        ("aDel", false, true,  false, "delegate[]"),
        ("jDel", false, true,  false, "delegate[][]"),
        ("aObj", false, false, true,  "object[]"),
        ("jObj", false, false, true,  "object[][]"),
        // Tuple-element arm (ruling shape: delegate-in-tuple).
        ("tDel", false, true,  false, "delegate-in-tuple"),
        ("tCls", true,  false, false, "class-in-tuple"),
        ("tObj", false, false, true,  "object-in-tuple"),
        // Aggregate-field arm (ruling shape: object[]-in-struct).
        ("sObjArr", false, false, true,  "object[]-in-struct"),
        ("sDel",    false, true,  false, "delegate-in-struct"),
        ("sCls",    true,  false, false, "class-in-struct"),
        // Generic-type-argument arm — the arm the old delegate/opaque twins were missing
        // (ruling shape: class-in-generic-arg).
        ("gCls", true,  false, false, "class-in-generic-arg"),
        ("gDel", false, true,  false, "delegate-in-generic-arg (widened arm)"),
        ("gObj", false, false, true,  "object-in-generic-arg (widened arm)"),
        // Delegate-signature arm — the other arm the old twins were missing
        // (ruling shape: delegate-sig-carrying-class).
        ("dsCls", true,  true,  false, "delegate-sig-carrying-class"),
        ("dsObj", false, true,  true,  "delegate-sig-carrying-object (widened arm)"),
        // Nullable<T> wrapping each — reached via the type-argument arm (Nullable is a native
        // struct, not an aggregate, so the field arm never sees inside it).
        ("nCls",    true,  false, false, "Nullable<Wrap<class>>"),
        ("nDel",    false, true,  false, "Nullable<Wrap<delegate>>"),
        ("nObj",    false, false, true,  "Nullable<Wrap<object>>"),
        ("nObjArr", false, false, true,  "Nullable<object[]-in-struct>"),
        ("nClsBox", true,  false, false, "Nullable<class-in-struct>"),
        ("nTupDel", false, true,  false, "Nullable<delegate-in-tuple>"),
        // Arrays of Nullable — array arm composed over the type-argument arm.
        ("anObjArr", false, false, true,  "Nullable<object[]-in-struct>[]"),
        ("anCls",    true,  false, false, "Nullable<Wrap<class>>[]"),
        // v1-class opacity: a class IS the user-class leaf, and its own fields are program-local
        // bundle slots — the delegate/object predicates must NOT see through it.
        ("dNode", true, false, false, "class with self + delegate fields (opaque to Del/Obj)"),
        // Self-referential shapes — the visited set must terminate the walk.
        ("sSelf",    false, false, false, "self-referential struct (termination)"),
        ("sSelfDel", false, true,  false, "self-referential struct carrying a delegate"),
    };

    static System.Collections.Generic.Dictionary<string, ITypeSymbol> BuildCensusTypes()
    {
        TestHelper.BuildCompilation(CensusSource, "WalkerCensusCarrier", out var carrier);
        return carrier.GetMembers().OfType<IFieldSymbol>()
            .ToDictionary(f => f.Name, f => f.Type);
    }

    [Fact]
    public void ThreePredicates_AgreeWithHandComputedVerdicts_OnEveryBatteryShape()
    {
        var types = BuildCensusTypes();
        Assert.Equal(Battery.Length, types.Count); // every carrier field has a pinned row, and vice versa
        foreach (var (field, cls, del, obj, shape) in Battery)
        {
            var t = types[field];
            Assert.True(EmitPolicy.ContainsUserClassType(t) == cls,
                $"descent drift [{shape}]: ContainsUserClassType({field}) expected {cls}");
            Assert.True(EmitPolicy.ContainsDelegateType(t) == del,
                $"descent drift [{shape}]: ContainsDelegateType({field}) expected {del}");
            Assert.True(EmitPolicy.ContainsOpaqueObjectType(t) == obj,
                $"descent drift [{shape}]: ContainsOpaqueObjectType({field}) expected {obj}");
        }
    }

    [Fact]
    public void TypeClassifierDelegation_MatchesUserClassWalker_OnEveryBatteryShape()
    {
        // RuntimeShape owns the public classification; pin that its recursive payload fact stays
        // identical to the shared ContainsUserClassType walker.
        var types = BuildCensusTypes();
        var ctx = new TypeClassifierContext(null);
        foreach (var (field, cls, _, _, shape) in Battery)
            Assert.True(TypeClassifier.ContainsUserClassPayload(types[field], ctx) == cls,
                $"facade drift [{shape}]: ContainsUserClassPayload({field}) expected {cls}");
    }

    [Fact]
    public void RuntimeShape_DistinguishesObjectArrayMeanings()
    {
        var types = BuildCensusTypes();
        var ctx = new TypeClassifierContext(null);

        var cls = TypeClassifier.ShapeOf(types["dCls"], ctx);
        Assert.Equal(RuntimeBundleKind.Class, cls.Bundle);
        Assert.Equal(RuntimeIdentityKind.Reference, cls.Identity);
        Assert.True(cls.Supports(TransportCapabilities.TypedProgramChannel));
        Assert.True(cls.Supports(TransportCapabilities.DelegateEnvironment));
        Assert.False(cls.Supports(TransportCapabilities.ObjectErasure));
        Assert.True(cls.ContainsUserClassPayload);

        var aggregate = TypeClassifier.ShapeOf(types["cTup"], ctx);
        Assert.Equal(RuntimeBundleKind.Aggregate, aggregate.Bundle);
        Assert.Equal(RuntimeIdentityKind.Value, aggregate.Identity);
        Assert.Equal(RuntimeTypeIdentityKind.Collapsed, aggregate.RuntimeTypeIdentity);
        Assert.True(aggregate.Supports(TransportCapabilities.DelegateEnvironment));
        Assert.False(aggregate.Supports(TransportCapabilities.ExternCall));

        var dlg = TypeClassifier.ShapeOf(types["dDel"], ctx);
        Assert.Equal(RuntimeBundleKind.Delegate, dlg.Bundle);
        Assert.True(dlg.Supports(TransportCapabilities.TypedProgramChannel));

        var ndim = TypeClassifier.ShapeOf(types["cNdim"], ctx);
        Assert.Equal(RuntimeBundleKind.MultiDimensionalArray, ndim.Bundle);
        Assert.Equal(RuntimeIdentityKind.Reference, ndim.Identity);
        Assert.True(ndim.Supports(TransportCapabilities.DelegateEnvironment));
        Assert.False(ndim.Supports(TransportCapabilities.ExternCall));

        var native = TypeClassifier.ShapeOf(types["cIntArr"], ctx);
        Assert.Equal(RuntimeStorageKind.Native, native.Storage);
        Assert.Equal(RuntimeTypeIdentityKind.Exact, native.RuntimeTypeIdentity);
        Assert.True(native.Supports(TransportCapabilities.ExternCall));
        Assert.True(native.Supports(TransportCapabilities.Network));

        var classArray = TypeClassifier.ShapeOf(types["aCls"], ctx);
        Assert.Equal(RuntimeStorageKind.Native, classArray.Storage);
        Assert.True(classArray.ContainsUserClassPayload);
        Assert.True(classArray.Supports(TransportCapabilities.ExternCall));
        Assert.True(classArray.Supports(TransportCapabilities.DelegateEnvironment));
        Assert.False(classArray.Supports(TransportCapabilities.TypedProgramChannel));
        Assert.False(classArray.Supports(TransportCapabilities.Network));
    }
}
