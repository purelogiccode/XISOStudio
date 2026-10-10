using XISOStudio.Interfaces;
using Serilog;

namespace XISOStudio.Services;

/// <summary>
/// Provides helpers for working with filesystem paths, including drive letters, UNC shares,
/// and classification of network, disk-space, and device I/O errors.
/// </summary>
public static class PathHelper
{
    /// <summary>
    ///     Comparison to use for filesystem paths: case-insensitive on Windows and macOS,
    ///     ordinal on case-sensitive file systems (Linux).
    /// </summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// Extracts the drive letter (e.g., "C:") from a given path.
    /// </summary>
    /// <param name="path">Path to inspect; may be <c>null</c>.</param>
    /// <returns>The drive letter without a trailing separator (for example, "C:"), or <c>null</c> when the path is empty, UNC, or the drive cannot be determined.</returns>
    public static string? GetDriveLetter(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            // Handle UNC paths (network shares) which don't have traditional drive letters
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return null;

            var fullPath = Path.GetFullPath(path);
            var pathRoot = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(pathRoot)) return null;

            var driveInfo = new DriveInfo(pathRoot);
            var driveName = driveInfo.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Unix roots ("/") trim to an empty string; report them as "no drive letter"
            // instead of an empty string, matching the documented contract.
            return driveName.Length == 0 ? null : driveName;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to determine drive letter for path: {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Determines if the given path is a UNC (Universal Naming Convention) network path.
    /// Examples: \\server\share, \\server\share\folder\file.txt
    /// </summary>
    /// <param name="path">Path to inspect; may be <c>null</c>.</param>
    /// <returns><c>true</c> when the path starts with <c>\\</c>; otherwise <c>false</c>.</returns>
    public static bool IsUncPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        return path.StartsWith(@"\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines if the given path is a network path (either UNC or a mapped network drive).
    /// </summary>
    /// <param name="path">Path to inspect; may be <c>null</c>.</param>
    /// <returns><c>true</c> when the path is UNC or resides on a mapped network drive; otherwise <c>false</c>.</returns>
    public static bool IsNetworkPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        // Check for UNC path first
        if (IsUncPath(path)) return true;

        // Check if it's a mapped network drive
        try
        {
            var driveLetter = GetDriveLetter(path);
            if (string.IsNullOrEmpty(driveLetter)) return false;

            var driveInfo = new DriveInfo(driveLetter);
            return driveInfo.DriveType == DriveType.Network;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to determine if path is a network path: {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Extracts the server and share name from a UNC path.
    /// Returns null if the path is not a valid UNC path.
    /// Example: \\server\share\folder -> (server: "server", share: "share")
    /// </summary>
    /// <param name="path">Path to inspect; may be <c>null</c>.</param>
    /// <returns>The server and share name of the UNC path, or <c>null</c> when the path is not a valid UNC path.</returns>
    public static (string Server, string Share)? TryGetUncShareInfo(string? path)
    {
        if (string.IsNullOrEmpty(path) || !IsUncPath(path))
            return null;

        try
        {
            // Remove the leading \\
            var trimmed = path.Substring(2);

            // Split by backslash
            var parts = trimmed.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
            {
                return (parts[0], parts[1]);
            }
        }
        catch (Exception ex)
        {
            // Ignore parsing errors
            Log.Debug(ex, "Failed to parse UNC share info from path: {Path}", path);
        }

        return null;
    }

    /// <summary>
    /// Common network-related error messages that indicate transient network failures.
    /// These errors may be resolved by retrying the operation.
    /// </summary>
    private static readonly string[] NetworkErrorPatterns =
    [
        "network path was not found",
        "network name is no longer available",
        "the specified network name is no longer available",
        "an unexpected network error occurred",
        "the network location cannot be reached",
        "a device attached to the system is not functioning",
        "the semaphore timeout period has expired",
        "the network path was not found",
        "the specified server cannot perform the requested operation",
        "the remote procedure call failed",
        "the remote procedure call was cancelled",
        "the network bios session limit was exceeded",
        "network access is denied",
        "the network connection was aborted",
        "the network connection was reset",
        "the network is not present or not started",
        "the account is not authorized to login from this station",
        "logon failure: unknown user name or bad password",
        "the session was cancelled"
    ];

    /// <summary>
    /// Checks if an exception message contains network-related error patterns
    /// that suggest a transient network failure. Supports messages in multiple
    /// languages (English, German, French, Spanish, Italian).
    /// </summary>
    /// <param name="exception">Exception to inspect; may be <c>null</c>.</param>
    /// <returns><c>true</c> when the exception or its inner exception describes a transient network failure; otherwise <c>false</c>.</returns>
    public static bool IsNetworkError(Exception? exception)
    {
        if (exception == null) return false;

        if (MatchesNetworkPatterns(exception.Message))
            return true;

        if (exception.InnerException != null && MatchesNetworkPatterns(exception.InnerException.Message))
            return true;

        return false;
    }

    /// <summary>
    /// Checks whether a message matches a known network error pattern, including localized
    /// and device-related phrases.
    /// </summary>
    /// <param name="message">Message to inspect.</param>
    /// <returns><c>true</c> when a network error pattern matches; otherwise <c>false</c>.</returns>
    private static bool MatchesNetworkPatterns(string message)
    {
        // English patterns
        if (NetworkErrorPatterns.Any(pattern => message.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            return true;

        // "device" can appear in non-network errors like "The device is not ready" (ERROR_NOT_READY)
        // or a hardware "I/O device error" (ERROR_IO_DEVICE); neither is a network failure.
        if (message.Contains("device", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("device is not ready", StringComparison.OrdinalIgnoreCase) &&
            !MatchesDeviceIoPatterns(message))
        {
            return true;
        }

        // German
        if (message.Contains("Netzwerk", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("nicht mehr verfügbar", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // French
        if (message.Contains("réseau", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("n'est plus disponible", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Spanish — use contextual phrases to avoid matching English words like "redirect"
        if (message.Contains("la red", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("de red", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Italian
        if (message.Contains("rete", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Determines if an exception is related to disk space issues.
    /// Checks HResult codes for ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL,
    /// as well as multilingual error messages.
    /// </summary>
    /// <param name="ex">Exception to inspect.</param>
    /// <returns><c>true</c> when the exception indicates a full disk; otherwise <c>false</c>.</returns>
    public static bool IsDiskSpaceError(Exception ex)
    {
        if (ex is IOException ioEx)
        {
            var hResult = ioEx.HResult & 0xFFFF;
            if (hResult is 0x70 or 0x27) return true; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
        }

        if (ex.InnerException is IOException innerIoEx)
        {
            var hResult = innerIoEx.HResult & 0xFFFF;
            if (hResult is 0x70 or 0x27) return true;
        }

        var message = ex.Message;
        if (ex.InnerException != null)
        {
            message += " " + ex.InnerException.Message;
        }

        return message.Contains("Not enough space", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("not enough disk space", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("insufficient disk space", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Disk full", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Espace insuffisant", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("disque plein", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Win32 error code ERROR_IO_DEVICE: the request could not be performed because
    /// of an I/O device error (e.g. a failing or disconnected drive).
    /// </summary>
    private const int ErrorIoDevice = 0x45D;

    /// <summary>
    /// Win32 error code ERROR_DEVICE_HARDWARE_ERROR: the request failed because of a
    /// fatal device hardware error (e.g. a failing external drive or a disconnected USB disk).
    /// </summary>
    private const int ErrorDeviceHardwareError = 0x1E3;

    /// <summary>
    /// Determines if an exception was caused by a hardware I/O failure on the source or
    /// destination device (e.g. a failing, disconnected, or power-cycling drive).
    /// Windows localizes the message, so the Win32 error code is checked first, with
    /// localized message patterns as a fallback for wrapped exceptions that lost the code.
    /// </summary>
    /// <param name="exception">Exception to inspect; may be <c>null</c>.</param>
    /// <returns><c>true</c> when the exception or its inner exception was caused by a hardware I/O failure; otherwise <c>false</c>.</returns>
    public static bool IsDeviceIoError(Exception? exception)
    {
        if (exception == null) return false;

        if (HasDeviceIoErrorCode(exception) || MatchesDeviceIoPatterns(exception.Message)) return true;

        return exception.InnerException != null &&
               (HasDeviceIoErrorCode(exception.InnerException) ||
                MatchesDeviceIoPatterns(exception.InnerException.Message));
    }

    /// <summary>
    /// Checks whether an exception is an <see cref="IOException" /> carrying a Win32
    /// hardware failure code (ERROR_IO_DEVICE or ERROR_DEVICE_HARDWARE_ERROR).
    /// </summary>
    /// <param name="exception">Exception to inspect.</param>
    /// <returns><c>true</c> when the exception carries a device failure code; otherwise <c>false</c>.</returns>
    private static bool HasDeviceIoErrorCode(Exception exception)
    {
        if (exception is not IOException ioException) return false;

        var hResult = ioException.HResult & 0xFFFF;
        return hResult is ErrorIoDevice or ErrorDeviceHardwareError;
    }

    /// <summary>
    /// Checks whether a message contains a localized hardware device failure pattern.
    /// </summary>
    /// <param name="message">Message to inspect.</param>
    /// <returns><c>true</c> when a device failure pattern matches; otherwise <c>false</c>.</returns>
    private static bool MatchesDeviceIoPatterns(string message)
    {
        // English, Italian, German, French and Spanish variants of the Windows message.
        // "Fatal device hardware error" (ERROR_DEVICE_HARDWARE_ERROR) and its Italian
        // translation are matched as well; other locales are covered by the error code.
        return message.Contains("I/O device error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("fatal device hardware error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("errore hardware del dispositivo", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("dispositivo I/O", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("E/A-Gerät", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("périphérique d'E/S", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("dispositivo de E/S", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Win32 error codes ERROR_SHARING_VIOLATION (0x20) and ERROR_LOCK_VIOLATION (0x21):
    /// the file is held open by another process (antivirus scanner, emulator, Explorer preview, ...).
    /// </summary>
    private const int ErrorSharingViolation = 0x20;
    private const int ErrorLockViolation = 0x21;

    /// <summary>
    /// Determines if an exception was caused by a file being held open by another process.
    /// Windows localizes the message, so the Win32 error code is checked first, with
    /// localized message patterns as a fallback for wrapped exceptions that lost the code.
    /// </summary>
    /// <param name="exception">Exception to inspect; may be <c>null</c>.</param>
    /// <returns><c>true</c> when the exception or its inner exception indicates a file-in-use error; otherwise <c>false</c>.</returns>
    public static bool IsFileInUseError(Exception? exception)
    {
        if (exception == null) return false;

        if (HasFileInUseErrorCode(exception) || MatchesFileInUsePatterns(exception.Message)) return true;

        return exception.InnerException != null &&
               (HasFileInUseErrorCode(exception.InnerException) ||
                MatchesFileInUsePatterns(exception.InnerException.Message));
    }

    /// <summary>
    /// Checks whether an exception is an <see cref="IOException" /> carrying a Win32
    /// sharing-violation or lock-violation code.
    /// </summary>
    /// <param name="exception">Exception to inspect.</param>
    /// <returns><c>true</c> when the exception carries a file-locked error code; otherwise <c>false</c>.</returns>
    private static bool HasFileInUseErrorCode(Exception exception)
    {
        if (exception is not IOException ioException) return false;

        var hResult = ioException.HResult & 0xFFFF;
        return hResult is ErrorSharingViolation or ErrorLockViolation;
    }

    /// <summary>
    /// Checks whether a message contains a localized "file in use" pattern.
    /// </summary>
    /// <param name="message">Message to inspect.</param>
    /// <returns><c>true</c> when a file-in-use pattern matches; otherwise <c>false</c>.</returns>
    private static bool MatchesFileInUsePatterns(string message)
    {
        return message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("used by another process", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("utilizado por otro proceso", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("utilizzato da un altro processo", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("utilisé par un autre processus", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("von einem anderen Prozess verwendet", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds the standard safety buffer (10% or 200 MB, whichever is larger) to a required
    /// size, saturating at <see cref="long.MaxValue" /> so extreme sizes cannot wrap around
    /// and make an undersized drive look sufficient.
    /// </summary>
    /// <param name="requiredBytes">Base size in bytes before the safety buffer is added.</param>
    /// <returns>The required size plus the safety buffer, saturated at <see cref="long.MaxValue" />.</returns>
    internal static long AddSafetyBuffer(long requiredBytes)
    {
        var buffer = Math.Max(requiredBytes / 10, 200L * 1024 * 1024);
        return requiredBytes > long.MaxValue - buffer ? long.MaxValue : requiredBytes + buffer;
    }

    /// <summary>
    /// Resolves and creates a temporary directory path with sufficient disk space.
    /// First checks the system temp drive, then falls back to other local drives. Each
    /// candidate is created immediately so a drive whose root is not writable (ACL
    /// restrictions, BitLocker-locked or read-only volumes) is skipped before the caller
    /// starts extracting into it.
    /// </summary>
    /// <param name="requiredSize">Number of bytes the temporary files will need.</param>
    /// <param name="tempSubfolder">Name of the subfolder created under the selected drive.</param>
    /// <param name="diskMonitorService">Service used to locate alternative drives with sufficient free space.</param>
    /// <returns>The full path of a new unique temporary directory, already created.</returns>
    public static string ResolveTempDirectory(long requiredSize, string tempSubfolder,
        IDiskMonitorService diskMonitorService)
    {
        var defaultTempPath = Path.GetTempPath();
        var defaultTempDriveRoot = Path.GetPathRoot(defaultTempPath);
        var requiredWithBuffer = AddSafetyBuffer(requiredSize);

        var candidateRoots = new List<string>();

        if (defaultTempDriveRoot != null)
        {
            try
            {
                var defaultDrive = new DriveInfo(defaultTempDriveRoot);
                if (defaultDrive.IsReady && defaultDrive.AvailableFreeSpace >= requiredWithBuffer)
                    candidateRoots.Add(defaultTempPath);
            }
            catch (Exception ex)
            {
                // Ignore and fall through to alternative search
                Log.Debug(ex, "Could not inspect default temp drive: {TempDriveRoot}", defaultTempDriveRoot);
            }
        }

        Exception? lastCreationError = null;
        foreach (var root in Candidates())
        {
            var candidate = Path.Combine(root, tempSubfolder, Guid.NewGuid().ToString());
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // The drive has space but cannot host the folder: try the next candidate
                // instead of failing the whole operation on a drive the user cannot write.
                lastCreationError = ex;
                Log.Debug(ex, "Could not create a temporary folder under {Root}; trying the next drive", root);
            }
        }

        if (lastCreationError != null)
        {
            throw new IOException(
                $"Unable to create temporary files. No drive with sufficient free space allows writing. {lastCreationError.Message}",
                lastCreationError);
        }

        var requiredFormatted = Formatter.FormatBytes(requiredWithBuffer);
        var defaultAvailable = Formatter.FormatBytes(diskMonitorService.GetAvailableFreeSpace(defaultTempPath));
        throw new IOException(
            $"Not enough disk space to create temporary files. Required: {requiredFormatted}, Available: {defaultAvailable}. No other local drives have sufficient free space. Please free up disk space and try again.");

        // Alternative drives are only scanned when the default temp path is not usable,
        // so the common case does not inspect every drive.
        IEnumerable<string> Candidates()
        {
            foreach (var root in candidateRoots)
                yield return root;

            foreach (var root in diskMonitorService.FindDrivesWithFreeSpace(requiredSize, defaultTempDriveRoot))
                yield return root;
        }
    }

    /// <summary>
    /// Deletes an existing output file so it can be rewritten, retrying briefly while another
    /// process holds it (antivirus scanner, emulator, Explorer preview). Locked and
    /// environmental failures are logged at Information so they do not generate automatic bug
    /// reports; unexpected failures stay at Warning. Never throws for a failed deletion.
    /// </summary>
    /// <param name="filePath">Full path of the file to delete.</param>
    /// <param name="logger">Logger that receives the outcome.</param>
    /// <param name="token">Token used to cancel the retry delays.</param>
    /// <returns><c>true</c> when the file was deleted; otherwise <c>false</c>.</returns>
    internal static async Task<bool> TryDeleteExistingFileWithRetryAsync(string filePath, ILogger logger,
        CancellationToken token)
    {
        const int maxAttempts = 3;
        var fileName = Path.GetFileName(filePath);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Delete(filePath);
                return true;
            }
            catch (IOException ex) when (IsFileInUseError(ex) && attempt < maxAttempts)
            {
                var delayMs = 250 * attempt;
                logger.Information(
                    "The existing output file '{FileName}' is in use by another process. Retrying in {DelayMs}ms... (attempt {Attempt}/{MaxAttempts})",
                    fileName, delayMs, attempt, maxAttempts);
                await Task.Delay(delayMs, token);
            }
            catch (Exception ex) when (IsFileInUseError(ex))
            {
                logger.Information(
                    "The existing output file '{FileName}' is in use by another process (for example an emulator, antivirus scanner, or Explorer preview). " +
                    "Close the application using the file and run the conversion again.", fileName);
                return false;
            }
            catch (Exception ex) when (IsDiskSpaceError(ex) || IsDeviceIoError(ex) || IsNetworkError(ex) ||
                                       ex is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                logger.Information(ex,
                    "Could not delete the existing output file '{FileName}' due to an environmental error", fileName);
                return false;
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Could not delete the existing output file '{FileName}'", fileName);
                return false;
            }
        }

        return false;
    }
}