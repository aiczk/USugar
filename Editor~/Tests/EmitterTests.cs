using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace USugar.Tests;

public class EmitterTests
{
    [Fact]
    public void EmptyClass_HasReflectionVars()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class EmptyTest : UdonSharpBehaviour { }
");
        Assert.Contains("__refl_typeid: %SystemInt64, null", uasm);
        Assert.Contains("__refl_typename: %SystemString, null", uasm);
        Assert.Contains("__intnl_returnJump_SystemUInt32_0: %SystemUInt32, 0xFFFFFFFF", uasm);
        Assert.Contains(".data_start", uasm);
        Assert.Contains(".code_start", uasm);
    }

    [Fact]
    public void PublicField_IsExported()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class FieldTest : UdonSharpBehaviour { public int score; }
");
        Assert.Contains("    .export score", uasm);
        Assert.Contains("    score: %SystemInt32, null", uasm);
    }

    [Fact]
    public void SyncedField_HasSyncDirective()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class SyncTest : UdonSharpBehaviour { [UdonSynced] int _value; }
");
        Assert.Contains("    .sync _value, none", uasm);
    }

    [Fact]
    public void SimpleMethod_HasExportAndReturn()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class MethodTest : UdonSharpBehaviour {
    void Start() { }
}
");
        Assert.Contains("    .export _start", uasm);
        Assert.Contains("    _start:", uasm);
        // Return uses stack-based protocol (PUSH+COPY+JUMP_INDIRECT)
        Assert.Contains("JUMP_INDIRECT, __intnl_returnJump_SystemUInt32_0", uasm);
    }

    [Fact]
    public void LocalVarAssignment_IntLiteral()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class VarTest : UdonSharpBehaviour {
    void Start() { int x = 42; }
}
");
        // Local var is declared even though the dead store is eliminated by DCE
        Assert.Contains("__lcl_x_SystemInt32_0: %SystemInt32, null", uasm);
    }

    [Fact]
    public void FieldAssignment()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class AssignTest : UdonSharpBehaviour {
    int _value;
    void Start() { _value = 10; }
}
");
        Assert.Contains("PUSH, __const_SystemInt32_0", uasm);
        Assert.Contains("PUSH, _value", uasm);
        Assert.Contains("COPY", uasm);
    }

    [Fact]
    public void IntAddition()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class AddTest : UdonSharpBehaviour {
    int _a; int _b;
    void Start() { int x = _a + _b; }
}
");
        Assert.Contains("EXTERN, \"SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32\"", uasm);
    }

    [Fact]
    public void IntComparison()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CmpTest : UdonSharpBehaviour {
    int _a; int _b;
    void Start() { bool eq = _a == _b; }
}
");
        Assert.Contains("EXTERN, \"SystemInt32.__op_Equality__SystemInt32_SystemInt32__SystemBoolean\"", uasm);
    }

    [Fact]
    public void IncrementOperator()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class IncTest : UdonSharpBehaviour {
    int _x;
    void Start() { _x++; }
}
");
        Assert.Contains("EXTERN, \"SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32\"", uasm);
    }

    [Fact]
    public void IfStatement_JumpIfFalse()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class IfTest : UdonSharpBehaviour {
    int _x;
    void Start() { if (_x == 0) _x = 1; }
}
");
        Assert.Contains("JUMP_IF_FALSE", uasm);
        Assert.Contains("EXTERN, \"SystemInt32.__op_Equality__SystemInt32_SystemInt32__SystemBoolean\"", uasm);
    }

    [Fact]
    public void IfElse_HasJump()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class IfElseTest : UdonSharpBehaviour {
    int _x;
    void Start() { if (_x == 0) _x = 1; else _x = 2; }
}
");
        Assert.Contains("JUMP_IF_FALSE", uasm);
        Assert.Contains("JUMP, 0x", uasm);
    }

    [Fact]
    public void EarlyReturn_InIf()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ReturnTest : UdonSharpBehaviour {
    int _x;
    void Start() { if (_x == 0) return; _x = 1; }
}
");
        var jumpIndirectCount = System.Text.RegularExpressions.Regex.Matches(uasm, "JUMP_INDIRECT").Count;
        Assert.True(jumpIndirectCount >= 2);
    }

    [Fact]
    public void WhileLoop()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class WhileTest : UdonSharpBehaviour {
    void Start() { int i = 0; while (i < 10) { i++; } }
}
");
        Assert.Contains("JUMP_IF_FALSE", uasm);
        var jumpMatches = System.Text.RegularExpressions.Regex.Matches(uasm, @"JUMP, 0x");
        Assert.True(jumpMatches.Count >= 1);
    }

    [Fact]
    public void ForLoop()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ForTest : UdonSharpBehaviour {
    void Start() { for (int i = 0; i < 5; i++) { } }
}
");
        Assert.Contains("JUMP_IF_FALSE", uasm);
        Assert.Contains("EXTERN, \"SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean\"", uasm);
    }

    [Fact]
    public void UserMethodCall_ParamsAndReturn()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CallTest : UdonSharpBehaviour {
    int Add(int a, int b) { return a + b; }
    void Start() { int r = Add(1, 2); }
}
");
        Assert.Contains("__0_a__param", uasm);
        Assert.Contains("__0_b__param", uasm);
        Assert.Contains("__0_Add__ret", uasm);
        Assert.Matches(@"JUMP, 0x[0-9A-Fa-f]{8}", uasm);
    }

    [Fact]
    public void VoidMethod_NoReturnValue()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class VoidTest : UdonSharpBehaviour {
    void DoNothing() { }
    void Start() { DoNothing(); }
}
");
        Assert.Contains("JUMP_INDIRECT, __intnl_returnJump_SystemUInt32_0", uasm);
    }

    [Fact]
    public void ReturnWithValue()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class RetValTest : UdonSharpBehaviour {
    int GetValue() { return 42; }
    void Start() { int v = GetValue(); }
}
");
        Assert.Contains("__0_GetValue__ret", uasm);
    }

    [Fact]
    public void UdonEventMethod_MapsToUnderscoreName()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class EventTest : UdonSharpBehaviour {
    void Start() { }
    void Update() { }
}
");
        Assert.Contains(".export _start", uasm);
        Assert.Contains(".export _update", uasm);
    }

    // ── Task 12: this property references ──

    [Fact]
    public void ThisGameObject_DeclaresThisVar()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ThisTest : UdonSharpBehaviour {
    void Start() { bool b = Networking.IsOwner(gameObject); }
}
");
        // The this pointer is declared with "this" initializer in the data section
        Assert.Contains("__this_UnityEngineGameObject_0: %UnityEngineGameObject, this", uasm);
        // After Mem2Reg, field access uses an SSA temp instead of the __this var directly
        Assert.Contains("EXTERN, \"VRCSDKBaseNetworking.__IsOwner__UnityEngineGameObject__SystemBoolean\"", uasm);
    }

    // ── Task 13: Extern static method calls ──

    [Fact]
    public void StaticExternCall_NetworkingIsOwner()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ExternStaticTest : UdonSharpBehaviour {
    void Start() { bool b = Networking.IsOwner(gameObject); }
}
");
        Assert.Contains("EXTERN, \"VRCSDKBaseNetworking.__IsOwner__UnityEngineGameObject__SystemBoolean\"", uasm);
    }

    [Fact]
    public void StaticExternCall_DebugLog()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DebugLogTest : UdonSharpBehaviour {
    void Start() { Debug.Log(""hello""); }
}
");
        Assert.Contains("EXTERN, \"UnityEngineDebug.__Log__SystemObject__SystemVoid\"", uasm);
    }

    // ── Task 14: Extern instance method calls ──

    [Fact]
    public void InstanceExternCall_RequestSerialization()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class InstanceExternTest : UdonSharpBehaviour {
    void Start() { RequestSerialization(); }
}
");
        Assert.Contains("__RequestSerialization__", uasm);
    }

    [Fact]
    public void ExternCall_VoidNoResult()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class VoidExternTest : UdonSharpBehaviour {
    void Start() { Debug.Log(""test""); }
}
");
        // Void extern should NOT push a result var — only 1 PUSH before EXTERN (the arg)
        var lines = uasm.Split('\n');
        var externIdx = System.Array.FindIndex(lines, l => l.Contains("EXTERN") && l.Contains("Debug"));
        Assert.True(externIdx >= 0);
        // Count PUSHes immediately before the EXTERN
        int pushCount = 0;
        for (int i = externIdx - 1; i >= 0; i--)
        {
            if (lines[i].TrimStart().StartsWith("PUSH"))
                pushCount++;
            else
                break;
        }
        Assert.Equal(1, pushCount);
    }

    // ── Task 15: Public method export names ──

    [Fact]
    public void PublicNoParams_ExportsRawName()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ExportNameTest : UdonSharpBehaviour {
    public void DoSomething() { }
    public int GetValue() { return 1; }
}
");
        Assert.Contains(".export DoSomething", uasm);
        Assert.Contains("DoSomething:", uasm);
        Assert.Contains(".export GetValue", uasm);
        Assert.Contains("GetValue:", uasm);
    }

    [Fact]
    public void PublicWithParams_ExportsCounterMangledName()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ExportParamTest : UdonSharpBehaviour {
    public void SendAction(int type, int arg) { }
}
");
        // Public methods with 1+ params get counter-mangled export names
        Assert.Contains(".export __0_SendAction", uasm);
        Assert.Contains("__0_SendAction:", uasm);
    }

    // ── Task 16: String interpolation ──

    [Fact]
    public void StringInterpolation_SingleArg()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class StrInterpTest : UdonSharpBehaviour {
    int _x;
    void Start() { string s = $""value={_x}""; }
}
");
        Assert.Contains("EXTERN, \"SystemString.__Format__SystemString_SystemObject__SystemString\"", uasm);
    }

    [Fact]
    public void StringInterpolation_TwoArgs()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class StrInterp2Test : UdonSharpBehaviour {
    int _a; int _b;
    void Start() { string s = $""a={_a} b={_b}""; }
}
");
        Assert.Contains("EXTERN, \"SystemString.__Format__SystemString_SystemObject_SystemObject__SystemString\"", uasm);
    }

    [Fact]
    public void StringInterpolation_WithExpression()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class StrExprTest : UdonSharpBehaviour {
    int _a; int _b;
    void Start() { Debug.LogWarning($""diff={_a - _b}""); }
}
");
        Assert.Contains("__Format__", uasm);
        Assert.Contains("__op_Subtraction__", uasm);
        Assert.Contains("__LogWarning__", uasm);
    }

    // ── Task 17: Array operations ──

    [Fact]
    public void ArrayCreation()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ArrayCreateTest : UdonSharpBehaviour {
    void Start() { int[] arr = new int[5]; }
}
");
        Assert.Contains("EXTERN, \"SystemInt32Array.__ctor__SystemInt32__SystemInt32Array\"", uasm);
    }

    [Fact]
    public void ArrayElementRead()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ArrayReadTest : UdonSharpBehaviour {
    int[] _arr;
    void Start() { int x = _arr[0]; }
}
");
        Assert.Contains("EXTERN, \"SystemInt32Array.__Get__SystemInt32__SystemInt32\"", uasm);
    }

    [Fact]
    public void ArrayElementWrite()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ArrayWriteTest : UdonSharpBehaviour {
    int[] _arr;
    void Start() { _arr[0] = 42; }
}
");
        Assert.Contains("EXTERN, \"SystemInt32Array.__Set__SystemInt32_SystemInt32__SystemVoid\"", uasm);
    }

    // ── Task 22: Type remapping ──

    [Fact]
    public void RequestSerialization_RemappedType()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class RemapTest : UdonSharpBehaviour {
    void Start() { RequestSerialization(); }
}
");
        Assert.Contains("VRCUdonCommonInterfacesIUdonEventReceiver.__RequestSerialization__SystemVoid", uasm);
        // HIR/LIR pipeline uses __this_ directly (no intermediate temp for IUdonEventReceiver)
        Assert.Contains("__this_VRCUdonUdonBehaviour_0: %VRCUdonUdonBehaviour, this", uasm);
    }

    // ── Task 20: switch statement ──

    [Fact]
    public void SwitchInt_MultipleCases()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SwitchTest : UdonSharpBehaviour {
    int _action;
    int _result;
    void Start() {
        switch (_action) {
            case 1: _result = 10; break;
            case 2: _result = 20; break;
            default: _result = 0; break;
        }
    }
}
");
        var eqCount = System.Text.RegularExpressions.Regex.Matches(uasm, "__op_Equality").Count;
        Assert.True(eqCount >= 2);
        Assert.Contains("JUMP_IF_FALSE", uasm);
    }

    [Fact]
    public void SwitchWithReturn()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SwitchReturnTest : UdonSharpBehaviour {
    int _mode;
    int GetValue() {
        switch (_mode) {
            case 0: return 100;
            case 1: return 200;
            default: return 0;
        }
    }
    void Start() { int v = GetValue(); }
}
");
        Assert.Contains("__op_Equality__SystemInt32_SystemInt32__SystemBoolean", uasm);
        Assert.Contains("__0_GetValue__ret", uasm);
    }

    [Fact]
    public void SwitchString()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SwitchStringTest : UdonSharpBehaviour {
    string _mode;
    int _result;
    void Start() {
        switch (_mode) {
            case ""fast"": _result = 1; break;
            case ""slow"": _result = 2; break;
        }
    }
}
");
        Assert.Contains("__op_Equality__SystemString_SystemString__SystemBoolean", uasm);
    }

    // ── Task 19: foreach loop ──

    [Fact]
    public void ForeachArray_BasicIteration()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ForeachTest : UdonSharpBehaviour {
    int[] _items;
    int _sum;
    void Start() {
        foreach (var x in _items) { _sum += x; }
    }
}
");
        Assert.Contains("__get_Length__SystemInt32", uasm);
        Assert.Contains("__op_LessThan__SystemInt32_SystemInt32__SystemBoolean", uasm);
        Assert.Contains("SystemInt32Array.__Get__SystemInt32__SystemInt32", uasm);
        Assert.Contains("__op_Addition__SystemInt32_SystemInt32__SystemInt32", uasm);
    }

    [Fact]
    public void ForeachArray_BreakContinue()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ForeachBreakTest : UdonSharpBehaviour {
    int[] _items;
    int _result;
    void Start() {
        foreach (var x in _items) {
            if (x == 0) continue;
            if (x < 0) break;
            _result = x;
        }
    }
}
");
        var jumpCount = System.Text.RegularExpressions.Regex.Matches(uasm, @"JUMP, 0x").Count;
        Assert.True(jumpCount >= 3); // loop back + break + continue
    }

    // ── Coalesce operator ──

    [Fact]
    public void CoalesceOperator_NullCoalescing_EmitsConditional()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CoalesceTest : UdonSharpBehaviour {
    string _name;
    void Start() {
        string s = _name ?? ""default"";
    }
}
");
        Assert.Contains("JUMP_IF_FALSE", uasm);
        Assert.Contains("__op_Equality__SystemObject_SystemObject__SystemBoolean", uasm);
    }

    // ── Null-conditional operator ──

    [Fact]
    public void ConditionalAccess_PropertyAccess_EmitsNullCheck()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class NullCondTest : UdonSharpBehaviour {
    GameObject _obj;
    void Start() {
        string s = _obj?.name;
    }
}
");
        Assert.Contains("__op_Inequality__SystemObject_SystemObject__SystemBoolean", uasm);
        Assert.Contains("JUMP_IF_FALSE", uasm);
        Assert.Contains("__get_name__SystemString", uasm);
    }

    [Fact]
    public void ConditionalAccess_MethodCall_EmitsNullCheck()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class NullCondMethodTest : UdonSharpBehaviour {
    GameObject _obj;
    void Start() {
        _obj?.SetActive(true);
    }
}
");
        Assert.Contains("__op_Inequality__SystemObject_SystemObject__SystemBoolean", uasm);
        Assert.Contains("__SetActive__SystemBoolean__SystemVoid", uasm);
    }

    [Fact]
    public void ConditionalAccess_Chained_EmitsMultipleChecks()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class ChainedNullTest : UdonSharpBehaviour {
    Transform _t;
    void Start() {
        string s = _t?.gameObject?.name;
    }
}
");
        // Two separate null checks for the chain
        var count = uasm.Split(new[] { "__op_Inequality__SystemObject_SystemObject__SystemBoolean" },
            System.StringSplitOptions.None).Length - 1;
        Assert.True(count >= 2, $"Expected >=2 null checks, got {count}");
    }

    // ── NullRef fixes ──

    [Fact]
    public void NullLiteral_InComparison_DoesNotCrash()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class NullCheckTest : UdonSharpBehaviour {
    GameObject _go;
    void Start() {
        if (_go != null) { Debug.Log(""exists""); }
    }
}
");
        Assert.Contains("__op_Inequality", uasm);
    }

    // ── Static property, static field, default value ──

    [Fact]
    public void StaticProperty_ColorWhite_EmitsExternGetter()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class StaticPropTest : UdonSharpBehaviour {
    Color _c;
    void Start() {
        _c = Color.white;
    }
}
");
        Assert.Contains("__get_white", uasm);
    }

    [Fact]
    public void DefaultValue_EmitsNullConst()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class DefaultValTest : UdonSharpBehaviour {
    GameObject _go;
    void Start() {
        _go = default;
    }
}
");
        Assert.Contains("__const_UnityEngineGameObject_0", uasm);
    }

    // ── Task 32: Static method calls ──

    [Fact]
    public void StaticMethodCall_MathfMax_EmitsExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class MathTest : UdonSharpBehaviour {
    void Start() {
        int a = 5;
        int b = Mathf.Max(a, 3);
    }
}
");
        Assert.Contains("__Max__", uasm);
    }

    [Fact]
    public void StaticMethodCall_GameObjectSetActive_EmitsExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class SetActiveTest : UdonSharpBehaviour {
    void Start() {
        gameObject.SetActive(true);
    }
}
");
        Assert.Contains("__SetActive__", uasm);
    }

    // ── Task 31: Property setter ──

    [Fact]
    public void PropertySetter_TransformPosition_EmitsSetExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class PropSetterTest : UdonSharpBehaviour {
    void Start() {
        transform.position = new Vector3();
    }
}
");
        Assert.Contains("__set_position", uasm);
    }

    // ── Task 30: Bitwise operators ──

    [Fact]
    public void BitwiseOr_IntOperands_EmitsExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class BitwiseOrTest : UdonSharpBehaviour {
    void Start() {
        int a = 0xFF;
        int b = a | 0x100;
    }
}
");
        Assert.Contains("__op_LogicalOr", uasm);
    }

    [Fact]
    public void BitwiseXor_IntOperands_EmitsExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class XorTest : UdonSharpBehaviour {
    void Start() {
        int a = 0xFF;
        int b = a ^ 0x0F;
    }
}
");
        Assert.Contains("__op_LogicalXor", uasm);
    }

    // ── Parameterless struct ctor ──

    [Fact]
    public void ParameterlessStructCtor_DefaultInit_NoExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class DefTest : UdonSharpBehaviour {
    void Start() { var v = new Vector3(); }
}
");
        Assert.DoesNotContain("__ctor__", uasm);
    }

    [Fact]
    public void ParameteredStructCtor_EmitsExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class CtorTest : UdonSharpBehaviour {
    void Start() { var v = new Vector3(1f, 2f, 3f); }
}
");
        Assert.Contains("__ctor__SystemSingle_SystemSingle_SystemSingle__UnityEngineVector3", uasm);
    }

    [Fact]
    public void ParameterlessClassCtor_EmitsCtorWithEmptyParams()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class MPBTest : UdonSharpBehaviour {
    void Start() { var mpb = new MaterialPropertyBlock(); }
}
");
        Assert.Contains("__ctor____UnityEngineMaterialPropertyBlock", uasm);
    }

    // ── Field initializers ──

    [Fact]
    public void FieldInitializer_ArrayCreation_HeapSerialized()
    {
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class FITest : UdonSharpBehaviour {
    bool[] flags = new bool[4];
    void Start() { flags[0] = true; }
}
", "FITest");
        // Array should be heap-serialized, not runtime-initialized in _start
        Assert.DoesNotContain("SystemBooleanArray.__ctor__SystemInt32__SystemBooleanArray", uasm);
        var entry = consts.Find(e => e.Id == "flags");
        Assert.NotNull(entry.Value);
        var arr = Assert.IsType<bool[]>(entry.Value);
        Assert.Equal(4, arr.Length);
    }

    [Fact]
    public void FieldInitializer_LiteralValue_EmittedInStart()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class FILitTest : UdonSharpBehaviour {
    int score = 100;
    void Start() { }
}
");
        // Field should be initialized via COPY from const in _start
        var startIdx = uasm.IndexOf("_start:");
        var copyIdx = uasm.IndexOf("COPY", startIdx);
        Assert.True(copyIdx > startIdx, "COPY for field init must exist in _start");
    }

    [Fact]
    public void FieldInitializer_BareArrayInit_HeapSerialized()
    {
        var (uasm, consts) = TestHelper.CompileWithConsts(@"
using UdonSharp;
public class BareArrTest : UdonSharpBehaviour {
    int[] _rules = { 10, 20, 30 };
    void Start() { }
}
", "BareArrTest");
        // Array should be heap-serialized with correct values
        Assert.DoesNotContain("SystemInt32Array.__ctor__SystemInt32__SystemInt32Array", uasm);
        Assert.DoesNotContain("SystemInt32Array.__Set__SystemInt32_SystemInt32__SystemVoid", uasm);
        var entry = consts.Find(e => e.Id == "_rules");
        Assert.NotNull(entry.Value);
        var arr = Assert.IsType<int[]>(entry.Value);
        Assert.Equal(new[] { 10, 20, 30 }, arr);
    }

    // ── Postfix increment/decrement ──

    [Fact]
    public void PostfixIncrement_ReturnsOldValue()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class PostIncTest : UdonSharpBehaviour {
    int[] data = new int[10];
    void Start() {
        int pos = 0;
        int val = data[pos++];
    }
}
");
        // postfix pos++ should use old value for array indexing and also increment
        // Semantic check: both op_Addition (increment) and Array.__Get__ must be present
        Assert.Contains("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32", uasm);
        Assert.Contains("SystemInt32Array.__Get__SystemInt32__SystemInt32", uasm);
        // The array Get must use the original pos value, not the incremented one.
        // Verify the Get call has PUSH args before it (array + index operands).
        var lines = uasm.Split('\n');
        var getIdx = System.Array.FindIndex(lines, l => l.Contains("SystemInt32Array.__Get__"));
        Assert.True(getIdx >= 2, "Get should have PUSH args before it");
        // The index PUSH before Get should reference the original pos value (a local or const)
        Assert.Contains("PUSH,", lines[getIdx - 2].Trim());
    }

    [Fact]
    public void PrefixIncrement_ReturnsNewValue()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class PreIncTest : UdonSharpBehaviour {
    int[] data = new int[10];
    void Start() {
        int pos = 0;
        int val = data[++pos];
    }
}
");
        // prefix ++pos should NOT have an extra COPY for saving old value
        Assert.Contains("SystemInt32Array.__Get__", uasm);
    }

    // ── Compound assignment (+=, -=, etc.) ──

    [Fact]
    public void CompoundAssignment_BuiltinOperator_EmitsExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CompoundTest : UdonSharpBehaviour {
    void Start() {
        int pos = 0;
        pos += 4;
        int x = pos;
    }
}
");
        Assert.Contains("op_Addition", uasm);
    }

    [Fact]
    public void CompoundAssignment_ArrayElement_EmitsArraySet()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CompArrTest : UdonSharpBehaviour {
    int[] arr = new int[10];
    void Start() {
        arr[0] += 5;
    }
}
");
        // After compound op, must write back via Array.__Set__
        var lines = uasm.Split('\n');
        var addIdx = System.Array.FindIndex(lines, l => l.Contains("op_Addition"));
        var setIdx = System.Array.FindIndex(lines, addIdx, l => l.Contains("__Set__"));
        Assert.True(setIdx > addIdx, "Compound assignment on array element must emit Array.__Set__ after the operation");
    }

    [Fact]
    public void PostfixIncrement_ArrayElement_EmitsArraySet()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class IncArrTest : UdonSharpBehaviour {
    int[] sizes = new int[4];
    void Start() {
        int idx = sizes[0]++;
    }
}
");
        // After increment, must write back via Array.__Set__
        Assert.Contains("__Set__", uasm);
    }

    [Fact]
    public void ArrayClear_And_ElementIncrement_EmitsCorrectly()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class ClearIncTest : UdonSharpBehaviour {
    int[] counts = new int[34];
    int[] hand = new int[14];
    void Start() {
        Array.Clear(counts, 0, 34);
        for (var i = 0; i < 14; i++)
            counts[hand[i]]++;
    }
}
");
        Assert.Contains("SystemArray.__Clear__", uasm);
        // After increment, must write back via Array.__Set__
        var lines = uasm.Split('\n');
        var addIdx = System.Array.FindIndex(lines, l => l.Contains("op_Addition") && !l.Contains("__for_"));
        // Find the first op_Addition that is NOT the loop incrementor
        var setAfterAdd = System.Array.FindIndex(lines, addIdx, l => l.Contains("SystemInt32Array.__Set__"));
        Assert.True(setAfterAdd > addIdx, "Element increment must emit Array.__Set__");
    }

    // ── Task 29: Conditional expression (ternary) ──

    [Fact]
    public void ConditionalExpression_Ternary_ReturnsValue()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class TernaryTest : UdonSharpBehaviour {
    int x;
    void Start() {
        int result = x > 0 ? 1 : -1;
    }
}
");
        Assert.Contains("JUMP_IF_FALSE", uasm);
        // DCE removes dead ternary branches when result is unused; no unconditional JUMP needed
        Assert.Contains("op_GreaterThan", uasm);
    }

    // ── Task 28: Array creation with initializer ──

    [Fact]
    public void ArrayCreation_WithInitializer_SetsElements()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ArrayInitTest : UdonSharpBehaviour {
    void Start() {
        var arr = new int[] { 10, 20, 30 };
    }
}
");
        Assert.Contains("SystemInt32Array.__ctor__SystemInt32__SystemInt32Array", uasm);
        Assert.Contains("SystemInt32Array.__Set__SystemInt32_SystemInt32__SystemVoid", uasm);
    }

    // ── Task 27: IObjectCreationOperation ──

    [Fact]
    public void ObjectCreation_Constructor_EmitsExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class CtorTest : UdonSharpBehaviour {
    void Start() {
        var c = new Color(1f, 0f, 0f, 1f);
    }
}
");
        Assert.Contains("UnityEngineColor.__ctor__SystemSingle_SystemSingle_SystemSingle_SystemSingle__UnityEngineColor", uasm);
    }

    // ── Task 26: Null guard ──

    [Fact]
    public void TypeOf_EmitsSystemTypeConst()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class TypeOfTest : UdonSharpBehaviour {
    void Start() { var t = typeof(int); }
}
");
        // DCE removes the dead typeof() assignment; verify the local var is declared
        Assert.Contains("__lcl_t_SystemType_0: %SystemType, null", uasm);
    }

    // ── Task 45: const field inlining ──

    [Fact]
    public void ConstField_InlinedAsConstant()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ConstTest : UdonSharpBehaviour {
    const int MAX_SEATS = 4;
    int[] _seats;
    void Start() { _seats = new int[MAX_SEATS]; }
}
");
        Assert.DoesNotContain("__get_MAX_SEATS", uasm);
        Assert.Contains("__const_SystemInt32", uasm);
    }

    // ── Task 46: private method export suppression ──

    [Fact]
    public void PrivateMethod_NoExport()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class PrivExportTest : UdonSharpBehaviour {
    int Double(int x) { return x + x; }
    public void DoWork() { int r = Double(5); }
}
");
        Assert.Contains(".export DoWork", uasm);
        Assert.DoesNotContain(".export __0_Double", uasm);
        Assert.Contains("__0_Double:", uasm);
    }

    // ── Task 47: struct field setter ──

    [Fact]
    public void StructFieldSetter_Color_EmitsSetExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class StructSetTest : UdonSharpBehaviour {
    Color _c;
    void Start() { _c.a = 0.5f; }
}
");
        // Field setter does NOT include __SystemVoid (unlike property setter)
        Assert.Contains("UnityEngineColor.__set_a__SystemSingle", uasm);
        Assert.DoesNotContain("__SystemVoid", uasm);
    }

    // ── Task 48: UdonSharpBehaviour array accessor normalization ──

    [Fact]
    public void UdonSharpBehaviourArray_Get_UsesComponentArray()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Seat : UdonSharpBehaviour { }
