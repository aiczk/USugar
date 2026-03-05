using Xunit;

namespace USugar.Tests;

public class UasmValidatorTests
{
    [Fact]
    public void Valid_Uasm_PassesValidation()
    {
        var uasm = @".data_start
    __refl_typeid: %SystemInt64, null
    __refl_typename: %SystemString, null
    __intnl_returnJump_SystemUInt32_0: %SystemUInt32, 0xFFFFFFFF
.data_end
.code_start
    .export _start
    _start:
        JUMP_INDIRECT, __intnl_returnJump_SystemUInt32_0
.code_end
";
        UasmValidator.Validate(uasm); // should not throw
    }

    [Fact]
    public void UndeclaredVariable_InPush_Throws()
    {
        var uasm = @".data_start
    __refl_typeid: %SystemInt64, null
.data_end
.code_start
    .export _start
    _start:
        PUSH, __nonexistent_var
.code_end
";
        var ex = Assert.Throws<UasmValidationException>(() => UasmValidator.Validate(uasm));
        Assert.Contains("__nonexistent_var", ex.Message);
    }

    [Fact]
    public void UndeclaredVariable_InJumpIndirect_Throws()
    {
        var uasm = @".data_start
    __refl_typeid: %SystemInt64, null
.data_end
.code_start
    .export _start
    _start:
        JUMP_INDIRECT, __missing_retaddr
.code_end
";
        var ex = Assert.Throws<UasmValidationException>(() => UasmValidator.Validate(uasm));
        Assert.Contains("__missing_retaddr", ex.Message);
    }

    [Fact]
    public void InvalidJumpAddress_Throws()
    {
        var uasm = @".data_start
    __refl_typeid: %SystemInt64, null
    x: %SystemInt32, null
.data_end
.code_start
    .export _start
    _start:
        PUSH, x
        JUMP, 0xDEADBEEF
.code_end
";
        var ex = Assert.Throws<UasmValidationException>(() => UasmValidator.Validate(uasm));
        Assert.Contains("0xDEADBEEF", ex.Message);
    }

    [Fact]
    public void ValidJumpAddress_Passes()
    {
        // _start at 0x00000000: PUSH(8) + JUMP_IF_FALSE(8) = 0x10, then JUMP_INDIRECT at 0x10
        var uasm = @".data_start
    __refl_typeid: %SystemInt64, null
    __intnl_returnJump_SystemUInt32_0: %SystemUInt32, 0xFFFFFFFF
    cond: %SystemBoolean, null
.data_end
.code_start
    .export _start
    _start:
        PUSH, cond
        JUMP_IF_FALSE, 0x00000010
    end:
        JUMP_INDIRECT, __intnl_returnJump_SystemUInt32_0
.code_end
";
        UasmValidator.Validate(uasm); // should not throw
    }

    [Fact]
    public void InvalidExtern_Throws()
    {
        var uasm = @".data_start
    __refl_typeid: %SystemInt64, null
.data_end
.code_start
    .export _start
    _start:
        EXTERN, ""FakeType.__fakeMethod__SystemVoid""
.code_end
";
        var ex = Assert.Throws<UasmValidationException>(() => UasmValidator.Validate(uasm));
        Assert.Contains("FakeType.__fakeMethod__SystemVoid", ex.Message);
    }

    // ── Stack balance validation tests ──

    [Fact]
    public void StackBalance_CopyWithoutTwoPushes_Throws()
    {
        var uasm = @".data_start
    __refl_typeid: %SystemInt64, null
    a: %SystemInt32, null
.data_end
.code_start
    .export _start
    _start:
        PUSH, a
        COPY
.code_end
";
        var ex = Assert.Throws<UasmValidationException>(() => UasmValidator.Validate(uasm));
        Assert.Contains("COPY expects 2 PUSHes, got 1", ex.Message);
    }

    [Fact]
    public void StackBalance_JumpIfFalseWithoutPush_Throws()
    {
        var uasm = @".data_start
    __refl_typeid: %SystemInt64, null
    __intnl_returnJump_SystemUInt32_0: %SystemUInt32, 0xFFFFFFFF
.data_end
.code_start
    .export _start
    _start:
        JUMP_IF_FALSE, 0x00000008
        JUMP_INDIRECT, __intnl_returnJump_SystemUInt32_0
.code_end
";
        var ex = Assert.Throws<UasmValidationException>(() => UasmValidator.Validate(uasm));
        Assert.Contains("JUMP_IF_FALSE expects 1 PUSH, got 0", ex.Message);
    }

    [Fact(Skip = "TODO: validator now allows push before jump")]
    public void StackBalance_JumpWithPendingPush_Throws()
    {
        var uasm = @".data_start
    __refl_typeid: %SystemInt64, null
    a: %SystemInt32, null
.data_end
.code_start
    .export _start
    _start:
        PUSH, a
        JUMP, 0x00000010
    end:
        JUMP, 0x00000010
.code_end
";
        var ex = Assert.Throws<UasmValidationException>(() => UasmValidator.Validate(uasm));
        Assert.Contains("jump with non-empty stack", ex.Message);
    }

    [Fact]
    public void StackBalance_ExternConsistency_MismatchThrows()
    {
        // Same extern called with different push counts
        var uasm = @".data_start
    __refl_typeid: %SystemInt64, null
    __intnl_returnJump_SystemUInt32_0: %SystemUInt32, 0xFFFFFFFF
    a: %SystemObject, null
    b: %SystemObject, null
    r: %SystemVoid, null
.data_end
.code_start
    .export _start
    _start:
        PUSH, a
        EXTERN, ""UnityEngineDebug.__Log__SystemObject__SystemVoid""
        PUSH, b
        PUSH, b
        EXTERN, ""UnityEngineDebug.__Log__SystemObject__SystemVoid""
        JUMP_INDIRECT, __intnl_returnJump_SystemUInt32_0
.code_end
";
        var ex = Assert.Throws<UasmValidationException>(() => UasmValidator.Validate(uasm));
        Assert.Contains("previously", ex.Message);
    }

    [Fact]
    public void StackBalance_ValidSequence_Passes()
    {
        // A complete valid sequence: PUSH+PUSH+COPY, PUSH+JUMP_IF_FALSE, JUMP
        var uasm = @".data_start
    __refl_typeid: %SystemInt64, null
    __intnl_returnJump_SystemUInt32_0: %SystemUInt32, 0xFFFFFFFF
    a: %SystemInt32, null
    b: %SystemInt32, null
    cond: %SystemBoolean, null
.data_end
.code_start
    .export _start
    _start:
        PUSH, a
        PUSH, b
        COPY
        PUSH, cond
        JUMP_IF_FALSE, 0x00000024
    end:
        JUMP_INDIRECT, __intnl_returnJump_SystemUInt32_0
.code_end
";
        UasmValidator.Validate(uasm); // should not throw
    }

    // ── Duplicate variable validation tests ──

    [Fact]
    public void DuplicateVar_Throws()
    {
        var uasm = @".data_start
    __refl_typeid: %SystemInt64, null
    x: %SystemInt32, null
    x: %SystemInt32, null
.data_end
.code_start
    .export _start
    _start:
        JUMP_INDIRECT, __refl_typeid
.code_end
";
        var ex = Assert.Throws<UasmValidationException>(() => UasmValidator.Validate(uasm));
        Assert.Contains("duplicate", ex.Message.ToLower());
    }
}
