using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace USugar.Tests;

public class ArraySemanticProfileTests
{
    [Fact]
    public void CollapsedArrayCovarianceRejects()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => TestHelper.CompileToUasm(@"
using UdonSharp;
public class ArrayCovarianceBase { }
public class ArrayCovarianceDerived : ArrayCovarianceBase { }
public class CollapsedArrayCovariance : UdonSharpBehaviour
{
    void Start()
    {
        ArrayCovarianceDerived[] derived =
            new ArrayCovarianceDerived[1];
        ArrayCovarianceBase[] values = derived;
    }
}", "CollapsedArrayCovariance"));

        Assert.Contains("Array conversion", error.Message);
        Assert.Contains("ArrayTypeMismatchException", error.Message);
    }

    [Fact]
    public void JaggedArrayCovarianceRejects()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => TestHelper.CompileToUasm(@"
using UdonSharp;
public class JaggedArrayCovariance : UdonSharpBehaviour
{
    void Start()
    {
        int[][] jagged = new int[1][];
        object[] values = jagged;
    }
}", "JaggedArrayCovariance"));

        Assert.Contains("Array conversion", error.Message);
        Assert.Contains("runtime element type", error.Message);
    }

    [Fact]
    public void ObjectToCollapsedArrayHardCastRejects()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => TestHelper.CompileToUasm(@"
using UdonSharp;
public class CollapsedArrayHardCast : UdonSharpBehaviour
{
    void Start()
    {
        object value = null;
        int[][] jagged = (int[][])value;
    }
}", "CollapsedArrayHardCast"));

        Assert.Contains("Array conversion", error.Message);
        Assert.Contains("InvalidCastException", error.Message);
    }

    [Fact]
    public void ExactCollapsedArrayAssignmentAndStaticNullCompile()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ExactCollapsedArrayAssignment : UdonSharpBehaviour
{
    public int result;
    void Start()
    {
        int[][] first = null;
        int[][] second = first;
        int[][] third = (int[][])(object)null;
        result = second == third ? 1 : 0;
    }
}", "ExactCollapsedArrayAssignment");

        Assert.Contains(
            "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            uasm);
    }

    [Fact]
    public void NullableScalarArrayUsesCollapsedCarrier()
    {
        var compilation = TestHelper.BuildCompilation(@"
using UdonSharp;
public class NullableScalarArrayCarrier : UdonSharpBehaviour
{
    public int?[] values;
}", "NullableScalarArrayCarrier", out var behaviour);
        var field = behaviour.GetMembers()
            .OfType<IFieldSymbol>()
            .Single(member => member.Name == "values");
        var session = new CompilationSession(
            compilation, TestHelper.RegistryFacts);

        Assert.Equal(
            UdonRepresentationKind.ObjectArrayBundle,
            session.Types.Describe(field.Type).Representation);
    }

    [Fact]
    public void NullableScalarArrayGetTypeRejects()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => TestHelper.CompileToUasm(@"
using UdonSharp;
public class NullableScalarArrayGetType : UdonSharpBehaviour
{
    public System.Type result;
    void Start()
    {
        int?[] values = new int?[1];
        result = values.GetType();
    }
}", "NullableScalarArrayGetType"));

        Assert.Contains("extern-call ABI", error.Message);
    }

    [Fact]
    public void NativeAndCollapsedArrayEqualityUseObjectReferenceIdentity()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class ArrayReferenceEquality : UdonSharpBehaviour
{
    public bool result;
    void Start()
    {
        int[] nativeA = new int[1];
        int[] nativeB = nativeA;
        int[][] collapsedA = new int[1][];
        int[][] collapsedB = collapsedA;
        result = nativeA == nativeB
            && nativeA != null
            && collapsedA == collapsedB
            && collapsedA != null;
    }
}", "ArrayReferenceEquality");

        Assert.Contains(
            "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            uasm);
        Assert.Contains(
            "SystemObject.__op_Inequality__SystemObject_SystemObject__SystemBoolean",
            uasm);
        Assert.DoesNotContain(
            "SystemInt32Array.__op_Equality",
            uasm);
        Assert.DoesNotContain(
            "SystemObjectArray.__op_Equality",
            uasm);
    }
}