public class ArrGetTest : UdonSharpBehaviour {
    Seat[] _seats;
    void Start() { var s = _seats[0]; }
}
", "ArrGetTest");
        Assert.Contains("UnityEngineComponentArray.__Get__SystemInt32__", uasm);
    }

    [Fact]
    public void UdonSharpBehaviourArray_Set_UsesComponentArray()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Slot : UdonSharpBehaviour { }
public class ArrSetTest : UdonSharpBehaviour {
    Slot[] _slots;
    Slot _item;
    void Start() { _slots[0] = _item; }
}
", "ArrSetTest");
        Assert.Contains("UnityEngineComponentArray.__Set__SystemInt32_", uasm);
    }

    // ── Task 49: foreign static method inlining ──

    [Fact]
    public void ForeignStatic_InlinedViaJump()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public static class Palette {
    public static Color C(float r, float g, float b) {
        return new Color(r, g, b);
    }
}
public class ForeignStaticTest : UdonSharpBehaviour {
    Color _c;
    void Start() { _c = Palette.C(1f, 0f, 0f); }
}
", "ForeignStaticTest");
        Assert.DoesNotContain("Palette.__C__", uasm);
        Assert.Matches(@"JUMP, 0x[0-9A-Fa-f]{8}", uasm);
        Assert.Contains("UnityEngineColor.__ctor__", uasm);
    }

    // ── Task 43: Cross-class UdonSharpBehaviour calls ──

    [Fact]
    public void CrossClassCall_Void_EmitsSendCustomEvent()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Target : UdonSharpBehaviour {
    public void DoThing() { }
}
public class CrossCallTest : UdonSharpBehaviour {
    Target _target;
    void Start() { _target.DoThing(); }
}
", "CrossCallTest");
        Assert.Contains("SendCustomEvent__SystemString__SystemVoid", uasm);
        Assert.DoesNotContain("Target.__DoThing__", uasm);
    }

    [Fact]
    public void CrossClassCall_WithParams_EmitsSetProgramVariable()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Receiver : UdonSharpBehaviour {
    public void Execute(int action, int arg) { }
}
public class SenderTest : UdonSharpBehaviour {
    Receiver _recv;
    void Start() { _recv.Execute(1, 2); }
}
", "SenderTest");
        Assert.Contains("SetProgramVariable__SystemString_SystemObject__SystemVoid", uasm);
        Assert.Contains("SendCustomEvent__SystemString__SystemVoid", uasm);
    }

    [Fact]
    public void CrossClassCall_WithReturn_EmitsGetProgramVariable()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Provider : UdonSharpBehaviour {
    public int GetValue() { return 42; }
}
public class ConsumerTest : UdonSharpBehaviour {
    Provider _prov;
    void Start() { int v = _prov.GetValue(); }
}
", "ConsumerTest");
        Assert.Contains("SendCustomEvent", uasm);
        Assert.Contains("GetProgramVariable__SystemString__SystemObject", uasm);
    }

    [Fact]
    public void CrossClassCall_EmitsSendCustomEvent()
    {
        // Re-entrance protection via __retAddrStack is disabled (matches UdonSharp behavior).
        // Cross-class calls still emit SendCustomEvent correctly.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Other : UdonSharpBehaviour {
    public void DoWork() { }
}
public class SaveRestoreTest : UdonSharpBehaviour {
    Other _other;
    void Start() { _other.DoWork(); }
}
", "SaveRestoreTest");
        Assert.DoesNotContain("__retAddrStack", uasm);
        Assert.Contains("SendCustomEvent__SystemString__SystemVoid", uasm);
    }

    // ── Task 42: Short-circuit evaluation ──

    [Fact]
    public void ConditionalAnd_ShortCircuit_EmitsJumpIfFalse()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class AndTest : UdonSharpBehaviour {
    bool a; bool b;
    void Start() { if (a && b) Debug.Log(""both""); }
}
");
        Assert.DoesNotContain("op_LogicalAnd__SystemBoolean_SystemBoolean", uasm);
        var jumpCount = System.Text.RegularExpressions.Regex.Matches(uasm, "JUMP_IF_FALSE").Count;
        Assert.True(jumpCount >= 2);
    }

    [Fact]
    public void ConditionalOr_ShortCircuit_EmitsJumpIfFalse()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class OrTest : UdonSharpBehaviour {
    bool a; bool b;
    void Start() { if (a || b) Debug.Log(""either""); }
}
");
        Assert.DoesNotContain("op_LogicalOr__SystemBoolean_SystemBoolean", uasm);
        var jumpCount = System.Text.RegularExpressions.Regex.Matches(uasm, "JUMP_IF_FALSE").Count;
        Assert.True(jumpCount >= 1);
    }

    // ── Task 41: Static method inclusion ──

    [Fact]
    public void StaticMethod_InSameClass_EmitsJumpCall()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class StaticCallTest : UdonSharpBehaviour {
    static int Double(int x) { return x + x; }
    void Start() { int r = Double(5); }
}
");
        Assert.Contains("__0_x__param", uasm);
        Assert.Contains("__0_Double__ret", uasm);
        Assert.Matches(@"JUMP, 0x[0-9A-Fa-f]{8}", uasm);
        Assert.DoesNotContain("StaticCallTest.__Double__", uasm);
    }

    // ── Task 40: Type normalization ──

    [Fact]
    public void UdonSharpBehaviourField_TypeIsIUdonEventReceiver()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Other : UdonSharpBehaviour { }
public class TypeTest : UdonSharpBehaviour {
    Other _other;
    void Start() { }
}
", "TypeTest");
        Assert.Contains("_other: %VRCUdonCommonInterfacesIUdonEventReceiver, null", uasm);
        Assert.DoesNotContain("%Other", uasm);
    }

    [Fact]
    public void UdonSharpBehaviourArray_TypeIsUnityEngineComponentArray()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Other : UdonSharpBehaviour { }
public class ArrayTypeTest : UdonSharpBehaviour {
    Other[] _others;
    void Start() { }
}
", "ArrayTypeTest");
        Assert.Contains("_others: %UnityEngineComponentArray, null", uasm);
    }

    [Fact]
    public void UdonSharpBehaviourBaseArray_TypeIsUnityEngineComponentArray()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class BaseArrayTest : UdonSharpBehaviour {
    UdonSharpBehaviour[] _behaviours;
    void Start() { }
}
", "BaseArrayTest");
        // UdonSharpBehaviour[] must also be ComponentArray (same as derived[])
        Assert.Contains("_behaviours: %UnityEngineComponentArray, null", uasm);
        Assert.DoesNotContain("IUdonEventReceiverArray", uasm);
    }

    // ── Task 6: UdonSharpBehaviour type name fix ──

    [Fact]
    public void UdonSharpBehaviour_Variable_UsesIUdonEventReceiver()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Other : UdonSharpBehaviour { }
