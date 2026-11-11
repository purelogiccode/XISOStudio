using CCISharp;
using XISOSharp;
using XISOSharp.Models;
using XISOStudio.Services;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests exception classification for image and environmental I/O errors.</summary>
public class ImageErrorClassifierTests
{
    [Theory]
    [MemberData(nameof(InvalidImageErrors))]
    public void IsInvalidImageErrorWithInvalidImageExceptionsReturnsTrue(Exception exception)
    {
        Assert.True(ImageErrorClassifier.IsInvalidImageError(exception));
    }

    [Theory]
    [MemberData(nameof(EnvironmentalIoErrors))]
    public void IsInvalidImageErrorWithEnvironmentalErrorsReturnsFalse(Exception exception)
    {
        Assert.False(ImageErrorClassifier.IsInvalidImageError(exception));
    }

    [Theory]
    [MemberData(nameof(InvalidImageErrors))]
    [MemberData(nameof(MissingOrUnreadableErrors))]
    public void IsUserInputImageErrorWithExpectedInputProblemsReturnsTrue(Exception exception)
    {
        Assert.True(ImageErrorClassifier.IsUserInputImageError(exception));
    }

    [Theory]
    [MemberData(nameof(NonUserInputEnvironmentalErrors))]
    public void IsUserInputImageErrorWithEnvironmentalIoErrorsReturnsFalse(Exception exception)
    {
        Assert.False(ImageErrorClassifier.IsUserInputImageError(exception));
    }

    [Fact]
    public void IsUserInputImageErrorWithUnexpectedErrorReturnsFalse()
    {
        Assert.False(ImageErrorClassifier.IsUserInputImageError(new InvalidOperationException("bug")));
    }

    [Theory]
    [MemberData(nameof(EnvironmentalIoErrors))]
    public void IsEnvironmentalIoErrorWithIoFailuresReturnsTrue(Exception exception)
    {
        Assert.True(ImageErrorClassifier.IsEnvironmentalIoError(exception));
    }

    [Fact]
    public void IsEnvironmentalIoErrorWithWrappedWriteFailureReturnsTrue()
    {
        var exception = new ExtractFileException(ExtractError.ErrFileWrite,
            "Failed to extract file", new IOException("No space left on device"));

        Assert.True(ImageErrorClassifier.IsEnvironmentalIoError(exception));
    }

    [Fact]
    public void IsEnvironmentalIoErrorWithTruncatedEntryReturnsFalse()
    {
        var exception = new ExtractFileException(ExtractError.ErrFileTruncated, "truncated entry");

        Assert.False(ImageErrorClassifier.IsEnvironmentalIoError(exception));
    }

    [Fact]
    public void IsEnvironmentalIoErrorWithUnexpectedErrorReturnsFalse()
    {
        Assert.False(ImageErrorClassifier.IsEnvironmentalIoError(new InvalidOperationException("bug")));
    }

    [Fact]
    public void IsCciIoErrorWithWriterIoFailureReturnsTrue()
    {
        var exception = new IOException("CCI I/O error for 'X:\\Games\\game.cci': I/O error");

        Assert.True(ImageErrorClassifier.IsCciIoError(exception));
    }

    [Fact]
    public void IsCciIoErrorWithImageFormatErrorReturnsFalse()
    {
        Assert.False(ImageErrorClassifier.IsCciIoError(
            new InvalidDataException("Invalid CCI image 'game.cci': corrupt sector")));
    }

    [Fact]
    public void IsCciIoErrorWithReadErrorReturnsFalse()
    {
        Assert.False(ImageErrorClassifier.IsCciIoError(new IOException("Read error while reading sector 0")));
    }

    [Fact]
    public void IsMissingApplicationComponentErrorWithAssemblyLoadFailureReturnsTrue()
    {
        var exception = new FileNotFoundException(
            "Could not load file or assembly 'XISOSharp, Version=1.0.0.0, Culture=neutral, " +
            "PublicKeyToken=8368ef6ea2a36128'. The system cannot find the file specified.",
            "XISOSharp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=8368ef6ea2a36128");

        Assert.True(ImageErrorClassifier.IsMissingApplicationComponentError(exception));
    }

    [Fact]
    public void IsMissingApplicationComponentErrorWithDllFileNameReturnsTrue()
    {
        var exception = new FileNotFoundException("Could not load the engine.", "XISOSharp.dll");

        Assert.True(ImageErrorClassifier.IsMissingApplicationComponentError(exception));
    }

    [Fact]
    public void IsMissingApplicationComponentErrorWithUserFileNotFoundReturnsFalse()
    {
        var exception = new FileNotFoundException("The image was not found.", "game.iso");

        Assert.False(ImageErrorClassifier.IsMissingApplicationComponentError(exception));
    }

    [Fact]
    public void IsMissingApplicationComponentErrorWithWrappedAssemblyLoadFailureReturnsTrue()
    {
        var exception = new TypeInitializationException(
            "XISOStudio.Services.XisoImageExplorer",
            new FileNotFoundException("Could not load file or assembly 'XISOSharp'."));

        Assert.True(ImageErrorClassifier.IsMissingApplicationComponentError(exception));
    }

    [Fact]
    public void IsMissingApplicationComponentErrorWithUnexpectedErrorReturnsFalse()
    {
        Assert.False(ImageErrorClassifier.IsMissingApplicationComponentError(new InvalidOperationException("bug")));
    }

    public static TheoryData<Exception> InvalidImageErrors()
    {
        return
        [
            new XisoFormatException("Not a valid XISO"),
            new XisoEmptyException("empty image"),
            new XisoFileTooLargeException("file too large"),
            new ExtractFileException("Failed to extract file"),
            new TruncatedCopyException(1, 2),
            new CciException(CciError.CorruptSector),
            new InvalidDataException("not an Xbox DVD image"),
            new EndOfStreamException(),
            new IOException("Read error while reading sector 0")
        ];
    }

    public static TheoryData<Exception> MissingOrUnreadableErrors()
    {
        return
        [
            new FileNotFoundException("missing.iso"),
            new DirectoryNotFoundException("missing folder"),
            new UnauthorizedAccessException("access denied")
        ];
    }

    public static TheoryData<Exception> EnvironmentalIoErrors()
    {
        return
        [
            new IOException("The device is not ready"),
            new UnauthorizedAccessException("access denied"),
            new NotSupportedException("unsupported path")
        ];
    }

    public static TheoryData<Exception> NonUserInputEnvironmentalErrors()
    {
        return
        [
            new IOException("The device is not ready"),
            new NotSupportedException("unsupported path")
        ];
    }
}