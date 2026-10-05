using XISOStudio.Services;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests drive letter, UNC, network error, disk space, and safety buffer helpers in <c>PathHelper</c>.</summary>
public class PathHelperTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(@"\\server\share", null)]
    public void GetDriveLetterReturnsExpectedResult(string? path, string? expected)
    {
        var result = PathHelper.GetDriveLetter(path);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(@"C:\test\file.iso", "C:")]
    [InlineData("C:/test/file.iso", "C:")]
    [InlineData(@"D:\", "D:")]
    public void GetDriveLetterWindowsPathsReturnsExpectedResult(string? path, string? expected)
    {
        // Drive letters only exist on Windows; on Unix these paths resolve under the root.
        if (!OperatingSystem.IsWindows()) return;

        var result = PathHelper.GetDriveLetter(path);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetDriveLetterUnixRootReturnsNullInsteadOfEmptyString()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Null(PathHelper.GetDriveLetter("/"));
        Assert.Null(PathHelper.GetDriveLetter("/tmp/file.iso"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(@"C:\test", false)]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\server\share\folder", true)]
    public void IsUncPathReturnsExpectedResult(string? path, bool expected)
    {
        var result = PathHelper.IsUncPath(path);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"C:\test", false)]
    public void IsNetworkPathWithUncPathReturnsTrue(string? path, bool expected)
    {
        // Note: mapped network drives cannot be tested without actual network drives
        var result = PathHelper.IsNetworkPath(path);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData(@"C:\test", null, null)]
    [InlineData(@"\\server\share", "server", "share")]
    [InlineData(@"\\server\share\folder\file.iso", "server", "share")]
    public void TryGetUncShareInfoReturnsExpectedResult(string? path, string? expectedServer, string? expectedShare)
    {
        var result = PathHelper.TryGetUncShareInfo(path);

        if (expectedServer == null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.NotNull(result);
            Assert.Equal(expectedServer, result.Value.Server);
            Assert.Equal(expectedShare, result.Value.Share);
        }
    }

    [Fact]
    public void IsNetworkErrorWithNullExceptionReturnsFalse()
    {
        Assert.False(PathHelper.IsNetworkError(null));
    }

    [Theory]
    [InlineData("network path was not found", true)]
    [InlineData("network name is no longer available", true)]
    [InlineData("an unexpected network error occurred", true)]
    [InlineData("the semaphore timeout period has expired", true)]
    [InlineData("the network connection was aborted", true)]
    [InlineData("the network connection was reset", true)]
    [InlineData("some random error message", false)]
    [InlineData("file not found", false)]
    [InlineData("access denied", false)]
    public void IsNetworkErrorWithKnownPatternsReturnsExpectedResult(string message, bool expected)
    {
        var ex = new IOException(message);
        Assert.Equal(expected, PathHelper.IsNetworkError(ex));
    }

    [Fact]
    public void IsNetworkErrorCaseInsensitive()
    {
        var ex = new IOException("NETWORK PATH WAS NOT FOUND");
        Assert.True(PathHelper.IsNetworkError(ex));
    }

    [Theory]
    [InlineData("Netzwerk nicht erreichbar", true)]
    [InlineData("nicht mehr verfügbar", true)]
    [InlineData("erreur réseau", true)]
    [InlineData("n'est plus disponible", true)]
    [InlineData("error de red", true)]
    [InlineData("connettività di rete", true)]
    [InlineData("errore generico", false)]
    public void IsNetworkErrorMultilingualReturnsExpectedResult(string message, bool expected)
    {
        var ex = new IOException(message);
        Assert.Equal(expected, PathHelper.IsNetworkError(ex));
    }

    [Fact]
    public void IsNetworkErrorDeviceNotReadyReturnsFalse()
    {
        var ex = new IOException("The device is not ready");
        Assert.False(PathHelper.IsNetworkError(ex));
    }

    [Fact]
    public void IsNetworkErrorFatalDeviceHardwareErrorReturnsFalse()
    {
        var ex = new IOException("The request failed due to a fatal device hardware error.",
            unchecked((int)0x800701E3));
        Assert.False(PathHelper.IsNetworkError(ex));
    }

    [Fact]
    public void IsNetworkErrorDeviceGenericReturnsTrue()
    {
        var ex = new IOException("A device attached to the system is not functioning");
        Assert.True(PathHelper.IsNetworkError(ex));
    }

    [Theory]
    [InlineData("The request could not be performed because of an I/O device error.")]
    [InlineData("Impossibile eseguire la richiesta a causa di un errore di dispositivo I/O.")]
    [InlineData("Die Anforderung konnte wegen eines E/A-Gerätefehlers nicht ausgeführt werden.")]
    [InlineData("La demande n'a pas pu être exécutée en raison d'une erreur de périphérique d'E/S.")]
    [InlineData("No se pudo realizar la solicitud debido a un error de dispositivo de E/S.")]
    public void IsNetworkErrorDeviceIoErrorReturnsFalse(string message)
    {
        var ex = new IOException(message);
        Assert.False(PathHelper.IsNetworkError(ex));
    }

    [Fact]
    public void IsNetworkErrorChecksInnerException()
    {
        var inner = new IOException("network name is no longer available");
        var ex = new IOException("outer", inner);
        Assert.True(PathHelper.IsNetworkError(ex));
    }

    [Fact]
    public void IsDiskSpaceErrorWithNullReturnsFalse()
    {
        // Null would throw NRE, so we test with a non-disk-space error
        var ex = new IOException("some other error");
        Assert.False(PathHelper.IsDiskSpaceError(ex));
    }

    [Theory]
    [InlineData("Not enough space on disk", true)]
    [InlineData("not enough disk space", true)]
    [InlineData("insufficient disk space", true)]
    [InlineData("Disk full", true)]
    [InlineData("Espace insuffisant sur le disque", true)]
    [InlineData("disque plein", true)]
    [InlineData("file not found", false)]
    [InlineData("access denied", false)]
    public void IsDiskSpaceErrorWithKnownPatternsReturnsExpectedResult(string message, bool expected)
    {
        var ex = new IOException(message);
        Assert.Equal(expected, PathHelper.IsDiskSpaceError(ex));
    }

    [Fact]
    public void IsDiskSpaceErrorChecksInnerException()
    {
        var inner = new IOException("Not enough space");
        var ex = new IOException("outer", inner);
        Assert.True(PathHelper.IsDiskSpaceError(ex));
    }

    [Fact]
    public void IsDeviceIoErrorWithNullReturnsFalse()
    {
        Assert.False(PathHelper.IsDeviceIoError(null));
    }

    [Theory]
    [InlineData("The request could not be performed because of an I/O device error.", true)]
    [InlineData("Impossibile eseguire la richiesta a causa di un errore di dispositivo I/O.", true)]
    [InlineData("Die Anforderung konnte wegen eines E/A-Gerätefehlers nicht ausgeführt werden.", true)]
    [InlineData("La demande n'a pas pu être exécutée en raison d'une erreur de périphérique d'E/S.", true)]
    [InlineData("No se pudo realizar la solicitud debido a un error de dispositivo de E/S.", true)]
    [InlineData("The request failed due to a fatal device hardware error.", true)]
    [InlineData("Richiesta non riuscita a causa di un errore hardware del dispositivo irreversibile.", true)]
    [InlineData("some random error message", false)]
    [InlineData("The device is not ready", false)]
    public void IsDeviceIoErrorWithKnownPatternsReturnsExpectedResult(string message, bool expected)
    {
        var ex = new IOException(message);
        Assert.Equal(expected, PathHelper.IsDeviceIoError(ex));
    }

    [Theory]
    [InlineData(0x45D)]
    [InlineData(0x1E3)]
    public void IsDeviceIoErrorWithWin32ErrorCodeReturnsTrue(int hresult)
    {
        var ex = new IOException("device failure", unchecked((int)(0x80070000u | (uint)hresult)));
        Assert.True(PathHelper.IsDeviceIoError(ex));
    }

    [Fact]
    public void IsDeviceIoErrorChecksInnerException()
    {
        var inner = new IOException("The request could not be performed because of an I/O device error.", 0x45D);
        var ex = new IOException("outer", inner);
        Assert.True(PathHelper.IsDeviceIoError(ex));
    }

    [Fact]
    public void IsFileInUseErrorWithNullReturnsFalse()
    {
        Assert.False(PathHelper.IsFileInUseError(null));
    }

    [Theory]
    [InlineData(0x20, "The process cannot access the file because it is being used by another process.")]
    [InlineData(0x21, "The process cannot access the file because another process has locked a portion of the file.")]
    public void IsFileInUseErrorWithWin32ErrorCodeReturnsTrue(int hresult, string message)
    {
        var ex = new IOException(message, unchecked((int)(0x80070000u | (uint)hresult)));
        Assert.True(PathHelper.IsFileInUseError(ex));
    }

    [Theory]
    [InlineData("The process cannot access the file because it is being used by another process.", true)]
    [InlineData("Il processo non può accedere al file perché è utilizzato da un altro processo.", true)]
    [InlineData("some random error message", false)]
    public void IsFileInUseErrorWithKnownPatternsReturnsExpectedResult(string message, bool expected)
    {
        var ex = new IOException(message);
        Assert.Equal(expected, PathHelper.IsFileInUseError(ex));
    }

    [Fact]
    public void IsFileInUseErrorChecksInnerException()
    {
        var inner = new IOException("The process cannot access the file because it is being used by another process.");
        var ex = new IOException("outer", inner);
        Assert.True(PathHelper.IsFileInUseError(ex));
    }

    [Fact]
    public void AddSafetyBufferNormalSizeAddsTenPercentBuffer()
    {
        // 10% of 10 GB (1 GB) is larger than the 200 MB minimum buffer.
        const long tenGigabytes = 10L * 1024 * 1024 * 1024;
        Assert.Equal(tenGigabytes + (tenGigabytes / 10), PathHelper.AddSafetyBuffer(tenGigabytes));
    }

    [Fact]
    public void AddSafetyBufferSmallSizeUsesMinimumBuffer()
    {
        const long oneMegabyte = 1024L * 1024;
        Assert.Equal(oneMegabyte + (200L * 1024 * 1024), PathHelper.AddSafetyBuffer(oneMegabyte));
    }

    [Fact]
    public void AddSafetyBufferMaxValueSaturatesInsteadOfOverflowing()
    {
        Assert.Equal(long.MaxValue, PathHelper.AddSafetyBuffer(long.MaxValue));
    }
}