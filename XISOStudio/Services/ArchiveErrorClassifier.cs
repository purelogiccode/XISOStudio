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
}
