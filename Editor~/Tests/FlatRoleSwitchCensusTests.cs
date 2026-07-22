using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Totality census for the flat-role switches (hand-enumeration audit 2026-07-17, Tier-1 item ①):
/// CoreFlatOptimizer's slot extraction/remap family + spill classifiers, and the shared
/// <see cref="CTerminator.GetSuccessors"/> edge source. These switches used to default silently
/// (yield nothing / null / passthrough / false / 0), so a future flat kind would corrupt slot
/// allocation while VerifyNoInterference — derived from the same switches — stayed green
/// (self-shadowing). The census pins the flipped polarity: every KNOWN flat-role kind (the domain
/// is FlatVerify.VerifyInstruction / VerifyTerminator's arms) is handled explicitly without
/// throwing, and any UNKNOWN kind (modeled by a test-local fake subclass) throws
/// <see cref="VerificationException"/> naming the switch and the kind.
/// </summary>
public class FlatRoleSwitchCensusTests
{
    sealed class FakeStmt : CStmt { }
    sealed class FakeTerminator : CTerminator { }

    static readonly Dictionary<int, int> EmptyMap = new();
    static readonly HashSet<string> NoNames = new();

    // One probe per hardened switch; iterator-backed functions are forced so a deferred throw fires.
    public static IEnumerable<object[]> StmtSwitches()
    {
        yield return Probe("GetWrittenSlot", s => CoreFlatOptimizer.GetWrittenSlot(s));
        yield return Probe("GetReadSlotsInst", s => CoreFlatOptimizer.GetReadSlotsInst(s).ToList());
        yield return Probe("RemapInst", s => CoreFlatOptimizer.RemapInst(s, EmptyMap));
        yield return Probe("IsRecursiveCall", s => CoreFlatOptimizer.IsRecursiveCall(s, NoNames));
        yield return Probe("IsReentrantFlagged", s => CoreFlatOptimizer.IsReentrantFlagged(s));
        yield return Probe("PreSpillStmtCount", s => CoreFlatOptimizer.PreSpillStmtCount(s));
    }

    public static IEnumerable<object[]> TermSwitches()
    {
        yield return TermProbe("GetReadSlotsTerm", t => CoreFlatOptimizer.GetReadSlotsTerm(t).ToList());
        yield return TermProbe("RemapTerminator", t => CoreFlatOptimizer.RemapTerminator(t, EmptyMap));
        yield return TermProbe("GetSuccessors", t => CTerminator.GetSuccessors(t));
    }

    static object[] Probe(string name, Action<CStmt> f) => new object[] { name, f };
    static object[] TermProbe(string name, Action<CTerminator> f) => new object[] { name, f };

    // The flat-role CStmt domain — FlatVerify.VerifyInstruction's arms, one instance per shape.
    static IEnumerable<CStmt> KnownFlatStmts()
    {
        yield return new CAssign(0, new CConst(1, StorageTypes.Int32));
        yield return new CAssign(0, new CSlotRef(1, StorageTypes.Int32));
        yield return new CStoreField("f", new CSlotRef(0, StorageTypes.Int32));
        yield return new CLoadField(0, "f", StorageTypes.Int32);
        yield return new CExprStmt(new CExternCall("Foo.__Bar__SystemVoid",
            new List<CLeaf> { new CSlotRef(0, StorageTypes.Int32) }, StorageTypes.Void));
        yield return new CExprStmt(new CExternCall("Foo.__Baz__SystemInt32",
            new List<CLeaf> { new CSlotRef(0, StorageTypes.Int32) }, StorageTypes.Int32, 1));
        yield return new CExprStmt(new CInternalCall("bar",
            new List<CLeaf> { new CSlotRef(0, StorageTypes.Int32) }, StorageTypes.Void));
        yield return new CExprStmt(new CInternalCall("baz",
            new List<CLeaf> { new CSlotRef(0, StorageTypes.Int32) }, StorageTypes.Int32, 1));
    }

    // The flat-role CTerminator domain — FlatVerify.VerifyTerminator's arms.
    static IEnumerable<CTerminator> KnownTerminators()
    {
        yield return new CJump(0);
        yield return new CBranch(new CSlotRef(0, StorageTypes.Boolean), 1, 2);
        yield return new CRet();
        yield return new CRet(new CSlotRef(0, StorageTypes.Int32));
    }

    [Theory]
    [MemberData(nameof(StmtSwitches))]
    public void UnknownFlatStmtKind_ThrowsNamingSwitchAndKind(string switchName, Action<CStmt> probe)
    {
        var ex = Assert.Throws<VerificationException>(() => probe(new FakeStmt()));
        Assert.Contains(switchName, ex.Message);
        Assert.Contains(nameof(FakeStmt), ex.Message);
    }

    [Theory]
    [MemberData(nameof(TermSwitches))]
    public void UnknownTerminatorKind_ThrowsNamingSwitchAndKind(string switchName, Action<CTerminator> probe)
    {
        var ex = Assert.Throws<VerificationException>(() => probe(new FakeTerminator()));
        Assert.Contains(switchName, ex.Message);
        Assert.Contains(nameof(FakeTerminator), ex.Message);
    }

    [Theory]
    [MemberData(nameof(StmtSwitches))]
    public void EveryKnownFlatStmtKind_IsHandledExplicitly(string switchName, Action<CStmt> probe)
    {
        Assert.NotNull(switchName);
        foreach (var stmt in KnownFlatStmts())
            probe(stmt); // must not throw — legitimately-nothing kinds have explicit arms
    }

    [Theory]
    [MemberData(nameof(TermSwitches))]
    public void EveryKnownTerminatorKind_IsHandledExplicitly(string switchName, Action<CTerminator> probe)
    {
        Assert.NotNull(switchName);
        foreach (var term in KnownTerminators())
            probe(term); // must not throw
    }
}