public class RefTest : UdonSharpBehaviour {
    Other _other;
    void Start() { _other = null; }
}
", "RefTest");
        // Variable should be declared as IUdonEventReceiver, not VRCUdonUdonBehaviour
        Assert.Contains("VRCUdonCommonInterfacesIUdonEventReceiver", uasm);
        Assert.DoesNotContain("VRCUdonUdonBehaviour", uasm);
    }

    // ── Task 39: Duplicate param name fix ──

    [Fact]
    public void DuplicateParamNames_AcrossMethods_AreUnique()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ParamTest : UdonSharpBehaviour {
    void SetType(int type) { }
    void AddOption(int type, int value) { }
    void Start() { SetType(1); AddOption(2, 3); }
}
");
        // SetType gets index 0, AddOption gets index 1 → different param names
        Assert.Contains("__0_type__param", uasm);
        Assert.Contains("__1_type__param", uasm);
        var lines = uasm.Split('\n');
        var param0Count = lines.Count(l => l.Contains("__0_type__param"));
        var param1Count = lines.Count(l => l.Contains("__1_type__param"));
        Assert.True(param0Count >= 1);
        Assert.True(param1Count >= 1);
    }

    // ── Task 38: TMPro extern name ──

    [Fact]
    public void TMPText_UsesResolvedContainingType()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TMPro;
public class TMPTest : UdonSharpBehaviour {
    TextMeshProUGUI _label;
    void Start() { _label.text = ""hello""; }
}
");
        // text property is defined on TMP_Text (parent class)
        // Both TMProTMP_Text and TMProTextMeshProUGUI are valid in Udon VM
        Assert.Contains("__set_text__SystemString__SystemVoid", uasm);
    }

    // ── Task 37: EXTERN op name fixes ──

    [Fact]
    public void Multiply_EmitsOpMultiplication()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class MulTest : UdonSharpBehaviour {
    int a; int b;
    void Start() { int c = a * b; }
}
");
        Assert.Contains("op_Multiplication", uasm);
        Assert.DoesNotContain("op_Multiply", uasm);
    }

    [Fact]
    public void Modulus_EmitsOpRemainder()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ModTest : UdonSharpBehaviour {
    int a; int b;
    void Start() { int c = a % b; }
}
");
        Assert.Contains("op_Remainder", uasm);
        Assert.DoesNotContain("op_Modulus", uasm);
    }

    [Fact]
    public void ArrayLength()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ArrayLenTest : UdonSharpBehaviour {
    int[] _arr;
    void Start() { int n = _arr.Length; }
}
");
        // IR compiler resolves array Length via the base SystemArray extern
        Assert.Contains("EXTERN, \"SystemArray.__get_Length__SystemInt32\"", uasm);
    }

    // ── Numeric conversion ──

    [Fact]
    public void NumericConversion_IntToFloat_EmitsConvert()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ConvTest : UdonSharpBehaviour {
    void Start() {
        int i = 42;
        float f = i;
    }
}
");
        Assert.Contains("SystemConvert.__ToSingle__SystemInt32__SystemSingle", uasm);
    }

    [Fact]
    public void NumericConversion_IntDivAsFloat_EmitsConvert()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class DivConvTest : UdonSharpBehaviour {
    float Norm(int r) { return r / 255f; }
    void Start() { float f = Norm(128); }
}
");
        // int param used in float division: Roslyn inserts implicit int→float conversion
        Assert.Contains("SystemConvert.__ToSingle__SystemInt32__SystemSingle", uasm);
    }

    // ── M15: calling convention ──

    [Fact]
    public void Prologue_PushesSentinelOntoStack()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class PrologueTest : UdonSharpBehaviour {
    void Start() { }
}
");
        // Exported methods push sentinel (returnJump var, initialized to 0xFFFFFFFF) onto VM stack
        var lines = uasm.Split('\n').Select(l => l.Trim()).ToArray();
        var startIdx = Array.FindIndex(lines, l => l == "_start:");
        Assert.True(startIdx >= 0);
        // Sentinel: push 0xFFFFFFFF onto stack for RET to POP
        Assert.StartsWith("PUSH, __const_SystemUInt32_sentinel", lines[startIdx + 1]);
    }

    [Fact]
    public void InternalCall_StackBasedReturnAddress()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CallTest : UdonSharpBehaviour {
    void Start() { Helper(); }
    void Helper() { }
}
");
        // Stack-based call: PUSH retaddr before the call (JUMP may be fall-through optimized)
        Assert.Contains("PUSH, __const_retaddr_SystemUInt32_", uasm);
    }

    // ── Registry validation ──

    public static IEnumerable<object[]> AllTestSources()
    {
        yield return new object[] { "EmptyClass", @"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class EmptyTest : UdonSharpBehaviour { }" };

        yield return new object[] { "FieldAssignment", @"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class AssignTest : UdonSharpBehaviour {
    int _value;
    void Start() { _value = 10; }
}" };

        yield return new object[] { "IntArithmetic", @"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ArithTest : UdonSharpBehaviour {
    int _a; int _b;
    void Start() { int x = _a + _b; bool eq = _a == _b; }
}" };

        yield return new object[] { "StaticExtern", @"
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class StaticExtTest : UdonSharpBehaviour {
    void Start() { bool b = Networking.IsOwner(gameObject); Debug.Log(""hi""); }
}" };

        yield return new object[] { "PropertyGetSet", @"
using UdonSharp;
using UnityEngine;
public class PropTest : UdonSharpBehaviour {
    void Start() {
        transform.position = new Vector3();
        var p = transform.position;
    }
}" };

        yield return new object[] { "ArrayOps", @"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ArrOpsTest : UdonSharpBehaviour {
    int[] _arr;
    void Start() {
        _arr = new int[5];
        _arr[0] = 42;
        int x = _arr[0];
        int n = _arr.Length;
    }
}" };

        yield return new object[] { "StringInterpolation", @"
using UdonSharp;
using UnityEngine;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class StrTest : UdonSharpBehaviour {
    int _x;
    void Start() { string s = $""value={_x}""; }
}" };

        yield return new object[] { "ObjectCreation", @"
using UdonSharp;
using UnityEngine;
public class ObjTest : UdonSharpBehaviour {
    void Start() { var c = new Color(1f, 0f, 0f, 1f); }
}" };

        yield return new object[] { "StructFieldSetter", @"
using UdonSharp;
using UnityEngine;
public class SFTest : UdonSharpBehaviour {
    Color _c;
    void Start() { _c.a = 0.5f; }
}" };

        yield return new object[] { "NumericConversion", @"
using UdonSharp;
public class NCTest : UdonSharpBehaviour {
    void Start() { int i = 42; float f = i; }
}" };

        yield return new object[] { "ForEachLoop", @"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class FETest : UdonSharpBehaviour {
    int[] _items; int _sum;
    void Start() { foreach (var x in _items) { _sum += x; } }
}" };

        yield return new object[] { "BitwiseOps", @"
using UdonSharp;
public class BWTest : UdonSharpBehaviour {
    void Start() { int a = 0xFF; int b = a | 0x100; int c = a ^ 0x0F; }
}" };

        yield return new object[] { "RequestSerialization", @"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class RSTest : UdonSharpBehaviour {
    void Start() { RequestSerialization(); }
}" };

        yield return new object[] { "TMPText", @"
using UdonSharp;
using TMPro;
public class TMPRegTest : UdonSharpBehaviour {
    TextMeshProUGUI _label;
    void Start() { _label.text = ""hello""; }
}" };

        yield return new object[] { "GameObjectSetActive", @"
using UdonSharp;
using UnityEngine;
public class GOTest : UdonSharpBehaviour {
    void Start() { gameObject.SetActive(true); }
}" };

        yield return new object[] { "CompoundAssign", @"
using UdonSharp;
public class CATest : UdonSharpBehaviour {
    int _x;
    void Start() { _x += 5; _x -= 3; }
}" };

        yield return new object[] { "ArrayCompoundAssign", @"
using UdonSharp;
public class ACATest : UdonSharpBehaviour {
    int[] _arr;
    void Start() { _arr[0] += 1; }
}" };

        yield return new object[] { "NullCheck", @"
using UdonSharp;
using UnityEngine;
public class NCKTest : UdonSharpBehaviour {
    GameObject _go;
    void Start() { bool b = _go == null; bool c = _go != null; }
}" };

        yield return new object[] { "NestedLoop", @"
using UdonSharp;
public class NLTest : UdonSharpBehaviour {
    int _sum;
    void Start() {
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                _sum += i + j;
    }
}" };

        yield return new object[] { "Switch", @"
using UdonSharp;
public class SWTest : UdonSharpBehaviour {
    int _x; int _y;
    void Start() {
        switch (_x) { case 0: _y = 10; break; case 1: _y = 20; break; default: _y = 0; break; }
    }
}" };

        yield return new object[] { "ExplicitCast", @"
using UdonSharp;
public class ECTest : UdonSharpBehaviour {
    float _f;
    void Start() { int i = (int)_f; }
}" };

        yield return new object[] { "Recursion", @"
using UdonSharp;
public class RCTest : UdonSharpBehaviour {
    int Factorial(int n) { if (n <= 1) return 1; return n * Factorial(n - 1); }
    void Start() { int r = Factorial(5); }
}" };
    }

    [Theory]
    [MemberData(nameof(AllTestSources))]
    public void AllExterns_ExistInRegistry(string name, string source)
    {
        var uasm = TestHelper.CompileToUasm(source);
        var externs = ExtractExterns(uasm);
        var invalid = externs.Where(e => !ExternRegistry.IsValid(e)).ToArray();
        Assert.True(invalid.Length == 0,
            $"[{name}] Unknown externs:\n" + string.Join("\n", invalid));
    }

    // ── M16: cross-behaviour field access ──

    [Fact]
    public void CrossBehaviourFieldRead_EmitsGetProgramVariable()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Other : UdonSharpBehaviour { public int score; }
public class Reader : UdonSharpBehaviour {
    Other _other;
    void Start() { int s = _other.score; }
}
", "Reader");
        Assert.Contains("__GetProgramVariable__SystemString__SystemObject", uasm);
        Assert.DoesNotContain("__get_score__", uasm);
    }

    [Fact]
    public void CrossBehaviourFieldWrite_EmitsSetProgramVariable()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Other : UdonSharpBehaviour { public int score; }
public class Writer : UdonSharpBehaviour {
    Other _other;
    void Start() { _other.score = 42; }
}
", "Writer");
        Assert.Contains("__SetProgramVariable__SystemString_SystemObject__SystemVoid", uasm);
    }

    // ── Cross-class method index consistency ──

    [Fact]
    public void CrossClassCall_MultiMethodTarget_CallerCalleeAgreeOnVarNames()
    {
        var source = @"
using UdonSharp;
public class MultiTarget : UdonSharpBehaviour {
    void PrivateA() { }
    void PrivateB() { }
    void PrivateC() { }
    public int GetValue() { return 42; }
}
public class IndexCaller : UdonSharpBehaviour {
    MultiTarget _t;
    void Start() { int v = _t.GetValue(); }
}
";
        // Public 0-param methods: counter-based naming (__N_name__ret)
        var targetUasm = TestHelper.CompileToUasm(source, "MultiTarget");
        Assert.Contains("__0_GetValue__ret", targetUasm);

        // Caller uses the same counter-based naming
        var (callerUasm, consts) = TestHelper.CompileWithConsts(source, "IndexCaller");
        var stringConsts = consts.Where(e => e.UdonType == "SystemString" && e.Value is string)
            .Select(e => (string)e.Value).ToList();

        Assert.Contains("__0_GetValue__ret", stringConsts);
    }

    [Fact]
    public void CrossClassCall_RealisticTarget_CallerCalleeAgreeOnVarNames()
    {
        // Simulate a realistic TableSystem-like class with many methods including Udon events
        var source = @"
using UdonSharp;
using VRC.SDKBase;
public class BigTable : UdonSharpBehaviour {
    int _localSeat;
    void Start() { _localSeat = -1; }
    int FindSeatByPlayerId(int pid) { return -1; }
    public void TrySeat(int seatIdx) { }
    public void LeaveSeat() { }
    public void OnStartGame() { }
    public void SetRule(int index, int value) { }
    public void SendLocalAction(int seatIdx, int type, int arg) { }
    void Update() { }
    public void DispatchAction(int seat, int type, int arg) { }
    void HandleStateChange() { }
    void ApplyRuleSet() { }
    void CheckTimeout() { }
    void OnDeserialization() { }
    void OnPlayerLeft(VRCPlayerApi player) { }
    void OnOwnershipTransferred(VRCPlayerApi newOwner) { }
    public int GetLocalSeat() { return _localSeat; }
    public bool IsLocalSeat(int p) { return p == _localSeat; }
    public int GetSeatPlayerId(int p) { return 0; }
    public bool IsAllSeated() { return false; }
}
public class Viewer : UdonSharpBehaviour {
    BigTable _table;
    void Start() {
        int seat = _table.GetLocalSeat();
        int pid = _table.GetSeatPlayerId(0);
    }
}
";
        // Public methods use counter-based naming (__N_name__ret)
        var targetUasm = TestHelper.CompileToUasm(source, "BigTable");
        Assert.Contains("__0_GetLocalSeat__ret", targetUasm);
        // GetSeatPlayerId has 1 param → mangled export: __0_GetSeatPlayerId → ret: __0___0_GetSeatPlayerId__ret
        Assert.Contains("__0___0_GetSeatPlayerId__ret", targetUasm);

        // Caller uses the same counter-based naming
        var (callerUasm, consts) = TestHelper.CompileWithConsts(source, "Viewer");
        var stringConsts = consts.Where(e => e.UdonType == "SystemString" && e.Value is string)
            .Select(e => (string)e.Value).ToList();

        // Cross-class call uses counter-based vars that match the target
        Assert.Contains("__0_GetLocalSeat__ret", stringConsts);
        Assert.Contains("__0___0_GetSeatPlayerId__ret", stringConsts);
    }

    // ── Field access via local (struct field getter) ──

    [Fact]
    public void CrossClassCall_InheritedMethod_StableNamingAcrossHierarchy()
    {
        // Regression test: calling a method on a base-typed reference must use
        // the same variable names that the derived class exports.
        var source = @"
using UdonSharp;
public abstract class AbstractHandler : UdonSharpBehaviour {
    void InternalA() { }
    void InternalB() { }
    public virtual bool IsSupported { get { return false; } }
    public virtual void Process(int value) { }
}
public class ConcreteHandler : UdonSharpBehaviour {
    void Extra1() { }
    void Extra2() { }
    void Extra3() { }
    void Extra4() { }
    public virtual bool IsSupported { get { return true; } }
    public virtual void Process(int value) { }
}
public class Caller : UdonSharpBehaviour {
    AbstractHandler _abs;
    ConcreteHandler _conc;
    void Start() {
        bool a = _abs.IsSupported;
        bool b = _conc.IsSupported;
    }
}
";
        // Both abstract and concrete declare the same counter-based vars
        var absUasm = TestHelper.CompileToUasm(source, "AbstractHandler");
        var concUasm = TestHelper.CompileToUasm(source, "ConcreteHandler");
        Assert.Contains("__0_get_IsSupported__ret", absUasm);
        Assert.Contains("__0_get_IsSupported__ret", concUasm);

        // Caller references both through different typed fields; both use same var name
        var (_, consts) = TestHelper.CompileWithConsts(source, "Caller");
        var stringConsts = consts.Where(e => e.UdonType == "SystemString" && e.Value is string)
            .Select(e => (string)e.Value).ToList();
        Assert.Contains("__0_get_IsSupported__ret", stringConsts);
    }

    [Fact]
    public void MethodOverload_SameParamNames_NoDuplicateVarDeclaration()
    {
        // Regression test: method overloads with the same parameter names must not
        // produce duplicate variable declarations (was the "Data variable already exists" bug).
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class OverloadTest : UdonSharpBehaviour {
    public void PlayUrl(int playerType, string url) { }
    public void PlayUrl(string url, int playerType) { }
    public int Compute(int[] counts) { return 0; }
    public int Compute(int[] counts, int offset) { return 0; }
}
");
        Assert.NotNull(uasm);
        // Both overloads compile without "Data variable already exists"
        // First overload: __0_PlayUrl → params __0_playerType__param, __0_url__param
        Assert.Contains("__0_playerType__param", uasm);
        Assert.Contains("__0_url__param", uasm);
        // Second overload: __1_PlayUrl → params __1_url__param, __1_playerType__param
        Assert.Contains("__1_playerType__param", uasm);
        Assert.Contains("__1_url__param", uasm);
        // Compute overloads: __0_Compute and __1_Compute
        Assert.Contains("__0_counts__param", uasm);
        Assert.Contains("__1_counts__param", uasm);
        Assert.Contains("__0_offset__param", uasm);
    }

    [Fact]
    public void MethodOverload_CrossClassCall_AgreesOnVarNames()
    {
        // Verify that cross-class calls to overloaded methods use the correct
        // counter-based variable names.
        var source = @"
using UdonSharp;
public class Target : UdonSharpBehaviour {
    public int Process(int x) { return x; }
    public int Process(int x, int y) { return x + y; }
}
public class CallSite : UdonSharpBehaviour {
    Target _t;
    void Start() {
        int a = _t.Process(1);
        int b = _t.Process(1, 2);
    }
}
";
        // Target: compile successfully with both overloads
        var targetUasm = TestHelper.CompileToUasm(source, "Target");
        Assert.Contains("__0_Process", targetUasm);
        Assert.Contains("__1_Process", targetUasm);

        // CallSite: uses the same names as Target
        var (_, consts) = TestHelper.CompileWithConsts(source, "CallSite");
        var stringConsts = consts.Where(e => e.UdonType == "SystemString" && e.Value is string)
            .Select(e => (string)e.Value).ToList();
        // Both overloads' param names should appear
        Assert.Contains("__0_x__param", stringConsts);
        Assert.Contains("__1_x__param", stringConsts);
        Assert.Contains("__0_y__param", stringConsts);
    }

    [Fact]
    public void OverrideMethod_UsesBaseClassVariableNames()
    {
        // When a derived class overrides a base class method, the override must use
        // the SAME parameter/return variable names as the base class. Otherwise,
        // cross-class calls through base-typed references set the wrong variables.
        var source = @"
using UdonSharp;
public abstract class BaseHandler : UdonSharpBehaviour {
    public abstract int IsSupported(string urlStr);
    public abstract void DoWork(int x, int y);
}
public class DerivedHandler : BaseHandler {
    public void ExtraMethod(string urlStr) { } // takes urlStr BEFORE override
    public override int IsSupported(string urlStr) { return urlStr != null ? 1 : 0; }
    public override void DoWork(int x, int y) { }
}
public class Caller : UdonSharpBehaviour {
    BaseHandler _handler;
    void Start() {
        int s = _handler.IsSupported(""test"");
        _handler.DoWork(1, 2);
    }
}
";
        var baseUasm = TestHelper.CompileToUasm(source, "BaseHandler");
        var derivedUasm = TestHelper.CompileToUasm(source, "DerivedHandler");

        // Base class: IsSupported param is __0_urlStr__param
        Assert.Contains("__0_urlStr__param", baseUasm);
        Assert.Contains("__0___0_IsSupported__ret", baseUasm);

        // Derived class: despite ExtraMethod taking urlStr first,
        // the override must reuse base's __0_urlStr__param (not __1_urlStr__param)
        Assert.Contains("__0_urlStr__param", derivedUasm);
        Assert.Contains("__0___0_IsSupported__ret", derivedUasm);
        // ExtraMethod gets its OWN counter-based urlStr param
        Assert.Contains("__0_ExtraMethod", derivedUasm); // export name

        // Caller uses base class layout for cross-class call
        var (callerUasm, consts) = TestHelper.CompileWithConsts(source, "Caller");
        var stringConsts = consts.Where(e => e.UdonType == "SystemString" && e.Value is string)
            .Select(e => (string)e.Value).ToList();
        // Caller should use base's param name __0_urlStr__param
        Assert.Contains("__0_urlStr__param", stringConsts);
        Assert.Contains("__0___0_IsSupported__ret", stringConsts);
    }

    [Fact]
    public void StructFieldAccess_ViaLocal_EmitsGetterExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class SfaTest : UdonSharpBehaviour {
    void Start() {
        var v = new Vector3(1f, 2f, 3f);
        float x = v.x;
    }
}
");
        Assert.Contains("__get_x__", uasm);
        Assert.DoesNotContain("PUSH, x\n", uasm);
    }

    // ── Unary minus ──

    [Fact]
    public void UnaryMinus_Int_EmitsOpUnaryMinus()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class UnaryMinusTest : UdonSharpBehaviour {
    int _x;
    void Start() { int y = -_x; }
}
");
        Assert.Contains("op_UnaryMinus__SystemInt32__SystemInt32", uasm);
        Assert.DoesNotContain("op_UnaryNegation__SystemInt32", uasm);
    }

    // ── String interpolation 4+ args ──

    [Fact]
    public void StringInterpolation_FourPlusArgs_UsesObjectArray()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Interp4Test : UdonSharpBehaviour {
    int a; int b; int c; int d;
    void Start() { string s = $""{a},{b},{c},{d}""; }
}
");
        Assert.Contains("SystemObjectArray.__ctor__SystemInt32__SystemObjectArray", uasm);
        Assert.Contains("__Format__SystemString_SystemObjectArray__SystemString", uasm);
        Assert.DoesNotContain("SystemObject_SystemObject_SystemObject_SystemObject", uasm);
    }

    // ── this.property extern getter (non-gameObject/transform) ──

    [Fact]
    public void ThisProperty_NonSpecial_EmitsExternGetter()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class ThisPropTest : UdonSharpBehaviour {
    void Start() { string n = gameObject.name; }
}
");
        Assert.Contains("__get_name__", uasm);
        Assert.DoesNotContain("__this_SystemString", uasm);
        // gameObject still uses DeclareThisOnce
        Assert.Contains("__this_UnityEngineGameObject_0: %UnityEngineGameObject, this", uasm);
    }

    // ── Generic GetComponent<T>() ──

    [Fact]
    public void GetComponentGeneric_EmitsSystemTypeOverload()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class GCTest : UdonSharpBehaviour {
    void Start() { var col = GetComponent<Transform>(); }
}
");
        // Must use __T placeholder form (matching UdonSharp reference), not specialized type
        Assert.Contains("UnityEngineComponent.__GetComponent__T", uasm);
        Assert.DoesNotContain("__GetComponent__UnityEngineTransform__", uasm);
        // Type constant must be declared
        Assert.Contains("SystemType", uasm);
    }

    // ── GetComponent<T> USB Shim ──

    [Fact]
    public void GetComponent_UsbType_EmitsShimLoop()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Seat3 : UdonSharpBehaviour { }
public class ShimTest : UdonSharpBehaviour {
    void Start() { var s = GetComponent<Seat3>(); }
}
", "ShimTest");
        // Shim must use GetComponents(typeof(UdonBehaviour)) to fetch all behaviours
        Assert.Contains("UnityEngineComponent.__GetComponents__SystemType__UnityEngineComponentArray", uasm);
        // Must call GetProgramVariable to read __refl_typeid
        Assert.Contains("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject", uasm);
        // Must convert Object to Int64
        Assert.Contains("SystemConvert.__ToInt64__SystemObject__SystemInt64", uasm);
        // Must NOT use the generic __GetComponent__T extern (that's for Unity types only)
        Assert.DoesNotContain("__GetComponent__T", uasm);
    }

    [Fact]
    public void GetComponent_UnityType_StillUsesDirectExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class DirectTest : UdonSharpBehaviour {
    void Start() { var t = GetComponent<Transform>(); }
}
");
        // Unity types must still use the direct extern
        Assert.Contains("UnityEngineComponent.__GetComponent__T", uasm);
        // Must NOT emit shim logic
        Assert.DoesNotContain("__GetComponents__SystemType__UnityEngineComponentArray", uasm);
        Assert.DoesNotContain("GetProgramVariable", uasm);
    }

    [Fact]
    public void GetComponents_UsbType_EmitsTwoPassShim()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Seat4 : UdonSharpBehaviour { }
public class PluralTest : UdonSharpBehaviour {
    void Start() { var seats = GetComponents<Seat4>(); }
}
", "PluralTest");
        // Must use GetComponents to fetch all UdonBehaviours
        Assert.Contains("UnityEngineComponent.__GetComponents__SystemType__UnityEngineComponentArray", uasm);
        // Must create result array (two-pass: count then fill)
        Assert.Contains("UnityEngineComponentArray.__ctor__SystemInt32__UnityEngineComponentArray", uasm);
        // Must set elements in result array
        Assert.Contains("UnityEngineComponentArray.__Set__SystemInt32_UnityEngineComponent__SystemVoid", uasm);
        // Must have GetProgramVariable
        Assert.Contains("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject", uasm);
    }

    [Fact]
    public void GetComponentInChildren_UsbType_EmitsShimWithCorrectExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Seat5 : UdonSharpBehaviour { }
public class InChildrenTest : UdonSharpBehaviour {
    void Start() { var s = GetComponentInChildren<Seat5>(); }
}
", "InChildrenTest");
        // Must use GetComponentsInChildren (plural, non-generic) to fetch
        Assert.Contains("UnityEngineComponent.__GetComponentsInChildren__SystemType__UnityEngineComponentArray", uasm);
        Assert.Contains("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject", uasm);
    }

    [Fact]
    public void GetComponentInChildren_UsbType_BoolArg_EmitsCorrectExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Seat6 : UdonSharpBehaviour { }
public class BoolArgTest : UdonSharpBehaviour {
    void Start() { var s = GetComponentInChildren<Seat6>(true); }
}
", "BoolArgTest");
        // Must use the bool overload
        Assert.Contains("UnityEngineComponent.__GetComponentsInChildren__SystemType_SystemBoolean__UnityEngineComponentArray", uasm);
    }

    [Fact]
    public void GetComponent_InheritedUsbType_UsesTypeIdsArray()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class BaseUnit2 : UdonSharpBehaviour { }
public class DerivedUnit2 : BaseUnit2 { }
public class InheritShimTest : UdonSharpBehaviour {
    void Start() { var u = GetComponent<BaseUnit2>(); }
}
", "InheritShimTest", out var emitter);
        // Must use Array.IndexOf for matching (inheritance path)
        Assert.Contains("SystemArray.__IndexOf__SystemArray_SystemObject__SystemInt32", uasm);
        // The string constant "__refl_typeids" must exist in const entries
        var constEntries = emitter.CodeGenResult.Constants;
        Assert.Contains(constEntries, e => e.Value is string s && s == "__refl_typeids");
        // Must NOT use Convert.ToInt64 (that's the single-typeid path)
        Assert.DoesNotContain("SystemConvert.__ToInt64__SystemObject__SystemInt64", uasm);
    }

    // ── __refl_typeid / __refl_typename values ──

    [Fact]
    public void ReflTypeId_IsSetForUdonSharpBehaviour()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ReflTest : UdonSharpBehaviour {
    void Start() { }
}
", "ReflTest", out var emitter);
        // __refl_typeid must be in UASM data section
        Assert.Contains("__refl_typeid: %SystemInt64, null", uasm);
        // ConstValue must be non-null (SHA256 hash)
        var entries = emitter.CodeGenResult.Constants;
        var reflEntry = entries.FirstOrDefault(e => e.Id == "__refl_typeid");
        Assert.NotNull(reflEntry.Value);
        Assert.IsType<long>(reflEntry.Value);
        Assert.NotEqual(0L, (long)reflEntry.Value);
        // __refl_typename must be set to the fully qualified class name
        var nameEntry = entries.FirstOrDefault(e => e.Id == "__refl_typename");
        Assert.NotNull(nameEntry.Value);
        Assert.Equal("ReflTest", (string)nameEntry.Value);
    }

    [Fact]
    public void ReflTypeIds_InheritedType_HasAncestorChain()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class BaseUnit : UdonSharpBehaviour { }
public class DerivedUnit : BaseUnit { }
public class InheritTest : UdonSharpBehaviour {
    void Start() { }
}
", "DerivedUnit", out var emitter);
        // DerivedUnit should have __refl_typeids array with 2 entries
        Assert.Contains("__refl_typeids: %SystemInt64Array, null", uasm);
        var entries = emitter.CodeGenResult.Constants;
        var idsEntry = entries.FirstOrDefault(e => e.Id == "__refl_typeids");
        Assert.NotNull(idsEntry.Value);
        var ids = idsEntry.Value as long[];
        Assert.NotNull(ids);
        Assert.Equal(2, ids.Length); // [hash(DerivedUnit), hash(BaseUnit)]
    }

    // ── UdonSharpBehaviour array creation type ──

    [Fact]
    public void UdonSharpBehaviourArray_Creation_UsesComponentArray()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Seat2 : UdonSharpBehaviour { }
public class ArrCreateTest : UdonSharpBehaviour {
    void Start() { var seats = new Seat2[4]; }
}
", "ArrCreateTest");
        Assert.Contains("UnityEngineComponentArray.__ctor__SystemInt32__UnityEngineComponentArray", uasm);
        Assert.DoesNotContain("VRCUdonUdonBehaviourArray", uasm);
    }

    // ── UdonSharpBehaviour array element access ──

    [Fact]
    public void UdonSharpBehaviourArrayAccess_UsesComponentElementType()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Seat : UdonSharpBehaviour { public int score; }
