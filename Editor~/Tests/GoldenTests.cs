using Xunit;

namespace USugar.Tests;

public class GoldenTests
{
    [Fact]
    public void StructuralComparer_IdenticalUasm_AllMatch()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class GoldenBasic : UdonSharpBehaviour {
    public int score;
    void Start() { score = 42; }
}", "GoldenBasic");

        var result = UasmStructuralComparer.Compare(uasm, uasm);
        Assert.True(result.ExternsMatch);
        Assert.True(result.ExportsMatch);
        Assert.True(result.DataTypesMatch);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void StructuralComparer_DifferentExterns_DetectsMismatch()
    {
        var uasmA = @"
.code_start
    PUSH, __0_x_SystemInt32_0
    PUSH, __0_y_SystemInt32_0
    PUSH, __0_z_SystemInt32_0
    EXTERN ""SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32""
.code_end
.data_start
    .export score
    score: %SystemInt32, null
.data_end";
        var uasmB = @"
.code_start
    PUSH, __0_x_SystemInt32_0
    PUSH, __0_y_SystemInt32_0
    PUSH, __0_z_SystemInt32_0
    EXTERN ""SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32""
.code_end
.data_start
    .export score
    score: %SystemInt32, null
.data_end";

        var result = UasmStructuralComparer.Compare(uasmA, uasmB);
        Assert.False(result.ExternsMatch);
        Assert.True(result.Differences.Count > 0);
    }

    [Fact]
    public void StructuralComparer_DifferentExports_DetectsMismatch()
    {
        var uasmA = @"
.data_start
    .export foo
    foo: %SystemInt32, null
    .export bar
    bar: %SystemString, null
.data_end";
        var uasmB = @"
.data_start
    .export foo
    foo: %SystemInt32, null
.data_end";

        var result = UasmStructuralComparer.Compare(uasmA, uasmB);
        Assert.False(result.ExportsMatch);
        Assert.Contains(result.Differences, d => d.Contains("Missing export: bar"));
    }

    [Fact]
    public void StructuralComparer_ExtractExternList_PreservesOrder()
    {
        var uasm = @"
EXTERN ""A.__foo__SystemVoid""
EXTERN ""B.__bar__SystemVoid""
EXTERN ""A.__foo__SystemVoid""
";
        var list = UasmStructuralComparer.ExtractExternList(uasm);
        Assert.Equal(3, list.Count);
        Assert.Equal("A.__foo__SystemVoid", list[0]);
        Assert.Equal("B.__bar__SystemVoid", list[1]);
        Assert.Equal("A.__foo__SystemVoid", list[2]);
    }
}
