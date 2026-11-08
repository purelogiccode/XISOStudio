using XISOSharp;

namespace XISOStudio.Services;

/// <summary>
///     Produces actionable descriptions for host filesystem failures (permissions, long
///     paths, sharing violations, illegal names, full disks) through XISOSharp's
///     <see cref="HostIoDiagnostics" /> mapper. Image-format and decompression exceptions are
///     deliberately excluded: they describe the input, not the host.
/// </summary>
internal static class HostIoErrorDescriber
{
    /// <summary>
    ///     Describes a host filesystem failure, or returns <c>null</c> when the exception is
    ///     not one (for example a corrupt-image or decompression error).
    /// </summary>
    /// <param name="exception">Caught exception.</param>
    /// <param name="operation">Short verb phrase describing the failed work.</param>
    /// <param name="path">Host path involved, when known.</param>
    /// <returns>An actionable message, or <c>null</c> when the exception is not host I/O.</returns>
    public static string? TryDescribe(Exception exception, string operation, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!IsHostIoFailure(exception)) return null;

        var message = HostIoDiagnostics.Describe(exception, operation, path);
        return string.IsNullOrWhiteSpace(message) ? null : message;
    }

    /// <summary>
    ///     Returns true for the plain host-I/O exception types. The exact-type check for
    ///     <see cref="IOException" /> keeps library format exceptions (which derive from it)
    ///     classified as image problems.
    /// </summary>
    /// <param name="exception">Exception to classify.</param>
    /// <returns><c>true</c> when the exception describes a host filesystem failure.</returns>
    private static bool IsHostIoFailure(Exception exception)
    {
        return exception.GetType() == typeof(IOException)
               || exception is UnauthorizedAccessException
                   or PathTooLongException
                   or DirectoryNotFoundException
                   or FileNotFoundException
                   or NotSupportedException
                   or ArgumentException and not ArgumentNullException;
    }
}
