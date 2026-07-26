using Xunit;

namespace USugar.Tests;

// Phase-D enforcement pins (the Phase-B shadow's successor): CoreVerify's relaxed-arm GUESSES are gone —
// a slot/value type pair may differ only under RawCopyCompatibility (SystemObject wildcard, Nullable
// erasure, fact-enum↔Int32, both-fact-reference COPY). These tests pin the flipped polarity: fact-backed
// pairs still pass (accept controls), while no-fact and fact-contradicted pairs throw loudly, naming the
// missing fact.
public class StrictVerifyShadowTests
{
    static string Fn(string name) => "ssv_" + name;

    static FlatFunction VerifyAssign(string funcName, string slotType, CValue value,
        UdonTypeFactRegistry typeFacts = null)
    {
        var module = new FlatModule(typeFacts);
        var function = module.AddFunction(funcName);
        function.Slots.Add(new SlotDecl(
            0, new StorageType(slotType), SlotClass.Frame));
        function.Blocks.Add(new FlatBlock(
            0,
            new[] { (IFlatInstruction)new CAssign(0, value) },
            new CRet()));
        FlatVerify.Verify(module);
        return function;
    }

    [Fact]
    public void EnumInterop_NoFactName_Throws()
    {
        var ex = Assert.Throws<VerificationException>(
            () => VerifyAssign(Fn("enumguess_unknown"), "FooMysteryType", new CConst(1, StorageTypes.Int32)));
        Assert.Contains("no fact recorded for 'FooMysteryType'", ex.Message);
    }

    [Fact]
    public void EnumInterop_FactEnum_Passes() // accept control
    {
        var facts = new UdonTypeFactRegistry();
        facts.RecordForTest("SsvFakeSdkEnum", isEnum: true, isValueType: true);
        VerifyAssign(Fn("enumguess_fact"), "SsvFakeSdkEnum", new CConst(1, StorageTypes.Int32), facts); // no throw
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
                new CConst(null, new StorageType("SsvFakeGameObject")), facts));
        Assert.Contains("'SsvFakeBounds' is a fact value type", ex.Message);
    }

    [Fact]
    public void RefCopy_BothFactReferences_Passes() // accept control
    {
        var facts = new UdonTypeFactRegistry();
        facts.RecordForTest("SsvFakeTransform", isEnum: false, isValueType: false);
        facts.RecordForTest("SsvFakeCollider", isEnum: false, isValueType: false);
        VerifyAssign(Fn("refguess_bothref"), "SsvFakeTransform",
            new CConst(null, new StorageType("SsvFakeCollider")), facts); // no throw
    }

}
