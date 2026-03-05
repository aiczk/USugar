using Xunit;

namespace USugar.Tests;

public class PlayerActionTests
{
    const string PlayerActionSource = @"
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class PlayerAction : UdonSharpBehaviour
{
    public int seatIndex;

    [UdonSynced] int _actionSeq;
    [UdonSynced] int _actionType;
    [UdonSynced] int _actionArg;

    int _localSeq;

    public void SendAction(int type, int arg)
    {
        if (!Networking.IsOwner(gameObject)) return;
        _actionType = type;
        _actionArg = arg;
        _actionSeq++;
        RequestSerialization();
    }

    public bool ConsumeAction()
    {
        if (_actionSeq == _localSeq) return false;
        if (_actionSeq - _localSeq > 1)
            Debug.LogWarning($""[PlayerAction] seat{seatIndex}: skipped {_actionSeq - _localSeq - 1} actions"");
        _localSeq = _actionSeq;
        return true;
    }

    public void ResetSeq()
    {
        _localSeq = _actionSeq;
    }

    public int GetActionType() { return _actionType; }
    public int GetActionArg() { return _actionArg; }
}
";

    [Fact]
    public void PlayerAction_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(PlayerActionSource);
        Assert.NotNull(uasm);
        Assert.Contains(".data_start", uasm);
        Assert.Contains(".code_start", uasm);
    }

    [Fact]
    public void PlayerAction_HasCorrectExports()
    {
        var uasm = TestHelper.CompileToUasm(PlayerActionSource);
        Assert.Contains(".export seatIndex", uasm);
        Assert.Contains(".sync _actionSeq, none", uasm);
        Assert.Contains(".sync _actionType, none", uasm);
        Assert.Contains(".sync _actionArg, none", uasm);
    }

    [Fact]
    public void PlayerAction_SendAction_ExportName()
    {
        var uasm = TestHelper.CompileToUasm(PlayerActionSource);
        // Public methods with params → counter-mangled (__N_Name)
        Assert.Contains(".export __0_SendAction", uasm);
    }

    [Fact]
    public void PlayerAction_ParameterlessPublic_ExportNames()
    {
        var uasm = TestHelper.CompileToUasm(PlayerActionSource);
        // Public no params → raw name
        Assert.Contains(".export ConsumeAction", uasm);
        Assert.Contains(".export ResetSeq", uasm);
        Assert.Contains(".export GetActionType", uasm);
        Assert.Contains(".export GetActionArg", uasm);
    }

    [Fact]
    public void PlayerAction_HasThisGameObject()
    {
        var uasm = TestHelper.CompileToUasm(PlayerActionSource);
        Assert.Contains("__this_UnityEngineGameObject_0: %UnityEngineGameObject, this", uasm);
    }

    [Fact]
    public void PlayerAction_HasNetworkingExtern()
    {
        var uasm = TestHelper.CompileToUasm(PlayerActionSource);
        Assert.Contains("EXTERN, \"VRCSDKBaseNetworking.__IsOwner__UnityEngineGameObject__SystemBoolean\"", uasm);
    }

    [Fact]
    public void PlayerAction_HasRequestSerialization()
    {
        var uasm = TestHelper.CompileToUasm(PlayerActionSource);
        Assert.Contains("__RequestSerialization__SystemVoid\"", uasm);
    }

    [Fact]
    public void PlayerAction_HasStringFormat()
    {
        var uasm = TestHelper.CompileToUasm(PlayerActionSource);
        Assert.Contains("__Format__SystemString_SystemObject_SystemObject__SystemString\"", uasm);
    }

    [Fact]
    public void PlayerAction_HasReturnBool()
    {
        var uasm = TestHelper.CompileToUasm(PlayerActionSource);
        // ConsumeAction returns bool — 0-param public method, counter-based var
        Assert.Contains("__0_ConsumeAction__ret: %SystemBoolean, null", uasm);
    }

    [Fact]
    public void PlayerAction_StructuralMatch()
    {
        var uasm = TestHelper.CompileToUasm(PlayerActionSource);

        // Data block: all fields declared
        Assert.Contains("seatIndex: %SystemInt32, null", uasm);
        Assert.Contains("_actionSeq: %SystemInt32, null", uasm);
        Assert.Contains("_actionType: %SystemInt32, null", uasm);
        Assert.Contains("_actionArg: %SystemInt32, null", uasm);
        Assert.Contains("_localSeq: %SystemInt32, null", uasm);

        // Params for SendAction (counter-based: __N_name__param)
        Assert.Contains("__0_type__param: %SystemInt32, null", uasm);
        Assert.Contains("__0_arg__param: %SystemInt32, null", uasm);

        // Return vars (counter-based: __N_exportName__ret)
        Assert.Contains("__0_ConsumeAction__ret: %SystemBoolean, null", uasm);
        Assert.Contains("__0_GetActionType__ret: %SystemInt32, null", uasm);
        Assert.Contains("__0_GetActionArg__ret: %SystemInt32, null", uasm);

        // Code block: methods present (1+ param methods are mangled)
        Assert.Contains("__0_SendAction:", uasm);
        Assert.Contains("ConsumeAction:", uasm);
        Assert.Contains("ResetSeq:", uasm);
        Assert.Contains("GetActionType:", uasm);
        Assert.Contains("GetActionArg:", uasm);

        // Key extern calls
        Assert.Contains("__IsOwner__", uasm);
        Assert.Contains("__RequestSerialization__", uasm);
        Assert.Contains("__Format__", uasm);
        Assert.Contains("__LogWarning__", uasm);
        Assert.Contains("__op_Addition__", uasm);
        Assert.Contains("__op_Equality__", uasm);
        Assert.Contains("__op_Subtraction__", uasm);
        Assert.Contains("__op_GreaterThan__", uasm);
    }
}
