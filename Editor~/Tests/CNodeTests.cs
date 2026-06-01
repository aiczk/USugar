using System.Collections.Generic;
using System.Text;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Phase 2 of the Core IR migration: proves the structured CStmt vocabulary losslessly
/// represents every HStmt. Round-trips via HStmt.Dump structural equality, plus direct field
/// assertions for the loop forms whose Dump omits Init/Update/CondBlock. Structured-only —
/// flat blocks/terminators are exercised by CoreFlatten later. Additive: oracle stays byte-identical.
/// </summary>
public class CNodeTests
{
    static string Dump(HStmt s) { var sb = new StringBuilder(); s.Dump(sb, 0); return sb.ToString(); }

    static HStmt RoundTrip(HStmt s) => CNodeBridge.ToHStmt(CNodeBridge.FromHStmt(s));

    [Fact]
    public void AllStructuredStatements_RoundTrip_PreserveStructure()
    {
        var body = new HBlock(new List<HStmt>
        {
            new HAssign(0, new HConst(1, "SystemInt32")),
            new HStoreField("score", new HSlotRef(0, "SystemInt32")),
            new HIf(new HSlotRef(0, "SystemBoolean"),
                new HBlock(new List<HStmt> { new HAssign(0, new HConst(2, "SystemInt32")) }),
                new HBlock(new List<HStmt> { new HReturn(new HConst(3, "SystemInt32")) })),
            new HGoto("done"),
            new HLabelStmt("done"),
            new HReturn(),
        });
        Assert.Equal(Dump(body), Dump(RoundTrip(body)));
    }

    [Fact]
    public void For_RoundTrip_PreservesInitCondUpdateBody()
    {
        var o = new HFor(
            new HBlock(new List<HStmt> { new HAssign(0, new HConst(0, "SystemInt32")) }),   // init
            new HSlotRef(0, "SystemBoolean"),                                                // cond
            new HBlock(new List<HStmt> { new HAssign(0, new HConst(1, "SystemInt32")) }),   // update
            new HBlock(new List<HStmt>                                                       // body
                { new HExprStmt(new HExternCall("Body", new List<HExpr>(), "SystemVoid")) }));
        var r = (HFor)RoundTrip(o);
        Assert.Single(r.Init.Stmts);
        Assert.Single(r.Update.Stmts);
        Assert.Single(r.Body.Stmts);
        Assert.Equal(0, ((HSlotRef)r.Cond).SlotId);
    }

    [Fact]
    public void While_RoundTrip_PreservesCondBlockAndFlag()
    {
        var o = new HWhile(new HSlotRef(0, "SystemBoolean"),
            new HBlock(new List<HStmt> { new HBreak() }),
            isDoWhile: false,
            condBlock: new HBlock(new List<HStmt> { new HAssign(1, new HConst(7, "SystemInt32")) }));
        var r = (HWhile)RoundTrip(o);
        Assert.Single(r.CondBlock.Stmts);
        Assert.Single(r.Body.Stmts);
        Assert.False(r.IsDoWhile);
    }

    [Fact]
    public void DoWhile_And_InfiniteFor_RoundTrip()
    {
        var doWhile = new HWhile(new HSlotRef(0, "SystemBoolean"),
            new HBlock(new List<HStmt> { new HAssign(0, new HConst(1, "SystemInt32")) }), isDoWhile: true);
        Assert.True(((HWhile)RoundTrip(doWhile)).IsDoWhile);

        var infFor = new HFor(new HBlock(), null, new HBlock(),
            new HBlock(new List<HStmt> { new HBreak() }));
        Assert.Null(((HFor)RoundTrip(infFor)).Cond);
    }

    [Fact]
    public void Statements_With_SelectAndCrossCall_Expressions_RoundTrip()
    {
        var sel = new HAssign(0, new HSelect(new HSlotRef(1, "SystemBoolean"),
            new HConst(1, "SystemInt32"), new HConst(2, "SystemInt32"), "SystemInt32"));
        var cross = new HExprStmt(new HCrossBehaviourCall(
            new HLoadField("enemy", "VRCUdonCommonInterfacesIUdonEventReceiver"), "Ping",
            new List<(string, HExpr)>(), new List<ReturnSlot>(), "SystemVoid"));
        var block = new HBlock(new List<HStmt> { sel, cross });
        Assert.Equal(Dump(block), Dump(RoundTrip(block)));
    }

    [Fact]
    public void FlatBlock_CarriesTerminatorAndId()
    {
        // type-level smoke for the flat role that CoreFlatten will populate
        var flat = new CBlock(new List<CStmt> { new CAssign(0, new CConst(1, "SystemInt32")) })
        {
            FlatId = 3,
            Terminator = new CJump(5),
        };
        Assert.Equal(3, flat.FlatId);
        Assert.Equal(5, ((CJump)flat.Terminator).TargetBlockId);

        var br = new CBranch(new CSlotRef(0, "SystemBoolean"), 1, 2);
        Assert.Equal(1, br.TrueBlockId);
        Assert.Equal(2, br.FalseBlockId);
    }
}
