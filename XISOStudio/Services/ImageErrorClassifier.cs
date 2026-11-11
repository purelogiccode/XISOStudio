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
    /// Full names of the XISOSharp exception types that indicate an invalid input image.
    /// They are matched by name rather than by a compile-time reference: if the XISOSharp
    /// assembly itself is missing or damaged, this classifier must still be able to run
    /// and classify the resulting <see cref="FileNotFoundException" />.
    /// </summary>
    private static readonly string[] InvalidImageExceptionTypeNames =
    [
        "CCISharp.CciException",
        "XISOSharp.ExtractErrorException",
        "XISOSharp.ExtractFileException",
        "XISOSharp.TruncatedCopyException",
        "XISOSharp.XisoEmptyException",
        "XISOSharp.XisoFileTooLargeException",
        "XISOSharp.XisoFormatException"
    ];

    /// <summary>
    /// Errors that indicate the input image itself is unsupported or corrupt rather
    /// than an application defect. XISOSharp's low-level reader reports truncated
    /// images as a plain IOException with a "Read error" message.
    /// </summary>
    /// <param name="exception">Exception to inspect.</param>
    /// <returns><c>true</c> when the error indicates an invalid input image; otherwise <c>false</c>.</returns>
    public static bool IsInvalidImageError(Exception exception)
    {
        if (exception is InvalidDataException or EndOfStreamException) return true;

        if (HasAnyTypeName(exception, InvalidImageExceptionTypeNames)) return true;

        return exception is IOException ioException &&
               ioException.Message.StartsWith("Read error", StringComparison.OrdinalIgnoreCase);
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
        // Library wrappers such as XISOSharp's ExtractFileException carry the OS-level
        // IOException as their inner exception. Walk the chain instead of looking only at
        // the outermost type, otherwise disk-full writes are logged as application defects.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is IOException or UnauthorizedAccessException or NotSupportedException) return true;
        }

        return false;
    }

    /// <summary>
    /// Errors reported by XISOSharp's CCI writer when the underlying host I/O operation
    /// fails (for example a disconnected, full, or failing source/output drive). CCISharp
    /// reports these failures with a generic error code, which XISOSharp wraps in an
    /// <see cref="IOException" /> whose message starts with <c>CCI I/O error for</c>.
    /// </summary>
    /// <param name="exception">Exception to inspect.</param>
    /// <returns><c>true</c> when the exception reports a CCI host I/O failure; otherwise <c>false</c>.</returns>
    public static bool IsCciIoError(Exception exception)
    {
        return exception is IOException &&
               exception.Message.StartsWith("CCI I/O error for", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Errors caused by a missing or damaged application assembly (for example a truncated
    /// download or a partially deleted installation). These are installation problems, not
    /// image-input problems or application defects, and the user can fix them by reinstalling.
    /// </summary>
    /// <param name="exception">Exception to inspect.</param>
    /// <returns><c>true</c> when the exception reports a failed application-assembly load.</returns>
    public static bool IsMissingApplicationComponentError(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not (FileNotFoundException or FileLoadException or BadImageFormatException))
                continue;

            if (current.Message.Contains("Could not load file or assembly", StringComparison.OrdinalIgnoreCase))
                return true;

            if (current is FileNotFoundException { FileName: { Length: > 0 } fileName } &&
                fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> when the exception or one of its base types has one of the
    /// supplied full names. Type names are compared instead of types so that classifying
    /// an error never requires loading the library that raised it.
    /// </summary>
    /// <param name="exception">Exception whose inheritance chain is inspected.</param>
    /// <param name="typeNames">Full type names to look for.</param>
    /// <returns><c>true</c> when a matching type name is found; otherwise <c>false</c>.</returns>
    private static bool HasAnyTypeName(Exception exception, string[] typeNames)
    {
        for (var type = exception.GetType(); type is not null; type = type.BaseType)
        {
            var fullName = type.FullName;
            if (fullName is not null && Array.IndexOf(typeNames, fullName) >= 0) return true;
        }

        return false;
    }
}
