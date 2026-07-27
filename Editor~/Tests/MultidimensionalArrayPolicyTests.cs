using System;
using Xunit;

namespace USugar.Tests;

public class MultidimensionalArrayPolicyTests
{
    [Fact]
    public void MultidimensionalField_IsRejectedByTypePlanning()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
public class MultidimensionalField : UdonSharpBehaviour
{
    public int[,] values;
}", "MultidimensionalField"));

        Assert.Contains("Multidimensional array 'int[*,*]' is not supported", ex.Message);
        Assert.Contains("Use a one-dimensional or jagged array", ex.Message);
    }

    [Fact]
    public void MultidimensionalLocal_IsRejectedBeforeArrayLowering()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
public class MultidimensionalLocal : UdonSharpBehaviour
{
    void Start()
    {
        var values = new int[2, 3];
        values[1, 2] = 4;
    }
}", "MultidimensionalLocal"));

        Assert.Contains("Multidimensional array", ex.Message);
    }

    [Fact]
    public void JaggedArray_RemainsSupported()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
public class JaggedArray : UdonSharpBehaviour
{
    public int result;
    void Start()
    {
        int[][] values = new int[2][];
        values[0] = new int[3];
        values[0][2] = 5;
        result = values[0][2];
    }
}", "JaggedArray");
    }
}
