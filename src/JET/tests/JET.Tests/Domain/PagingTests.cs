using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class PagingTests
{
    [Fact]
    public void Cursor_RoundTrips()
    {
        var encoded = PageCursor.Encode("1102A*");
        Assert.True(PageCursor.TryDecode(encoded, out var key));
        Assert.Equal("1102A*", key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Cursor_NullOrEmpty_IsFirstPage(string? cursor)
    {
        Assert.False(PageCursor.TryDecode(cursor, out _));
    }

    [Fact]
    public void Cursor_Malformed_ReturnsFalse()
    {
        Assert.False(PageCursor.TryDecode("!!not-base64!!", out _));
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(-5, 200)]
    [InlineData(50, 50)]
    [InlineData(999, 500)]
    public void PageSize_IsClamped(int requested, int expected)
    {
        Assert.Equal(expected, new PageRequest(null, requested).ClampedPageSize);
    }
}
