using System.Linq;
using Xunit;

namespace USugar.Tests;

// Phase-D enforcement pins (the Phase-B shadow's successor): CoreVerify's relaxed-arm GUESSES are gone —
// a slot/value type pair may differ only under DeclaredRelaxations (SystemObject wildcard, Nullable
// erasure, fact-enum↔Int32, both-fact-reference COPY). These tests pin the flipped polarity: fact-backed
// pairs still pass (accept controls), while no-fact and fact-contradicted pairs throw loudly, naming the
// missing fact. The kept ledger infrastructure retains one smoke test; its SelfTestFuncPrefix keeps that
// deliberate entry out of any USUGAR_STRICT_SHADOW mirror window.
public class StrictVerifyShadowTests
{
    static string Fn(string name) => StrictVerifyLedger.SelfTestFuncPrefix + name;

    static CFunction VerifyAssign(string funcName, string slotType, CValue value,
        UdonTypeFactRegistry typeFacts = null)
    {
        var func = new CFunction(funcName);
        func.Slots.Add(new SlotDecl(0, slotType, SlotClass.Frame));
        func.Body.Stmts.Add(new CAssign(0, value));
        CoreVerify.VerifyFunction(func, typeFacts);
        return func;
    }

    [Fact]
    public void EnumInterop_NoFactName_Throws()
    {
        var ex = Assert.Throws<VerificationException>(
            () => VerifyAssign(Fn("enumguess_unknown"), "FooMysteryType", new CConst(1, "SystemInt32")));
        Assert.Contains("no fact recorded for 'FooMysteryType'", ex.Message);
    }

    [Fact]
    public void EnumInterop_FactEnum_Passes() // accept control
    {
        var facts = new UdonTypeFactRegistry();
        facts.RecordForTest("SsvFakeSdkEnum", isEnum: true, isValueType: true);
        VerifyAssign(Fn("enumguess_fact"), "SsvFakeSdkEnum", new CConst(1, "SystemInt32"), facts); // no throw
    }

    [Fact]
    public void RefCopy_FactValueType_Throws()
    {
        // The deleted prefix-list heuristic called any unlisted non-primitive a reference — an SDK struct
        // like Bounds slipped through as COPY-compatible with a real reference. The facts know better.
        var facts = new UdonTypeFactRegistry();
        facts.RecordForTest("SsvFakeBounds", isEnum: false, isValueType: true);
        facts.RecordForTest("SsvFakeGameObject", isEnum: false, isValueType: false);
        var ex = Assert.Throws<VerificationException>(
            () => VerifyAssign(Fn("refguess_valuetype"), "SsvFakeBounds",
                new CConst(null, "SsvFakeGameObject"), facts));
        Assert.Contains("'SsvFakeBounds' is a fact value type", ex.Message);
    }

    [Fact]
    public void RefCopy_BothFactReferences_Passes() // accept control
    {
        var facts = new UdonTypeFactRegistry();
        facts.RecordForTest("SsvFakeTransform", isEnum: false, isValueType: false);
        facts.RecordForTest("SsvFakeCollider", isEnum: false, isValueType: false);
        VerifyAssign(Fn("refguess_bothref"), "SsvFakeTransform",
            new CConst(null, "SsvFakeCollider"), facts); // no throw
    }

    [Fact]
    public void Ledger_RecordGuess_StaysDrainable() // kept infrastructure (future verifiers) must not rot
    {
        StrictVerifyLedger.RecordGuess("smoke", "A", "B", "ctx", Fn("ledger_smoke"), "kept-infrastructure");
        var mine = StrictVerifyLedger.DrainForTest()
            .Where(e => e.Contains("\"func\":\"" + Fn("ledger_smoke") + "\"")).ToArray();
        Assert.Contains(mine, e => e.Contains("\"arm\":\"smoke\""));
    }
}
