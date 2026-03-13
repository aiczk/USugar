using System;
using Xunit;

namespace USugar.Tests;

public class VariableTableTests
{
    [Fact]
    public void DeclareField_UsesRawName()
    {
        var vt = new VariableTable();
        var id = vt.DeclareField("seatIndex", "SystemInt32");
        Assert.Equal("seatIndex", id);
    }

    [Fact]
    public void DeclareLocal_UsesLclPrefix()
    {
        var vt = new VariableTable();
        var id = vt.DeclareLocal("x", "SystemInt32");
        Assert.Equal("__lcl_x_SystemInt32_0", id);
    }

    [Fact]
    public void DeclareLocal_DuplicateName_Increments()
    {
        var vt = new VariableTable();
        var id1 = vt.DeclareLocal("x", "SystemInt32");
        var id2 = vt.DeclareLocal("x", "SystemInt32");
        Assert.Equal("__lcl_x_SystemInt32_0", id1);
        Assert.Equal("__lcl_x_SystemInt32_1", id2);
    }

    [Fact]
    public void DeclareTemp_UsesIntnlPrefix()
    {
        var vt = new VariableTable();
        var id = vt.DeclareTemp("SystemBoolean");
        Assert.Equal("__intnl_SystemBoolean_0", id);
    }

    [Fact]
    public void DeclareConst_UsesConstPrefix()
    {
        var vt = new VariableTable();
        var id = vt.DeclareConst("SystemInt32", "42");
        Assert.Equal("__const_SystemInt32_0", id);
    }

