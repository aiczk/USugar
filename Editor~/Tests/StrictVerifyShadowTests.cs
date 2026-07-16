using System.Linq;
using Xunit;

namespace USugar.Tests;

// Phase-B instrument check: the strict shadow must log exactly the GUESS-dependent passes of CoreVerify's
// relaxed type checks — and stay silent for fact-backed ones. The ledger is process-global (xUnit runs
// classes in parallel), so every assertion filters by this file's unique function names. The shared
// SelfTestFuncPrefix keeps these deliberate entries out of a mirrored measurement window.
public class StrictVerifyShadowTests
{
    static string Fn(string name) => StrictVerifyLedger.SelfTestFuncPrefix + name;

    static CFunction VerifyAssign(string funcName, string slotType, CValue value)
    {
        var func = new CFunction(funcName);
        func.Slots.Add(new SlotDecl(0, slotType, SlotClass.Frame));
        func.Body.Stmts.Add(new CAssign(0, value));
        CoreVerify.VerifyFunction(func);
        return func;
    }

    static string[] EntriesFor(string funcName)
        => StrictVerifyLedger.DrainForTest().Where(e => e.Contains("\"func\":\"" + funcName + "\"")).ToArray();

    [Fact]
    public void EnumGuess_UnknownName_IsLogged()
    {
        VerifyAssign(Fn("enumguess_unknown"), "FooMysteryType", new CConst(1, "SystemInt32"));
        var mine = EntriesFor(Fn("enumguess_unknown"));
        Assert.Contains(mine, e => e.Contains("\"arm\":\"enum-int32\"") && e.Contains("no-fact:FooMysteryType"));
    }

    [Fact]
    public void EnumGuess_FactEnum_IsSilent()
    {
        UdonTypeFacts.RecordForTest("SsvFakeSdkEnum", isEnum: true, isValueType: true);
        VerifyAssign(Fn("enumguess_fact"), "SsvFakeSdkEnum", new CConst(1, "SystemInt32"));
        Assert.Empty(EntriesFor(Fn("enumguess_fact")));
    }

    [Fact]
    public void RefCopyGuess_FactValueType_IsLogged()
    {
        // The relaxed prefix-list heuristic calls any unlisted non-primitive a reference — an SDK struct
        // like Bounds slips through as COPY-compatible with a real reference. The registry knows better.
        UdonTypeFacts.RecordForTest("SsvFakeBounds", isEnum: false, isValueType: true);
        UdonTypeFacts.RecordForTest("SsvFakeGameObject", isEnum: false, isValueType: false);
        VerifyAssign(Fn("refguess_valuetype"), "SsvFakeBounds", new CConst(null, "SsvFakeGameObject"));
        var mine = EntriesFor(Fn("refguess_valuetype"));
        Assert.Contains(mine, e => e.Contains("\"arm\":\"ref-copy\"")
            && e.Contains("fact-contradicts:SsvFakeBounds is a value type"));
    }

    [Fact]
    public void RefCopyGuess_BothFactReferences_IsSilent()
    {
        UdonTypeFacts.RecordForTest("SsvFakeTransform", isEnum: false, isValueType: false);
        UdonTypeFacts.RecordForTest("SsvFakeCollider", isEnum: false, isValueType: false);
        var func = new CFunction(Fn("refguess_bothref"));
        func.Slots.Add(new SlotDecl(0, "SsvFakeTransform", SlotClass.Frame));
        func.Body.Stmts.Add(new CAssign(0, new CConst(null, "SsvFakeCollider")));
        CoreVerify.VerifyFunction(func);
        Assert.Empty(EntriesFor(Fn("refguess_bothref")));
    }
}
