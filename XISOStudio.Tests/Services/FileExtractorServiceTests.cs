using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using XISOStudio.Services;
using Serilog.Events;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests archive inspection, extraction, transient I/O detection, and zip-slip protection in <c>FileExtractorService</c>.</summary>
public class FileExtractorServiceTests : IDisposable
{
    private readonly TestLogger _logger = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"FileExtractorTests_{Guid.NewGuid():N}");

    public FileExtractorServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch
        {
            // ignored
        }

        GC.SuppressFinalize(this);
    }

    private FileExtractorService CreateService()
    {
        return new FileExtractorService(_logger.Logger);
    }

    private string CreateTestZip(string zipName, Dictionary<string, string> entries)
    {
        var zipPath = Path.Combine(_tempDir, zipName);
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return zipPath;
    }

    private string CreateCorruptZip(string zipName)
    {
        var zipPath = Path.Combine(_tempDir, zipName);
        File.WriteAllBytes(zipPath, [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0xFF, 0xFF]);
        return zipPath;
    }

    #region GetArchiveInfoAsync Tests

    [Fact]
    public async Task GetArchiveInfoAsyncValidZipReturnsCorrectCount()
    {
        var zipPath = CreateTestZip("test.zip", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "file1.txt", "content1" },
            { "file2.txt", "content2" },
            { "file3.txt", "content3" }
        });
        var service = CreateService();

        var (totalSize, fileCount) = await service.GetArchiveInfoAsync(zipPath, CancellationToken.None);

        Assert.Equal(3, fileCount);
        Assert.True(totalSize > 0);
    }

    [Fact]
    public async Task GetArchiveInfoAsyncValidZipReturnsCorrectSize()
    {
        const string content1 = "Hello, World!"; // 13 bytes
        const string content2 = "Test content for size calculation"; // 33 bytes
        var zipPath = CreateTestZip("sized.zip", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "a.txt", content1 },
            { "b.txt", content2 }
        });
        var service = CreateService();

        var (totalSize, _) = await service.GetArchiveInfoAsync(zipPath, CancellationToken.None);

        Assert.Equal(content1.Length + content2.Length, totalSize);
    }

    [Fact]
    public async Task GetArchiveInfoAsyncSingleFileZipReturnsOne()
    {
        var zipPath = CreateTestZip("single.zip", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "only.txt", "data" }
        });
        var service = CreateService();

        var (_, fileCount) = await service.GetArchiveInfoAsync(zipPath, CancellationToken.None);

        Assert.Equal(1, fileCount);
    }

    [Fact]
    public async Task GetArchiveInfoAsyncZipWithSubdirectoriesOnlyCountsFiles()
    {
        var zipPath = CreateTestZip("nested.zip", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "root.txt", "root content" },
            { "sub/deep/file.txt", "deep content" }
        });
        var service = CreateService();

        var (_, fileCount) = await service.GetArchiveInfoAsync(zipPath, CancellationToken.None);

        Assert.Equal(2, fileCount);
    }

    [Fact]
    public async Task GetArchiveInfoAsyncEmptyZipReturnsZero()
    {
        var zipPath = Path.Combine(_tempDir, "empty.zip");
        await using (await ZipFile.OpenAsync(zipPath, ZipArchiveMode.Create))
        {
            // empty archive
        }

        var service = CreateService();
        var (totalSize, fileCount) = await service.GetArchiveInfoAsync(zipPath, CancellationToken.None);

        Assert.Equal(0, fileCount);
        Assert.Equal(0, totalSize);
    }

    [Fact]
    public async Task GetArchiveInfoAsyncCancellationThrowsOperationCanceled()
    {
        var zipPath = CreateTestZip("cancel.zip",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "f.txt", "d" } });
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetArchiveInfoAsync(zipPath, cts.Token));
    }

    #endregion

    #region Transient Error Detection Tests

    [Theory]
    [InlineData(0x20, "The process cannot access the file because it is being used by another process.")]
    [InlineData(0x21, "The process cannot access the file because another process has locked a portion of the file.")]
    public void IsTransientIoErrorSharingAndLockViolationsReturnTrue(int hresult, string message)
    {
        var ex = new IOException(message, unchecked((int)(0x80070000u | (uint)hresult)));
        Assert.True(FileExtractorService.IsTransientIoError(ex));
    }

    [Fact]
    public void IsTransientIoErrorDiskFullReturnsFalse()
    {
        var ex = new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        Assert.False(FileExtractorService.IsTransientIoError(ex));
    }

    [Fact]
    public void IsTransientIoErrorEndOfStreamReturnsFalse()
    {
        Assert.False(FileExtractorService.IsTransientIoError(new EndOfStreamException()));
    }

    [Fact]
    public void IsTransientIoErrorNetworkMessageReturnsTrue()
    {
        var ex = new IOException("The network path was not found.");
        Assert.True(FileExtractorService.IsTransientIoError(ex));
    }

    #endregion

    #region ExtractArchiveAsync - Error Handling Tests

    [Fact]
    public async Task ExtractArchiveAsyncNonExistentFileThrows()
    {
        var service = CreateService();
        var nonExistent = Path.Combine(_tempDir, "does_not_exist.zip");

        await Assert.ThrowsAnyAsync<IOException>(() =>
            service.ExtractArchiveAsync(nonExistent, Path.Combine(_tempDir, "out"), CancellationToken.None));
    }

    [Fact]
    public async Task ExtractArchiveAsyncCorruptZipThrowsAndDoesNotLogWarningOrError()
    {
        var corruptPath = CreateCorruptZip("corrupt.zip");
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "extract_out");
        Directory.CreateDirectory(outDir);

        try
        {
            await service.ExtractArchiveAsync(corruptPath, outDir, CancellationToken.None);
        }
        catch
        {
            // Expected to throw
        }

        // Corrupt archives are user/input errors and must not be logged as warnings or errors
        Assert.DoesNotContain(_logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    [Fact]
    public async Task ExtractArchiveAsyncValidZipExtractsSuccessfully()
    {
        var zipPath = CreateTestZip("valid.zip", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "test.txt", "hello world" }
        });
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "extract_valid");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(outDir, "test.txt")));
        Assert.Equal("hello world", await File.ReadAllTextAsync(Path.Combine(outDir, "test.txt")));
    }

    [Fact]
    public async Task ExtractArchiveAsyncPasswordProtectedZipReturnsFalseAndLogsError()
    {
        // Create a password-protected ZIP using System.IO.Compression
        var zipPath = Path.Combine(_tempDir, "protected.zip");
        const string entryName = "secret.txt";

        // Create a minimal encrypted zip (PKWARE encryption)
        // SharpCompress will throw CryptographicException for encrypted zips
        // We'll create a zip with the encryption flag set
        CreateEncryptedZip(zipPath, entryName, "secret content", "password123");

        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "extract_protected");
        Directory.CreateDirectory(outDir);

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.False(result.Success);

        // Verify the error message was logged (contains "encrypted" or "password")
        Assert.True(
            _logger.HasMessage("encrypted") ||
            _logger.HasMessage("password-protected"));
    }

    [Fact]
    public async Task ExtractArchiveAsyncPasswordProtectedZipDoesNotLogWarningOrError()
    {
        var zipPath = Path.Combine(_tempDir, "protected2.zip");
        CreateEncryptedZip(zipPath, "secret.txt", "data", "pw");

        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "extract_protected2");
        Directory.CreateDirectory(outDir);

        await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        // Password-protected archives are user/input errors and must not be logged as warnings or errors
        Assert.DoesNotContain(_logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    #endregion

    #region ExtractArchiveAsync - Logging Tests

    [Fact]
    public async Task ExtractArchiveAsyncLogsExtractionStart()
    {
        var zipPath = CreateTestZip("logtest.zip",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "f.txt", "d" } });
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "extract_log");

        await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(_logger.HasMessage("Starting extraction"));
    }

    [Fact]
    public async Task ExtractArchiveAsyncLogsSuccess()
    {
        var zipPath = CreateTestZip("success.zip",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "f.txt", "d" } });
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "extract_success");

        await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(_logger.HasMessage("Successfully extracted"));
    }

    [Fact]
    public async Task ExtractArchiveAsyncCancellationLogsCancellation()
    {
        var zipPath = CreateTestZip("cancellog.zip",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "f.txt", "d" } });
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "extract_cancel");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        try
        {
            await service.ExtractArchiveAsync(zipPath, outDir, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        Assert.True(_logger.HasMessage("canceled"));
    }

    #endregion

    #region ExtractArchiveAsync - Zip Slip Prevention Tests

    [Fact]
    public async Task ExtractArchiveAsyncZipSlipAbsolutePathEntrySkipsEntry()
    {
        // Create a zip with a path traversal attempt
        var zipPath = Path.Combine(_tempDir, "zipslip.zip");
        await using (var archive = await ZipFile.OpenAsync(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../../../etc/passwd");
            await using var writer = new StreamWriter(await entry.OpenAsync());
            await writer.WriteAsync("malicious content");
        }

        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "extract_zipslip");

        // Should not throw, but skip the malicious entry
        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Contains(result.SkippedEntries, entry => entry.Contains("etc/passwd", StringComparison.Ordinal));
    }

    #endregion

    #region GetArchiveInfoAsync - Additional Archive Types and Failures

    [Fact]
    public async Task GetArchiveInfoAsyncTarReturnsCorrectCountAndSize()
    {
        var tarPath = CreateTestTar("info.tar", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "hello.txt", "hello tar" },
            { "sub/world.txt", "world tar" }
        });
        var service = CreateService();

        var (totalSize, fileCount) = await service.GetArchiveInfoAsync(tarPath, CancellationToken.None);

        Assert.Equal(2, fileCount);
        Assert.Equal("hello tar".Length + "world tar".Length, totalSize);
    }

    [Fact]
    public async Task GetArchiveInfoAsyncCountsEmptyFileWithZeroSize()
    {
        var zipPath = CreateTestZip("empty-file.zip", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "empty.txt", string.Empty },
            { "full.txt", "12345" }
        });
        var service = CreateService();

        var (totalSize, fileCount) = await service.GetArchiveInfoAsync(zipPath, CancellationToken.None);

        Assert.Equal(2, fileCount);
        Assert.Equal(5, totalSize);
    }

    [Fact]
    public async Task GetArchiveInfoAsyncOnlyDirectoryEntriesReturnsZero()
    {
        var zipPath = Path.Combine(_tempDir, "dirs-only.zip");
        await using (var archive = await ZipFile.OpenAsync(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("folder/");
            archive.CreateEntry("folder/nested/");
        }

        var service = CreateService();
        var (totalSize, fileCount) = await service.GetArchiveInfoAsync(zipPath, CancellationToken.None);

        Assert.Equal(0, fileCount);
        Assert.Equal(0, totalSize);
    }

    [Fact]
    public async Task GetArchiveInfoAsyncSumsSizesAcrossNestedDirectories()
    {
        var zipPath = CreateTestZip("nested-sizes.zip",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "a.txt", "1234567890" },
                { "sub/b.txt", "12345" },
                { "sub/deep/c.txt", "123" }
            });
        var service = CreateService();

        var (totalSize, fileCount) = await service.GetArchiveInfoAsync(zipPath, CancellationToken.None);

        Assert.Equal(3, fileCount);
        Assert.Equal(18, totalSize);
    }

    [Fact]
    public async Task GetArchiveInfoAsyncMissingFileThrowsAndLogsWarning()
    {
        var service = CreateService();
        var missing = Path.Combine(_tempDir, "missing-info.zip");

        await Assert.ThrowsAnyAsync<Exception>(() => service.GetArchiveInfoAsync(missing, CancellationToken.None));

        Assert.True(_logger.HasMessage(LogEventLevel.Warning, "Failed to read archive info"));
    }

    [Fact]
    public async Task GetArchiveInfoAsyncPlainTextFileThrowsAndLogsWarning()
    {
        var path = Path.Combine(_tempDir, "plain-info.zip");
        await File.WriteAllTextAsync(path, "this is not an archive");
        var service = CreateService();

        await Assert.ThrowsAnyAsync<Exception>(() => service.GetArchiveInfoAsync(path, CancellationToken.None));

        Assert.True(_logger.HasMessage(LogEventLevel.Warning, "Failed to read archive info"));
    }

    [Fact]
    public async Task GetArchiveInfoAsyncCorruptZipThrowsAndLogsWarning()
    {
        var corruptPath = CreateCorruptZip("corrupt-info.zip");
        var service = CreateService();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.GetArchiveInfoAsync(corruptPath, CancellationToken.None));

        Assert.True(_logger.HasMessage(LogEventLevel.Warning, "Failed to read archive info"));
    }

    [Fact]
    public async Task GetArchiveInfoAsyncCancellationLogsDebugCanceled()
    {
        var zipPath = CreateTestZip("cancel-info.zip",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "f.txt", "d" } });
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetArchiveInfoAsync(zipPath, cts.Token));

        Assert.True(_logger.HasMessage(LogEventLevel.Debug, "canceled"));
    }

    #endregion

    #region Transient Error Detection - Additional Tests

    [Fact]
    public void IsTransientIoErrorDiskFullMessageReturnsFalse()
    {
        var ex = new IOException("There is not enough disk space on the drive.");
        Assert.False(FileExtractorService.IsTransientIoError(ex));
    }

    [Fact]
    public void IsTransientIoErrorDiskFullHResultTakesPrecedenceOverNetworkMessage()
    {
        var ex = new IOException("The network path was not found.", unchecked((int)0x80070070));
        Assert.False(FileExtractorService.IsTransientIoError(ex));
    }

    [Fact]
    public void IsTransientIoErrorDeviceNotReadyReturnsFalse()
    {
        Assert.False(FileExtractorService.IsTransientIoError(new IOException("The device is not ready.")));
    }

    [Fact]
    public void IsTransientIoErrorDeviceIoErrorReturnsFalse()
    {
        var ex = new IOException("The request could not be performed because of an I/O device error.",
            unchecked((int)0x8007045D));
        Assert.False(FileExtractorService.IsTransientIoError(ex));
    }

    [Fact]
    public void IsTransientIoErrorDeviceHardwareErrorReturnsFalse()
    {
        var ex = new IOException("The request failed due to a fatal device hardware error.",
            unchecked((int)0x800701E3));
        Assert.False(FileExtractorService.IsTransientIoError(ex));
    }

    [Fact]
    public void IsTransientIoErrorLocalizedDeviceHardwareErrorReturnsFalse()
    {
        // The Italian message from bug reports must not fall through to the generic
        // "device" network pattern and be retried as a network glitch.
        var ex = new IOException("Richiesta non riuscita a causa di un errore hardware del dispositivo irreversibile.");
        Assert.False(FileExtractorService.IsTransientIoError(ex));
    }

    [Fact]
    public void IsTransientIoErrorGenericIoReturnsFalse()
    {
        Assert.False(FileExtractorService.IsTransientIoError(new IOException("Something went wrong.")));
    }

    [Fact]
    public void IsTransientIoErrorNetworkMessageIsCaseInsensitive()
    {
        Assert.True(FileExtractorService.IsTransientIoError(new IOException("THE NETWORK PATH WAS NOT FOUND.")));
    }

    [Theory]
    [InlineData("Das Netzwerk ist nicht mehr verfügbar.")]
    [InlineData("Le réseau n'est plus disponible.")]
    [InlineData("La red no está disponible.")]
    [InlineData("La rete non è disponibile.")]
    public void IsTransientIoErrorLocalizedNetworkMessagesReturnTrue(string message)
    {
        Assert.True(FileExtractorService.IsTransientIoError(new IOException(message)));
    }

    [Fact]
    public void IsTransientIoErrorSemaphoreTimeoutReturnsTrue()
    {
        var ex = new IOException("The semaphore timeout period has expired.");
        Assert.True(FileExtractorService.IsTransientIoError(ex));
    }

    [Fact]
    public void IsTransientIoErrorWrappedNetworkErrorReturnsTrue()
    {
        var ex = new IOException("outer", new IOException("The network name is no longer available."));
        Assert.True(FileExtractorService.IsTransientIoError(ex));
    }

    #endregion

    #region ExtractArchiveAsync - Content and Structure Tests

    [Fact]
    public async Task ExtractArchiveAsyncMultipleFilesExtractsAllWithContent()
    {
        var zipPath = CreateTestZip("multi.zip", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "one.txt", "first" },
            { "two.txt", "second" },
            { "three.txt", "third" }
        });
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "multi_out");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(outDir, "one.txt")));
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(outDir, "two.txt")));
        Assert.Equal("third", await File.ReadAllTextAsync(Path.Combine(outDir, "three.txt")));
    }

    [Fact]
    public async Task ExtractArchiveAsyncNestedDirectoriesCreatesFolderStructure()
    {
        var zipPath = CreateTestZip("nested-extract.zip",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "sub/deep/file.txt", "deep content" }
            });
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "nested_out");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(Directory.Exists(Path.Combine(outDir, "sub", "deep")));
        Assert.Equal("deep content", await File.ReadAllTextAsync(Path.Combine(outDir, "sub", "deep", "file.txt")));
    }

    [Fact]
    public async Task ExtractArchiveAsyncDeeplyNestedPathIsCreated()
    {
        var zipPath = CreateTestZipOrdered("deep-nested.zip", ("a/b/c/d/e/f.txt", "deep"));
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "deep_nested_out");

        await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.Equal("deep", await File.ReadAllTextAsync(Path.Combine(outDir, "a", "b", "c", "d", "e", "f.txt")));
    }

    [Fact]
    public async Task ExtractArchiveAsyncEmptyZipSucceedsWithNoSkippedEntries()
    {
        var zipPath = Path.Combine(_tempDir, "empty-extract.zip");
        await using (await ZipFile.OpenAsync(zipPath, ZipArchiveMode.Create))
        {
            // empty archive
        }

        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "empty_out");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.SkippedEntries);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public async Task ExtractArchiveAsyncCreatesDestinationDirectoryAutomatically()
    {
        var zipPath = CreateTestZip("mkdir.zip",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "f.txt", "d" } });
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "auto_created", "nested");
        Assert.False(Directory.Exists(outDir));

        await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(Directory.Exists(outDir));
    }

    [Fact]
    public async Task ExtractArchiveAsyncOverwritesExistingDestinationFile()
    {
        var zipPath = CreateTestZip("overwrite.zip",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "f.txt", "new content" } });
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "overwrite_out");
        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, "f.txt"), "old content");

        await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.Equal("new content", await File.ReadAllTextAsync(Path.Combine(outDir, "f.txt")));
    }

    [Fact]
    public async Task ExtractArchiveAsyncPreservesEmptyFiles()
    {
        var zipPath = CreateTestZip("empty-files.zip",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "empty.txt", string.Empty } });
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "empty_files_out");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(outDir, "empty.txt")));
        Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(outDir, "empty.txt")));
    }

    [Fact]
    public async Task ExtractArchiveAsyncExtractsUnicodeFileNames()
    {
        var zipPath = CreateTestZip("unicode.zip",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "日本語.txt", "unicode content" } });
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "unicode_out");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("unicode content", await File.ReadAllTextAsync(Path.Combine(outDir, "日本語.txt")));
    }

    [Fact]
    public async Task ExtractArchiveAsyncExtractsNamesWithSpaces()
    {
        var zipPath = CreateTestZip("spaces.zip",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "my file name.txt", "spaced" } });
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "spaces_out");

        await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.Equal("spaced", await File.ReadAllTextAsync(Path.Combine(outDir, "my file name.txt")));
    }

    [Fact]
    public async Task ExtractArchiveAsyncTarExtractsFiles()
    {
        var tarPath = CreateTestTar("extract.tar", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "sub/hello.txt", "hello tar" }
        });
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "tar_out");

        var result = await service.ExtractArchiveAsync(tarPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("hello tar", await File.ReadAllTextAsync(Path.Combine(outDir, "sub", "hello.txt")));
    }

    [Fact]
    public async Task ExtractArchiveAsyncDuplicateEntryNameKeepsLastContent()
    {
        var zipPath = Path.Combine(_tempDir, "duplicate.zip");
        await using (var archive = await ZipFile.OpenAsync(zipPath, ZipArchiveMode.Create))
        {
            foreach (var content in new[] { "first version", "second version" })
            {
                var entry = archive.CreateEntry("dup.txt");
                await using var writer = new StreamWriter(await entry.OpenAsync());
                await writer.WriteAsync(content);
            }
        }

        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "duplicate_out");

        await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.Equal("second version", await File.ReadAllTextAsync(Path.Combine(outDir, "dup.txt")));
    }

    [Fact]
    public async Task ExtractArchiveAsyncDestinationIsExistingFileThrowsAndLogsError()
    {
        var zipPath = CreateTestZip("dest-file.zip",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "f.txt", "d" } });
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "dest_is_file");
        await File.WriteAllTextAsync(outDir, "i am a file");

        await Assert.ThrowsAnyAsync<IOException>(() =>
            service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None));

        Assert.True(_logger.HasMessage(LogEventLevel.Error, "Error extracting"));
    }

    #endregion

    #region ExtractArchiveAsync - ISO Selection Tests

    [Fact]
    public async Task ExtractArchiveAsyncExtractsFirstIsoAndSkipsAdditionalIsos()
    {
        var zipPath = CreateTestZipOrdered("multi-iso.zip",
            ("disc1.iso", "first iso"),
            ("disc2.iso", "second iso"),
            ("readme.txt", "notes"));
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "multi_iso_out");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(outDir, "disc1.iso")));
        Assert.False(File.Exists(Path.Combine(outDir, "disc2.iso")));
        Assert.True(File.Exists(Path.Combine(outDir, "readme.txt")));
        Assert.Contains("disc2.iso", result.SkippedEntries, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("GAME.ISO", "EXTRA.Iso")]
    [InlineData("game.iso", "extra.ISO")]
    public async Task ExtractArchiveAsyncIsoExtensionMatchIsCaseInsensitive(string firstName, string secondName)
    {
        var zipPath = CreateTestZipOrdered($"case-{firstName}.zip",
            (firstName, "first iso"),
            (secondName, "second iso"));
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, $"case_iso_{firstName}");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(outDir, firstName)));
        Assert.False(File.Exists(Path.Combine(outDir, secondName)));
        Assert.Contains(secondName, result.SkippedEntries, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractArchiveAsyncNonIsoFilesAreExtractedAlongsideIso()
    {
        var zipPath = CreateTestZipOrdered("with-iso.zip",
            ("game.iso", "iso content"),
            ("notes.txt", "readme"));
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "with_iso_out");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(outDir, "game.iso")));
        Assert.True(File.Exists(Path.Combine(outDir, "notes.txt")));
        Assert.Empty(result.SkippedEntries);
    }

    [Fact]
    public async Task ExtractArchiveAsyncFileEndingInIsoTxtIsNotTreatedAsIso()
    {
        var zipPath = CreateTestZipOrdered("iso-txt.zip",
            ("game.iso.txt", "text"),
            ("game.iso", "iso"));
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "iso_txt_out");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(outDir, "game.iso.txt")));
        Assert.True(File.Exists(Path.Combine(outDir, "game.iso")));
        Assert.Empty(result.SkippedEntries);
    }

    [Fact]
    public async Task ExtractArchiveAsyncIsoInSubdirectoryIsSelected()
    {
        var zipPath = CreateTestZipOrdered("sub-iso.zip",
            ("discs/disc1.iso", "first iso"),
            ("discs/disc2.iso", "second iso"));
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "sub_iso_out");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(outDir, "discs", "disc1.iso")));
        Assert.False(File.Exists(Path.Combine(outDir, "discs", "disc2.iso")));
        Assert.Contains("discs/disc2.iso", result.SkippedEntries, StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    #region ExtractArchiveAsync - Additional Zip Slip Tests

    [Fact]
    public async Task ExtractArchiveAsyncSkipsTraversalSegmentInMiddle()
    {
        var zipPath = CreateTestZipOrdered("traversal-mid.zip", ("sub/../evil.txt", "malicious"));
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "traversal_mid_out");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(outDir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "evil.txt")));
        Assert.Contains("sub/../evil.txt", result.SkippedEntries, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractArchiveAsyncSkipsAbsolutePathEntry()
    {
        var zipPath = CreateTestZipOrdered("absolute.zip", ("/tmp/evil.txt", "malicious"));
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "absolute_out");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(outDir, "tmp", "evil.txt")));
        Assert.Contains("/tmp/evil.txt", result.SkippedEntries, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractArchiveAsyncDoesNotWriteOutsideExtractionDirectory()
    {
        var zipPath = CreateTestZipOrdered("outside.zip", ("../../outside.txt", "malicious"));
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "outside_out");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(_tempDir, "outside.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "outside.txt")));
        Assert.Single(result.SkippedEntries);
    }

    [Fact]
    public async Task ExtractArchiveAsyncSkipsEntryNamedExactlyDotDot()
    {
        var zipPath = CreateTestZipOrdered("dotdot.zip", ("..", "malicious"));
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "dotdot_out");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("..", result.SkippedEntries, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractArchiveAsyncReportsUnsafeEntriesAlongsideIsoSkips()
    {
        var zipPath = CreateTestZipOrdered("mixed-skips.zip",
            ("first.iso", "iso"),
            ("second.iso", "iso2"),
            ("../evil.txt", "bad"));
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "mixed_skips_out");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.Equal(2, result.SkippedEntries.Count);
        Assert.Contains("second.iso", result.SkippedEntries, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("../evil.txt", result.SkippedEntries, StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    #region ExtractArchiveAsync - Additional Logging and Failure Tests

    [Fact]
    public async Task ExtractArchiveAsyncLogsArchiveFormatAndFileCount()
    {
        var zipPath = CreateTestZip("format-log.zip", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "a.txt", "1" },
            { "b.txt", "2" }
        });
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "format_log_out");

        await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(_logger.HasMessage("Archive format:"));
        Assert.True(_logger.HasMessage("Files to extract: 2"));
    }

    [Fact]
    public async Task ExtractArchiveAsyncLogsExtractionTargetPath()
    {
        var zipPath = CreateTestZip("target-log.zip",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "f.txt", "d" } });
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "target_log_out");

        await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(_logger.HasMessage("Extraction target:"));
        Assert.True(_logger.HasMessage(outDir));
    }

    [Fact]
    public async Task ExtractArchiveAsyncCorruptZipThrowsIoExceptionWithUnsupportedMessage()
    {
        var corruptPath = CreateCorruptZip("corrupt-strong.zip");
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "corrupt_strong_out");

        var exception = await Assert.ThrowsAnyAsync<IOException>(() =>
            service.ExtractArchiveAsync(corruptPath, outDir, CancellationToken.None));

        Assert.Contains("invalid, corrupted, or in an unsupported format", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractArchiveAsyncPlainTextZipThrowsIoExceptionAndLogsInformation()
    {
        var path = Path.Combine(_tempDir, "plain-archive.zip");
        await File.WriteAllTextAsync(path, "definitely not a zip");
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "plain_archive_out");

        await Assert.ThrowsAnyAsync<IOException>(() =>
            service.ExtractArchiveAsync(path, outDir, CancellationToken.None));

        Assert.True(_logger.HasMessage("invalid, corrupted, or in an unsupported format"));
    }

    [Fact]
    public async Task ExtractArchiveAsyncCancellationThrowsOperationCanceled()
    {
        var zipPath = CreateTestZip("cancel-type.zip",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "f.txt", "d" } });
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "cancel_type_out");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExtractArchiveAsync(zipPath, outDir, cts.Token));
    }

    [Fact]
    public async Task ExtractArchiveAsyncMissingFileThrowsAndLogsError()
    {
        var service = CreateService();
        var missing = Path.Combine(_tempDir, "missing-archive.zip");
        var outDir = Path.Combine(_tempDir, "missing_archive_out");

        await Assert.ThrowsAnyAsync<IOException>(() =>
            service.ExtractArchiveAsync(missing, outDir, CancellationToken.None));

        Assert.True(_logger.HasMessage(LogEventLevel.Error, "Error extracting"));
    }

    [Fact]
    public async Task ExtractArchiveAsyncPasswordProtectedLogsPasswordProtectedMessage()
    {
        var zipPath = Path.Combine(_tempDir, "protected-msg.zip");
        CreateEncryptedZip(zipPath, "secret.txt", "data", "pw");
        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "protected_msg_out");
        Directory.CreateDirectory(outDir);

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(_logger.HasMessage("password-protected"));
    }

    [Fact]
    public async Task ExtractArchiveAsyncEmptyZipLogsSuccess()
    {
        var zipPath = Path.Combine(_tempDir, "empty-success.zip");
        await using (await ZipFile.OpenAsync(zipPath, ZipArchiveMode.Create))
        {
            // empty archive
        }

        var service = CreateService();
        var outDir = Path.Combine(_tempDir, "empty_success_out");

        var result = await service.ExtractArchiveAsync(zipPath, outDir, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(_logger.HasMessage("Successfully extracted"));
    }

    #endregion

    #region Helper Methods

    private string CreateTestZipOrdered(string zipName, params (string Name, string Content)[] entries)
    {
        var zipPath = Path.Combine(_tempDir, zipName);
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return zipPath;
    }

    private string CreateTestTar(string tarName, Dictionary<string, string> entries)
    {
        var tarPath = Path.Combine(_tempDir, tarName);
        using var fs = File.Create(tarPath);
        using var writer = new TarWriter(fs, TarEntryFormat.Pax);
        foreach (var (name, content) in entries)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
            };
            writer.WriteEntry(entry);
        }

        return tarPath;
    }

    private static void CreateEncryptedZip(string zipPath, string entryName, string content, string _)
    {
        // Create a ZIP file with PKWARE traditional encryption
        // This is the simplest way to create an encrypted zip that SharpCompress will detect
        using var fs = File.Create(zipPath);
        using var bw = new BinaryWriter(fs);

        // Local file header
        bw.Write(0x04034B50); // signature
        bw.Write((ushort)20); // version needed
        bw.Write((ushort)1); // general purpose bit flag (bit 0 = encrypted)
        bw.Write((ushort)0); // compression method (stored)
        bw.Write((ushort)0); // last mod time
        bw.Write((ushort)0); // last mod date
        bw.Write((uint)0); // crc32
        bw.Write((uint)(content.Length + 12)); // compressed size (content + encryption header)
        bw.Write((uint)(content.Length + 12)); // uncompressed size
        bw.Write((ushort)entryName.Length);
        bw.Write((ushort)0); // extra field length

        var nameBytes = Encoding.UTF8.GetBytes(entryName);
        bw.Write(nameBytes);

        // Encryption header (12 bytes)
        var encHeader = new byte[12];
        new Random(42).NextBytes(encHeader);
        bw.Write(encHeader);

        // Content (encrypted - just raw bytes for testing)
        var contentBytes = Encoding.UTF8.GetBytes(content);
        bw.Write(contentBytes);

        // Central directory
        var cdOffset = (uint)fs.Position;
        bw.Write(0x02014B50); // central directory signature
        bw.Write((ushort)20); // version made by
        bw.Write((ushort)20); // version needed
        bw.Write((ushort)1); // general purpose bit flag (encrypted)
        bw.Write((ushort)0); // compression method
        bw.Write((ushort)0); // last mod time
        bw.Write((ushort)0); // last mod date
        bw.Write((uint)0); // crc32
        bw.Write((uint)(content.Length + 12)); // compressed size
        bw.Write((uint)(content.Length + 12)); // uncompressed size
        bw.Write((ushort)nameBytes.Length);
        bw.Write((ushort)0); // extra field length
        bw.Write((ushort)0); // file comment length
        bw.Write((ushort)0); // disk number start
        bw.Write((ushort)0); // internal file attributes
        bw.Write((uint)0); // external file attributes
        bw.Write((uint)0); // relative offset of local header
        bw.Write(nameBytes);

        var cdSize = (uint)(fs.Position - cdOffset);

        // End of central directory
        bw.Write(0x06054B50); // end of central directory signature
        bw.Write((ushort)0); // disk number
        bw.Write((ushort)0); // disk number with central directory
        bw.Write((ushort)1); // total entries on disk
        bw.Write((ushort)1); // total entries
        bw.Write(cdSize); // central directory size
        bw.Write(cdOffset); // central directory offset
        bw.Write((ushort)0); // comment length
    }

    #endregion

    #region Cloud File Hydration Tests

    [Fact]
    public async Task EnsureCloudFileHydratedAsyncCanceledTokenPropagatesCancellation()
    {
        var file = Path.Combine(_tempDir, "cloud.bin");
        await File.WriteAllTextAsync(file, "data");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = CreateService();
        var method = typeof(FileExtractorService).GetMethod("EnsureCloudFileHydratedAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var task = (Task<bool>)method.Invoke(service, [file, cts.Token])!;

        // Cancellation must not be misreported as a hydration failure (which would surface
        // as a bogus "cloud file provider is not running" error).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    #endregion
}