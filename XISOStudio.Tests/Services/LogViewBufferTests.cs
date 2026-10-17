using XISOStudio.Services;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests the bounded, thread-safe buffer behind the on-screen log viewer.</summary>
public class LogViewBufferTests
{
    [Fact]
    public void DrainReturnsPendingLinesInOrder()
    {
        var buffer = new LogViewBuffer();
        buffer.Enqueue("first");
        buffer.Enqueue("second");

        Assert.Equal(["first", "second"], buffer.Drain(10));
        Assert.False(buffer.HasPending);
    }

    [Fact]
    public void DrainCapsBatchSizeAndLeavesTheRestPending()
    {
        var buffer = new LogViewBuffer();
        for (var i = 0; i < 10; i++) buffer.Enqueue($"line {i}");

        var drained = buffer.Drain(3);

        Assert.Equal(["line 0", "line 1", "line 2"], drained);
        Assert.True(buffer.HasPending);
    }

    [Fact]
    public void CommitKeepsOnlyNewestLinesWithinTheDisplayLimit()
    {
        var buffer = new LogViewBuffer(maxLines: 3);

        Assert.False(buffer.Commit(["a", "b"]));
        Assert.True(buffer.Commit(["c", "d"]));

        Assert.Equal(["b", "c", "d"], buffer.Snapshot());
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void CommitReturnsFalseWhenNothingWasDropped()
    {
        var buffer = new LogViewBuffer(maxLines: 3);

        Assert.False(buffer.Commit(["a", "b", "c"]));
        Assert.Equal(["a", "b", "c"], buffer.Snapshot());
    }

    [Fact]
    public void EnqueueEllipsizesLinesLongerThanTheLimit()
    {
        var buffer = new LogViewBuffer(maxLineLength: 10);
        buffer.Enqueue(new string('x', 100));

        var line = Assert.Single(buffer.Drain(1));
        Assert.Equal(10, line.Length);
        Assert.EndsWith("…", line, StringComparison.Ordinal);
    }

    [Fact]
    public void EnqueueDropsOldestLinesWhenTheBacklogLimitIsExceeded()
    {
        var buffer = new LogViewBuffer(maxPendingLines: 3);
        for (var i = 0; i < 10; i++) buffer.Enqueue($"line {i}");

        Assert.Equal(["line 7", "line 8", "line 9"], buffer.Drain(100));
    }

    [Fact]
    public void ClearRemovesDisplayAndPendingLines()
    {
        var buffer = new LogViewBuffer();
        buffer.Enqueue("pending");
        buffer.Commit(["shown"]);

        buffer.Clear();

        Assert.False(buffer.HasPending);
        Assert.Empty(buffer.Snapshot());
        Assert.Empty(buffer.Drain(10));
    }

    [Fact]
    public void ConcurrentEnqueueKeepsEveryLineWithinTheBacklogLimit()
    {
        var buffer = new LogViewBuffer(maxLines: 10_000, maxPendingLines: 10_000);
        Parallel.For(0, 1000, i => buffer.Enqueue($"line {i}"));

        Assert.Equal(1000, buffer.Drain(10_000).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConstructorRejectsNonPositiveLimits(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogViewBuffer(maxLines: limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogViewBuffer(maxLineLength: limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogViewBuffer(maxPendingLines: limit));
    }
}