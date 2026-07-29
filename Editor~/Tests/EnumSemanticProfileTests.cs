using System;
using Xunit;

namespace USugar.Tests;

public class EnumSemanticProfileTests
{
    [Theory]
    [InlineData("object")]
    [InlineData("System.ValueType")]
    [InlineData("System.Enum")]
    [InlineData("System.IComparable")]
    [InlineData("System.IConvertible")]
    [InlineData("System.IFormattable")]
    public void FoldedEnum_RuntimeIdentityErasure_Rejects(
        string destination)
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm($@"
using System;
using UdonSharp;
public enum EspErase {{ A, B }}
public class EspEraseHost : UdonSharpBehaviour {{
    public int seed;
    void Start() {{
        EspErase value = (EspErase)seed;
        {destination} erased = value;
    }}
}}", "EspEraseHost"));

        Assert.Contains("folded enum", error.Message);
        Assert.Contains("runtime type identity", error.Message);
    }

    [Fact]
    public void FoldedEnum_ObjectUnbox_Rejects()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspUnbox { A, B }
public class EspUnboxHost : UdonSharpBehaviour {
    public object erased;
    public EspUnbox Read() => (EspUnbox)erased;
}", "EspUnboxHost"));

        Assert.Contains("unboxing", error.Message);
        Assert.Contains("folded enum", error.Message);
    }

    [Fact]
    public void FoldedEnum_ExactImmediateBoxRoundtrip_RemainsSupported()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspRoundtrip { A, B }
public class EspRoundtripHost : UdonSharpBehaviour {
    public int seed;
    public EspRoundtrip Read() {
        EspRoundtrip value = (EspRoundtrip)seed;
        return (EspRoundtrip)(object)value;
    }
}", "EspRoundtripHost");

    [Fact]
    public void FoldedEnum_ObjectArrayCarrierRoundtrip_RemainsSupported()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspObjectCell { A, B }
public class EspObjectCellHost : UdonSharpBehaviour {
    public int seed;
    public EspObjectCell Read() {
        EspObjectCell value = (EspObjectCell)seed;
        object[] carrier = new object[] { value };
        carrier[0] = EspObjectCell.B;
        return (EspObjectCell)carrier[0];
    }
}", "EspObjectCellHost");

    [Fact]
    public void FoldedEnum_ExplicitUnderlyingArrayCarrierCast_RemainsSupported()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspArrayCarrier { A, B }
public class EspArrayCarrierHost : UdonSharpBehaviour {
    public int seed;
    public EspArrayCarrier[] Read() {
        int[] carrier = new int[] { seed, 1 };
        return (EspArrayCarrier[])(object)carrier;
    }
}", "EspArrayCarrierHost");

    [Fact]
    public void FoldedEnum_MismatchedArrayCarrierCast_Rejects()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspArrayCarrierMismatch { A, B }
public class EspArrayCarrierMismatchHost : UdonSharpBehaviour {
    public short[] carrier;
    public EspArrayCarrierMismatch[] Read() =>
        (EspArrayCarrierMismatch[])(object)carrier;
}", "EspArrayCarrierMismatchHost"));

        Assert.Contains("folded enum array", error.Message);
        Assert.Contains("hard cast", error.Message);
    }

    [Fact]
    public void FoldedEnum_GetType_Rejects()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public enum EspType { A, B }
public class EspTypeHost : UdonSharpBehaviour {
    public EspType value;
    public Type Read() => value.GetType();
}", "EspTypeHost"));

        Assert.Contains("GetType", error.Message);
        Assert.Contains("folded enum", error.Message);
    }

    [Fact]
    public void FoldedEnum_Equals_SameStaticEnum_RemainsSupported()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspEqual { A, B }
public class EspEqualHost : UdonSharpBehaviour {
    public int seed;
    public bool Read() {
        EspEqual left = (EspEqual)seed;
        EspEqual right = EspEqual.B;
        return left.Equals(right)
            && left.Equals((EspEqual)(seed + 1))
            && object.Equals(left, right)
            && object.Equals(left, (EspEqual)(seed + 1));
    }
}", "EspEqualHost");

        Assert.Contains(
            "SystemObject.__Equals__SystemObject_SystemObject__SystemBoolean",
            uasm);
    }

    [Fact]
    public void FoldedEnum_Equals_StaticallyDifferentValue_IsKnownFalse()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspDifferent { A, B }
