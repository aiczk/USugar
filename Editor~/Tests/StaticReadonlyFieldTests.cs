using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// S-M1 gate for the static-readonly-materialization design (docs/superpowers/specs/
/// 2026-07-03-multicast-event-static-readonly-design.md §3, §4, §7). Structural (compile / reject-
/// message) coverage — real-VM VALUE coverage for the same scenarios lives in the local harness
/// (Editor~/_local_harness/StaticReadonlyMaterializeVmTests.cs). Pre-existing const-fold pins
/// (Editor~/_local_harness/AuditStaticReadonlyTest.cs) are untouched by this feature and stay green.
/// </summary>
public class StaticReadonlyFieldTests
{
    // ── §3.2 supported-type lattice: non-const static readonly materializes (compiles clean) ──

    [Fact]
    public void Array_Materializes()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SR_Array : UdonSharpBehaviour {
    static readonly int[] Table = { 1, 2, 3 };
    public int result;
    void Start(){ result = Table[1]; }
}", "SR_Array");
        Assert.Contains("Table", uasm);
    }

    [Fact]
    public void ExternStruct_Materializes()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp; using UnityEngine;
public class SR_ExternStruct : UdonSharpBehaviour {
    static readonly Vector3 Origin = new Vector3(1, 2, 3);
    public float result;
    void Start(){ result = Origin.x; }
}", "SR_ExternStruct");
        Assert.Contains("Origin", uasm);
    }

    [Fact]
    public void UserStruct_Materializes()
    {
        // Uninitialized (default-zeroed) — an explicit ctor-call/object-initializer initializer for a
        // user struct hits a pre-existing, feature-unrelated gap in the shared field-initializer
        // runtime-init path (reproduces identically for a plain instance field; see the local harness
        // file's note). Materialization itself (declare + default-init) is what this pins.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SR_UserStruct : UdonSharpBehaviour {
    static readonly TestStubs.MyVec3 Origin;
    public float result;
    void Start(){ result = Origin.x; }
}", "SR_UserStruct");
        Assert.Contains("Origin", uasm);
    }

    [Fact]
    public void Tuple_Materializes()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SR_Tuple : UdonSharpBehaviour {
    static readonly (int a, int b) Pair = (1, 2);
    public int result;
    void Start(){ result = Pair.a; }
}", "SR_Tuple");
        Assert.Contains("Pair", uasm);
    }

    [Fact]
    public void Delegate_Materializes()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp; using System;
public class SR_Delegate : UdonSharpBehaviour {
    static int Square(int x) => x * x;
    static readonly Func<int,int> Op = Square;
    public int result;
    void Start(){ result = Op(5); }
}", "SR_Delegate");
        Assert.Contains("Op", uasm);
    }

    [Fact]
    public void PrimitiveComputedFromAnotherStaticReadonly_Materializes()
    {
        // `Base`'s literal initializer is trivially const-foldable (own storage-free, unaffected by
        // this feature). `Derived`'s initializer READS Base — not itself a Roslyn compile-time constant
        // (only `const` folds), so Derived exercises real materialization + the "reads another static
        // readonly" purity rule (§3.4).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SR_Derived : UdonSharpBehaviour {
    static readonly int Base = 5;
    static readonly int Derived = Base + 1;
    public int result;
    void Start(){ result = Derived; }
}", "SR_Derived");
        Assert.Contains("Derived", uasm);
    }

    [Fact]
    public void StringComputedFromAnotherStaticReadonly_Materializes()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SR_StringDerived : UdonSharpBehaviour {
    static readonly string Base = ""x"";
    static readonly string Derived = Base;
    public string result;
    void Start(){ result = Derived; }
}", "SR_StringDerived");
        Assert.Contains("Derived", uasm);
    }

    [Fact]
    public void EnumComputedFromAnotherStaticReadonly_Materializes()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum SR_E { A, B, C }
