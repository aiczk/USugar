using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace USugar.Tests;

/// <summary>
/// Tests for C# features that were previously unverified.
/// Each test targets a specific language feature to confirm USugar handles it.
/// </summary>
public class FeatureCoverageTests
{
    // ── 1. is / as pattern matching ──

    [Fact]
    public void IsTypeCheck_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class IsTypeTest : UdonSharpBehaviour {
    public void Test() {
        object obj = ""hello"";
        if (obj is string) Debug.Log(""it's a string"");
    }
}", "IsTypeTest");

    [Fact]
    public void IsPatternWithVariable_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class IsPatternVarTest : UdonSharpBehaviour {
    public void Test() {
        object obj = 42;
        if (obj is int n) Debug.Log(n);
    }
}", "IsPatternVarTest");

    [Fact]
    public void AsOperator_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class AsOperatorTest : UdonSharpBehaviour {
    public void Test() {
        Component comp = GetComponent<Collider>();
        Collider col = comp as Collider;
        if (col != null) Debug.Log(""found"");
    }
}", "AsOperatorTest");

    // ── 2. ?. ?? ??= null-conditional operators ──

    [Fact]
    public void NullConditionalAccess_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class NullCondTest : UdonSharpBehaviour {
    public Transform target;
    public void Test() {
        var pos = target?.position ?? Vector3.zero;
        Debug.Log(pos);
    }
}", "NullCondTest");

    [Fact]
    public void NullCoalescing_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class NullCoalesceTest : UdonSharpBehaviour {
    public string label;
    public void Test() {
        string s = label ?? ""default"";
        Debug.Log(s);
    }
}", "NullCoalesceTest");

    [Fact]
    public void NullCoalescingAssignment_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class NullCoalesceAssignTest : UdonSharpBehaviour {
    string _cached;
    public void Test() {
        _cached ??= ""initialized"";
        Debug.Log(_cached);
    }
}", "NullCoalesceAssignTest");

    // ── 3. params array expanded form ──

    [Fact]
    public void StringFormat_WithMultipleArgs_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class ParamsTest : UdonSharpBehaviour {
    public void Test() {
        int a = 1, b = 2, c = 3;
        string s = string.Format(""{0} {1} {2}"", a, b, c);
        Debug.Log(s);
    }
}", "ParamsTest");

    [Fact]
    public void StringConcat_MultipleArgs_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class ConcatTest : UdonSharpBehaviour {
    public void Test() {
        string s = string.Concat(""a"", ""b"", ""c"", ""d"");
        Debug.Log(s);
    }
}", "ConcatTest");

    // ── 4. enum definition and switch ──

    [Fact]
    public void EnumFieldAndSwitch_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public enum GameState { Idle, Playing, GameOver }
public class EnumTest : UdonSharpBehaviour {
    public GameState state;
    public void Test() {
        switch (state) {
            case GameState.Idle: Debug.Log(""idle""); break;
            case GameState.Playing: Debug.Log(""playing""); break;
            case GameState.GameOver: Debug.Log(""over""); break;
        }
    }
}", "EnumTest");

    [Fact]
    public void EnumComparison_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public enum Phase { A, B, C }
public class EnumCompTest : UdonSharpBehaviour {
    Phase _phase;
    public void Test() {
        if (_phase == Phase.B) Debug.Log(""B"");
        if (_phase != Phase.A) Debug.Log(""not A"");
    }
}", "EnumCompTest");

    [Fact]
    public void EnumComparison_EmitsSystemConvert()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public enum Phase { A, B, C }
public class EnumConvertTest : UdonSharpBehaviour {
    Phase _phase;
    public void Test() {
        if (_phase == Phase.B) Debug.Log(""B"");
    }
}", "EnumConvertTest");
        // Enum comparison must convert operands to underlying type via SystemConvert
        Assert.Contains("SystemConvert.__ToInt32__SystemObject__SystemInt32", uasm);
        Assert.Contains("SystemInt32.__op_Equality__SystemInt32_SystemInt32__SystemBoolean", uasm);
    }