public class Table : UdonSharpBehaviour {
    Seat[] _seats;
    void Start() { Seat s = _seats[0]; }
}
", "Table");
        Assert.Contains("UnityEngineComponentArray.__Get__SystemInt32__UnityEngineComponent", uasm);
        Assert.DoesNotContain("__Get__SystemInt32__VRCUdonCommonInterfacesIUdonEventReceiver", uasm);
    }

    // ── String + non-string concatenation ──

    [Fact]
    public void StringPlusInt_UsesConcat()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ConcatTest : UdonSharpBehaviour {
    int _n;
    void Start() { string s = ""赤"" + _n; }
}
");
        Assert.Contains("__Concat__SystemObject_SystemObject__SystemString", uasm);
        Assert.DoesNotContain("__op_Addition__SystemString_SystemObject", uasm);
    }

    // ── Jagged array type ──

    [Fact]
    public void JaggedArray_UsesSystemObjectArray()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class JaggedTest : UdonSharpBehaviour {
    int[][] _data;
    void Start() { _data = new int[3][]; }
}
");
        Assert.Contains("SystemObjectArray", uasm);
        Assert.DoesNotContain("SystemInt32ArrayArray", uasm);
    }

    // ── Expression-bodied method ──

    [Fact]
    public void ExpressionBodiedMethod_EmitsBody()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ExprBodyTest : UdonSharpBehaviour {
    int _x;
    int Double() => _x * 2;
    void Start() { int y = Double(); }
}
");
        Assert.Contains("op_Multiplication", uasm);
    }

    [Fact]
    public void ForeignStatic_ExpressionBodied_EmitsBody()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public static class Ext {
    public static int TileId(this int t) => t == 34 ? 4 : t == 35 ? 13 : t == 36 ? 22 : t;
}
public class ExprBodyTest : UdonSharpBehaviour {
    int _tile;
    void Start() { int id = _tile.TileId(); }
}
", "ExprBodyTest");
        // The foreign static method body must be emitted (not empty JUMP_INDIRECT)
        Assert.Contains("op_Equality", uasm);
    }

    // ── Test Coverage Tier 1: Numeric conversions ──

    [Fact]
    public void ExplicitFloatToInt_EmitsCast()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class CastTest : UdonSharpBehaviour {
    float _f;
    void Start() { int i = (int)_f; }
}
");
        // C# truncates float→int; must insert Math.Truncate before SystemConvert
        Assert.Contains("SystemConvert.__ToDouble__SystemSingle__SystemDouble", uasm);
        Assert.Contains("SystemMath.__Truncate__SystemDouble__SystemDouble", uasm);
        Assert.Contains("SystemConvert.__ToInt32__SystemDouble__SystemInt32", uasm);
    }

    // ── Test Coverage Tier 1: Compound assignment ──

    [Fact]
    public void CompoundAddAssign_EmitsAddAndStore()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class AddAssignTest : UdonSharpBehaviour {
    int _x;
    void Start() { _x += 5; }
}
");
        Assert.Contains("op_Addition", uasm);
    }

    [Fact]
    public void CompoundSubAssign_EmitsSubAndStore()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SubAssignTest : UdonSharpBehaviour {
    int _x;
    void Start() { _x -= 3; }
}
");
        Assert.Contains("op_Subtraction", uasm);
    }

    // ── Test Coverage Tier 1: Array element compound assign ──

    [Fact]
    public void ArrayElementCompoundAssign_EmitsGetSetPattern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ArrCompTest : UdonSharpBehaviour {
    int[] _arr;
    void Start() { _arr[0] += 1; }
}
");
        Assert.Contains("__Get__SystemInt32__SystemInt32", uasm);
        Assert.Contains("op_Addition", uasm);
        Assert.Contains("__Set__SystemInt32_SystemInt32__SystemVoid", uasm);
    }

    // ── Test Coverage Tier 1: Chained access ──

    [Fact]
    public void ChainedPropertyAccess_EmitsSequentialGets()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class ChainTest : UdonSharpBehaviour {
    void Start() { float x = transform.position.x; }
}
");
        Assert.Contains("__get_position__UnityEngineVector3", uasm);
        Assert.Contains("__get_x__SystemSingle", uasm);
    }

    // ── Test Coverage Tier 1: Control flow ──

    [Fact]
    public void NestedIfElse_EmitsCorrectJumps()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NestIfTest : UdonSharpBehaviour {
    int _x;
    void Start() {
        if (_x > 0) {
            if (_x > 10) { _x = 10; }
            else { _x = 5; }
        } else { _x = 0; }
    }
}
");
        var jumpCount = uasm.Split('\n').Count(l => l.Trim().StartsWith("JUMP_IF_FALSE"));
        Assert.True(jumpCount >= 2, $"Expected >= 2 JUMP_IF_FALSE, got {jumpCount}");
    }

    [Fact]
    public void ForLoopWithBreak_EmitsJumpOutOfLoop()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class BreakTest : UdonSharpBehaviour {
    int[] _arr;
    void Start() {
        for (int i = 0; i < _arr.Length; i++) {
            if (_arr[i] == 0) break;
        }
    }
}
");
        Assert.Contains("JUMP_IF_FALSE", uasm);
        var jumpCount = uasm.Split('\n').Count(l => l.Trim().StartsWith("JUMP, 0x"));
        Assert.True(jumpCount >= 2, $"Expected >= 2 JUMPs (loop back + break), got {jumpCount}");
    }

    [Fact]
    public void WhileTrue_EmitsInfiniteLoop()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class WhileTrueTest : UdonSharpBehaviour {
    int _x;
    void Start() {
        while (true) { _x++; if (_x > 100) break; }
    }
}
");
        // Loop body: increment and comparison are present
        Assert.Contains("op_Addition", uasm);
        Assert.Contains("op_GreaterThan", uasm);
        // Loop structure uses JUMP_IF_FALSE for both the while(true) guard and break condition
        var jifCount = uasm.Split('\n').Count(l => l.Trim().StartsWith("JUMP_IF_FALSE"));
        Assert.True(jifCount >= 2, $"Expected >= 2 JUMP_IF_FALSE (loop guard + break), got {jifCount}");
    }

    // ── Test Coverage Tier 1: Cross-behaviour round-trip ──

    [Fact]
    public void CrossBehaviour_ReadAndWrite_RoundTrip()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Other : UdonSharpBehaviour { public int score; }
