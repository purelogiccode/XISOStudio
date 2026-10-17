using XISOStudio.Services;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests reassembly and sanitizing of XISOSharp's chunked console output.</summary>
public class LibraryOutputLineBufferTests
{
    [Fact]
    public void AppendForwardsOnlyCompleteLines()
    {
        var logger = new TestLogger();
        var buffer = new LibraryOutputLineBuffer(logger.Logger, "  [xiso] ");

        buffer.Append("adding media/data.bin (4096 bytes) ");

        Assert.Empty(logger.Events);

        buffer.Append("[OK]\n");

        Assert.True(logger.HasMessage("  [xiso] adding media/data.bin (4096 bytes) [OK]"));
    }

    [Fact]
    public void AppendStripsBackspaceAnimationAndCollapsesSpaces()
    {
        var logger = new TestLogger();
        var buffer = new LibraryOutputLineBuffer(logger.Logger, "  [xiso] ");

        buffer.Append("generating avl tree from filesystem: ");
        buffer.Append(new string('\b', 40));
        buffer.Append(new string(' ', 40));
        buffer.Append(new string('\b', 40));
        buffer.Append("[OK]\n\n");

        var line = Assert.Single(logger.Events);
        Assert.Equal("  [xiso] generating avl tree from filesystem: [OK]", line.RenderMessage());
    }

    [Fact]
    public void AppendDropsControlOnlyAndEmptyLines()
    {
        var logger = new TestLogger();
        var buffer = new LibraryOutputLineBuffer(logger.Logger, "  [xiso] ");

        buffer.Append("\n");
        buffer.Append("\b\b\b\n");
        buffer.Append("   \n");

        Assert.Empty(logger.Events);
    }

    [Fact]
    public void AppendFlushesLongUnterminatedFragments()
    {
        var logger = new TestLogger();
        var buffer = new LibraryOutputLineBuffer(logger.Logger, "  [xiso] ");

        buffer.Append(new string('x', LibraryOutputLineBuffer.MaxBufferedLength + 100));

        var message = Assert.Single(logger.Events).RenderMessage();
        Assert.StartsWith("  [xiso] x", message, StringComparison.Ordinal);
        Assert.EndsWith("…", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendHandlesMultipleLinesInOneChunk()
    {
        var logger = new TestLogger();
        var buffer = new LibraryOutputLineBuffer(logger.Logger, "  [xiso] ");

        buffer.Append("first\nsecond\n");

        Assert.Equal(2, logger.Events.Count);
        Assert.True(logger.HasMessage("  [xiso] first"));
        Assert.True(logger.HasMessage("  [xiso] second"));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("  padded  ", "padded")]
    [InlineData("a\tb", "a b")]
    [InlineData("a\bb", "ab")]
    [InlineData("a\r\n", "a")]
    [InlineData("a   b", "a b")]
    public void SanitizeRemovesControlCharactersAndCollapsesSpaces(string input, string expected)
    {
        Assert.Equal(expected, LibraryOutputLineBuffer.Sanitize(input));
    }
}