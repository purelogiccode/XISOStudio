using XISOSharp;
using XISOStudio.Services;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests the actionable host filesystem message mapper.</summary>
public class HostIoErrorDescriberTests
{
    [Fact]
    public void PermissionDeniedNamesThePathAndOperation()
    {
        var message = HostIoErrorDescriber.TryDescribe(
            new UnauthorizedAccessException("Access is denied."), "writing the extracted file",
            @"C:\games\out\file.bin");

        Assert.NotNull(message);
        Assert.Contains("permission denied", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\games\out\file.bin", message, StringComparison.Ordinal);
        Assert.Contains("writing the extracted file", message, StringComparison.Ordinal);
    }

    [Fact]
    public void PathTooLongSuggestsShorteningThePath()
    {
        var message = HostIoErrorDescriber.TryDescribe(new PathTooLongException("path too long"),
            "creating the image", "long.iso");

        Assert.NotNull(message);
        Assert.Contains("too long", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingDirectoryIsReportedAsUnavailable()
    {
        var message = HostIoErrorDescriber.TryDescribe(
            new DirectoryNotFoundException("Could not find a part of the path."),
            "writing the extracted file", @"Z:\missing\file.bin");

        Assert.NotNull(message);
        Assert.Contains("does not exist", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IllegalHostNameSuggestsTheNamePolicy()
    {
        var message = HostIoErrorDescriber.TryDescribe(
            new NotSupportedException("The given path's format is not supported."),
            "writing the extracted file", "CON");

        Assert.NotNull(message);
        Assert.Contains("host rejected", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name policy", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LibraryFormatExceptionsAreNotHostFailures()
    {
        Assert.Null(HostIoErrorDescriber.TryDescribe(new XisoFormatException("invalid image"),
            "reading the image", "game.iso"));
        Assert.Null(HostIoErrorDescriber.TryDescribe(new InvalidDataException("corrupt block"),
            "reading the archive", "game.zar"));
    }

    [Fact]
    public void CancellationIsNotAHostFailure()
    {
        Assert.Null(HostIoErrorDescriber.TryDescribe(new OperationCanceledException(),
            "reading the image", "game.iso"));
    }

    [Fact]
    public void NullExceptionThrows()
    {
        Assert.Throws<ArgumentNullException>(static () =>
            HostIoErrorDescriber.TryDescribe(null!, "reading the image"));
    }
}