public enum EspOther { A, B }
public class EspDifferentHost : UdonSharpBehaviour {
    public int seed;
    public bool Read() {
        EspDifferent value = (EspDifferent)seed;
        return value.Equals(seed)
            || value.Equals(EspOther.A)
            || object.Equals(value, seed)
            || object.Equals(value, EspOther.A);
    }
}", "EspDifferentHost");

    [Fact]
    public void FoldedEnum_Equals_ErasedArgument_Rejects()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspAmbiguous { A, B }
public class EspAmbiguousHost : UdonSharpBehaviour {
    public EspAmbiguous value;
    public object other;
    public bool Read() => value.Equals(other);
}", "EspAmbiguousHost"));

        Assert.Contains("Equals(object)", error.Message);
        Assert.Contains("runtime type identity", error.Message);
    }

    [Fact]
    public void FoldedEnum_StaticObjectEquals_RightOperandDispatch_Rejects()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspRightOperand { A, B }
public class EspAlwaysEqual {
    public override bool Equals(object other) => true;
    public override int GetHashCode() => 0;
}
public class EspRightOperandHost : UdonSharpBehaviour {
    public bool Read() =>
        object.Equals(new EspAlwaysEqual(), EspRightOperand.A);
}", "EspRightOperandHost"));

        Assert.Contains(
            "object.Equals(non-enum, folded enum)",
            error.Message);
        Assert.Contains("runtime type identity", error.Message);
    }

    [Fact]
    public void FoldedEnum_ReferenceEquals_RemainsSupported()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspReference { A, B }
public class EspReferenceHost : UdonSharpBehaviour {
    public EspReference value;
    public EspReference? nullable;
    public bool Read() =>
        object.ReferenceEquals(value, value)
        || object.ReferenceEquals(nullable, null);
}", "EspReferenceHost");

        Assert.Contains(
            "SystemObject.__ReferenceEquals__SystemObject_SystemObject__SystemBoolean",
            uasm);
    }

    [Fact]
    public void FoldedEnum_CompareTo_SameEnumCompiles_DifferentTypeRejects()
    {
        TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspCompare { A, B }
public class EspCompareOk : UdonSharpBehaviour {
    public int seed;
    public EspCompare value;
    public int Read() => value.CompareTo(EspCompare.B)
        + value.CompareTo((EspCompare)seed);
}", "EspCompareOk");

        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspCompareBad { A, B }
public class EspCompareBadHost : UdonSharpBehaviour {
    public EspCompareBad value;
    public int Read() => value.CompareTo(1);
}", "EspCompareBadHost"));
        Assert.Contains("CompareTo", error.Message);
        Assert.Contains("folded enum", error.Message);
    }

    [Fact]
    public void FoldedEnum_StringSurfaces_RemainSupported()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspString { A, B }
public class EspStringHost : UdonSharpBehaviour {
    public EspString value;
    public string Read() =>
        value.ToString() + "":"" + value + $""/{value}"";
}", "EspStringHost");

    [Fact]
    public void FoldedEnum_FormattedToString_RejectsIdentityDependentOverload()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspFormat { A, B }
