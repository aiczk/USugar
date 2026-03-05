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
    public void Reserve_AdvancesCounterPastUsedValue()
    {
        var alloc = new NameAllocator();
        alloc.Reserve("foo", 3);
        Assert.Equal(4, alloc.Allocate("foo"));
    }

    [Fact]
    public void Reserve_DoesNotGoBackward()
    {
        var alloc = new NameAllocator();
        alloc.Allocate("foo"); // 0 → counter becomes 1
        alloc.Allocate("foo"); // 1 → counter becomes 2
        alloc.Reserve("foo", 0); // should NOT reset
        Assert.Equal(2, alloc.Allocate("foo"));
    }

    [Fact]
    public void Reserve_MultipleReservations()
    {
        var alloc = new NameAllocator();
        alloc.Reserve("foo", 2);
        alloc.Reserve("foo", 5);
        alloc.Reserve("foo", 3); // should not go backward
        Assert.Equal(6, alloc.Allocate("foo"));
    }

    [Fact]
    public void FormatId_ProducesUdonSharpFormat()
    {
        Assert.Equal("__0_SendAction", NameAllocator.FormatId("SendAction", 0));
        Assert.Equal("__2_urlStr__param", NameAllocator.FormatId("urlStr__param", 2));
    }

    [Fact]
    public void ParseId_RoundTrips()
    {
        var parsed = NameAllocator.ParseId("__3_myKey");
        Assert.NotNull(parsed);
        Assert.Equal(3, parsed.Value.counter);
        Assert.Equal("myKey", parsed.Value.key);
    }

    [Fact]
    public void ParseId_ReturnsNull_ForNonMatchingFormats()
    {
        Assert.Null(NameAllocator.ParseId("myVar"));
        Assert.Null(NameAllocator.ParseId("_start"));
        Assert.Null(NameAllocator.ParseId(null));
    }
}