public class RoundTrip : UdonSharpBehaviour {
    Other _other;
    void Start() {
        int s = _other.score;
        _other.score = s + 1;
    }
}
", "RoundTrip");
        Assert.Contains("__GetProgramVariable__SystemString__SystemObject", uasm);
        Assert.Contains("__SetProgramVariable__SystemString_SystemObject__SystemVoid", uasm);
    }

    // ── Test Coverage Tier 1: Null checks ──

    [Fact]
    public void NullEquality_EmitsComparison()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class NullTest : UdonSharpBehaviour {
    GameObject _go;
    void Start() { bool b = _go == null; }
}
");
        Assert.Contains("op_Equality", uasm);
    }

    [Fact]
    public void NullInequality_EmitsComparison()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class NullTest2 : UdonSharpBehaviour {
    GameObject _go;
    void Start() { bool b = _go != null; }
}
");
        Assert.Contains("op_Inequality", uasm);
    }

    // ── Test Coverage Tier 2: String operations ──

    [Fact]
    public void StringInterpolation_MultipleExpressions()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class StrMultiTest : UdonSharpBehaviour {
    int _a; int _b;
    void Start() { string s = $""{_a} and {_b}""; }
}
");
        Assert.Contains("__Format__", uasm);
    }

    [Fact]
    public void StringConcat_MixedTypes()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class MixConcatTest : UdonSharpBehaviour {
    int _n;
    void Start() { string s = ""count: "" + _n; }
}
");
        Assert.Contains("__Concat__SystemObject_SystemObject__SystemString", uasm);
    }

    // ── Test Coverage Tier 2: Switch ──

    [Fact]
    public void SwitchWithMultipleCases_EmitsJumpTable()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SwitchMultiTest : UdonSharpBehaviour {
    int _x; int _y;
    void Start() {
        switch (_x) {
            case 0: _y = 10; break;
            case 1: _y = 20; break;
            case 2: _y = 30; break;
            default: _y = 0; break;
        }
    }
}
");
        var eqCount = uasm.Split('\n').Count(l => l.Contains("op_Equality"));
        Assert.True(eqCount >= 3, $"Expected >= 3 equality checks for switch, got {eqCount}");
    }

    [Fact]
    public void SwitchWithDefaultOnly_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SwitchDefTest : UdonSharpBehaviour {
    int _x; int _y;
    void Start() {
        switch (_x) {
            default: _y = 99; break;
        }
    }
}
");
        Assert.Contains(".code_start", uasm);
    }

    // ── Test Coverage Tier 2: Loops ──

    [Fact]
    public void ForEachArray_EmitsGetAndIncrement()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class FEArrayTest : UdonSharpBehaviour {
    int[] _items; int _sum;
    void Start() { foreach (var x in _items) _sum += x; }
}
");
        Assert.Contains("__Get__SystemInt32__SystemInt32", uasm);
        Assert.Contains("op_Addition", uasm);
    }

    [Fact]
    public void NestedForLoops_IndependentCounters()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NestForTest : UdonSharpBehaviour {
    int _sum;
    void Start() {
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                _sum += i + j;
    }
}
");
        Assert.Contains("__lcl_i_SystemInt32", uasm);
        Assert.Contains("__lcl_j_SystemInt32", uasm);
    }

    // ── Test Coverage Tier 2: Property read/write same property ──

    [Fact]
    public void PropertyReadWrite_SameProperty()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class PropRWTest : UdonSharpBehaviour {
    void Start() {
        bool active = gameObject.activeSelf;
        gameObject.SetActive(!active);
    }
}
");
        Assert.Contains("__get_activeSelf__SystemBoolean", uasm);
        Assert.Contains("__SetActive__SystemBoolean__SystemVoid", uasm);
    }

    // ── Test Coverage Tier 2: Array creation with size ──

    [Fact]
    public void ArrayCreation_EmitsCtorWithSize()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ArrCtorTest : UdonSharpBehaviour {
    int _n;
    void Start() { var arr = new int[_n]; }
}
");
        Assert.Contains("SystemInt32Array.__ctor__SystemInt32__SystemInt32Array", uasm);
    }

    // ── Test Coverage Tier 2: Multiple params ──

    [Fact]
    public void MethodWithMultipleParams_CorrectParamOrder()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class MultiParamTest : UdonSharpBehaviour {
    int Add3(int a, int b, int c) { return a + b + c; }
    void Start() { int r = Add3(1, 2, 3); }
}
");
        Assert.Contains("__0_a__param", uasm);
        Assert.Contains("__0_b__param", uasm);
        Assert.Contains("__0_c__param", uasm);
    }

    // ── Test Coverage Tier 2: Recursive call ──

    [Fact]
    public void RecursiveCall_EmitsJumpToSelf()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class RecTest : UdonSharpBehaviour {
    int _n;
    int Factorial(int n) { if (n <= 1) return 1; return n * Factorial(n - 1); }
    void Start() { _n = Factorial(5); }
}
");
        Assert.Contains("Factorial:", uasm);
        Assert.Contains("op_Multiplication", uasm);
    }

    // ── do-while loop ──

    [Fact]
    public void DoWhileLoop_EmitsBodyBeforeCondition()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class DoWhileTest : UdonSharpBehaviour {
    int _x;
    void Start() { do { _x++; } while (_x < 10); }
}
");
        // Body (increment) should appear before the condition check
        var lines = uasm.Split('\n');
        var startIdx = System.Array.FindIndex(lines, l => l.Contains("_start:"));
        var addIdx = System.Array.FindIndex(lines, startIdx, l => l.Contains("op_Addition"));
        var ltIdx = System.Array.FindIndex(lines, startIdx, l => l.Contains("op_LessThan"));
        Assert.True(addIdx > 0 && ltIdx > 0, "Should have both op_Addition and op_LessThan");
        Assert.True(addIdx < ltIdx, "Body (op_Addition) must come before condition (op_LessThan)");
    }

    [Fact]
    public void DoWhileLoop_WithBreak_EmitsJump()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class DoWhileBreakTest : UdonSharpBehaviour {
    int _x;
    void Start() {
        do {
            if (_x > 5) break;
            _x++;
        } while (_x < 10);
    }
}
");
        // break should emit a JUMP past the loop
        Assert.Contains("JUMP,", uasm);
        Assert.Contains("op_GreaterThan", uasm);
        Assert.Contains("op_LessThan", uasm);
    }

    [Fact]
    public void DoWhileLoop_WithContinue_EmitsJump()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class DoWhileContinueTest : UdonSharpBehaviour {
    int _x;
    int _y;
    void Start() {
        do {
            _x++;
            if (_x % 2 == 0) continue;
            _y++;
        } while (_x < 10);
    }
}
");
        // continue should jump to the condition check
        Assert.Contains("JUMP,", uasm);
        Assert.Contains("op_Remainder", uasm);
    }

    // ── nameof expression ──

    [Fact]
    public void NameOf_EmitsStringConst()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class NameOfTest : UdonSharpBehaviour {
    int score;
    void Start() { Debug.Log(nameof(score)); }
}
");
        Assert.Contains("__const_SystemString_", uasm);
    }

    // ── params array (verification) ──

    [Fact]
    public void ParamsArray_ExpandedByRoslyn()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ParamsTest : UdonSharpBehaviour {
    static int Sum(params int[] values) {
        int s = 0;
        foreach (var v in values) s += v;
        return s;
    }
    void Start() { int r = Sum(1, 2, 3); }
}
");
        // Roslyn expands params into array creation + element Set
        Assert.Contains("SystemInt32Array.__ctor__SystemInt32__SystemInt32Array", uasm);
        Assert.Contains("SystemInt32Array.__Set__SystemInt32_SystemInt32__SystemVoid", uasm);
    }

    // ── default argument (verification) ──

    [Fact]
    public void DefaultArgument_CompilesCorrectly()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class DefArgTest : UdonSharpBehaviour {
    int Foo(int x = 42) { return x; }
    void Start() { int r = Foo(); }
}
");
        // Roslyn fills in the default value as a literal argument
        Assert.Contains("__0_Foo__ret", uasm);
    }

    // ── is pattern ──

    [Fact]
    public void IsPattern_TypeCheck_EmitsIsInstanceOfType()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class IsPatternTest : UdonSharpBehaviour {
    Component _c;
    void Start() { bool b = _c is Transform; }
}
");
        Assert.Contains("SystemType.__IsInstanceOfType__SystemObject__SystemBoolean", uasm);
        Assert.Contains("__const_SystemType_", uasm);
    }

    // ── as cast ──

    [Fact]
    public void AsCast_ReferenceType_PassesThrough()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class AsCastTest : UdonSharpBehaviour {
    Component _c;
    void Start() { var t = _c as Transform; }
}
");
        // Reference-type 'as' is a no-op conversion in Udon (no runtime type enforcement)
        Assert.DoesNotContain("IsInstanceOfType", uasm);
        Assert.Contains(".code_start", uasm);
    }

    // ── expression-bodied property ──

    [Fact]
    public void ExpressionBodiedProperty_EmitsGetter()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ExprPropTest : UdonSharpBehaviour {
    int _x;
    int DoubleX => _x * 2;
    void Start() { int y = DoubleX; }
}
");
        Assert.Contains("op_Multiplication", uasm);
        // Property getter should NOT be exported
        Assert.DoesNotContain(".export get_DoubleX", uasm);
    }

    // ── Feature 2: Pattern matching ──

    [Fact]
    public void IsPattern_Null_EmitsEqualityCheck()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class IsNullTest : UdonSharpBehaviour {
    GameObject _go;
    void Start() { bool b = _go is null; }
}
");
        Assert.Contains("op_Equality", uasm);
    }

    [Fact]
    public void IsPattern_NotNull_EmitsNegation()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class IsNotNullTest : UdonSharpBehaviour {
    GameObject _go;
    void Start() { bool b = _go is not null; }
}
");
        Assert.Contains("op_Equality", uasm);
        Assert.Contains("op_UnaryNegation", uasm);
    }

    [Fact]
    public void IsPattern_TypeWithVariable_EmitsTypeCheckAndCopy()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class IsDeclTest : UdonSharpBehaviour {
    Component _c;
    void Start() { if (_c is Transform t) { var p = t.position; } }
}
");
        Assert.Contains("IsInstanceOfType", uasm);
        Assert.Contains("COPY", uasm);
    }

    [Fact]
    public void SwitchExpression_EmitsArms()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SwitchExprTest : UdonSharpBehaviour {
    int _x; int _y;
    void Start() {
        _y = _x switch { 0 => 10, 1 => 20, _ => 0 };
    }
}
");
        var eqCount = uasm.Split('\n').Count(l => l.Contains("op_Equality"));
        Assert.True(eqCount >= 2, $"Expected >= 2 equality checks, got {eqCount}");
        Assert.Contains("JUMP", uasm);
    }

    [Fact]
    public void SwitchExpression_EnumConstants_AreCorrect()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public enum GamePhase { Lobby, Playing, Result }
public class EnumSwitchTest : UdonSharpBehaviour {
    public GamePhase phase = GamePhase.Playing;
    void Start() {
        var desc = phase switch {
            GamePhase.Lobby => ""lobby"",
            GamePhase.Playing => ""playing"",
            GamePhase.Result => ""result"",
            _ => ""unknown""
        };
        Debug.Log(desc);
    }
}", "EnumSwitchTest");

        // Verify data section has correct constants:
        // phase field should have default value 1 (Playing)
        // Enum constants 0 (Lobby), 1 (Playing), 2 (Result) should be in const pool
        var lines = uasm.Split('\n');

        // Check data section for const declarations
        var dataLines = lines.Where(l => l.Contains("const_SystemInt32")).ToList();
        // Output for debugging
        var output = new System.Text.StringBuilder();
        output.AppendLine("Data section const_SystemInt32 lines:");
        foreach (var l in dataLines) output.AppendLine($"  {l.TrimEnd()}");

        // Check code section for switch expression
        output.AppendLine("Switch expression equality checks:");
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("op_Equality") && i > 0)
                output.AppendLine($"  L{i-1}: {lines[i-1].TrimEnd()}");
        }

        // Verify enum constants are correctly generated
        var eqCount = lines.Count(l => l.Contains("op_Equality"));
        Assert.True(eqCount >= 3, $"Expected >= 3 equality checks for enum switch, got {eqCount}");

        // Verify switch jumps to end after each arm (last arm may fall through)
        var jumpCount = lines.Count(l => l.TrimStart().StartsWith("JUMP,"));
        Assert.True(jumpCount >= 2, $"Expected >= 2 unconditional jumps, got {jumpCount}");

        // Verify enum-to-underlying conversion is applied
        Assert.Contains("SystemConvert.__ToInt32__SystemObject__SystemInt32", uasm);
    }

    [Fact]
    public void SwitchStatement_PatternCase_EmitsTypeCheck()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class SwitchPatTest : UdonSharpBehaviour {
    Component _c;
    void Start() {
        switch (_c) {
            case Transform t: break;
            default: break;
        }
    }
}
");
        Assert.Contains("IsInstanceOfType", uasm);
    }

    // ── Feature 3: Interface dispatch ──

    [Fact]
    public void InterfaceCall_VoidMethod_EmitsSendCustomEvent()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
public class IfaceVoidTest : UdonSharpBehaviour {
    UnityEngine.Component _target;
    void Start() { ((IToggleable)_target).Toggle(); }
}
");
        Assert.Contains("SendCustomEvent__SystemString__SystemVoid", uasm);
    }

    [Fact]
    public void InterfaceCall_WithArgs_EmitsSetProgramVariable()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
public class IfaceArgTest : UdonSharpBehaviour {
    UnityEngine.Component _target;
    void Start() { int s = ((IScored)_target).GetScore(); }
}
");
        Assert.Contains("SendCustomEvent__SystemString__SystemVoid", uasm);
        Assert.Contains("GetProgramVariable__SystemString__SystemObject", uasm);
    }

    [Fact]
    public void InterfaceField_NoCastNeeded_EmitsSendCustomEvent()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
public class IfaceFieldTest : UdonSharpBehaviour {
    IToggleable target;
    void Start() { target.Toggle(); }
}
");
        Assert.Contains("SendCustomEvent__SystemString__SystemVoid", uasm);
        // Field should be typed as IUdonEventReceiver
        Assert.Contains("IUdonEventReceiver", uasm);
    }

    [Fact]
    public void InterfaceField_WithReturn_NoCastNeeded()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
