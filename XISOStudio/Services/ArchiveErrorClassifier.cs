using SharpCompress.Common;
using SharpCompress.Compressors.Deflate;

namespace XISOStudio.Services;

/// <summary>
/// Classifies exceptions raised while reading user-supplied archives. SharpCompress reports
/// archives that are incomplete, corrupt, or in a format it cannot decode with its own
/// exception types. Those are input conditions, not application defects, so callers log
/// them below the bug-report threshold instead of auto-reporting them.
/// </summary>
internal static class ArchiveErrorClassifier
{
    /// <summary>
    /// Full name of SharpCompress's LZMA decoder failure. The type is internal, so it is
    /// matched by name rather than by a compile-time reference.
    /// </summary>
    private const string LzmaDataErrorExceptionTypeName = "SharpCompress.Compressors.LZMA.DataErrorException";

    /// <summary>
    /// Returns <c>true</c> when the exception (or one of its inner exceptions) indicates that
    /// the archive itself cannot be read: wrong/unknown format, missing ZIP header, truncated
    /// stream, unsupported compression, or corrupt compressed data.
    /// </summary>
    /// <param name="exception">Exception raised while opening or extracting the archive.</param>
    /// <returns><c>true</c> for an expected archive-input condition; otherwise <c>false</c>.</returns>
    internal static bool IsArchiveInputError(Exception? exception)
    {
        if (exception is null) return false;

        if (exception is ArchiveException or ArchiveOperationException or InvalidFormatException or ZlibException)
        {
            return true;
        }

        // LZMA/LZMA2 payloads that fail their range decoder or header checks surface as
        // DataErrorException ("Data Error") from the 7z decoder. This means the compressed
        // data is corrupt or uses an unsupported variant, not that the application failed;
        // the caller retries with the 7-Zip CLI.
        if (IsLzmaDataError(exception))
        {
            return true;
        }

        // A truncated or still-downloading archive surfaces as EndOfStreamException from
        // the entry streams.
        if (exception is EndOfStreamException) return true;

        // SharpCompress occasionally reports an unrecognized archive as an
        // InvalidOperationException whose message mentions the archive.
        if (exception is InvalidOperationException &&
            exception.Message.Contains("Archive", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions.Any(IsArchiveInputError);
        }

        return exception.InnerException is not null && IsArchiveInputError(exception.InnerException);
    }

    /// <summary>
    /// Returns <c>true</c> when the exception indicates that the archive's compressed data is
    /// damaged or truncated, so the file is probably corrupt rather than merely unsupported.
    /// The caller uses this to choose an actionable "re-download the archive" message.
    /// </summary>
    /// <param name="exception">Exception raised while reading or extracting the archive.</param>
    /// <returns><c>true</c> for damaged or truncated compressed data; otherwise <c>false</c>.</returns>
    internal static bool IsCorruptArchiveData(Exception? exception)
    {
        if (exception is null) return false;

        if (exception is EndOfStreamException or ZlibException) return true;

        // SharpCompress reports corrupt LZMA/LZMA2 data as "Data Error"; this is the exact
        // failure seen when a large game .7z is truncated or damaged during download.
        if (IsLzmaDataError(exception)) return true;

        if (exception.Message.Contains("End of stream reached", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("Unable to read beyond the end of the stream",
                StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("Bad state", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("Data Error", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions.Any(IsCorruptArchiveData);
        }

        return exception.InnerException is not null && IsCorruptArchiveData(exception.InnerException);
    }

    private static bool IsLzmaDataError(Exception exception)
    {
        return string.Equals(exception.GetType().FullName, LzmaDataErrorExceptionTypeName, StringComparison.Ordinal);
    }
}