public class EspFormatHost : UdonSharpBehaviour {
    public EspFormat value;
    public string Read() => value.ToString(""G"");
}", "EspFormatHost"));

        Assert.Contains("ToString", error.Message);
        Assert.Contains("folded enum", error.Message);
    }

    [Fact]
    public void RegisteredEnum_RuntimeNumericProducer_Rejects()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class EspRegistered : UdonSharpBehaviour {
    public int seed;
    public DayOfWeek value;
    void Start() {
        value = (DayOfWeek)seed;
    }
}", "EspRegistered"));

        Assert.Contains("runtime conversion", error.Message);
        Assert.Contains("registered enum", error.Message);
        Assert.Contains("StrongBox", error.Message);
    }

    [Fact]
    public void RegisteredEnum_RuntimeTypeGuardedGenericCast_RemainsSupported()
        => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class EspRegisteredTypeGuard : UdonSharpBehaviour {
    public int seed;
    public DayOfWeek value;

    static DayOfWeek ConvertIfDayOfWeek<T>(T item) {
        var itemType = item.GetType();
        if (itemType == typeof(DayOfWeek))
            return (DayOfWeek)(object)item;
        return DayOfWeek.Sunday;
    }

    void Start() {
        value = ConvertIfDayOfWeek<int>(seed);
        value = ConvertIfDayOfWeek<DayOfWeek>(value);
    }
}", "EspRegisteredTypeGuard");

    [Fact]
    public void RegisteredEnum_UntiedRuntimeTypeGuard_StillRejects()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class EspRegisteredUntiedGuard : UdonSharpBehaviour {
    public int seed;

    static DayOfWeek Convert<T>(T item) {
        var unrelated = typeof(DayOfWeek);
        if (unrelated == typeof(DayOfWeek))
            return (DayOfWeek)(object)item;
        return DayOfWeek.Sunday;
    }

    void Start() {
        DayOfWeek value = Convert<int>(seed);
    }
}", "EspRegisteredUntiedGuard"));

        Assert.Contains("registered enum", error.Message);
        Assert.Contains("StrongBox", error.Message);
    }

    [Theory]
    [InlineData("value = value | DayOfWeek.Tuesday;")]
    [InlineData("value = value + 1;")]
    [InlineData("value = value - 1;")]
    [InlineData("value |= DayOfWeek.Monday;")]
    [InlineData("value++;")]
    [InlineData("value = ~value;")]
    public void RegisteredEnum_RuntimeOperatorProducer_Rejects(
        string statement)
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm($@"
using System;
using UdonSharp;
public class EspRegisteredOperator : UdonSharpBehaviour {{
    public DayOfWeek value;
    void Start() {{
        {statement}
    }}
}}", "EspRegisteredOperator"));

        Assert.Contains("registered enum", error.Message);
        Assert.Contains("StrongBox", error.Message);
    }

    [Fact]
    public void RegisteredEnum_ToNumericAndNumericResult_RemainSupported()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class EspRegisteredNumeric : UdonSharpBehaviour {
    public DayOfWeek value;
    public int Read() =>
        (int)value + (value - DayOfWeek.Sunday);
}", "EspRegisteredNumeric");

        Assert.Contains(
            "SystemConvert.__ToInt32__SystemObject__SystemInt32",
            uasm);
        Assert.Contains(
            "SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32",
            uasm);
    }

    [Fact]
    public void RegisteredEnum_CompileTimeConstantProducer_RemainsSupported()
        => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class EspRegisteredConstant : UdonSharpBehaviour {
    public DayOfWeek value;
    void Start() {
        value = DayOfWeek.Tuesday;
        value = (DayOfWeek)3;
        value = DayOfWeek.Monday | DayOfWeek.Tuesday;
    }
}", "EspRegisteredConstant");

    [Fact]
    public void RegisteredNullableEnum_RuntimeProducer_Rejects()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class EspRegisteredNullableProducer : UdonSharpBehaviour {
    public int seed;
    public DayOfWeek? value;
    void Start() {
        value = (DayOfWeek?)seed;
    }
}", "EspRegisteredNullableProducer"));

        Assert.Contains("registered enum", error.Message);
        Assert.Contains("StrongBox", error.Message);
    }

    [Fact]
    public void RegisteredNullableEnum_ToNullableNumeric_RemainsSupported()
    {
        var uasm = TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class EspRegisteredNullableNumeric : UdonSharpBehaviour {
    public DayOfWeek? value;
    public int? number;
    void Start() {
        number = (int?)value;
    }
}", "EspRegisteredNullableNumeric");

        Assert.Contains(
            "SystemConvert.__ToInt64__SystemObject__SystemInt64",
            uasm);
    }

    [Fact]
    public void RegisteredEnumArray_WithoutSdkArrayType_RejectsClearly()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public class EspRegisteredArray : UdonSharpBehaviour {
    public DayOfWeek[] values;
}", "EspRegisteredArray"));

        Assert.Contains("registered enum-array type", error.Message);
    }

    [Theory]
    [InlineData("values.GetType()")]
    [InlineData("values.ToString()")]
    [InlineData(@"""x="" + values")]
    [InlineData(@"$""{values}""")]
    public void FoldedEnumArray_TypeNameObservation_Rejects(
        string expression)
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm($@"
using UdonSharp;
public enum EspArrayValue {{ A, B }}
public class EspArrayTypeHost : UdonSharpBehaviour {{
    public EspArrayValue[] values;
    void Start() {{ object observed = {expression}; }}
}}", "EspArrayTypeHost"));

        Assert.Contains("folded enum", error.Message);
        Assert.Contains("array", error.Message);
    }

    [Fact]
    public void FoldedEnumArray_ObjectErasure_Rejects()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspArrayErase { A, B }
