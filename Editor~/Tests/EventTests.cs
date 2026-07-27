using System;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// `event` keyword (design 2026-07-03 §2, A-M2). Structural/UASM-level and reject-surface pins for the
/// headless main suite — real VM VALUE verification (subscribe→invoke, mid-invoke snapshot, null-event
/// guard, unsubscribe) lives in Editor~/_local_harness/EventVmTests.cs (DiffFuzz CLR-vs-UVM oracle).
/// </summary>
public class EventTests
{
    // ── field-like event materializes backing storage + combine/remove/fanout, same as a delegate field ──

    [Fact]
    public void FieldLikeEvent_MaterializesBackingFieldAndMulticastSynthetics()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class EvtBasic : UdonSharpBehaviour {
    public event Action Foo;
    void Sub() { Foo += () => { }; }
    void Fire() { Foo(); }
    void Start() { Sub(); Fire(); }
}", "EvtBasic");
        Assert.Contains("Foo: %SystemObjectArray, null", uasm);
        Assert.DoesNotContain(".export Foo", uasm);   // backing storage is private (§2.1)
        Assert.Contains("__dlg_combine_Void__Void", uasm);
        Assert.Contains("__dlg_fanout_Void__Void", uasm);
    }

    [Fact]
    public void FieldLikeEvent_WithInitializer_Compiles()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class EvtInit : UdonSharpBehaviour {
    public int hits;
    event Action Foo = null;
    void M() { }
    void Start() { Foo += M; Foo?.Invoke(); }
}", "EvtInit");

    [Fact]
    public void FieldLikeEvent_InheritedFromBaseClass_SubscribeInvokeCompiles()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class EvtBaseA : UdonSharpBehaviour {
    public event Action Foo;
    protected void RaiseFoo() { Foo(); }
}
public class EvtBaseB : EvtBaseA {
    void Start() { Foo += () => { }; RaiseFoo(); }
}", "EvtBaseB");

    // ── R1: custom-accessor events are field-like only ──

    [Fact]
    public void CustomAccessorEvent_CompilesAndCallsAccessors()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class EvtCustom : UdonSharpBehaviour {
    Action _backing;
    public event Action Foo { add { _backing += value; } remove { _backing -= value; } }
    void Handler() { }
    void Start() { Foo += Handler; Foo -= Handler; }
}", "EvtCustom");
        Assert.Contains("_backing", uasm);
    }

    [Fact]
    public void StaticCustomAccessorEvent_CompilesAndCallsAccessors()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class EvtStaticCustom : UdonSharpBehaviour {
    static Action _backing;
    public static event Action Foo { add { _backing += value; } remove { _backing -= value; } }
    void Handler() { }
    void Start() { Foo += Handler; Foo -= Handler; }
}", "EvtStaticCustom");
        Assert.Contains("_backing", uasm);
    }

    // ── R2: cross-behaviour field-like event subscription ──

    [Fact]
    public void CrossBehaviourEventSubscribe_UsesProgramVariableRmw()
    {
        var uasm = TestHelper.CompileToUasm(new[]
        {
            @"
using UdonSharp;
using System;
public class EvtTargetX : UdonSharpBehaviour {
    public event Action Foo;
}",
            @"
using UdonSharp;
using System;
public class EvtSubscriberX : UdonSharpBehaviour {
    public EvtTargetX other;
    void Handler() { }
    void Start() { other.Foo += Handler; other.Foo -= Handler; }
}",
        }, "EvtSubscriberX");
        Assert.Contains("__GetProgramVariable__", uasm);
        Assert.Contains("__SetProgramVariable__", uasm);
        Assert.Contains("__dlg_combine_", uasm);
        Assert.Contains("__dlg_remove_", uasm);
    }

    [Fact]
    public void CrossBehaviourCustomEventSubscribe_CallsAccessors()
    {
        var uasm = TestHelper.CompileToUasm(new[]
        {
            @"
using UdonSharp;
using System;
public class EvtCustomTargetX : UdonSharpBehaviour {
    Action _foo;
    public event Action Foo { add { _foo += value; } remove { _foo -= value; } }
}",
            @"
using UdonSharp;
using System;
public class EvtCustomSubscriberX : UdonSharpBehaviour {
    public EvtCustomTargetX other;
    void Handler() { }
    void Start() { other.Foo += Handler; other.Foo -= Handler; }
}",
        }, "EvtCustomSubscriberX");
        Assert.Contains("__SendCustomEvent__", uasm);
    }

    // ── R4: [UdonSynced] / [NetworkCallable] on an event ──

    [Fact]
    public void UdonSyncedEvent_Throws()
    {
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class EvtSynced : UdonSharpBehaviour {
    [UdonSynced]
    public event Action Foo;
}", "EvtSynced"));
        Assert.Contains("[UdonSynced] delegate field 'Foo' cannot be synced", ex.Message);
    }

    [Fact]
    public void NetworkCallableEvent_Throws()
    {
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
using VRC.SDK3.UdonNetworkCalling;
public class EvtNetCallable : UdonSharpBehaviour {
    [NetworkCallable]
    public event Action Foo;
}", "EvtNetCallable"));
        Assert.Contains("[NetworkCallable]", ex.Message);
    }

    // ── static event: no shared-static storage on Udon ──

    [Fact]
    public void StaticEvent_UsesOwnerScopedStorage()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class EvtStatic : UdonSharpBehaviour {
    public static event Action Foo;
    public int Result;
    void Mark() { Result++; }
    void Start() { Foo += Mark; Foo(); Foo -= Mark; }
}", "EvtStatic");
        Assert.Contains("_event_Foo", uasm);
    }

    // ── tuple-return delegate event (Stage 1.75 design 2026-07-04 §1): SUPPORTED ──
    // ref-out delegate event reject (R9, inherited policy) stays below.

    [Fact]
    public void TupleReturnDelegateEvent_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public delegate (int, int) PairDel();
public class EvtTupleRet : UdonSharpBehaviour {
    public event PairDel Foo;
}", "EvtTupleRet");
        Assert.Contains("SystemObjectArray", uasm);
    }

    [Fact]
    public void RefOutDelegateEvent_UsesBundleAbi()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public delegate void RefDel(ref int x);
public class EvtRefOut : UdonSharpBehaviour {
    public event RefDel Foo;
}", "EvtRefOut");
        Assert.Contains("Foo: %SystemObjectArray", uasm);
    }

    // ── design §8 item 6: event name colliding with an inherited field/prop (or vice versa) is
    //    'new'-style shadowing — two C# storages that would collapse onto one heap var. ──

    [Fact]
    public void EventShadowingBaseField_Throws()
    {
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class EvtShadowBase : UdonSharpBehaviour {
    public Action Foo;
}
public class EvtShadowDerived : EvtShadowBase {
    public new event Action Foo;
}", "EvtShadowDerived"));
        Assert.Contains("hidden by", ex.Message);
    }

    [Fact]
    public void FieldShadowingBaseEvent_Throws()
    {
        var ex = Assert.ThrowsAny<Exception>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class FieldShadowBase : UdonSharpBehaviour {
    public event Action Foo;
}
public class FieldShadowDerived : FieldShadowBase {
    public new Action Foo;
}", "FieldShadowDerived"));
        Assert.Contains("hidden by", ex.Message);
    }
}