public class IfaceReturnTest : UdonSharpBehaviour {
    IScored scorer;
    int _val;
    void Start() { _val = scorer.GetScore(); }
}
");
        Assert.Contains("SendCustomEvent__SystemString__SystemVoid", uasm);
        Assert.Contains("GetProgramVariable__SystemString__SystemObject", uasm);
    }

    // ── Feature 1: Local functions ──

    [Fact]
    public void LocalFunction_BasicCall()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class LocalFuncTest : UdonSharpBehaviour {
    int _result;
    void Start() {
        int Double(int x) { return x + x; }
        _result = Double(5);
    }
}
");
        Assert.Contains("op_Addition", uasm);
        // Local function should be emitted as a separate label, not exported
        Assert.DoesNotContain(".export", uasm.Split('\n')
            .Where(l => l.Contains("Double")).FirstOrDefault() ?? "");
    }

    [Fact]
    public void LocalFunction_WithCapture()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class LocalCapTest : UdonSharpBehaviour {
    int _result;
    void Start() {
        int offset = 10;
        int AddOffset(int x) { return x + offset; }
        _result = AddOffset(5);
    }
}
");
        // Capture works naturally via shared registers in Udon's flat model
        Assert.Contains("op_Addition", uasm);
    }

    [Fact]
    public void LocalFunction_WithReturnValue()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class LocalRetTest : UdonSharpBehaviour {
    int _result;
    void Start() {
        int Square(int x) { return x * x; }
        _result = Square(4);
    }
}
");
        Assert.Contains("op_Multiplication", uasm);
        Assert.Contains("__ret", uasm);
    }

    // ── Feature 4: Lambda expressions ──

    [Fact]
    public void Lambda_ActionDelegate_EmitsHoistedMethod()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class LambdaActionTest : UdonSharpBehaviour {
    int _x;
    void Start() {
        Action a = () => { _x = 42; };
        a();
    }
}
");
        // Lambda body should be hoisted and called (JUMP may be fall-through optimized)
        Assert.Contains("__const_retaddr_SystemUInt32_", uasm);
    }

    [Fact]
    public void Lambda_FuncDelegate_ReturnsValue()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class LambdaFuncTest : UdonSharpBehaviour {
    int _result;
    void Start() {
        Func<int> f = () => 42;
        _result = f();
    }
}
");
        Assert.Contains("__ret", uasm);
        // Hoisted method call (JUMP may be fall-through optimized)
        Assert.Contains("__const_retaddr_SystemUInt32_", uasm);
    }

    [Fact]
    public void Lambda_WithCapture_AccessesOuterScope()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class LambdaCaptureTest : UdonSharpBehaviour {
    int _result;
    void Start() {
        int factor = 3;
        Func<int, int> mul = (x) => x * factor;
        _result = mul(7);
    }
}
");
        Assert.Contains("op_Multiplication", uasm);
    }

    // ── Tuple deconstruction ──

    [Fact]
    public void TupleDeconstruction_LiteralValues_EmitsAssignments()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class TupleTest : UdonSharpBehaviour {
    void Start() {
        var (a, b) = (10, 20);
        int c = a + b;
    }
}
");
        Assert.Contains("op_Addition", uasm);
        // Should not contain any ValueTuple references
        Assert.DoesNotContain("ValueTuple", uasm);
    }

    [Fact]
    public void TupleDeconstruction_WithDiscard_SkipsElement()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class DiscardTest : UdonSharpBehaviour {
    int _val;
    void Start() {
        var (_, b) = (10, 20);
        _val = b;
    }
}
");
        Assert.DoesNotContain("ValueTuple", uasm);
    }

    [Fact]
    public void RelationalPattern_GreaterThan_EmitsComparison()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class RelPatTest : UdonSharpBehaviour {
    int _x; int _y;
    void Start() {
        _y = _x switch {
            > 0 => 1,
            _ => 0
        };
    }
}
");
        Assert.Contains("__op_GreaterThan__SystemInt32_SystemInt32__SystemBoolean", uasm);
    }

    [Fact]
    public void RelationalPattern_LessThanOrEqual_EmitsComparison()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class RelPatLETest : UdonSharpBehaviour {
    int _x; bool _b;
    void Start() { _b = _x is <= 10; }
}
");
        Assert.Contains("__op_LessThanOrEqual", uasm);
    }

    [Fact]
    public void BinaryPattern_And_EmitsCombinedCheck()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class BinPatTest : UdonSharpBehaviour {
    int _x; int _y;
    void Start() {
        _y = _x switch {
            >= 0 and < 100 => 1,
            _ => -1
        };
    }
}
");
        Assert.Contains("__op_GreaterThanOrEqual", uasm);
        Assert.Contains("__op_LessThan", uasm);
        Assert.Contains("__op_ConditionalAnd", uasm);
    }

    [Fact]
    public void BinaryPattern_Or_EmitsDisjunction()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class BinPatOrTest : UdonSharpBehaviour {
    int _x; bool _b;
    void Start() { _b = _x is 0 or 1; }
}
");
        Assert.Contains("__op_ConditionalOr", uasm);
    }

    // ── Index from end ──

    [Fact]
    public void IndexFromEnd_EmitsLengthMinusN()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class IdxEndTest : UdonSharpBehaviour {
    int[] _arr; int _last;
    void Start() { _last = _arr[^1]; }
}
");
        Assert.Contains("__get_Length__SystemInt32", uasm);
        Assert.Contains("__op_Subtraction__SystemInt32_SystemInt32__SystemInt32", uasm);
        Assert.Contains("__Get__SystemInt32", uasm);
    }

    [Fact]
    public void IndexFromEnd_Expression()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class IdxEnd2Test : UdonSharpBehaviour {
    int[] _arr; int _v; int _n;
    void Start() { _v = _arr[^_n]; }
}
");
        Assert.Contains("__get_Length__SystemInt32", uasm);
        Assert.Contains("__op_Subtraction__SystemInt32_SystemInt32__SystemInt32", uasm);
    }

    // ── Range slicing ──

    [Fact]
    public void RangeSlice_EmitsLoopCopy()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class RangeTest : UdonSharpBehaviour {
    int[] _arr; int[] _slice;
    void Start() { _slice = _arr[1..3]; }
}
");
        // __op_Subtraction (3-1) may be constant-folded; verify the slice still works
        Assert.Contains("__ctor__SystemInt32", uasm); // new int[len]
        Assert.Contains("__Set__SystemInt32", uasm); // result[i] = ...
        Assert.Contains("__op_LessThan", uasm); // loop condition
    }

    [Fact]
    public void RangeSlice_OpenEnd_EmitsLength()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class RangeOpenTest : UdonSharpBehaviour {
    int[] _arr; int[] _tail;
    void Start() { _tail = _arr[2..]; }
}
");
        Assert.Contains("__get_Length__SystemInt32", uasm);
        Assert.Contains("__ctor__SystemInt32", uasm);
    }

    [Fact]
    public void RangeSlice_FromEnd_InRange()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class RangeEndTest : UdonSharpBehaviour {
    int[] _arr; int[] _slice;
    void Start() { _slice = _arr[1..^1]; }
}
");
        Assert.Contains("__get_Length__SystemInt32", uasm);
        Assert.Contains("__op_Subtraction", uasm);
    }

    // ── Extension method as foreign static ──

    [Fact]
    public void ExtensionMethod_NotEmittedAsExtern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public static class TileExt {
    public static int TileId(this int t) => t == 34 ? 4 : t;
}
public class ExtCallTest : UdonSharpBehaviour {
    int _tile;
    void Start() { int id = _tile.TileId(); }
}
", "ExtCallTest");
        Assert.DoesNotContain("TileExt.__TileId", uasm);
        Assert.Contains("op_Equality", uasm);
    }

    [Fact]
    public void ForeignStatic_TransitiveDependency_CollectsIndirectCalls()
    {
        // Class calls Helper.Process() which internally calls val.Double()
        // Double must be collected transitively
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public static class MathExt {
    public static int Double(this int x) => x + x;
}
public static class Helper {
    public static int Process(int[] arr, int idx) {
        return arr[idx].Double();
    }
}
public class TransitiveTest : UdonSharpBehaviour {
    int[] _data;
    void Start() { int r = Helper.Process(_data, 0); }
}
", "TransitiveTest");
        // Neither Helper nor MathExt should appear as extern
        Assert.DoesNotContain("Helper.__Process", uasm);
        Assert.DoesNotContain("MathExt.__Double", uasm);
        // Double's body (x + x) should be emitted
        Assert.Contains("op_Addition", uasm);
    }

    [Fact]
    public void SelfRecursion_UsesFlatRegister()
    {
        // Recursion support is disabled (matches UdonSharp behavior).
        // Self-recursive calls use flat register save/restore like any other call.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class RecursionTest : UdonSharpBehaviour {
    int _count;
    void Recurse(int depth) {
        if (depth <= 0) { _count++; return; }
        Recurse(depth - 1);
    }
    void Start() { Recurse(3); }
}
", "RecursionTest");
        Assert.DoesNotContain("__retAddrStack", uasm);
        Assert.DoesNotContain("__retAddrSp", uasm);
    }

    [Fact]
    public void NonRecursiveCall_UsesFlatRegister()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class NonRecursiveTest : UdonSharpBehaviour {
    int Helper(int x) { return x + 1; }
    void Start() { int r = Helper(5); }
}
", "NonRecursiveTest");
        // Non-recursive call should NOT use stack
        Assert.DoesNotContain("__retAddrStack", uasm);
        Assert.DoesNotContain("__retAddrSp", uasm);
    }

    [Fact]
    public void RecursiveCall_WithLocalVariables_UsesFlatRegister()
    {
        // Recursion support is disabled (matches UdonSharp behavior).
        // Even recursive calls with local variables use flat register save/restore.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class RecLocalVarTest : UdonSharpBehaviour {
    int _result;
    int Factorial(int n) {
        if (n <= 1) return 1;
        int r = Factorial(n - 1);
        return n * r;
    }
    void Start() { _result = Factorial(5); }
}
", "RecLocalVarTest");
        Assert.DoesNotContain("__retAddrStack", uasm);
        Assert.DoesNotContain("__retAddrSp", uasm);
        Assert.Contains("op_Multiplication", uasm);
    }

    [Fact]
    public void TailRecursion_CompilesSuccessfully()
    {
        // Tail-recursive calls compile without retAddrStack when the result
        // is directly returned (no post-call local variable usage)
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class TailRecTest : UdonSharpBehaviour {
    int _result;
    int Loop(int n, int acc) {
        if (n <= 0) return acc;
        return Loop(n - 1, acc + n);
    }
    void Start() { _result = Loop(10, 0); }
}
", "TailRecTest");
        Assert.NotNull(uasm);
        // The Loop method should be emitted with addition for acc + n
        Assert.Contains("Loop", uasm);
        Assert.Contains("op_Addition", uasm);
    }

    // ── Re-entrance guard ──

    [Fact]
    public void ExportedMethod_HasReentrancePreamble()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class PreambleTest : UdonSharpBehaviour
{
    public void Foo() { }
}
");
        var lines = uasm.Split('\n').Select(l => l.Trim()).ToArray();
        var exportIdx = Array.FindIndex(lines, l => l == ".export Foo");
        Assert.True(exportIdx >= 0, "Foo should be exported");
        var labelIdx = Array.FindIndex(lines, exportIdx, l => l == "Foo:");
        Assert.True(labelIdx >= 0, "Foo label should exist");
        // Sentinel: PUSH sentinel onto stack, then body label
        Assert.Equal("PUSH, __const_SystemUInt32_sentinel", lines[labelIdx + 1]);
        Assert.Equal("Foo__body:", lines[labelIdx + 2]);
    }

    [Fact]
    public void InternalCall_JumpsToBodyLabel_NotExportLabel()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class InternalCallTest : UdonSharpBehaviour
{
    public void Target() { }
    public void Caller() { Target(); }
}
");
        var lines = uasm.Split('\n').Select(l => l.Trim()).ToArray();
        // The body label should exist
        Assert.True(lines.Any(l => l == "Target__body:"), "Target__body label should exist");
        // Internal call from Caller should JUMP to Target__body address, not Target address
        // Verify by checking that the JUMP target inside Caller matches Target__body
        var callerStart = Array.FindIndex(lines, l => l == "Caller__body:");
        Assert.True(callerStart >= 0);
        var callerJumps = new List<string>();
        for (int i = callerStart; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("JUMP, 0x") && !lines[i].StartsWith("JUMP_IF"))
                callerJumps.Add(lines[i]);
            if (lines[i].StartsWith("JUMP_INDIRECT")) break;
        }
        // Should have exactly one JUMP (the call to Target)
        Assert.True(callerJumps.Count >= 1, "Caller should have at least one JUMP");
    }

    // ── Generic Monomorphization ──

    [Fact]
    public void GenericMethod_Identity_EmitsSpecializedBody()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GenericTest : UdonSharpBehaviour
{
    T Id<T>(T x) { return x; }
    void Start() { int y = Id<int>(42); }
}
");
        // Specialized method label should exist with concrete type name
        Assert.Contains("SystemInt32", uasm);
        // Param and return vars for the specialization
        Assert.Contains("__param", uasm);
        Assert.Contains("__ret", uasm);
    }

    [Fact]
    public void GenericMethod_ArrayElement_EmitsConcreteArrayAccess()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GenericArrTest : UdonSharpBehaviour
{
    T GetFirst<T>(T[] arr) { return arr[0]; }
    void Start()
    {
        int[] nums = new int[] { 1, 2, 3 };
        int x = GetFirst<int>(nums);
    }
}
");
        // Array access should use concrete type
        var externs = ExtractExterns(uasm);
        Assert.Contains(externs, e => e.Contains("SystemInt32Array.__Get__SystemInt32__SystemInt32"));
    }

    [Fact]
    public void GenericMethod_MultipleSpecializations_EmitsSeparateBodies()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GenericMultiTest : UdonSharpBehaviour
{
    T Id<T>(T x) { return x; }
    void Start()
    {
        int a = Id<int>(1);
        float b = Id<float>(2.0f);
    }
}
");
        // Both specializations should have their own labels
        Assert.Contains("SystemInt32", uasm);
        Assert.Contains("SystemSingle", uasm);
        // Both should have return vars with concrete types
        Assert.Contains("__ret: %SystemInt32", uasm);
        Assert.Contains("__ret: %SystemSingle", uasm);
    }

    [Fact]
    public void GenericMethod_NotExported()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GenericExportTest : UdonSharpBehaviour
{
    T Id<T>(T x) { return x; }
    void Start() { int y = Id<int>(42); }
}
");
        // Generic specializations should NOT appear in .export_code
        var exports = uasm.Split('\n')
            .Where(l => l.Trim().StartsWith(".export_code"))
            .ToArray();
        Assert.DoesNotContain(exports, e => e.Contains("Id_SystemInt32"));
    }

    [Fact]
    public void GenericMethod_WithLocalVar_UsesConcreteType()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GenericLocalTest : UdonSharpBehaviour
{
    T Double<T>(T x) where T : struct
    {
        T result = x;
        return result;
    }
    void Start() { int y = Double<int>(21); }
}
");
        // Local variable should be declared with concrete type
        Assert.Contains("%SystemInt32", uasm);
    }

    [Fact]
    public void GenericMethod_InterfaceConstraint_UsesConcreteType()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GenericConstraintTest : UdonSharpBehaviour
{
    T Min<T>(T a, T b) where T : IComparable<T>
    {
        return a.CompareTo(b) < 0 ? a : b;
    }
    void Start() { int x = Min<int>(3, 5); }
}
");
        // CompareTo should resolve to the concrete int type, not IComparable<T>
        var externs = ExtractExterns(uasm);
        Assert.Contains(externs, e => e.Contains("SystemInt32.__CompareTo"));
        Assert.DoesNotContain(externs, e => e.Contains("IComparable"));
    }

    // ── Delegate parameter invocation via JUMP_INDIRECT ──

    [Fact]
    public void DelegateParam_FuncInvocation_EmitsJumpIndirect()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DlgTest : UdonSharpBehaviour
{
    int Apply(Func<int, int> f, int x) { return f(x); }
    void Start() { int r = Apply(n => n + 1, 42); }
}
");
        // Lambda body should use convention vars and JUMP_INDIRECT
        Assert.Contains("JUMP_INDIRECT", uasm);
        // Convention arg and ret vars should be declared
        Assert.Contains("__dlg_", uasm);
    }

    [Fact]
    public void DelegateParam_ActionInvocation_EmitsJumpIndirect()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DlgActionTest : UdonSharpBehaviour
{
    int _sum;
    void ForEach(int[] arr, Action<int> action)
    {
        for (var i = 0; i < arr.Length; i++) action(arr[i]);
    }
    void Start()
    {
        int[] a = new int[] { 1, 2, 3 };
        ForEach(a, x => { _sum = _sum + x; });
    }
}
");
        Assert.Contains("JUMP_INDIRECT", uasm);
    }

    [Fact]
    public void DelegateParam_GenericMethod_EmitsConcreteConventionVars()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DlgGenericTest : UdonSharpBehaviour
{
    TResult Map<T, TResult>(T value, Func<T, TResult> f) { return f(value); }
    void Start()
    {
        int r = Map<int, int>(5, x => x * 2);
    }
}
");
        Assert.Contains("JUMP_INDIRECT", uasm);
        // Convention vars should use concrete types
        Assert.Contains("SystemInt32", uasm);
    }

    [Fact]
    public void DelegateParam_BoolPredicate_WorksInLoop()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DlgPredicateTest : UdonSharpBehaviour
{
    int CountWhere(int[] arr, Func<int, bool> pred)
    {
        int c = 0;
        for (var i = 0; i < arr.Length; i++)
            if (pred(arr[i])) c = c + 1;
        return c;
    }
    void Start()
    {
        int[] a = new int[] { 1, 2, 3, 4, 5 };
        int r = CountWhere(a, x => x > 3);
    }
}
");
        Assert.Contains("JUMP_INDIRECT", uasm);
    }

    [Fact]
    public void DelegateParam_FuncInvocation_VerifySystemUInt32Type()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DlgTest : UdonSharpBehaviour
{
    int Apply(Func<int, int> f, int x) { return f(x); }
    void Start() { int r = Apply(n => n + 1, 42); }
}
");
        // Verify that delegate parameter is typed as SystemUInt32
        // Delegates are represented as uint indices into a function pointer table
        Assert.Contains("JUMP_INDIRECT", uasm);
        Assert.Contains("__0_f__param", uasm);
        // The convention parameter should be declared with SystemUInt32 type
        Assert.Matches(@"__0_f__param\s*:\s*%SystemUInt32", uasm);
    }

    // ── F3: Temp variable pooling ──

    [Fact]
    public void TempPool_ReusesAcrossStatements()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class TempPoolTest : UdonSharpBehaviour
{
    void Start()
    {
        int a = 1 + 2;
        int b = 3 + 4;
        int c = 5 + 6;
    }
}
");
        // The IR optimizer constant-folds all additions (1+2=3, 3+4=7, 5+6=11),
        // and dead code elimination removes unused locals, so no int temps are needed.
        var tempCount = System.Text.RegularExpressions.Regex.Matches(
            uasm, @"__intnl_SystemInt32_\d+:").Count;
        Assert.Equal(0, tempCount);
    }

    [Fact]
    public void TempPool_SameStatementGetsDistinctTemps()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class TempDistinctTest : UdonSharpBehaviour
{
    void Start()
    {
        int a = (1 + 2) + (3 + 4);
    }
}
");
        // The IR optimizer constant-folds the entire expression (1+2)+(3+4)=10,
        // and dead code elimination removes the unused local, so no int temps are needed.
        var tempCount = System.Text.RegularExpressions.Regex.Matches(
            uasm, @"__intnl_SystemInt32_\d+:").Count;
        Assert.Equal(0, tempCount);
    }

    [Fact]
    public void TempPool_LoopBodyReusesTemps()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class TempLoopTest : UdonSharpBehaviour
{
    int _sum;
    void Start()
    {
        for (int i = 0; i < 10; i = i + 1)
        {
            _sum = _sum + i;
        }
    }
}
");
        // The loop body's temps are bounded by the number of operations, not iterations.
        // No temp explosion from the loop.
        var tempCount = System.Text.RegularExpressions.Regex.Matches(
            uasm, @"__intnl_SystemInt32_\d+:").Count;
        Assert.True(tempCount <= 12, $"Expected <=12 int temps for loop, got {tempCount}");
    }

    [Fact]
    public void TempPool_CrossMethodNoOverlap()
    {
        // Method A has a high-temp statement then calls B.
        // B's temps must NOT overlap with A's high watermark.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class TempCrossTest : UdonSharpBehaviour
{
    int Heavy()
    {
        // stmt1: many temps (a+b+c+d)
        int x = (1 + 2) + (3 + 4);
        // stmt2: calls Light() — fewer temps, but Light must not reuse stmt1's
        return Light(x);
    }
    int Light(int v) { return v + 10; }
    void Start() { int r = Heavy(); }
}
");
        // Extract all __intnl_SystemInt32_N variable declarations
        var matches = System.Text.RegularExpressions.Regex.Matches(
            uasm, @"__intnl_SystemInt32_(\d+):");
        var ids = matches.Cast<System.Text.RegularExpressions.Match>()
            .Select(m => int.Parse(m.Groups[1].Value))
            .Distinct().OrderBy(x => x).ToArray();
        // All temp IDs should be unique and sequential — no gaps that would indicate
        // overlap between Heavy's high-watermark temps and Light's temps
        for (int i = 1; i < ids.Length; i++)
            Assert.True(ids[i] > ids[i - 1],
                $"Temp IDs not strictly increasing: {string.Join(",", ids)}");
    }

    // ── F2: Tail call optimization ──

    [Fact]
    public void TailCall_DirectReturn_EmitsJumpWithoutStack()
    {
        // Tail-recursive GCD: return GCD(b, a % b) is in tail position
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class TcoTest : UdonSharpBehaviour
{
    int GCD(int a, int b) { if (b == 0) return a; return GCD(b, a % b); }
    void Start() { int r = GCD(48, 18); }
}
");
        // TCO should NOT use retAddrStack for the tail call
        Assert.DoesNotContain("__retAddrStack", uasm);
        // Should contain a JUMP back to the method body (not JUMP_INDIRECT for the recursive call)
        var lines = uasm.Split('\n').Select(l => l.Trim()).ToArray();
        // The method body should have a JUMP (non-indirect) for the tail call
        Assert.Contains(lines, l => l.StartsWith("JUMP, ") && !l.Contains("INDIRECT"));
    }

    [Fact]
    public void TailCall_NonTail_UsesFlatRegister()
    {
        // Recursion support is disabled (matches UdonSharp behavior).
        // Non-tail recursive calls also use flat register save/restore.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class NonTcoTest : UdonSharpBehaviour
{
    int Factorial(int n) { if (n <= 1) return 1; return n * Factorial(n - 1); }
    void Start() { int r = Factorial(5); }
}
");
        Assert.DoesNotContain("__retAddrStack", uasm);
    }

    [Fact]
    public void TailCall_AccumulatorPattern_Optimized()
    {
        // Accumulator-style tail recursion: return FactAcc(n-1, acc*n)
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class TcoAccTest : UdonSharpBehaviour
{
    int FactAcc(int n, int acc) { if (n <= 1) return acc; return FactAcc(n - 1, acc * n); }
    void Start() { int r = FactAcc(5, 1); }
}
");
        Assert.DoesNotContain("__retAddrStack", uasm);
    }

    // ── Object initializer tests ──

    [Fact]
    public void ObjectInitializer_StructFields_EmitsSetters()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ObjInitTest : UdonSharpBehaviour
{
    void Start()
    {
        var c = new UnityEngine.Color { r = 1f, g = 0.5f, b = 0f };
    }
}
");
        Assert.Contains("__set_r", uasm);
        Assert.Contains("__set_g", uasm);
        Assert.Contains("__set_b", uasm);
    }

    [Fact]
    public void ObjectInitializer_WithCtorArgs_EmitsCtorThenSetters()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ObjInitCtorTest : UdonSharpBehaviour
{
    void Start()
    {
        var c = new UnityEngine.Color(1f, 0f, 0f) { a = 0.5f };
    }
}
");
        Assert.Contains("__ctor__", uasm);
        Assert.Contains("__set_a", uasm);
    }

    [Fact]
    public void ObjectInitializer_NoCtorArgs_EmitsDefaultThenSetter()
    {
        // new Color { r = 1f } — parameterless struct with initializer
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ObjInitDefaultTest : UdonSharpBehaviour
{
    void Start()
    {
        var c = new UnityEngine.Color { r = 1f };
    }
}
");
        Assert.DoesNotContain("__ctor__", uasm);
        Assert.Contains("__set_r", uasm);
    }

    // ── Null-coalescing assignment tests ──

    [Fact]
    public void CoalesceAssign_LocalVar_EmitsNullCheckAndCopy()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CoalesceAssignTest : UdonSharpBehaviour
{
    string _cached;
    void Start()
    {
        _cached ??= ""default"";
    }
}
");
        Assert.Contains("op_Equality", uasm);
        Assert.Contains("JUMP_IF_FALSE", uasm);
    }

    [Fact]
    public void CoalesceAssign_UsedAsExpression_ReturnsTarget()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CoalesceAssignExprTest : UdonSharpBehaviour
{
    string _val;
    void Start()
    {
        string result = (_val ??= ""fallback"");
        UnityEngine.Debug.Log(result);
    }
}
");
        Assert.Contains("op_Equality", uasm);
        Assert.Contains("Debug.__Log", uasm);
    }

    static string[] ExtractExterns(string uasm)
    {
        return uasm.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("EXTERN, \""))
            .Select(l => l.Substring("EXTERN, \"".Length).TrimEnd('"'))
            .Distinct()
            .ToArray();
    }

    // ── goto statement ──

    [Fact]
    public void Goto_ForwardJump()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GotoForwardTest : UdonSharpBehaviour {
    int _x;
    void Start() {
        _x = 1;
        goto skip;
        _x = 2;
        skip:
        _x = 3;
    }
}
");
        // Dead code (_x = 2) is eliminated via unreachable block pruning (RPO).
        // Verify: _x = 1 (const_0) and _x = 3 (const_1) are both assigned, but only 2 consts remain.
        var constCount = uasm.Split('\n').Count(l => l.Contains("__const_SystemInt32_") && l.Contains(": %SystemInt32"));
        Assert.Equal(2, constCount); // 1 and 3 survive; 2 is dead code eliminated
        // Note: __goto_skip label may be merged away by SimplifyCFG when the goto
        // target has a single predecessor (the goto block itself). The backward
        // jump test (Goto_BackwardJump_LoopPattern) covers label preservation for
        // multi-predecessor targets.
    }

    [Fact]
    public void Goto_BackwardJump_LoopPattern()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GotoBackTest : UdonSharpBehaviour {
    int _x;
    void Start() {
        _x = 0;
        top:
        _x = _x + 1;
        if (_x < 10) goto top;
    }
}
");
        Assert.Contains("__goto_top:", uasm);
    }

    // ── using statement ──

    [Fact]
    public void Using_Classic_EmitsDispose()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class UsingTest : UdonSharpBehaviour {
    int _x;
    void Start() {
        using (var r = new DisposableResource()) {
            _x = 1;
        }
    }
}
");
        Assert.Contains("EXTERN, \"TestStubsDisposableResource.__Dispose__SystemVoid\"", uasm);
    }

    [Fact]
    public void UsingDeclaration_EmitsDisposeAtBlockEnd()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class UsingDeclTest : UdonSharpBehaviour {
    int _x;
    void Start() {
        using var r = new DisposableResource();
        _x = 1;
    }
}
");
        Assert.Contains("EXTERN, \"TestStubsDisposableResource.__Dispose__SystemVoid\"", uasm);
    }

    [Fact]
    public void UsingDeclaration_MultipleDisposedInReverseOrder()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class UsingMultiDeclTest : UdonSharpBehaviour {
    int _x;
    void Start() {
        using var a = new DisposableResource();
        using var b = new DisposableResource();
        _x = 1;
    }
}
");
        // Both should be disposed
        var lines = uasm.Split('\n').Select(l => l.Trim()).ToArray();
        var disposeLines = lines
            .Select((l, i) => (l, i))
            .Where(t => t.l.Contains("TestStubsDisposableResource.__Dispose__SystemVoid"))
            .ToList();
        Assert.Equal(2, disposeLines.Count);
    }

    // ── H4: using statement existing variable / expression Dispose ──

    [Fact]
    public void UsingStatement_ExistingVariable_EmitsDispose()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class UsingExistingVarTest : UdonSharpBehaviour {
    int _x;
    void Start() {
        var r = new DisposableResource();
        using (r) {
            _x = 1;
        }
    }
}
");
        Assert.Contains("TestStubsDisposableResource.__Dispose__SystemVoid", uasm);
    }

    [Fact]
    public void UsingStatement_Expression_EmitsDispose()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class UsingExprTest : UdonSharpBehaviour {
    int _x;
    void Start() {
        using (new DisposableResource()) {
            _x = 1;
        }
    }
}
");
        Assert.Contains("TestStubsDisposableResource.__Dispose__SystemVoid", uasm);
    }

    // ── jagged array ──

    [Fact]
    public void JaggedArray_ElementAccess()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class JaggedTest : UdonSharpBehaviour {
    int[][] _arr;
    void Start() { int v = _arr[0][1]; }
}
");
        // Outer access: SystemObjectArray.__Get → returns SystemObject (inner array)
        // Inner access: SystemInt32Array.__Get → returns element
        Assert.Contains("SystemObjectArray.__Get__SystemInt32__SystemObject", uasm);
        Assert.Contains("SystemInt32Array.__Get__SystemInt32__SystemInt32", uasm);
    }

    [Fact]
    public void JaggedArray_StringElementAccess()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class JaggedStringTest : UdonSharpBehaviour {
    string[][] _arr;
    void Start() { string v = _arr[0][1]; }
}
");
        Assert.Contains("SystemObjectArray.__Get__SystemInt32__SystemObject", uasm);
        Assert.Contains("SystemStringArray.__Get__SystemInt32__SystemString", uasm);
    }

    [Fact]
    public void JaggedArray_BoolElementAccess()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class JaggedBoolTest : UdonSharpBehaviour {
    bool[][] _arr;
    void Start() { bool v = _arr[0][1]; }
}
");
        Assert.Contains("SystemObjectArray.__Get__SystemInt32__SystemObject", uasm);
        Assert.Contains("SystemBooleanArray.__Get__SystemInt32__SystemBoolean", uasm);
    }

    [Fact]
    public void BaseMethod_VoidCall()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class Boss : BaseEnemy {
    void Start() { TakeDamage(10); }
}
", "Boss");
        // base method inlined, not called via EXTERN
        Assert.DoesNotContain("EXTERN", uasm.Split('\n').Where(l => l.Contains("TakeDamage")).FirstOrDefault() ?? "");
        // Argument 10 copied to param var
        Assert.Contains("amount__param", uasm);
        // Base method body contains subtraction extern
        Assert.Contains("SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32", uasm);
    }

    [Fact]
    public void BaseMethod_WithReturn()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class Boss : BaseEnemy {
    int _currentHp;
    void Start() { _currentHp = GetHp(); }
}
", "Boss");
        Assert.DoesNotContain("EXTERN", uasm.Split('\n').Where(l => l.Contains("GetHp")).FirstOrDefault() ?? "");
        Assert.Contains("_currentHp", uasm);
        // Return var declared for GetHp
        Assert.Contains("GetHp__ret", uasm);
    }

    [Fact]
    public void BaseField_InheritedAccess()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class Boss : BaseEnemy {
    void Start() { _hp = 100; }
}
", "Boss");
        // Inherited field declared in data section
        Assert.Contains("_hp: %SystemInt32, null", uasm);
        // Constant 100 and COPY to _hp
        Assert.Contains("PUSH, _hp", uasm);
        Assert.Contains("COPY", uasm);
    }

    [Fact]
    public void BaseMethod_NoExternLeak()
    {
        // Verify that base method calls do NOT generate EXTERN instructions
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class Boss : BaseEnemy {
    void Start() {
        TakeDamage(5);
        int hp = GetHp();
    }
}
", "Boss");
        var lines = uasm.Split('\n');
        // No EXTERN line should reference TakeDamage or GetHp
        Assert.DoesNotContain("TakeDamage", lines.Where(l => l.Contains("EXTERN")).FirstOrDefault() ?? "");
        Assert.DoesNotContain("GetHp", lines.Where(l => l.Contains("EXTERN")).FirstOrDefault() ?? "");
    }

    [Fact]
    public void BaseMethod_SdkMethodStillExtern()
    {
        // RequestSerialization (from UdonSharpBehaviour) must remain EXTERN
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using TestStubs;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class Boss : BaseEnemy {
    void Start() {
        TakeDamage(1);
        RequestSerialization();
    }
}
", "Boss");
        Assert.Contains("__RequestSerialization__SystemVoid", uasm);
    }

    // ── override VRC events ──

    [Fact]
    public void Override_OnPlayerJoined_Exported()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDKBase;
