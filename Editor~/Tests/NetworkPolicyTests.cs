using Xunit;

namespace USugar.Tests;

/// <summary>
/// M4 policy rejects (design §5.3/§1.6). [T1]: a [NetworkCallable] method's parameters cross the
/// network, but a delegate value is a program-local object[] bundle whose target/addr are
/// meaningless in any other client's program — pre-fix (probed at 931a9ab) a Func&lt;int&gt; param
/// compiled CLEAN (exported unmangled, SystemObjectArray param var), a silent runtime miscompile;
/// a delegate-typed RETURN also compiled clean even though stock UdonSharp forbids ANY return type
/// on [NetworkCallable]. [T2]: [UdonSynced] on a delegate field — pre-fix the own-class path threw
/// the generic IsSyncableType message while the BASE-class path compiled CLEAN with no .sync
/// directive (silent). Both are now loud, delegate-specific compile errors; plain-data
/// NetworkCallable methods and syncable [UdonSynced] fields are pinned compile-positive.
/// </summary>
public class NetworkPolicyTests
{
    // ── [T1] NetworkCallable delegate-param / delegate-return rejects ──

    [Fact]
    public void NetworkCallable_DelegateParam_Throws()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDK3.UdonNetworkCalling;
public class NetPol1 : UdonSharpBehaviour {
    [NetworkCallable]
    public void Handle(System.Func<int> f) { var x = f(); }
}", "NetPol1"));
        Assert.Contains("[NetworkCallable] method 'Handle' cannot take delegate-typed parameter 'f'", ex.Message);
        Assert.Contains("cannot cross a network call", ex.Message);
    }

    [Fact]
    public void NetworkCallable_DelegateArrayParam_Throws()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDK3.UdonNetworkCalling;
public class NetPol2 : UdonSharpBehaviour {
    [NetworkCallable]
    public void Handle(System.Func<int>[] fs) { var x = fs.Length; }
}", "NetPol2"));
        Assert.Contains("delegate-typed parameter 'fs'", ex.Message);
    }

    [Fact]
    public void NetworkCallable_DelegateReturn_Throws()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDK3.UdonNetworkCalling;
public class NetPol3 : UdonSharpBehaviour {
    [NetworkCallable]
    public System.Func<int> Get() { return null; }
}", "NetPol3"));
        Assert.Contains("[NetworkCallable] method 'Get' cannot return a delegate-typed value", ex.Message);
    }

    [Fact]
    public void NetworkCallable_InheritedDelegateParam_ThrowsInDerivedProgram()
    {
        // The first-pass registration loop covers own + inherited behaviour methods, so the
        // derived class's program rejects an inherited NetworkCallable delegate-param method too.
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDK3.UdonNetworkCalling;
public class NetPolBase4 : UdonSharpBehaviour {
    [NetworkCallable]
    public void Handle(System.Func<int> f) { var x = f(); }
}
public class NetPol4 : NetPolBase4 {
    public int n;
}", "NetPol4"));
        Assert.Contains("delegate-typed parameter 'f'", ex.Message);
    }

    [Fact]
    public void NetworkCallable_PlainParams_StillCompile()
    {
        // Positive control: int/string params are the supported networking shape (validated
        // real-world projects depend on it) — exported unmangled, param vars mangled.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDK3.UdonNetworkCalling;
public class NetPol5 : UdonSharpBehaviour {
    public int total;
    [NetworkCallable]
    public void Handle(int a, string b) { total = a + b.Length; }
}", "NetPol5");
        Assert.Contains(".export Handle", uasm);
    }

    // ── [T2] UdonSynced delegate-field reject (both declaration paths) ──

    [Fact]
    public void UdonSynced_DelegateField_ThrowsDelegateSpecificMessage()
    {
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class SyncPol1 : UdonSharpBehaviour {
    [UdonSynced] public System.Func<int> cb;
}", "SyncPol1"));
        Assert.Contains("[UdonSynced] delegate field 'cb' cannot be synced", ex.Message);
        Assert.Contains("object[] bundle", ex.Message);
    }

    [Fact]
    public void UdonSynced_DelegateField_OnBaseClass_ThrowsInDerivedProgram()
    {
        // Pre-fix hole: the base-class field walk read no sync attributes, so the derived program
        // compiled the synced delegate field CLEAN (no .sync directive, silent).
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class SyncPolBase2 : UdonSharpBehaviour {
    [UdonSynced] public System.Func<int> cb;
}
public class SyncPol2 : SyncPolBase2 {
    public int n;
}", "SyncPol2"));
        Assert.Contains("[UdonSynced] delegate field 'cb' cannot be synced", ex.Message);
    }

    [Fact]
    public void UdonSynced_DelegateArrayField_StillThrowsGenericSyncError()
    {
        // A delegate ARRAY field is not bundled through DeclareDelegateField (array type) — it stays
        // on the generic IsSyncableType reject. Loud either way; pinned so the path never goes quiet.
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class SyncPol3 : UdonSharpBehaviour {
    [UdonSynced] public System.Func<int>[] cbs;
}", "SyncPol3"));
        Assert.Contains("Cannot sync field 'cbs'", ex.Message);
    }

    [Fact]
    public void UdonSynced_InvalidType_MessageNamesFieldAndListsSyncableTypes()
    {
        // General sync-message quality (M4 'sync error message improvement'): the reject names the
        // field and now tells the user what Udon CAN sync. Accept/reject surface unchanged.
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class SyncPol4 : UdonSharpBehaviour {
    [UdonSynced] public UnityEngine.GameObject go;
}", "SyncPol4"));
        Assert.Contains("Cannot sync field 'go'", ex.Message);
        Assert.Contains("Udon can sync only", ex.Message);
    }

    [Fact]
    public void UdonSynced_IntField_StillCompilesWithSyncDirective()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SyncPol5 : UdonSharpBehaviour {
    [UdonSynced] public int score;
}", "SyncPol5");
        Assert.Contains(".sync score", uasm);
    }

    [Fact]
    public void UnsyncedDelegateField_StillCompiles()
    {
        // Positive control: the new DeclareDelegateField sync check must not touch plain delegate
        // fields on either declaration path (own-class and inherited-from-base).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SyncPolBase6 : UdonSharpBehaviour {
    public System.Func<int> baseCb;
}
public class SyncPol6 : SyncPolBase6 {
    public System.Func<int> cb;
    public int M5() { return 5; }
    void Start() { cb = M5; baseCb = M5; }
}", "SyncPol6");
        Assert.Contains("cb: %SystemObjectArray", uasm);
        Assert.Contains("baseCb: %SystemObjectArray", uasm);
    }

    // ── [B37] base-class [UdonSynced] fields keep their .sync directive in the derived program ──
    //
    // Pre-fix (probed at e1a4907, evidence in the M4 pre-fix probe P2f): the base-class field walk
    // read NO sync attributes — a [UdonSynced] int declared on a user base class compiled CLEAN in
    // the derived program but shipped WITHOUT .sync (networking silently dead on device). The fix
    // shares the own-class sync-attribute reader (ReadFieldSyncMode) with the base walk. The .sync
    // directive + mode in UASM is the correct headless gate — sync RUNTIME semantics cannot run in
    // the single-program harness; on-device confirmation is an acceptance-checklist item.
    //
    // The Linear-mode flavors use a test-source-local UdonSyncedAttribute stub (the emitter resolves
    // the attribute by simple name, exactly like production against the real SDK) because the shared
    // TestHelper stub's attribute is parameterless and TestHelper is gate code.

    // Appended AFTER the behaviour source (using directives must precede namespace declarations).
    const string ModeStub = @"