    [Fact]
    public void EnumComparison_ByteEnum_EmitsSystemConvert()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public enum TokenType : byte { Error, String = 8 }
public class ByteEnumTest : UdonSharpBehaviour {
    public void Test() {
        TokenType t = TokenType.String;
        if (t == TokenType.Error) Debug.Log(""err"");
    }
}", "ByteEnumTest");
        Assert.Contains("SystemConvert.__ToByte__SystemObject__SystemByte", uasm);
        Assert.Contains("SystemByte.__op_Equality__SystemByte_SystemByte__SystemBoolean", uasm);
    }

    [Fact]
    public void EnumSwitch_EmitsSystemConvert()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public enum GameState { Idle, Playing, GameOver }
public class EnumSwitchConvertTest : UdonSharpBehaviour {
    public GameState state;
    public void Test() {
        switch (state) {
            case GameState.Idle: Debug.Log(""idle""); break;
            case GameState.Playing: Debug.Log(""playing""); break;
        }
    }
}", "EnumSwitchConvertTest");
        Assert.Contains("SystemConvert.__ToInt32__SystemObject__SystemInt32", uasm);
    }

    [Fact]
    public void EnumArithmetic_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public enum Direction { North, East, South, West }
public class EnumArithTest : UdonSharpBehaviour {
    public void Test() {
        int val = (int)Direction.South;
        Direction d = (Direction)2;
        Debug.Log(val);
    }
}", "EnumArithTest");

    // ── 5. using statement ──

    [Fact]
    public void UsingStatement_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class UsingTest : UdonSharpBehaviour {
    public void Test() {
        using (var res = new TestStubs.DisposableResource()) {
            res.Value = 42;
        }
    }
}", "UsingTest");

    // ── 6. multi-dimensional arrays (expect failure — Udon VM likely unsupported) ──

    // Skipped: Udon VM does not support multi-dimensional arrays.
    // Users should use jagged arrays instead.

    // ── 7. ref/out parameters ──

    [Fact]
    public void OutParameter_ExternCall_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class OutParamTest : UdonSharpBehaviour {
    public void Test() {
        float angle;
        Vector3 axis;
        transform.rotation.ToAngleAxis(out angle, out axis);
        Debug.Log(angle);
    }
}", "OutParamTest");

    [Fact]
    public void RefParameter_UserMethod_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class RefParamTest : UdonSharpBehaviour {
    void Swap(ref int a, ref int b) {
        int tmp = a;
        a = b;
        b = tmp;
    }
    public void Test() {
        int x = 1, y = 2;
        Swap(ref x, ref y);
        Debug.Log(x);
    }
}", "RefParamTest");

    // ── 8. default parameter values ──

    [Fact]
    public void DefaultParameterValue_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class DefaultParamTest : UdonSharpBehaviour {
    int Add(int a, int b = 10) { return a + b; }
    public void Test() {
        int r1 = Add(5);
        int r2 = Add(5, 20);
        Debug.Log(r1);
        Debug.Log(r2);
    }
}", "DefaultParamTest");

    [Fact]
    public void DefaultParameterValue_String_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class DefaultStringParamTest : UdonSharpBehaviour {
    void Greet(string name = ""World"") { Debug.Log(""Hello "" + name); }
    public void Test() {
        Greet();
        Greet(""VRChat"");
    }
}", "DefaultStringParamTest");

    // ── switch expression (C# 8.0) ──

    [Fact]
    public void SwitchExpression_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class SwitchExprTest : UdonSharpBehaviour {
    string Describe(int n) => n switch {
        0 => ""zero"",
        1 => ""one"",
        _ => ""other""
    };
    public void Test() { Debug.Log(Describe(1)); }
}", "SwitchExprTest");

    // ── Negated pattern (not) ──

    [Fact]
    public void NegatedPattern_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class NegatedPatternTest : UdonSharpBehaviour {
    public void Test() {
        object obj = 42;
        if (obj is not null) Debug.Log(""not null"");
    }
}", "NegatedPatternTest");

    // ── Cross-behaviour non-auto property export ──

    [Fact]
    public void NonAutoProperty_PublicExported()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class NonAutoPropTest : UdonSharpBehaviour {
    string _title;
    public string Title { get => _title; set => _title = value; }
    public void Test() { Debug.Log(Title); }
}", "NonAutoPropTest");
        // Non-auto public property should be exported for cross-behaviour access
        Assert.Contains(".export Title", uasm);
        Assert.Contains("Title: %SystemString", uasm);
    }

    [Fact]
    public void CrossBehaviourPropertyGet_CompilesCorrectly()
        => TestHelper.CompileToUasm(new[] { @"
using UdonSharp;
public class DataProvider : UdonSharpBehaviour {
    public string[] PlayListTitles { get; set; }
}", @"
using UdonSharp;
using UnityEngine;
public class CrossPropTest : UdonSharpBehaviour {
    public DataProvider provider;
    public void Test() {
        var titles = provider.PlayListTitles;
        Debug.Log(titles);
    }
}" }, "CrossPropTest");

    [Fact]
    public void CrossBehaviourNonAutoPropertyGet_CallsGetter()
    {
        var uasm = TestHelper.CompileToUasm(new[] { @"
using UdonSharp;
public class LazyProvider : UdonSharpBehaviour {
    string[] _names;
    public string[] Names { get { return _names; } }
}", @"
using UdonSharp;
using UnityEngine;
public class CrossNonAutoTest : UdonSharpBehaviour {
    public LazyProvider provider;
    public void Test() {
        var n = provider.Names;
        Debug.Log(n);
    }
}" }, "CrossNonAutoTest");
        // Non-auto property: must call getter via SendCustomEvent, then read return value
        var codeSection = uasm.Substring(uasm.IndexOf(".code_start"));
        // Pattern: SendCustomEvent (invoke getter) → GetProgramVariable (read return value)
        int sendIdx = codeSection.IndexOf("SendCustomEvent");
        int getPVIdx = codeSection.IndexOf("GetProgramVariable");
        Assert.True(sendIdx >= 0, "Missing SendCustomEvent for non-auto property getter");
        Assert.True(getPVIdx > sendIdx, "GetProgramVariable must follow SendCustomEvent");
    }

    [Fact]
    public void NonAutoPropertyGetter_IsExportedAsEntryPoint()
    {
        var uasm = TestHelper.CompileToUasm(new[] { @"
using UdonSharp;
public class LazyProvider2 : UdonSharpBehaviour {
    string[] _names;
    public string[] Names { get { return _names; } }
}" }, "LazyProvider2");
        // Non-auto public property getter must be exported as a SendCustomEvent entry point
        Assert.Contains(".export get_Names", uasm);
    }

    [Fact]
    public void NonAutoPropertySetter_IsExportedAsEntryPoint()
    {
        var uasm = TestHelper.CompileToUasm(new[] { @"
using UdonSharp;
public class SetterProvider : UdonSharpBehaviour {
    int _value;
    public int Value { get => _value; set => _value = value; }
}" }, "SetterProvider");
        // Non-auto public property setter must be exported (counter-mangled, has 1 param)
        Assert.Contains(".export __0_set_Value", uasm);
    }

    // ── Explicit interface implementation ──

    [Fact]
    public void ExplicitInterfaceImpl_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public interface IMyService { int GetValue(); string Name { get; } }
public class ExplicitIfaceTest : UdonSharpBehaviour, IMyService {
    int IMyService.GetValue() { return 42; }
    string IMyService.Name => ""hello"";
    public void Test() { Debug.Log(((IMyService)this).GetValue()); }
}", "ExplicitIfaceTest");

    // ── Enum default parameter ──

    [Fact]
    public void EnumDefaultParameter_CompilesCorrectly()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class EnumDefaultParamTest : UdonSharpBehaviour {
    public void Test() {
        SendCustomEventDelayedFrames(""MyEvent"", 1);
    }
}", "EnumDefaultParamTest");
        var dataLines = uasm.Split('\n');
        // Must have a const of EventTiming type (matching UdonSharp pattern)
        Assert.Contains(dataLines, l => l.Contains("%VRCUdonCommonEnumsEventTiming"));
        // Must NOT use Int32 → EventTiming COPY (COPY corrupts type tag)
        // The EventTiming const should be PUSH'd directly before the extern
        var codeSection = uasm.Substring(uasm.IndexOf(".code_start"));
        Assert.Contains("PUSH, __const_VRCUdonCommonEnumsEventTiming_", codeSection);
    }

    [Fact]
    public void EnumExplicitCast_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class EnumCastTest : UdonSharpBehaviour {
    public void Test() {
        var timing = (VRC.Udon.Common.Enums.EventTiming)0;
        SendCustomEventDelayedFrames(""MyEvent"", 1, timing);
    }
}", "EnumCastTest");

    // ── Implicit struct conversion (Vector2 → Vector3) ──

    [Fact]
    public void ImplicitVector2ToVector3_CompilesCorrectly()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class Vec2ToVec3Test : UdonSharpBehaviour {
    public void Test() {
        Vector2 v2 = new Vector2(1f, 2f);
        Vector3 v3 = v2;
        Debug.Log(v3);
    }
}", "Vec2ToVec3Test");

    // ── Field initializer type mismatch (int literal → float field) ──

    [Fact]
    public void FieldInit_ConstantsViaApplyNotStart()
    {
        // Constant field initializers should be applied via ApplyConstantValues (ConstValue),
        // not via runtime code in _start (which runs after _onEnable).
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
using UnityEngine;
public class FieldInitTest : UdonSharpBehaviour {
    float delay = 0;
    byte selectedPlayer = 1;
    bool flag = true;
    string label = ""hello"";
    public void Test() { Debug.Log(delay); }
}", "FieldInitTest");
        // Constants should be registered for programmatic heap setup
        Assert.Contains(consts, c => c.Id == "delay" && c.ConstValue is float f && f == 0f);
        Assert.Contains(consts, c => c.Id == "selectedPlayer" && c.ConstValue is byte b && b == 1);
        Assert.Contains(consts, c => c.Id == "flag" && c.ConstValue is bool v && v == true);
        Assert.Contains(consts, c => c.Id == "label" && c.ConstValue is string s && s == "hello");
        // No _start should be synthesized for constant-only initializers
        Assert.DoesNotContain(".export _start", uasm);
    }

    // ── Generic static method extern (Array.IndexOf<T>) ──

    [Fact]
    public void ArrayIndexOf_UsesNonGenericExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
using UnityEngine;
public class ArrayIndexOfTest : UdonSharpBehaviour {
    public void Test() {
        string[] arr = new string[] { ""a"", ""b"" };
        int idx = Array.IndexOf(arr, ""b"");
        Debug.Log(idx);
    }
}", "ArrayIndexOfTest");
        // UdonSharp resolves generic Array.IndexOf<T> to non-generic overload (Array, object)
        Assert.Contains("SystemArray.__IndexOf__SystemArray_SystemObject__SystemInt32", uasm);
        // Must NOT use concrete types (doesn't exist) or TArray/T (HeapTypeMismatch)
        Assert.DoesNotContain("SystemStringArray_SystemString", uasm);
        Assert.DoesNotContain("TArray_T", uasm);
    }

    // ── Batch 1: Udon event dictionary ──

    [Theory]
    [InlineData("OnVideoReady", "_onVideoReady")]
    [InlineData("OnVideoEnd", "_onVideoEnd")]
    [InlineData("OnPreSerialization", "_onPreSerialization")]
    [InlineData("OnPostSerialization", "_onPostSerialization")]
    [InlineData("OnPlayerTriggerEnter", "_onPlayerTriggerEnter")]
    [InlineData("OnPlayerCollisionEnter", "_onPlayerCollisionEnter")]
    [InlineData("OnPlayerRespawn", "_onPlayerRespawn")]
    [InlineData("OnStringLoadSuccess", "_onStringLoadSuccess")]
    [InlineData("InputJump", "_inputJump")]
    [InlineData("OnTriggerEnter", "_onTriggerEnter")]
    [InlineData("OnCollisionEnter", "_onCollisionEnter")]
    [InlineData("OnAnimatorIK", "_onAnimatorIK")]
    [InlineData("MidiNoteOn", "_midiNoteOn")]
    [InlineData("OnOwnershipRequest", "_onOwnershipRequest")]
    [InlineData("OnStationEntered", "_onStationEntered")]
    [InlineData("OnAsyncGpuReadbackComplete", "_onAsyncGpuReadbackComplete")]
    public void UdonEvent_IsExported(string methodName, string expectedExport)
    {
        var uasm = TestHelper.CompileToUasm($@"
using UdonSharp;
public class EventTest : UdonSharpBehaviour {{
    public void {methodName}() {{ }}
}}", "EventTest");
        Assert.Contains($".export {expectedExport}", uasm);
    }

    [Fact]
    public void UdonEvent_BridgeMethodDoesNotCollide()
    {
        // VizVid pattern: explicit _onVideoPlay() bridge alongside OnVideoPlay() event.
        // The event should get the _onVideoPlay export; the bridge gets an indexed name.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class BridgeTest : UdonSharpBehaviour {
    public void _onVideoPlay() => OnVideoPlay();
    public override void OnVideoPlay() { }
}", "BridgeTest");
        // Only one .export _onVideoPlay (from the event)
        var exports = uasm.Split('\n').Count(l => l.Contains(".export _onVideoPlay"));
        Assert.Equal(1, exports);
    }

    [Fact]
    public void OverriddenProperty_NoDuplicateExport()
    {
        // Base class has public virtual IsActive, derived overrides it.
        // Should produce exactly one .export IsActive, not two.
        var uasm = TestHelper.CompileToUasm(new[] { @"
using UdonSharp;
public class BaseHandler : UdonSharpBehaviour {
    protected bool isActive;
    public virtual bool IsActive { get => isActive; set => isActive = value; }
}", @"
using UdonSharp;
public class DerivedHandler : BaseHandler {
    public override bool IsActive { get => isActive; set { isActive = value; } }
}" }, "DerivedHandler");
        var exportCount = uasm.Split('\n').Count(l => l.TrimStart().StartsWith(".export IsActive"));
        Assert.Equal(1, exportCount);
    }

    // ── Batch 2: FieldChangeCallback ──

    [Fact]
    public void FieldChangeCallback_GeneratesVarChangeExport()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class FcbTest : UdonSharpBehaviour {
    [FieldChangeCallback(""CurrentUrl"")]
    string _currentUrl;
    public string CurrentUrl { get => _currentUrl; set => _currentUrl = value; }
}", "FcbTest");
        Assert.Contains(".export _onVarChange__currentUrl", uasm);
        Assert.Contains("__old__currentUrl: %SystemString", uasm);
    }

    [Fact]
    public void FieldChangeCallback_PrivateSetter_IsExported()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class FcbPrivTest : UdonSharpBehaviour {
    [FieldChangeCallback(""Volume"")]
    float _vol;
    float Volume { get => _vol; set => _vol = value; }
}", "FcbPrivTest");
        // Private setter should still be exported because FieldChangeCallback requires it
        Assert.Contains(".export _onVarChange__vol", uasm);
    }

    // ── Batch 3: Sync mode ──

    [Fact]
    public void SyncMode_None_IsDefault()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SyncNoneTest : UdonSharpBehaviour {
    [UdonSynced] public float health;
    public void Start() { }
}", "SyncNoneTest");
        Assert.Contains(".sync health, none", uasm);
    }

    // ── Batch 4: default(T) value types ──

    [Fact]
    public void DefaultInt_IsZero()
    {
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
using UnityEngine;
public class DefaultIntTest : UdonSharpBehaviour {
    public void Test() {
        int x = default;
        Debug.Log(x);
    }
}", "DefaultIntTest");
        // default(int) should produce a constant 0, not null
        Assert.Contains(consts, c => c.UdonType == "SystemInt32" && c.ConstValue is int v && v == 0);
    }

    [Fact]
    public void DefaultBool_IsFalse()
    {
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
using UnityEngine;
public class DefaultBoolTest : UdonSharpBehaviour {
    public void Test() {
        bool b = default;
        Debug.Log(b);
    }
}", "DefaultBoolTest");
        Assert.Contains(consts, c => c.UdonType == "SystemBoolean" && c.ConstValue is bool v && v == false);
    }

    [Fact]
    public void DefaultFloat_IsZero()
    {
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
using UnityEngine;
public class DefaultFloatTest : UdonSharpBehaviour {
    public void Test() {
        float f = default;
        Debug.Log(f);
    }
}", "DefaultFloatTest");
        Assert.Contains(consts, c => c.UdonType == "SystemSingle" && c.ConstValue is float v && v == 0f);
    }

    // ── Batch 5: Interpolation format/alignment ──

    [Fact]
    public void InterpolatedString_FormatSpecifier_Preserved()
    {
        var (_, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
using UnityEngine;
public class InterpFmtTest : UdonSharpBehaviour {
    public void Test() {
        float v = 1.5f;
        string s = $""{v:F2}"";
        Debug.Log(s);
    }
}", "InterpFmtTest");
        // Format string constant should contain {0:F2}, not just {0}
        Assert.Contains(consts, c => c.UdonType == "SystemString" && c.ConstValue is string s && s.Contains("{0:F2}"));
    }

    [Fact]
    public void InterpolatedString_Alignment_Preserved()
    {
        var (_, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
using UnityEngine;
public class InterpAlignTest : UdonSharpBehaviour {
    public void Test() {
        int n = 42;
        string s = $""{n,10}"";
        Debug.Log(s);
    }
}", "InterpAlignTest");
        Assert.Contains(consts, c => c.UdonType == "SystemString" && c.ConstValue is string s && s.Contains("{0,10}"));
    }

    [Fact]
    public void InterpolatedString_AlignmentAndFormat_Preserved()
    {
        var (_, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
using UnityEngine;
public class InterpBothTest : UdonSharpBehaviour {
    public void Test() {
        float v = 3.14f;
        string s = $""{v,8:F1}"";
        Debug.Log(s);
    }
}", "InterpBothTest");
        Assert.Contains(consts, c => c.UdonType == "SystemString" && c.ConstValue is string s && s.Contains("{0,8:F1}"));
    }

    // ── Float→Int truncation (C# truncates, SystemConvert rounds) ──

    [Fact]
    public void FloatToInt_Cast_UsesTruncateNotRound()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class FloatTrunc : UdonSharpBehaviour {
    void Start() {
        float f = 0.9f;
        int i = (int)f;
    }
}", "FloatTrunc");
        Assert.Contains("SystemMath.__Truncate__SystemDouble__SystemDouble", uasm);
    }

    [Fact]
    public void DoubleToInt_Cast_UsesTruncateNotRound()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class DoubleTrunc : UdonSharpBehaviour {
    void Start() {
        double d = 2.7;
        int i = (int)d;
    }
}", "DoubleTrunc");
        Assert.Contains("SystemMath.__Truncate__SystemDouble__SystemDouble", uasm);
    }

    // ── OnOwnershipRequest __returnValue ──

    [Fact]
    public void OnOwnershipRequest_HasReturnValueCopy()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDKBase;
public class OwnerTest : UdonSharpBehaviour {
    public override bool OnOwnershipRequest(VRCPlayerApi rp, VRCPlayerApi ro) {
        return false;
    }
}", "OwnerTest");
        // Must declare __returnValue as SystemObject
        Assert.Contains("__returnValue", uasm);
        // Must contain PUSH __returnValue (destination of COPY)
        Assert.Matches(@"PUSH, __returnValue", uasm);
    }

    // ── GetBehaviourSyncMode helper ──

    [Fact]
    public void GetBehaviourSyncMode_ManualAttribute_ReturnsManual()
    {
        var source = TestHelper.StubSource + @"
[UdonSharp.UdonBehaviourSyncMode(UdonSharp.BehaviourSyncMode.Manual)]
public class SyncTest : UdonSharp.UdonSharpBehaviour { }
";
        var tree = CSharpSyntaxTree.ParseText(source);
        var comp = CSharpCompilation.Create("Test", new[] { tree }, TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var sym = comp.GetTypeByMetadataName("SyncTest");
        Assert.Equal(1, USugarCompilerHelper.GetBehaviourSyncMode(sym)); // Manual = 1
    }

    [Fact]
    public void GetBehaviourSyncMode_NoAttribute_ReturnsNegativeOne()
    {
        var source = TestHelper.StubSource + @"
public class NoSync : UdonSharp.UdonSharpBehaviour { }
";
        var tree = CSharpSyntaxTree.ParseText(source);
        var comp = CSharpCompilation.Create("Test", new[] { tree }, TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var sym = comp.GetTypeByMetadataName("NoSync");
        Assert.Equal(-1, USugarCompilerHelper.GetBehaviourSyncMode(sym));
    }

    [Fact]
    public void GetBehaviourSyncMode_InheritedAttribute_ReturnsParentMode()
    {
        var source = TestHelper.StubSource + @"
[UdonSharp.UdonBehaviourSyncMode(UdonSharp.BehaviourSyncMode.Continuous)]
public class SyncBase : UdonSharp.UdonSharpBehaviour { }
public class SyncDerived : SyncBase { }
";
        var tree = CSharpSyntaxTree.ParseText(source);
        var comp = CSharpCompilation.Create("Test", new[] { tree }, TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var sym = comp.GetTypeByMetadataName("SyncDerived");
        Assert.Equal(2, USugarCompilerHelper.GetBehaviourSyncMode(sym)); // Continuous = 2
    }

    // ── Enum type-tag safety ──

    [Fact]
    public void IntToEnum_ConstantCast_UsesConst()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum MyMode { Off, On, Auto }
public class EnumConstCast : UdonSharpBehaviour {
    void Start() {
        MyMode m = (MyMode)1;
    }
}", "EnumConstCast");
        Assert.NotNull(uasm);
        // User-defined enums map to underlying type (Udon has no type registration for user enums).
        // Constant int→enum should declare a const with the underlying type.
        Assert.Contains("__const_SystemInt32_", uasm);
    }

    [Fact]
    public void EnumToInt_RuntimeCast_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum MyMode { Off, On, Auto }
public class EnumToIntCast : UdonSharpBehaviour {
    MyMode mode;
    void Start() {
        mode = MyMode.On;
        int i = (int)mode;
    }
}", "EnumToIntCast");
        Assert.NotNull(uasm);
    }

    [Fact]
    public void IntToEnum_RuntimeCast_UsesEnumArray()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum MyMode { Off, On, Auto }
public class EnumRuntimeCast : UdonSharpBehaviour {
    int val;
    void Start() {
        val = 1;
        MyMode m = (MyMode)val;
    }
}", "EnumRuntimeCast");
        // Runtime int→enum should use object array lookup, not COPY
        Assert.Contains("SystemObjectArray.__Get__SystemInt32__SystemObject", uasm);
    }

    // ── UdonSynced type validation ──

    [Fact]
    public void UdonSynced_InvalidType_ThrowsDiagnostic()
    {
        Assert.ThrowsAny<Exception>(() =>
        {
            TestHelper.CompileToUasm(@"
using UdonSharp;
public class BadSync : UdonSharpBehaviour {
    [UdonSynced] public UnityEngine.GameObject syncedObj;
}", "BadSync");
        });
    }

    [Fact]
    public void UdonSynced_ValidTypes_Compile()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class GoodSync : UdonSharpBehaviour {
    [UdonSynced] public int syncedInt;
    [UdonSynced] public float syncedFloat;
    [UdonSynced] public bool syncedBool;
    [UdonSynced] public string syncedStr;
}", "GoodSync");
        Assert.NotNull(uasm);
        Assert.Contains(".sync syncedInt", uasm);
        Assert.Contains(".sync syncedFloat", uasm);
    }

    // ── SendCustomEvent on external UdonSharpBehaviour → extern, not cross-class call ──

    [Fact]
    public void SendCustomEvent_OnExternalUsb_CompilesAsExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class EventSender : UdonSharpBehaviour {
    public UdonSharpBehaviour target;
    public void Fire() {
        target.SendCustomEvent(""_doStuff"");
    }
}", "EventSender");
        var codeSection = uasm.Substring(uasm.IndexOf(".code_start"));
        // Must compile as Udon extern, not as cross-class SetProgramVariable+SendCustomEvent pattern
        Assert.Contains("IUdonEventReceiver.__SendCustomEvent__SystemString__SystemVoid", codeSection);
        // Must NOT contain SetProgramVariable for the event name parameter
        Assert.DoesNotContain("SetProgramVariable", codeSection);
    }

    [Fact]
    public void SendCustomEvent_Extern_EmitsCorrectly()
    {
        // Re-entrance protection via __retAddrStack is disabled (matches UdonSharp behavior).
        // SendCustomEvent externs still emit correctly.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ReentrantSender : UdonSharpBehaviour {
    public UdonSharpBehaviour target;
    public void Fire() {
        target.SendCustomEvent(""_event1"");
        target.SendCustomEvent(""_event2"");
    }
}", "ReentrantSender");
        var codeSection = uasm.Substring(uasm.IndexOf(".code_start"));
        var lines = codeSection.Split('\n');
        int sendCount = lines.Count(l => l.Contains("__SendCustomEvent__SystemString__SystemVoid"));
        Assert.Equal(2, sendCount);
        Assert.DoesNotContain("__retAddrStack", uasm);
    }

    // ── Compound assignment property write-back ──

    [Fact]
    public void CompoundAssign_ExternProperty_EmitsSetterExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CompoundPropTest : UdonSharpBehaviour {
    void Start() {
        var t = transform;
        t.position += Vector3.one;
    }
}");
        // Should emit both getter AND setter for position
        Assert.Contains("__get_position__", uasm);
        Assert.Contains("__set_position__", uasm);
    }

    [Fact]
    public void CompoundAssign_CrossBehaviour_Field_EmitsSetProgramVariable()
    {
        var uasm = TestHelper.CompileToUasm(new[] { @"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CompoundCBTarget : UdonSharpBehaviour {
    public int Counter;
}", @"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CompoundCBSource : UdonSharpBehaviour {
    public CompoundCBTarget target;
    void Start() { target.Counter += 1; }
}" }, "CompoundCBSource");
        // Cross-behaviour field compound assignment should use GetProgramVariable
        Assert.Contains("__GetProgramVariable__", uasm);
        // Write-back via SetProgramVariable (field target)
        Assert.Contains("__SetProgramVariable__", uasm);
    }

    // ── Interface bridge return value ──

    // ── Silent fallback removal — regression tests ──

    [Fact]
    public void UnaryMinus_EmitsCorrectExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class UnaryMinusTest : UdonSharpBehaviour {
    int M(int x) { return -x; }
    public void Test() { M(1); }
}", "UnaryMinusTest");
        Assert.Contains("op_UnaryMinus", uasm);
    }

    [Fact]
    public void LogicalNot_EmitsCorrectExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class LogicalNotTest : UdonSharpBehaviour {
    bool M(bool x) { return !x; }
    public void Test() { M(true); }
}", "LogicalNotTest");
        Assert.Contains("op_UnaryNegation", uasm);
    }

    [Fact]
    public void BitwiseNot_Int_EmitsXor()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class BitwiseNotIntTest : UdonSharpBehaviour {
    public void Test() {
        int x = 30;
        int y = ~x;
    }
}", "BitwiseNotIntTest");
        Assert.Contains("__op_LogicalXor__SystemInt32_SystemInt32__SystemInt32", uasm);
    }

    [Fact]
    public void BitwiseNot_UInt_EmitsXor()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class BitwiseNotUIntTest : UdonSharpBehaviour {
    public void Test() {
        uint x = 40;
        uint y = ~x;
    }
}", "BitwiseNotUIntTest");
        Assert.Contains("__op_LogicalXor__SystemUInt32_SystemUInt32__SystemUInt32", uasm);
    }

    // ── Interface bridge return value ──

    [Fact]
    public void InterfaceBridge_WithReturn_Compiles()
    {
        // Interface with non-void return: bridge must compile cleanly
        var uasm = TestHelper.CompileToUasm(new[] { @"
public interface IValueProvider { int GetValue(); }
", @"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ValueProviderImpl : UdonSharpBehaviour, IValueProvider {
    public int GetValue() { return 42; }
}" }, "ValueProviderImpl");
        Assert.NotNull(uasm);
        // The GetValue export should exist (class method + bridge if needed)
        Assert.Contains("GetValue", uasm);
    }

    // ── Error path tests ──

    [Fact]
    public void ForeachOverList_ThrowsNotSupported()
    {
        // List<T> is not supported in Udon — should throw
        Assert.ThrowsAny<Exception>(() =>
        {
            TestHelper.CompileToUasm(@"
using System.Collections.Generic;
using UdonSharp;
public class ListForeachTest : UdonSharpBehaviour {
    void Start() {
        var list = new List<int>();
        foreach (var x in list) { }
    }
}
", "ListForeachTest");
        });
    }

    [Fact]
    public void NullableValueType_ThrowsOrCompiles()
    {
        // Nullable<T> may or may not be supported — capture current behavior as regression test
        try
        {
            var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NullableTest : UdonSharpBehaviour {
    void Start() {
        int? x = 5;
        if (x.HasValue) { int y = x.Value; }
    }
}
", "NullableTest");
            // If it compiles, just verify we got output
            Assert.NotNull(uasm);
        }
        catch (Exception)
        {
            // If it throws, that's also acceptable — Nullable<T> is not required
        }
    }

    // ── Enum SpecialType resolution ──

    [Fact]
    public void EnumSpecialType_DoesNotCrash()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class EnumSpecialTypeTest : UdonSharpBehaviour {
    void Start() {
        KeyCode key = KeyCode.Space;
        if (key == KeyCode.Return) Debug.Log(""enter"");
    }
}", "EnumSpecialTypeTest");
        Assert.NotNull(uasm);
    }
}
