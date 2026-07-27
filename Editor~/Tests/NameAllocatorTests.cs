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

}
