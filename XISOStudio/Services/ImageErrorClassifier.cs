using XISOSharp;

namespace XISOStudio.Services;

/// <summary>
/// Classifies exceptions raised while opening or reading Xbox images so expected
/// user-input conditions (unsupported or corrupt images, missing or unreadable files)
/// are not reported as application defects, and environmental I/O failures (disk full,
/// access denied) are kept below the automatic bug-report threshold.
/// </summary>
public static class ImageErrorClassifier
{
    /// <summary>
    /// Errors that indicate the input image itself is unsupported or corrupt rather
    /// than an application defect. XISOSharp's low-level reader reports truncated
    /// images as a plain IOException with a "Read error" message.
    /// </summary>
    /// <param name="exception">Exception to inspect.</param>
    /// <returns><c>true</c> when the error indicates an invalid input image; otherwise <c>false</c>.</returns>
    public static bool IsInvalidImageError(Exception exception)
    {
        return exception is XisoFormatException or XisoEmptyException or XisoFileTooLargeException
                   or InvalidDataException or ExtractErrorException or EndOfStreamException ||
               (exception is IOException ioException &&
                ioException.Message.StartsWith("Read error", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Errors caused by the selected file being invalid, missing, or unreadable: expected
    /// user-input or environmental conditions that must not be reported as defects.
    /// </summary>
    /// <param name="exception">Exception to inspect.</param>
    /// <returns><c>true</c> when the error is an expected user-input image problem; otherwise <c>false</c>.</returns>
    public static bool IsUserInputImageError(Exception exception)
    {
        return IsInvalidImageError(exception) ||
               exception is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException;
    }

    /// <summary>
    /// Errors caused by the environment (disk full, access denied, unreadable path) rather
    /// than an application defect.
    /// </summary>
    /// <param name="exception">Exception to inspect.</param>
    /// <returns><c>true</c> when the error is an environmental I/O failure; otherwise <c>false</c>.</returns>
    public static bool IsEnvironmentalIoError(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or NotSupportedException;
    }
}
