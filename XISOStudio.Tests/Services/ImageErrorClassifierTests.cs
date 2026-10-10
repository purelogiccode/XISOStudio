using XISOSharp;
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
    public void IsEnvironmentalIoErrorWithUnexpectedErrorReturnsFalse()
    {
        Assert.False(ImageErrorClassifier.IsEnvironmentalIoError(new InvalidOperationException("bug")));
    }

    public static TheoryData<Exception> InvalidImageErrors()
    {
        return new TheoryData<Exception>
        {
            new XisoFormatException("Not a valid XISO"),
            new XisoEmptyException("empty image"),
            new XisoFileTooLargeException("file too large"),
            new InvalidDataException("not an Xbox DVD image"),
            new EndOfStreamException(),
            new IOException("Read error while reading sector 0")
        };
    }

    public static TheoryData<Exception> MissingOrUnreadableErrors()
    {
        return new TheoryData<Exception>
        {
            new FileNotFoundException("missing.iso"),
            new DirectoryNotFoundException("missing folder"),
            new UnauthorizedAccessException("access denied")
        };
    }

    public static TheoryData<Exception> EnvironmentalIoErrors()
    {
        return new TheoryData<Exception>
        {
            new IOException("The device is not ready"),
            new UnauthorizedAccessException("access denied"),
            new NotSupportedException("unsupported path")
        };
    }

    public static TheoryData<Exception> NonUserInputEnvironmentalErrors()
    {
        return new TheoryData<Exception>
        {
            new IOException("The device is not ready"),
            new NotSupportedException("unsupported path")
        };
    }
}
