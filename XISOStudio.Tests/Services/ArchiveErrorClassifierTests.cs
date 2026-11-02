using SharpCompress.Common;
using SharpCompress.Compressors.Deflate;
using XISOStudio.Services;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>
/// Tests that archive-format failures raised by SharpCompress are recognised as user-input
/// conditions so they stay below the automatic bug-report threshold.
/// </summary>
public class ArchiveErrorClassifierTests
{
    [Fact]
    public void ArchiveOperationExceptionIsArchiveInputError()
    {
        Assert.True(ArchiveErrorClassifier.IsArchiveInputError(
            new ArchiveOperationException("Cannot determine compressed stream type.")));
    }

    [Fact]
    public void ArchiveExceptionIsArchiveInputError()
    {
        Assert.True(ArchiveErrorClassifier.IsArchiveInputError(
            new ArchiveException("Failed to locate the Zip Header")));
    }

    [Fact]
    public void InvalidFormatExceptionIsArchiveInputError()
    {
        Assert.True(ArchiveErrorClassifier.IsArchiveInputError(new InvalidFormatException("bad format")));
    }

    [Fact]
    public void ZlibExceptionIsArchiveInputError()
    {
        Assert.True(ArchiveErrorClassifier.IsArchiveInputError(
            new ZlibException("Bad state (invalid block type)")));
    }

    [Fact]
    public void LzmaDataErrorExceptionIsArchiveInputError()
    {
        // Reported when a 7z archive's LZMA payload is corrupt or truncated.
        Assert.True(ArchiveErrorClassifier.IsArchiveInputError(CreateLzmaDataErrorException()));
    }

    [Fact]
    public void WrappedLzmaDataErrorExceptionIsArchiveInputError()
    {
        var exception = new IOException("failed", CreateLzmaDataErrorException());

        Assert.True(ArchiveErrorClassifier.IsArchiveInputError(exception));
    }

    [Fact]
    public void EndOfStreamExceptionIsArchiveInputError()
    {
        Assert.True(ArchiveErrorClassifier.IsArchiveInputError(new EndOfStreamException()));
    }

    [Fact]
    public void LzmaDataErrorExceptionIsCorruptArchiveData()
    {
        // "Data Error" from the LZMA decoder means the compressed payload is damaged.
        Assert.True(ArchiveErrorClassifier.IsCorruptArchiveData(CreateLzmaDataErrorException()));
    }

    [Fact]
    public void WrappedLzmaDataErrorExceptionIsCorruptArchiveData()
    {
        var exception = new IOException("failed", CreateLzmaDataErrorException());

        Assert.True(ArchiveErrorClassifier.IsCorruptArchiveData(exception));
    }

    [Fact]
    public void DataErrorMessageIsCorruptArchiveData()
    {
        Assert.True(ArchiveErrorClassifier.IsCorruptArchiveData(new IOException("Data Error")));
    }

    [Fact]
    public void EndOfStreamExceptionIsCorruptArchiveData()
    {
        Assert.True(ArchiveErrorClassifier.IsCorruptArchiveData(new EndOfStreamException()));
    }

    [Fact]
    public void ZlibBadStateIsCorruptArchiveData()
    {
        Assert.True(ArchiveErrorClassifier.IsCorruptArchiveData(new ZlibException("Bad state (invalid block type)")));
    }

    [Fact]
    public void ArchiveHeaderErrorIsNotCorruptArchiveData()
    {
        Assert.False(ArchiveErrorClassifier.IsCorruptArchiveData(
            new ArchiveException("Failed to locate the Zip Header")));
    }

    [Fact]
    public void NullIsNotCorruptArchiveData()
    {
        Assert.False(ArchiveErrorClassifier.IsCorruptArchiveData(null));
    }

    [Fact]
    public void WrappedArchiveExceptionIsArchiveInputError()
    {
        var exception = new IOException("failed", new ArchiveException("Failed to locate the Zip Header"));

        Assert.True(ArchiveErrorClassifier.IsArchiveInputError(exception));
    }

    [Fact]
    public void AggregateWithArchiveExceptionIsArchiveInputError()
    {
        var exception = new AggregateException(new InvalidOperationException("other"),
            new ArchiveOperationException("Cannot determine compressed stream type."));

        Assert.True(ArchiveErrorClassifier.IsArchiveInputError(exception));
    }

    [Fact]
    public void ArchiveOpenInvalidOperationExceptionIsArchiveInputError()
    {
        Assert.True(ArchiveErrorClassifier.IsArchiveInputError(
            new InvalidOperationException("Could not open the Archive")));
    }

    [Fact]
    public void FileInUseIoExceptionIsNotArchiveInputError()
    {
        Assert.False(ArchiveErrorClassifier.IsArchiveInputError(
            new IOException("The process cannot access the file because it is being used by another process.",
                unchecked((int)0x80070020))));
    }

    [Fact]
    public void UnexpectedExceptionIsNotArchiveInputError()
    {
        Assert.False(ArchiveErrorClassifier.IsArchiveInputError(new InvalidOperationException("bug")));
    }

    [Fact]
    public void NullIsNotArchiveInputError()
    {
        Assert.False(ArchiveErrorClassifier.IsArchiveInputError(null));
    }

    private static Exception CreateLzmaDataErrorException()
    {
        // SharpCompress marks its LZMA DataErrorException as internal, so it cannot be
        // constructed directly; the classifier matches it by full type name.
        var type = typeof(ArchiveException).Assembly.GetType(
            "SharpCompress.Compressors.LZMA.DataErrorException", throwOnError: true)!;
        return (Exception)Activator.CreateInstance(type)!;
    }
}