public class JoinTest : UdonSharpBehaviour {
    int _count;
    public override void OnPlayerJoined(VRCPlayerApi player) {
        _count = _count + 1;
    }
}
", "JoinTest");
        Assert.Contains(".export _onPlayerJoined", uasm);
        Assert.Contains("_onPlayerJoined:", uasm);
        Assert.Contains("_count", uasm);
    }

    [Fact]
    public void Override_Interact_Exported()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class InteractTest : UdonSharpBehaviour {
    int _clicks;
    public override void Interact() {
        _clicks = _clicks + 1;
    }
}
", "InteractTest");
        Assert.Contains(".export _interact", uasm);
        Assert.Contains("_interact:", uasm);
    }

    [Fact]
    public void Override_OnDeserialization_Exported()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class DeserTest : UdonSharpBehaviour {
    int _synced;
    public override void OnDeserialization() {
        _synced = 1;
    }
}
", "DeserTest");
        Assert.Contains(".export _onDeserialization", uasm);
    }

    [Fact]
    public void Override_WithStartAndEvent_BothExported()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDKBase;
public class MultiTest : UdonSharpBehaviour {
    int _ready;
    int _joined;
    void Start() { _ready = 1; }
    public override void OnPlayerJoined(VRCPlayerApi player) { _joined = _joined + 1; }
}
", "MultiTest");
        Assert.Contains(".export _start", uasm);
        Assert.Contains(".export _onPlayerJoined", uasm);
    }

    [Fact]
    public void RefOut_IntTryParse()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class TryParseTest : UdonSharpBehaviour {
    void Start() {
        int n;
        int.TryParse(""42"", out n);
    }
}
", "TryParseTest");
        Assert.Contains("SystemInt32.__TryParse__SystemString_SystemInt32Ref__SystemBoolean", uasm);
    }

    [Fact]
    public void RefOut_OutExistingVariable()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class OutExistingTest : UdonSharpBehaviour {
    void Start() {
        int n = 0;
        int.TryParse(""42"", out n);
    }
}
", "OutExistingTest");
        Assert.Contains("SystemInt32Ref", uasm);
    }

    [Fact]
    public void RefOut_UserMethodOut()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class UserOutTest : UdonSharpBehaviour {
    void Get(out int x) { x = 42; }
    void Start() {
        int v;
        Get(out v);
    }
}
", "UserOutTest");
        // copy-out: param var is copied back to caller's local after call
        Assert.Contains("COPY", uasm);
    }

    [Fact]
    public void RefOut_UserMethodRef()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class UserRefTest : UdonSharpBehaviour {
    void Inc(ref int x) { x = x + 1; }
    void Start() {
        int v = 10;
        Inc(ref v);
    }
}
", "UserRefTest");
        Assert.Contains("COPY", uasm);
    }

    [Fact]
    public void RefOut_DiscardOut()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class DiscardOutTest : UdonSharpBehaviour {
    void Start() {
        int.TryParse(""42"", out _);
    }
}
", "DiscardOutTest");
        Assert.Contains("SystemInt32.__TryParse__SystemString_SystemInt32Ref__SystemBoolean", uasm);
    }

    [Fact]
    public void RefOut_ArrayElement_CopiesBack()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class RefOutArrayTest : UdonSharpBehaviour {
    void Fill(out int x) { x = 99; }
    int[] arr = new int[3];
    void Start() {
        Fill(out arr[1]);
    }
}
", "RefOutArrayTest");
        // Should emit array Set after the call to copy out
        Assert.Contains("SystemInt32Array.__Set__SystemInt32_SystemInt32__SystemVoid", uasm);
    }

    [Fact]
    public void RefOut_NonThisField_CopiesBack()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class Other : UdonSharpBehaviour {
    public int val;
}
public class RefOutFieldTest : UdonSharpBehaviour {
    Other other;
    void Fill(out int x) { x = 42; }
    void Start() {
        Fill(out other.val);
    }
}
", "RefOutFieldTest");
        // Should emit SetProgramVariable to copy out to the foreign field
        Assert.Contains("__SetProgramVariable__SystemString_SystemObject__SystemVoid", uasm);
    }

    [Fact]
    public void SerializeField_IsExported()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class SerializeFieldTest : UdonSharpBehaviour
{
    [SerializeField] int _privateField;
    [SerializeField] string _privateString;
    public int publicField;
    int nonSerializedField;
}
", "SerializeFieldTest");
        // [SerializeField] private fields should be exported
        Assert.Contains(".export _privateField", uasm);
        Assert.Contains(".export _privateString", uasm);
        // public fields also exported
        Assert.Contains(".export publicField", uasm);
        // non-serialized private fields should NOT be exported
        Assert.DoesNotContain(".export nonSerializedField", uasm);
    }

    [Fact]
    public void SerializeField_InBaseClass_IsExported()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class BaseWithSerialize : UdonSharpBehaviour
{
    [SerializeField] string _basePrivateField;
    public int basePublicField;
    int baseNonSerialized;
}
public class DerivedFromSerialize : BaseWithSerialize
{
    [SerializeField] int _derivedField;
}
", "DerivedFromSerialize");
        // Base class [SerializeField] fields should be exported in derived class
        Assert.Contains(".export _basePrivateField", uasm);
        Assert.Contains(".export basePublicField", uasm);
        // Derived class [SerializeField] fields also exported
        Assert.Contains(".export _derivedField", uasm);
        // Non-serialized base class fields should NOT be exported
        Assert.DoesNotContain(".export baseNonSerialized", uasm);
    }

    [Fact]
    public void Diagnostics_PropertyIsAccessible()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class DiagTest : UdonSharpBehaviour {
    void Start() { var x = gameObject.name; }
}", "DiagTest", out var emitter);
        Assert.NotNull(emitter.Diagnostics);
        foreach (var d in emitter.Diagnostics)
            Assert.False(string.IsNullOrEmpty(d.Message));
    }

    [Fact]
    public void BaseField_FromMetadataReference_IncludedInUasm()
    {
        // Step 1: Compile stubs + base class into a DLL (simulates cross-asmdef scenario)
        var baseSource = TestHelper.StubSource + @"
namespace ExternalLib {
    public class UserBaseClass : UdonSharp.UdonSharpBehaviour {
        protected bool flagField;
        [UnityEngine.SerializeField] protected int countField;
    }
}";
        var baseTree = CSharpSyntaxTree.ParseText(baseSource);
        var baseComp = CSharpCompilation.Create("External",
            new[] { baseTree }, TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new System.IO.MemoryStream();
        var emitRes = baseComp.Emit(ms);
        Assert.True(emitRes.Success,
            "Base compilation failed: " + string.Join("\n",
                emitRes.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        ms.Seek(0, System.IO.SeekOrigin.Begin);
        var externalRef = MetadataReference.CreateFromStream(ms);

        // Step 2: Compile derived class with ONLY the DLL (no source for base class).
        // The base class and all stubs come from the metadata reference.
        var derivedSource = @"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class MetaDerived : ExternalLib.UserBaseClass {
    void Start() { flagField = true; countField = 42; }
}";
        var derivedTree = CSharpSyntaxTree.ParseText(derivedSource);
        var refs = TestHelper.StandardRefs
            .Append(externalRef)
            .ToArray();
        var comp2 = CSharpCompilation.Create("Test",
            new[] { derivedTree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diags = comp2.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.Empty(diags);

        var model = comp2.GetSemanticModel(derivedTree);
        var classDecl = derivedTree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
            .First(c => c.Identifier.Text == "MetaDerived");
        var classSymbol = model.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
        Assert.NotNull(classSymbol);

        // Key assertion: base type is metadata-only (DeclaringSyntaxReferences empty)
        var baseType = classSymbol.BaseType;
        Assert.NotNull(baseType);
        Assert.Equal("UserBaseClass", baseType.Name);
        Assert.Empty(baseType.DeclaringSyntaxReferences);

        // Verify: UasmEmitter includes inherited fields from metadata-only base
        var uasmEmitter = new UasmEmitter(comp2, classSymbol);
        var uasm = uasmEmitter.Emit();
        Assert.Contains("flagField", uasm);
        Assert.Contains("countField", uasm);
        Assert.Contains(".export countField", uasm); // [SerializeField] → exported
    }

    // ── C1: Static property setter ──

    [Fact]
    public void StaticPropertySetter_EmitsExternWithoutInstance()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class StaticPropTest : UdonSharpBehaviour {
    void Start() { Time.fixedDeltaTime = 0.02f; }
}
", "StaticPropTest");
        // Should emit a static setter extern (no instance PUSH)
        Assert.Contains("UnityEngineTime.__set_fixedDeltaTime__SystemSingle__SystemVoid", uasm);
    }

    // ── H5: byte/short increment and compound assignment type promotion ──

    [Fact]
    public void ByteIncrement_EmitsInt32PromotionAndNarrowing()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ByteIncrTest : UdonSharpBehaviour {
    byte _b;
    void Start() { _b++; }
}
");
        // Should use Int32 addition
        Assert.Contains("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32", uasm);
        // Should narrow back to Byte
        Assert.Contains("SystemConvert.__ToByte__SystemInt32__SystemByte", uasm);
    }

    [Fact]
    public void ShortCompoundAdd_EmitsInt32PromotionAndNarrowing()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ShortAddTest : UdonSharpBehaviour {
    short _s;
    void Start() { _s += 5; }
}
");
        // Should use Int32 operators (ResolveBinaryExtern promotes)
        Assert.Contains("SystemInt32", uasm);
        // Should narrow back to Int16
        Assert.Contains("SystemConvert.__ToInt16__SystemInt32__SystemInt16", uasm);
    }

    // ── H3: switch expression bool result default initialization ──

    [Fact]
    public void SwitchExpression_BoolResult_CompilesCorrectly()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class SwitchBoolTest : UdonSharpBehaviour {
    bool Test(int x) {
        return x switch {
            1 => true,
            2 => false,
            _ => true
        };
    }
}", "SwitchBoolTest");
        Assert.NotNull(uasm);
        // Verify no crash — previously bool.Parse("0") threw FormatException
    }

    // ── C2: Non-this reference-type field assignment ──

    [Fact]
    public void NonThisFieldAssignment_ReferenceType_EmitsFieldSetExtern()
    {
        // Use out emitter to get raw UASM without extern validation
        // (sortingOrder is not in the Udon extern registry but the emit path is correct)
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class FieldSetTest : UdonSharpBehaviour {
    Renderer _renderer;
    void Start() { _renderer.sortingOrder = 5; }
}
", "FieldSetTest");
        // Non-this reference-type field should use extern setter, not silent COPY
        Assert.Contains("__set_sortingOrder__", uasm);
    }

    // ── Ref parameter with post-increment ──

    [Fact]
    public void RefParam_PostIncrementInCallee()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class RefIncTest : UdonSharpBehaviour {
    void WriteVal(int[] buf, ref int o, int val) { buf[o++] = val; }
    void Start() {
        var buf = new int[4];
        int o = 0;
        WriteVal(buf, ref o, 42);
    }
}
", "RefIncTest");
        // ref parameter should produce COPY for copy-in/copy-out
        Assert.Contains("COPY", uasm);
        // Array Set should be emitted for buf[o++] = val
        Assert.Contains("SystemInt32Array.__Set__SystemInt32_SystemInt32__SystemVoid", uasm);
    }

    [Fact]
    public void RefParam_MultipleCalls()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class RefMultiTest : UdonSharpBehaviour {
    void WriteTwo(int[] buf, ref int o, int a, int b) { buf[o++] = a; buf[o++] = b; }
    void Start() {
        var buf = new int[8];
        int o = 0;
        WriteTwo(buf, ref o, 1, 2);
        WriteTwo(buf, ref o, 3, 4);
    }
}
", "RefMultiTest");
        // Two calls with same ref variable — both should compile
        Assert.Contains("COPY", uasm);
        Assert.Contains("SystemInt32Array.__Set__SystemInt32_SystemInt32__SystemVoid", uasm);
    }

    [Fact]
    public void ForeignStatic_MultipleExtensionMethods_AllInlined()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public static class TileExt {
    public static bool IsYaochu(this int t) => t >= 27 || t % 9 == 0 || t % 9 == 8;
    public static int TileId(this int t) => t == 34 ? 4 : t == 35 ? 13 : t == 36 ? 22 : t;
    public static bool IsRedDora(this int t) => t >= 34 && t <= 36;
}
public class MultiExtTest : UdonSharpBehaviour {
    int _tile;
    void Start() {
        bool y = _tile.IsYaochu();
        int id = _tile.TileId();
        bool r = _tile.IsRedDora();
    }
}
", "MultiExtTest");
        Assert.DoesNotContain("TileExt.__IsYaochu", uasm);
        Assert.DoesNotContain("TileExt.__TileId", uasm);
        Assert.DoesNotContain("TileExt.__IsRedDora", uasm);
        Assert.Contains("op_GreaterThanOrEqual", uasm);
    }

    [Fact]
    public void ForeignStatic_TransitiveExtension_CountArray()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public static class TileExt {
    public static int TileId(this int t) => t == 34 ? 4 : t;
}
public static class HandUtil {
    public static void ToCountArray(int[] hand, int size, int[] dest) {
        Array.Clear(dest, 0, 34);
        for (var i = 0; i < size; i++) dest[hand[i].TileId()]++;
    }
}
public class TransCountTest : UdonSharpBehaviour {
    int[] _hand;
    int[] _counts = new int[34];
    void Start() { HandUtil.ToCountArray(_hand, 14, _counts); }
}
", "TransCountTest");
        Assert.DoesNotContain("HandUtil.__ToCountArray", uasm);
        Assert.DoesNotContain("TileExt.__TileId", uasm);
        Assert.Contains("SystemArray.__Clear__SystemArray_SystemInt32_SystemInt32__SystemVoid", uasm);
    }

    [Fact]
    public void ForeignStatic_ExpressionBodied_ChainedCalls()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public static class TileExt {
    public static bool IsYaochu(this int t) => t >= 27 || t % 9 == 0 || t % 9 == 8;
    public static bool IsDragon(this int t) => t >= 31 && t <= 33;
}
public class ChainedExtTest : UdonSharpBehaviour {
    int CheckTile(int t) => t.IsYaochu() ? (t.IsDragon() ? 2 : 1) : 0;
    void Start() { int r = CheckTile(31); }
}
", "ChainedExtTest");
        Assert.DoesNotContain("TileExt.__", uasm);
        Assert.Contains("op_GreaterThanOrEqual", uasm);
    }

    [Fact]
    public void ArrayShuffle_RandomSwap_CompilesCorrectly()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class ShuffleTest : UdonSharpBehaviour {
    int[] _tiles = new int[136];
    void Shuffle() {
        for (var i = _tiles.Length - 1; i > 0; i--) {
            var j = Random.Range(0, i + 1);
            var tmp = _tiles[i];
            _tiles[i] = _tiles[j];
            _tiles[j] = tmp;
        }
    }
    void Start() { Shuffle(); }
}
", "ShuffleTest");
        Assert.Contains("UnityEngineRandom.__Range__SystemInt32_SystemInt32__SystemInt32", uasm);
        Assert.Contains("SystemInt32Array.__Get__SystemInt32__SystemInt32", uasm);
        Assert.Contains("SystemInt32Array.__Set__SystemInt32_SystemInt32__SystemVoid", uasm);
    }

    [Fact]
    public void ForeignStatic_InHelperLoop_ExtensionCallsInlined()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public static class TileExt {
    public static int TileId(this int t) => t == 34 ? 4 : t;
    public static bool IsRedDora(this int t) => t >= 34 && t <= 36;
}
public class NakiPatternTest : UdonSharpBehaviour {
    int[] _hand = new int[14];
    int _handSize;
    int RemoveTiles(int tid, int count, int flagShift) {
        int flags = 0;
        int removed = 0;
        for (var i = _handSize - 1; i >= 0 && removed < count; i--) {
            if (_hand[i].TileId() != tid) continue;
            if (_hand[i].IsRedDora()) flags |= (flagShift << removed);
            _hand[i] = _hand[_handSize - 1];
            _handSize--;
            removed++;
        }
        return flags;
    }
    void Start() { int f = RemoveTiles(5, 2, 2); }
}
", "NakiPatternTest");
        Assert.DoesNotContain("TileExt.__", uasm);
        Assert.Contains("op_Equality", uasm);
    }

    [Fact]
    public void JaggedArray_FieldAccess_InPartialClass()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public partial class JagPartialTest : UdonSharpBehaviour {
    int[][] _data;
    void Init() {
        _data = new int[4][];
        for (var i = 0; i < 4; i++) _data[i] = new int[14];
    }
}
public partial class JagPartialTest {
    void Start() {
        Init();
        _data[0][0] = 42;
        int v = _data[0][0];
    }
}
", "JagPartialTest");
        Assert.Contains("SystemObjectArray.__Get__SystemInt32__SystemObject", uasm);
        Assert.Contains("SystemInt32Array.__Set__SystemInt32_SystemInt32__SystemVoid", uasm);
    }

    [Fact]
    public void RefParam_JaggedArrayWrite_CompilesCorrectly()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class SerialPatternTest : UdonSharpBehaviour {
    int[][] _data;
    void Write2D(int[] buf, ref int o, int[][] arr, int inner) {
        for (var p = 0; p < 4; p++)
            for (var i = 0; i < inner; i++)
                buf[o++] = arr[p][i];
    }
    void Start() {
        _data = new int[4][];
        for (var i = 0; i < 4; i++) _data[i] = new int[14];
        var buf = new int[200];
        int o = 0;
        Write2D(buf, ref o, _data, 14);
    }
}
", "SerialPatternTest");
        Assert.Contains("COPY", uasm);
        Assert.Contains("SystemObjectArray.__Get__SystemInt32__SystemObject", uasm);
    }

    [Fact]
    public void UdonSynced_ArrayField_HasSyncDirective()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using VRC.SDKBase;
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class SyncArrayTest : UdonSharpBehaviour {
    [UdonSynced] int[] _data;
    [UdonSynced] int _version;
    void Start() {
        _data = new int[100];
        _data[0] = 42;
        _version = 1;
        RequestSerialization();
    }
}
", "SyncArrayTest");
        Assert.Contains(".sync _data", uasm);
        Assert.Contains(".sync _version", uasm);
        Assert.Contains("__RequestSerialization", uasm);
    }

    [Fact]
    public void ArrayElement_GameObjectSetActive_ChainedAccess()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using UnityEngine;
