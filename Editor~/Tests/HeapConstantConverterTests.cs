using Xunit;

namespace USugar.Tests;

public class HeapConstantConverterTests
{
    readonly struct LayerMaskLike
    {
        public readonly int Value;
        LayerMaskLike(int value) => Value = value;
        public static implicit operator LayerMaskLike(int value) => new(value);
    }

    [Fact]
    public void UserDefinedConstantConversionCreatesDestinationValueType()
    {
        var converted = Assert.IsType<LayerMaskLike>(
            HeapConstantConverter.ConvertTo(-1, typeof(LayerMaskLike)));

        Assert.Equal(-1, converted.Value);
    }

    [Fact]
    public void PrimitiveAndEnumConversionsRemainSupported()
    {
        Assert.Equal((byte)7, HeapConstantConverter.ConvertTo(7, typeof(byte)));
        Assert.Equal(System.DayOfWeek.Tuesday,
            HeapConstantConverter.ConvertTo(2, typeof(System.DayOfWeek)));
    }

    [Fact]
    public void UnsupportedConversionFailsLoudly()
    {
        Assert.ThrowsAny<System.Exception>(() =>
            HeapConstantConverter.ConvertTo(new object(), typeof(System.DateTime)));
    }
}