public class EspArrayEraseHost : UdonSharpBehaviour {
    public EspArrayErase[] values;
    public object Read() => values;
}", "EspArrayEraseHost"));

        Assert.Contains("folded enum array", error.Message);
        Assert.Contains("GetType", error.Message);
    }

    [Fact]
    public void FoldedEnumArray_ObjectHardCast_Rejects()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspArrayCast { A, B }
public class EspArrayCastHost : UdonSharpBehaviour {
    public object erased;
    public EspArrayCast[] Read() => (EspArrayCast[])erased;
}", "EspArrayCastHost"));

        Assert.Contains("folded enum array", error.Message);
        Assert.Contains("hard cast", error.Message);
    }

    [Fact]
    public void FoldedEnumArray_ExactCopyClearAndClone_RemainSupported()
        => TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public enum EspArrayCopy { A, B }
public class EspArrayCopyHost : UdonSharpBehaviour {
    public EspArrayCopy[] source;
    public EspArrayCopy[] destination;
    void Start() {
        Array.Copy(source, destination, source.Length);
        source.CopyTo(destination, 0);
        Array.Clear(destination, 0, destination.Length);
        EspArrayCopy[] clone = (EspArrayCopy[])source.Clone();
        destination = clone;
    }
}", "EspArrayCopyHost");

    [Fact]
    public void FoldedEnumArray_ReferenceEquality_RemainsSupported()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspArrayEqual { A, B }
public class EspArrayEqualHost : UdonSharpBehaviour {
    public EspArrayEqual[] left;
    public EspArrayEqual[] right;
    public bool Read() =>
        left == right
        && left.Equals(right)
        && object.Equals(left, right)
        && object.ReferenceEquals(left, right);
}", "EspArrayEqualHost");

    [Fact]
    public void FoldedEnumArray_CopyToDifferentEnum_Rejects()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public enum EspArrayA { A, B }
public enum EspArrayB { A, B }
public class EspArrayMismatchHost : UdonSharpBehaviour {
    public EspArrayA[] source;
    public EspArrayB[] destination;
    void Start() {
        Array.Copy(source, destination, source.Length);
    }
}", "EspArrayMismatchHost"));

        Assert.Contains("exact same enum array type", error.Message);
        Assert.Contains("ArrayTypeMismatchException", error.Message);
    }

    [Fact]
    public void FoldedEnumArray_CopyFromUnderlying_Rejects()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using System;
using UdonSharp;
public enum EspArrayDestination { A, B }
public class EspArrayUnderlyingHost : UdonSharpBehaviour {
    public int[] source;
    public EspArrayDestination[] destination;
    void Start() {
        Array.Copy(source, destination, source.Length);
    }
}", "EspArrayUnderlyingHost"));

        Assert.Contains("exact same enum array type", error.Message);
        Assert.Contains("ArrayTypeMismatchException", error.Message);
    }

    [Fact]
    public void FoldedEnumArray_GetValue_Rejects()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            TestHelper.CompileToUasm(@"
using UdonSharp;
public enum EspArrayGet { A, B }
public class EspArrayGetHost : UdonSharpBehaviour {
    public EspArrayGet[] values;
    public object Read() => values.GetValue(0);
}", "EspArrayGetHost"));

        Assert.Contains("GetValue", error.Message);
        Assert.Contains("folded enum array", error.Message);
    }
}