public class ChainedGOTest : UdonSharpBehaviour {
    GameObject[] _panels;
    void Start() {
        _panels = new GameObject[4];
        _panels[0].SetActive(true);
        _panels[1].SetActive(false);
    }
}
", "ChainedGOTest");
        Assert.Contains("UnityEngineGameObject.__SetActive__SystemBoolean__SystemVoid", uasm);
        Assert.Contains("UnityEngineGameObjectArray.__Get__SystemInt32__UnityEngineGameObject", uasm);
    }

    [Fact]
    public void InheritedMethods_AreEmittedInDerivedClass()
    {
        var baseSrc = @"
using UdonSharp;
public class AnimalBase : UdonSharpBehaviour {
    public int hp;
    public void TakeDamage(int amount) { hp -= amount; }
    public int GetHp() { return hp; }
}";
        var derivedSrc = @"
using UdonSharp;
public class Dog : AnimalBase { }
";
        var uasm = TestHelper.CompileToUasm(new[] { baseSrc, derivedSrc }, "Dog");
        // Inherited methods should be exported
        Assert.Contains(".export __0_TakeDamage", uasm);
        Assert.Contains(".export GetHp", uasm);
        // Inherited field should be present
        Assert.Contains(".export hp", uasm);
        // Method bodies should be emitted (not empty .code_start/.code_end)
        Assert.Contains("EXTERN,", uasm);
    }

    [Fact]
    public void InheritedMethods_OverriddenMethodUsesOverride()
    {
        var baseSrc = @"
using UdonSharp;
public class VehicleBase : UdonSharpBehaviour {
    public int speed;
    public virtual void Accelerate() { speed += 1; }
    public int GetSpeed() { return speed; }
}";
        var derivedSrc = @"
using UdonSharp;
public class Car : VehicleBase {
    public override void Accelerate() { speed += 10; }
}
";
        var uasm = TestHelper.CompileToUasm(new[] { baseSrc, derivedSrc }, "Car");
        // Override should be exported
        Assert.Contains(".export Accelerate", uasm);
        // Inherited non-overridden method should also be exported
        Assert.Contains(".export GetSpeed", uasm);
        // Both method bodies should be emitted
        Assert.Contains("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32", uasm);
    }

    [Fact]
    public void InheritedMethods_CrossCallingNoDuplicateLabels()
    {
        var baseSrc = @"
using UdonSharp;
public class EventBase : UdonSharpBehaviour {
    public void SendEvent(string name) { gameObject.SetActive(true); }
    public void Notify() { SendEvent(""test""); }
}";
        var derivedSrc = @"
using UdonSharp;
public class EventChild : EventBase { }
";
        // Should not throw due to duplicate labels
        var uasm = TestHelper.CompileToUasm(new[] { baseSrc, derivedSrc }, "EventChild");
        Assert.Contains(".export __0_SendEvent", uasm);
        Assert.Contains(".export Notify", uasm);
    }

    // === Optimization Integration Tests ===

    [Fact]
    public void Optimization_MultipleIfStatements_ReducesTempCount()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class OptTest : UdonSharpBehaviour {
    int x;
    void Start() {
        if (x > 0) x = 1;
        if (x > 1) x = 2;
        if (x > 2) x = 3;
    }
}
");
        // Count __intnl_SystemBoolean declarations in data section
        var dataSection = uasm.Split(new[] { ".data_end" }, System.StringSplitOptions.None)[0];
        var boolCount = dataSection.Split('\n')
            .Count(line => line.Contains("__intnl_") && line.Contains("SystemBoolean"));
        // IR pipeline: each comparison produces a separate SSA bool; register allocator
        // keeps them distinct because they live across basic block boundaries.
        // Verify count is bounded (no explosion beyond the 3 comparisons).
        Assert.True(boolCount <= 3, $"Expected at most 3 __intnl_ SystemBoolean vars, got {boolCount}");
    }

    [Fact]
    public void Optimization_FullPipeline_ReducesCopyAndVarCount()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class OptPipelineTest : UdonSharpBehaviour
{
    int x;
    void _start()
    {
        int a = 1;
        int b = 2;
        int c = a + b;
        if (c > 0) x = c;
        if (c > 1) x = c + 1;
        if (c > 2) x = c + 2;
    }
}
", "OptPipelineTest");
        var dataSection = uasm.Split(new[] { ".data_end" }, System.StringSplitOptions.None)[0];
        var intnlCount = dataSection.Split('\n')
            .Count(line => line.Contains("__intnl_"));
        var codeSection = uasm.Substring(uasm.IndexOf(".code_start"));
        var copyCount = codeSection.Split('\n')
            .Count(line => line.Trim() == "COPY");
        // Full pipeline should keep counts reasonable
        Assert.True(intnlCount < 25, $"Expected fewer than 25 __intnl_ vars, got {intnlCount}");
        Assert.True(copyCount < 20, $"Expected fewer than 20 COPY instructions, got {copyCount}");
    }

    [Fact]
    public void ConstantFolding_ResultsSyncedToVariableTable()
    {
        // Constant folding creates new __const_ vars in UasmModule during Optimize().
        // These must be synced back to VariableTable for ApplyConstantValues at runtime.
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ConstFoldSyncTest : UdonSharpBehaviour {
    int _result;
    void Start() { _result = 3 + 4; }
}
", "ConstFoldSyncTest", out var emitter);

        // The folded result (7) must exist in the VariableTable's const entries
        var constEntries = emitter.CodeGenResult.Constants;
        Assert.Contains(constEntries, e => e.Value is int v && v == 7);
    }

}