    [Fact]
    public void DeclareConst_SameValue_Reuses()
    {
        var vt = new VariableTable();
        var id1 = vt.DeclareConst("SystemInt32", "42");
        var id2 = vt.DeclareConst("SystemInt32", "42");
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ReflectionVars_AlwaysPresent()
    {
        var vt = new VariableTable();
        var entries = vt.GetAllEntries();
        Assert.Contains(entries, e => e.Id == "__refl_typeid");
        Assert.Contains(entries, e => e.Id == "__refl_typename");
        Assert.Contains(entries, e => e.Id == "__intnl_returnJump_SystemUInt32_0");
    }

    [Fact]
    public void LookupLocal_ReturnsId()
    {
        var vt = new VariableTable();
        vt.DeclareLocal("x", "SystemInt32");
        Assert.Equal("__lcl_x_SystemInt32_0", vt.Lookup("x"));
    }

    [Fact]
    public void Scope_PushPop_HidesLocal()
    {
        var vt = new VariableTable();
        vt.DeclareLocal("x", "SystemInt32");
        vt.PushScope();
        vt.DeclareLocal("x", "SystemInt32");
        Assert.Equal("__lcl_x_SystemInt32_1", vt.Lookup("x"));
        vt.PopScope();
        Assert.Equal("__lcl_x_SystemInt32_0", vt.Lookup("x"));
    }

    [Fact]
    public void DeclareConst_StoresTypedConstValue_Int()
    {
        var vt = new VariableTable();
        vt.DeclareConst("SystemInt32", "42");
        var consts = vt.GetConstEntries();
        Assert.Single(consts);
        Assert.Equal(42, consts[0].ConstValue);
    }

    [Fact]
    public void DeclareConst_StoresTypedConstValue_Bool()
    {
        var vt = new VariableTable();
        vt.DeclareConst("SystemBoolean", "True");
        var consts = vt.GetConstEntries();
        Assert.Single(consts);
        Assert.Equal(true, consts[0].ConstValue);
    }

    [Fact]
    public void DeclareConst_StoresTypedConstValue_Float()
    {
        var vt = new VariableTable();
        vt.DeclareConst("SystemSingle", "3.14");
        var consts = vt.GetConstEntries();
        Assert.Single(consts);
        Assert.IsType<float>(consts[0].ConstValue);
    }

    [Fact]
    public void DeclareConst_StoresTypedConstValue_String()
    {
        var vt = new VariableTable();
        vt.DeclareConst("SystemString", "hello");
        var consts = vt.GetConstEntries();
        Assert.Single(consts);
        Assert.Equal("hello", consts[0].ConstValue);
    }

    [Fact]
    public void DeclareConst_NullValue_NoConstEntry()
    {
        var vt = new VariableTable();
        vt.DeclareConst("SystemInt32", "null");
        var consts = vt.GetConstEntries();
        Assert.Empty(consts);
    }

    [Fact]
    public void DeclareConst_Dedup_ReturnsSameId()
    {
        var vt = new VariableTable();
        var id1 = vt.DeclareConst("SystemInt32", "42");
        var id2 = vt.DeclareConst("SystemInt32", "42");
        Assert.Equal(id1, id2);
        Assert.Single(vt.GetConstEntries());
    }

    [Fact]
    public void DeclareConst_UnknownEnumType_FallsBackToInt()
    {
        var vt = new VariableTable();
        vt.DeclareConst("VRCSDK3DataTokenType", "8");
        var consts = vt.GetConstEntries();
        Assert.Single(consts);
        Assert.Equal(8, consts[0].ConstValue);
    }

    [Fact]
    public void DeclareConst_UnknownEnumType_NonNumeric_ReturnsNull()
    {
        var vt = new VariableTable();
        vt.DeclareConst("SomeCustomType", "notANumber");
        var consts = vt.GetConstEntries();
        Assert.Empty(consts);
    }

    [Fact]
    public void ReturnJump_HasDefaultValue_0xFFFFFFFF()
    {
        var vt = new VariableTable();
        var entries = vt.GetAllEntries();
        var retJump = entries.Find(e => e.Id == "__intnl_returnJump_SystemUInt32_0");
        Assert.Equal("0xFFFFFFFF", retJump.DefaultValue);
    }

    [Fact]
    public void DeclareVar_Duplicate_SameType_IsIdempotent()
    {
        var vt = new VariableTable();
        var id1 = vt.DeclareVar("myVar", "SystemInt32");
        var id2 = vt.DeclareVar("myVar", "SystemInt32");
        Assert.Equal(id1, id2);
        Assert.Equal(4, vt.GetAllEntries().Count);
    }

    [Fact]
    public void DeclareVar_Duplicate_DifferentType_Throws()
    {
        var vt = new VariableTable();
        vt.DeclareVar("myVar", "SystemInt32");
        Assert.Throws<InvalidOperationException>(
            () => vt.DeclareVar("myVar", "SystemString"));
    }

    [Fact]
    public void DeclareField_Duplicate_SameType_IsIdempotent()
    {
        var vt = new VariableTable();
        vt.DeclareField("field", "SystemInt32");
        vt.DeclareField("field", "SystemInt32");
        Assert.Equal(4, vt.GetAllEntries().Count);
    }

    [Fact]
    public void DeclareField_Duplicate_DifferentType_Throws()
    {
        var vt = new VariableTable();
        vt.DeclareField("field", "SystemInt32");
        Assert.Throws<InvalidOperationException>(
            () => vt.DeclareField("field", "SystemString"));
    }

    [Fact]
    public void TryDeclareVar_Duplicate_SameType_ReturnsFalse()
    {
        var vt = new VariableTable();
        Assert.True(vt.TryDeclareVar("v", "SystemInt32"));
        Assert.False(vt.TryDeclareVar("v", "SystemInt32"));
        Assert.Equal(4, vt.GetAllEntries().Count);
    }

    [Fact]
    public void TryDeclareVar_Duplicate_DifferentType_Throws()
    {
        var vt = new VariableTable();
        vt.TryDeclareVar("v", "SystemInt32");
        Assert.Throws<InvalidOperationException>(
            () => vt.TryDeclareVar("v", "SystemString"));
    }

    [Fact]
    public void DeclareStructConst_SameValue_Reuses()
    {
        var vt = new VariableTable();
        // Use a simple struct-like value that implements Equals correctly
        var val = (1.0f, 2.0f, 3.0f);
        var id1 = vt.DeclareStructConst("UnityEngineVector3", val);
        var id2 = vt.DeclareStructConst("UnityEngineVector3", val);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void DeclareStructConst_DifferentValue_ReturnsDifferentId()
    {
        var vt = new VariableTable();
        var id1 = vt.DeclareStructConst("UnityEngineVector3", (1.0f, 2.0f, 3.0f));
        var id2 = vt.DeclareStructConst("UnityEngineVector3", (4.0f, 5.0f, 6.0f));
        Assert.NotEqual(id1, id2);
    }

    // ── ParseConstValue coverage (via DeclareConst) ──

    [Theory]
    [InlineData("SystemUInt32", "0xFF", (uint)0xFF)]
    [InlineData("SystemUInt32", "42", (uint)42)]
    [InlineData("SystemInt64", "9999999999", 9999999999L)]
    [InlineData("SystemDouble", "2.718", 2.718)]
    [InlineData("SystemByte", "255", (byte)255)]
    [InlineData("SystemChar", "A", 'A')]
    [InlineData("SystemSByte", "-1", (sbyte)-1)]
    [InlineData("SystemInt16", "-32000", (short)-32000)]
    [InlineData("SystemUInt16", "65000", (ushort)65000)]
    [InlineData("SystemUInt64", "18446744073709551615", ulong.MaxValue)]
    public void DeclareConst_ParsesVariousTypes(string udonType, string value, object expected)
    {
        var vt = new VariableTable();
        vt.DeclareConst(udonType, value);
        var consts = vt.GetConstEntries();
        Assert.Single(consts);
        Assert.Equal(expected, consts[0].ConstValue);
    }

    [Fact]
    public void DeclareConst_SystemType_StoresStringValue()
    {
        var vt = new VariableTable();
        vt.DeclareConst("SystemType", "UnityEngineVector3");
        var consts = vt.GetConstEntries();
        Assert.Single(consts);
        Assert.Equal("UnityEngineVector3", consts[0].ConstValue);
    }

    [Fact]
    public void DeclareConst_HexInt32()
    {
        var vt = new VariableTable();
        vt.DeclareConst("SystemInt32", "0xFFFFFFFF");
        var consts = vt.GetConstEntries();
        Assert.Single(consts);
        Assert.Equal(-1, consts[0].ConstValue);
    }

    [Fact]
    public void DeclareConst_SharedCounter_WithStructConst()
    {
        // DeclareConst and DeclareStructConst share the same counter namespace
        var vt = new VariableTable();
        var id1 = vt.DeclareConst("SystemInt32", "1");
        var id2 = vt.DeclareStructConst("SystemInt32", 999);
        Assert.Equal("__const_SystemInt32_0", id1);
        Assert.Equal("__const_SystemInt32_1", id2);
    }
}