public class SR_EnumDerived : UdonSharpBehaviour {
    static readonly SR_E Base = SR_E.B;
    static readonly SR_E Derived = Base;
    public int result;
    void Start(){ result = (int)Derived; }
}", "SR_EnumDerived");
        Assert.Contains("Derived", uasm);
    }

    // ── §3.3, R5: write-through mutation rejected loudly ──

    [Fact]
    public void ArrayElementWrite_ThroughStaticReadonly_Rejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class SR_WriteThrough : UdonSharpBehaviour {
    static readonly int[] Table = { 1, 2, 3 };
    void Start(){ Table[0] = 9; }
}", "SR_WriteThrough"));
        Assert.Contains("cannot mutate the contents of a static readonly field", ex.Message);
    }

    [Fact]
    public void ArrayElementCompoundWrite_ThroughStaticReadonly_Rejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class SR_WriteThroughCompound : UdonSharpBehaviour {
    static readonly int[] Table = { 1, 2, 3 };
    void Start(){ Table[0] += 9; }
}", "SR_WriteThroughCompound"));
        Assert.Contains("cannot mutate the contents of a static readonly field", ex.Message);
    }

    [Fact]
    public void ArrayElementIncrement_ThroughStaticReadonly_Rejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class SR_WriteThroughInc : UdonSharpBehaviour {
    static readonly int[] Table = { 1, 2, 3 };
    void Start(){ Table[0]++; }
}", "SR_WriteThroughInc"));
        Assert.Contains("cannot mutate the contents of a static readonly field", ex.Message);
    }

    [Fact]
    public void StructMemberWrite_ThroughArrayElement_RootedAtStaticReadonly_Rejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class SR_WriteThroughMember : UdonSharpBehaviour {
    static readonly TestStubs.MyVec3[] Points = { new TestStubs.MyVec3(1,2,3) };
    void Start(){ Points[0].x = 9; }
}", "SR_WriteThroughMember"));
        Assert.Contains("cannot mutate the contents of a static readonly field", ex.Message);
    }

    // ── §3.4, R6: impure initializer rejected loudly ──

    [Fact]
    public void MethodCallInitializer_Rejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class SR_Impure : UdonSharpBehaviour {
    static int Compute() => 7;
    static readonly int K = Compute();
    void Start(){ }
}", "SR_Impure"));
        Assert.Contains("must be pure", ex.Message);
    }

    [Fact]
    public void MutableExternStateInitializer_Rejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp; using UnityEngine;
public class SR_ImpureExtern : UdonSharpBehaviour {
    static readonly int K = Time.frameCount;
    void Start(){ }
}", "SR_ImpureExtern"));
        Assert.Contains("must be pure", ex.Message);
    }

    // NOTE: the doc's purity-gate surface also names "instance member reads" and "`this`" as impure
    // shapes to reject (§3.4). Both are UNREACHABLE via valid C#: a static field initializer cannot
    // reference an instance member or `this` at all (Roslyn CS0236/CS0026 — a static initializer runs
    // before any instance exists). IsPureStaticReadonlyInitializer's IFieldReferenceOperation/default
    // arms exclude them as defense-in-depth, but no compiling test can exercise that arm — flagged for
    // the record rather than asserted.

    // ── §3.5, R7: cross-class non-const read rejected loudly; const/foldable still folds ──

    [Fact]
    public void CrossClass_NonConstStaticReadonlyRead_Rejects()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(new[]
        {
            @"using UdonSharp; public class SR_Other : UdonSharpBehaviour { public static readonly int[] Table = { 1, 2, 3 }; }",
            @"using UdonSharp; public class SR_CrossReader : UdonSharpBehaviour { public int result; void Start(){ result = SR_Other.Table[0]; } }",
        }, "SR_CrossReader"));
        Assert.Contains("cannot read a non-constant static readonly field", ex.Message);
    }

    [Fact]
    public void CrossClass_ConstFoldableStaticReadonlyRead_StillFolds()
    {
        var uasm = TestHelper.CompileToUasm(new[]
        {
            @"using UdonSharp; public class SR_Other2 : UdonSharpBehaviour { public static readonly int K = 1 + 2; }",
            @"using UdonSharp; public class SR_CrossReader2 : UdonSharpBehaviour { public int result; void Start(){ result = SR_Other2.K; } }",
        }, "SR_CrossReader2");
        // Folded to the literal 3 — no cross-behaviour SetProgramVariable/GetProgramVariable reference
        // to the other class is emitted for this read (byte-invariant fold path, unchanged by this
        // feature).
        Assert.DoesNotContain("SR_Other2", uasm);
    }

    // ── §3.7, R8: static mutable field message sharpened ──

    [Fact]
    public void StaticMutableField_RejectsWithSharpenedMessage()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class SR_Mutable : UdonSharpBehaviour {
    static int counter;
    public int result;
    void Start(){ result = counter; }
}", "SR_Mutable"));
        Assert.Contains("'static readonly' IS supported", ex.Message);
    }

    // ── §3.6: init order — instance field initialized FROM a static readonly must see the value ──

    [Fact]
    public void InstanceFieldInitializedFromStaticReadonly_CompilesAndOrdersStaticTierFirst()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SR_InitOrder : UdonSharpBehaviour {
    static readonly int[] Table = { 10, 20, 30 };
    public int fromTable = Table[1];
}", "SR_InitOrder");
        Assert.Contains("Table", uasm);
        Assert.Contains("fromTable", uasm);

        // Static tier must run before instance tier in the synthesized _start body — the static Table
        // array's construction/population code must appear before the SET that stores into fromTable.
        int startIdx = uasm.IndexOf("_start:", StringComparison.Ordinal);
        Assert.True(startIdx >= 0, "no _start label found");
        int tableCodeIdx = uasm.IndexOf("Table", startIdx, StringComparison.Ordinal);
        int fromTableSetIdx = uasm.IndexOf("fromTable", startIdx, StringComparison.Ordinal);
        Assert.True(tableCodeIdx >= 0 && fromTableSetIdx >= 0 && tableCodeIdx < fromTableSetIdx,
            $"expected Table's static-tier init before fromTable's instance-tier init in _start (Table@{tableCodeIdx}, fromTable@{fromTableSetIdx})");
    }
}
