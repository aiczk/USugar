using Xunit;

public class NameAllocatorTests
{
    [Fact]
    public void Allocate_FirstCall_ReturnsZero()
    {
        var alloc = new NameAllocator();
        Assert.Equal(0, alloc.Allocate("foo"));
    }

    [Fact]
    public void Allocate_SecondCall_ReturnsOne()
    {
        var alloc = new NameAllocator();
        alloc.Allocate("foo");
        Assert.Equal(1, alloc.Allocate("foo"));
    }

    [Fact]
    public void Allocate_DifferentKeys_IndependentCounters()
    {
        var alloc = new NameAllocator();
        Assert.Equal(0, alloc.Allocate("a"));
        Assert.Equal(0, alloc.Allocate("b"));
        Assert.Equal(1, alloc.Allocate("a"));
    }

    [Fact]
    public void FormatId_ProducesUdonSharpFormat()
    {
        Assert.Equal("__0_SendAction", NameAllocator.FormatId("SendAction", 0));
        Assert.Equal("__2_urlStr__param", NameAllocator.FormatId("urlStr__param", 2));
    }

    [Theory]
    [InlineData("_name")]
    [InlineData("名前2")]
    [InlineData("value<SystemInt32>[]")]
    public void UasmSymbolRules_AcceptsScannerIdentifierGrammar(
        string symbol)
    {
        Assert.True(UasmSymbolRules.IsIdentifier(symbol));
    }

    [Theory]
    [InlineData("2name")]
    [InlineData("name.dot")]
    [InlineData("PUSH")]
    [InlineData("null")]
    public void UasmSymbolRules_RejectsNonIdentifiersAndReservedTokens(
        string symbol)
    {
        Assert.False(UasmSymbolRules.IsIdentifier(symbol));
    }

    [Fact]
    public void GeneratedNameAllocator_PreservesPreferredThenAllocatesFresh()
    {
        var allocator = new GeneratedNameAllocator(
            new[] { "__generated", "__generated_1" });

        Assert.Equal(
            "__generated_2",
            allocator.Allocate("__generated"));
        Assert.Equal(
            "__other",
            allocator.Allocate("__other"));
    }

    [Fact]
    public void Sanitize_RepairsAnInvalidLeadingCharacter()
    {
        Assert.Equal("_2name", NameAllocator.Sanitize("2name"));
    }

}
