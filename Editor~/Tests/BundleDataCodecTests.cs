using System;
using Xunit;

namespace USugar.Tests;

// The production source-domain linker is exercised by the Unity compilation because U# assembly
// discovery is an editor concern. These tests pin the data boundary used by Inspector serialization.
public class BundleDataCodecTests
{
    struct Point
    {
        public int X;
        public int Y;
    }

    sealed class Node
    {
        public int Value;
        public Node Next;
    }

    abstract class Animal
    {
        public string Name;
    }

    sealed class Cat : Animal
    {
        public int Lives;
    }

    static bool IsNativeLeaf(Type _) => false;

    [Fact]
    public void RoundTripPreservesCycles()
    {
        var source = new Node { Value = 7 };
        source.Next = source;

        Assert.True(BundleDataCodec.TryEncode(
            source, typeof(Node), IsNativeLeaf,
            out var encoded, out var encodeError), encodeError);
        Assert.True(BundleDataCodec.TryDecode(
            encoded, typeof(Node), IsNativeLeaf,
            out var decoded, out var decodeError), decodeError);

        var node = Assert.IsType<Node>(decoded);
        Assert.Equal(7, node.Value);
        Assert.Same(node, node.Next);
    }

    [Fact]
    public void RoundTripUsesRuntimeIdentityForPolymorphicFields()
    {
        Animal source = new Cat { Name = "Mochi", Lives = 9 };

        Assert.True(BundleDataCodec.TryEncode(
            source, typeof(Animal), IsNativeLeaf,
            out var encoded, out var encodeError), encodeError);
        Assert.True(BundleDataCodec.TryDecode(
            encoded, typeof(Animal), IsNativeLeaf,
            out var decoded, out var decodeError), decodeError);

        var cat = Assert.IsType<Cat>(decoded);
        Assert.Equal("Mochi", cat.Name);
        Assert.Equal(9, cat.Lives);
    }

    [Fact]
    public void RoundTripPreservesStructArrays()
    {
        var source = new[]
        {
            new Point { X = 1, Y = 2 },
            new Point { X = 3, Y = 4 },
        };

        Assert.True(BundleDataCodec.TryEncode(
            source, typeof(Point[]), IsNativeLeaf,
            out var encoded, out var encodeError), encodeError);
        Assert.True(BundleDataCodec.TryDecode(
            encoded, typeof(Point[]), IsNativeLeaf,
            out var decoded, out var decodeError), decodeError);

        var points = Assert.IsType<Point[]>(decoded);
        Assert.Equal(2, points.Length);
        Assert.Equal((1, 2), (points[0].X, points[0].Y));
        Assert.Equal((3, 4), (points[1].X, points[1].Y));
    }

    [Fact]
    public void RoundTripPreservesClassArrayIdentity()
    {
        var shared = new Node { Value = 7 };
        var source = new[] { shared, null, shared };

        Assert.True(BundleDataCodec.TryEncode(
            source, typeof(Node[]), IsNativeLeaf,
            out var encoded, out var encodeError), encodeError);
        Assert.True(BundleDataCodec.TryDecode(
            encoded, typeof(Node[]), IsNativeLeaf,
            out var decoded, out var decodeError), decodeError);

        var nodes = Assert.IsType<Node[]>(decoded);
        Assert.Equal(3, nodes.Length);
        Assert.Equal(7, nodes[0].Value);
        Assert.Null(nodes[1]);
        Assert.Same(nodes[0], nodes[2]);
    }

    [Fact]
    public void RoundTripPreservesEmptyNonNativeArrays()
    {
        var source = Array.Empty<Node>();

        Assert.True(BundleDataCodec.TryEncode(
            source, typeof(Node[]), IsNativeLeaf,
            out var encoded, out var encodeError), encodeError);
        Assert.True(BundleDataCodec.TryDecode(
            encoded, typeof(Node[]), IsNativeLeaf,
            out var decoded, out var decodeError), decodeError);

        Assert.Empty(Assert.IsType<Node[]>(decoded));
    }

    [Fact]
    public void MultiDimensionalArray_IsRejected()
    {
        var source = new[,] { { 1, 2, 3 }, { 4, 5, 6 } };

        Assert.False(BundleDataCodec.TryEncode(
            source, typeof(int[,]), IsNativeLeaf,
            out _, out var error));
        Assert.Contains(
            "multidimensional arrays have no Udon representation",
            error);
    }

    [Fact]
    public void ExecutableStateRejectsLoudly()
    {
        Action value = () => { };

        Assert.False(BundleDataCodec.TryEncode(
            value, typeof(Action), IsNativeLeaf,
            out _, out var error));
        Assert.Contains("executable state", error);
    }
}
