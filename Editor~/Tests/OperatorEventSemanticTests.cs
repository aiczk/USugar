using System;
using System.Linq;
using Xunit;

namespace USugar.Tests;

public class OperatorEventSemanticTests
{
    [Theory]
    [InlineData("value += other;")]
    [InlineData("value++;")]
    [InlineData("--value;")]
    public void CheckedCompoundAndIncrement_Reject(string statement)
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
public class CheckedMutationGuard : UdonSharpBehaviour {
    public int value, other;
    public void M() { checked { " + statement + @" } }
}", "CheckedMutationGuard"));

        Assert.Contains("'checked' context is not supported", ex.Message);
    }

    [Fact]
    public void UncheckedCompoundAndIncrement_StillCompile()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public class UncheckedMutationControl : UdonSharpBehaviour {
    public int value, other;
    public void M() { unchecked { value += other; value++; --value; } }
}", "UncheckedMutationControl");

    [Theory]
    [InlineData("&&", "&")]
    [InlineData("||", "|")]
    public void UserDefinedConditionalLogicalOperator_Rejects(
        string conditionalOperator, string bitwiseOperator)
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
public struct ConditionalValue {
    public int value;
    public static ConditionalValue operator "
                + bitwiseOperator + @"(
        ConditionalValue left, ConditionalValue right) => left;
    public static bool operator true(ConditionalValue value)
        => value.value != 0;
    public static bool operator false(ConditionalValue value)
        => value.value == 0;
}
public class ConditionalOperatorGuard : UdonSharpBehaviour {
    public void M() {
        ConditionalValue left = default, right = default;
        var result = left " + conditionalOperator + @" right;
    }
}", "ConditionalOperatorGuard"));

        Assert.Contains(
            "User-defined conditional logical operator", ex.Message);
    }

    [Fact]
    public void UserDefinedLogicalNot_InIf_StillCallsOperator()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public struct LogicalNotValue {
    public int value;
    public static bool operator !(LogicalNotValue value)
        => value.value == 0;
}
public class LogicalNotControl : UdonSharpBehaviour {
    public int result;
    public void M() {
        LogicalNotValue value = default;
        if (!value)
            result = 1;
    }
}", "LogicalNotControl");

        Assert.Contains("op_LogicalNot", uasm);
    }

    [Fact]
    public void BuiltInLogicalNot_InIf_StillInvertsBranches()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class LogicalNotBoolControl : UdonSharpBehaviour {
    public bool value;
    public int result;
    public void M() {
        if (!value)
            result = 1;
    }
}", "LogicalNotBoolControl");

        Assert.DoesNotContain(
            "SystemBoolean.__op_UnaryNegation__SystemBoolean__SystemBoolean",
            uasm);
    }

    [Theory]
    [InlineData(
        "LiftedValue? left = default, right = default; var result = left + right;",
        "binary operator")]
    [InlineData(
        "LiftedValue? value = default; var result = -value;",
        "unary operator")]
    [InlineData(
        "LiftedValue? left = default, right = default; left += right;",
        "compound assignment")]
    [InlineData(
        "LiftedValue? value = default; value++;",
        "increment/decrement")]
    [InlineData(
        "LiftedValue? value = default; int? result = value;",
        "conversion")]
    public void LiftedSourceOperator_Rejects(
        string statement, string surface)
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
public struct LiftedValue {
    public int value;
    public static LiftedValue operator +(
        LiftedValue left, LiftedValue right) => left;
    public static LiftedValue operator -(
        LiftedValue value) => value;
    public static LiftedValue operator ++(
        LiftedValue value) => value;
    public static implicit operator int(LiftedValue value)
        => value.value;
}
public class LiftedOperatorGuard : UdonSharpBehaviour {
    public void M() { " + statement + @" }
}", "LiftedOperatorGuard"));

        Assert.Contains($"Lifted source {surface}", ex.Message);
    }

    [Fact]
    public void FieldLikeEvent_StagesHandlerBeforeBackingRead()
    {
        TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class EventHandlerOrder : UdonSharpBehaviour {
    event Action Changed;
    void First() { }
    void Second() { }
    Action Prepare() {
        Changed += First;
        return Second;
    }
    void Start() { Changed += Prepare(); }
}", "EventHandlerOrder", out var emitter);

        var start = Assert.Single(
            emitter.FlatModule.Functions,
            function => function.ExportName == "_start");
        var instructions = start.Blocks
            .SelectMany(block => block.Instructions)
            .ToArray();
        var handlerCall = Array.FindIndex(
            instructions,
            instruction => instruction is CExprStmt
            {
                Expr: CInternalCall call
            } && call.FuncName.Contains(
                "Prepare", StringComparison.Ordinal));
        var backingRead = Array.FindIndex(
            instructions,
            instruction => instruction is CLoadField
            {
                FieldName: "Changed"
            });

        Assert.True(handlerCall >= 0, "Prepare call was not emitted.");
        Assert.True(
            backingRead > handlerCall,
            "Event backing value was read before the handler expression.");
    }
}
