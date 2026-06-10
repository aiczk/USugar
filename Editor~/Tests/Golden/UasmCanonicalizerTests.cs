using Xunit;

namespace USugar.Tests;

public class UasmCanonicalizerTests
{
    [Fact]
    public void RenumberedIntnl_CanonicalizesEqual()
    {
        // Same first-appearance order, different absolute indices -> must canonicalize equal.
        var a = "PUSH, __intnl_SystemInt32_3\nPUSH, __intnl_SystemInt32_7\n";
        var b = "PUSH, __intnl_SystemInt32_0\nPUSH, __intnl_SystemInt32_1\n";
        Assert.Equal(UasmCanonicalizer.Canonicalize(a), UasmCanonicalizer.Canonicalize(b));
    }

    [Fact]
    public void ReorderedIntnl_DoesNotCanonicalizeEqual()
    {
        // Different first-appearance order is a real difference -> must stay different.
        var a = "PUSH, __intnl_SystemInt32_0\nPUSH, __intnl_SystemString_0\n";
        var b = "PUSH, __intnl_SystemString_0\nPUSH, __intnl_SystemInt32_0\n";
        Assert.NotEqual(UasmCanonicalizer.Canonicalize(a), UasmCanonicalizer.Canonicalize(b));
    }

    [Fact]
    public void ReturnJumpSentinel_IsNotRenumbered()
    {
        var s = "PUSH, __intnl_returnJump_SystemUInt32_0\n";
        Assert.Contains("__intnl_returnJump_SystemUInt32_0", UasmCanonicalizer.Canonicalize(s));
    }

    [Fact]
    public void DifferentExtern_StaysDifferent()
    {
        var a = "EXTERN \"SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32\"\n";
        var b = "EXTERN \"SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32\"\n";
        Assert.NotEqual(UasmCanonicalizer.Canonicalize(a), UasmCanonicalizer.Canonicalize(b));
    }

    [Fact]
    public void DlgptrIntnl_RenumberPreservesType()
    {
        // dlgptr scratch (CoreToUasm) is also benign-renumberable but must not collapse
        // into a different-typed intnl bucket.
        var a = "__intnl_dlgptr_SystemUInt32_2 __intnl_SystemInt32_5";
        var b = "__intnl_dlgptr_SystemUInt32_0 __intnl_SystemInt32_0";
        Assert.Equal(UasmCanonicalizer.Canonicalize(a), UasmCanonicalizer.Canonicalize(b));
    }
}