namespace SyncStub {
    public enum UdonSyncMode { NotSynced, None, Linear, Smooth }
    public class UdonSyncedAttribute : System.Attribute { public UdonSyncedAttribute(UdonSyncMode mode) { } }
}";

    [Fact]
    public void UdonSynced_IntField_OnBaseClass_EmitsSyncInDerivedProgram()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SyncPolBase7 : UdonSharpBehaviour {
    [UdonSynced] public int v;
    public int plain;
}
public class SyncPol7 : SyncPolBase7 {
    public int n;
}", "SyncPol7");
        Assert.Contains(".sync v, none", uasm);
        // Negative control in the SAME derived program: an unsynced base field stays unsynced.
        Assert.DoesNotContain(".sync plain", uasm);
        Assert.DoesNotContain(".sync n", uasm);
    }

    [Fact]
    public void UdonSynced_LinearFloatField_OnBaseClass_EmitsSyncModeInDerivedProgram()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SyncPolBase8 : UdonSharpBehaviour {
    [SyncStub.UdonSynced(SyncStub.UdonSyncMode.Linear)] public float speed;
}
public class SyncPol8 : SyncPolBase8 {
    public int n;
}" + ModeStub, "SyncPol8");
        Assert.Contains(".sync speed, linear", uasm);
    }

    [Fact]
    public void UdonSynced_LinearFloatField_OwnClass_EmitsSyncMode()
    {
        // Own-class control (green pre-fix): pins the sync-mode argument mapping AND proves the
        // test-source-local attribute stub is resolved identically to the shared stub.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SyncPol9 : UdonSharpBehaviour {
    [SyncStub.UdonSynced(SyncStub.UdonSyncMode.Linear)] public float speed;
}" + ModeStub, "SyncPol9");
        Assert.Contains(".sync speed, linear", uasm);
    }

    [Fact]
    public void UdonSynced_InvalidType_OnBaseClass_ThrowsInDerivedProgram()
    {
        // The shared path brings the syncable-type validation to the base walk too: pre-fix a
        // [UdonSynced] GameObject base field compiled CLEAN in the derived program (doubly silent —
        // no .sync AND no reject); the same field on the own class always threw.
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class SyncPolBase10 : UdonSharpBehaviour {
    [UdonSynced] public UnityEngine.GameObject go;
}
public class SyncPol10 : SyncPolBase10 {
    public int n;
}", "SyncPol10"));
        Assert.Contains("Cannot sync field 'go'", ex.Message);
        Assert.Contains("Udon can sync only", ex.Message);
    }

    [Fact]
    public void FieldChangeCallback_OnBaseClassField_EmitsCallbackInDerivedProgram()
    {
        // B37 probe finding: FieldChangeCallback was ALREADY mirrored on the base-class walk
        // (registration + __old_ var; the _onVarChange_ export rides the inherited setter) — no
        // B37-style gap. Pinned so the mirror never silently regresses.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class FcbPolBase11 : UdonSharpBehaviour {
    [FieldChangeCallback(""Score"")] public int score;
    public int Score { get { return score; } set { score = value; } }
}
public class FcbPol11 : FcbPolBase11 {
    public int n;
}", "FcbPol11");
        Assert.Contains("__old_score:", uasm);
        Assert.Contains(".export _onVarChange_score", uasm);
    }

    [Fact]
    public void FieldChangeCallback_OwnClassField_EmitsCallback()
    {
        // Own-class control for the base-path FCB pin above.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class FcbPol12 : UdonSharpBehaviour {
    [FieldChangeCallback(""Score"")] public int score;
    public int Score { get { return score; } set { score = value; } }
}", "FcbPol12");
        Assert.Contains("__old_score:", uasm);
        Assert.Contains(".export _onVarChange_score", uasm);
    }
}
