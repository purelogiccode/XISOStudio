using XISOSharp;
using XISOSharp.Models;
using XISOStudio.Services;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests source-to-output tree comparison in <c>ConversionValidationService</c>.</summary>
public sealed class ConversionValidationServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(),
        $"ConversionValidationServiceTests_{Guid.NewGuid():N}");

    private readonly ConversionValidationService _service;

    public ConversionValidationServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
        _service = new ConversionValidationService(new TestLogger().Logger);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
        }
        catch
        {
            // ignored
        }
    }

    private string CreateSource(string name, bool includeSystemUpdate = false, string payload = "hello")
    {
        var sourceDirectory = Path.Combine(_tempRoot, name + "-source");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "default.xbe"), "fake xbe content");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "media"));
        File.WriteAllText(Path.Combine(sourceDirectory, "media", "data.bin"), payload);

        if (includeSystemUpdate)
        {
            var updateDirectory = Directory.CreateDirectory(Path.Combine(sourceDirectory, "$SystemUpdate")).FullName;
            File.WriteAllText(Path.Combine(updateDirectory, "system.bin"), "update payload");
        }

        var isoPath = Path.Combine(_tempRoot, name + ".iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(sourceDirectory, isoPath));
        return isoPath;
    }

    private string PackFrom(string name, Action<string> populate)
    {
        var directory = Path.Combine(_tempRoot, name + "-tree");
        Directory.CreateDirectory(directory);
        populate(directory);

        var isoPath = Path.Combine(_tempRoot, name + ".iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(directory, isoPath));
        return isoPath;
    }

    [Fact]
    public void IdenticalTreesPassWithChecksums()
    {
        var source = CreateSource("identical");
        var output = PackFrom("identical-output", static directory =>
        {
            File.WriteAllText(Path.Combine(directory, "default.xbe"), "fake xbe content");
            Directory.CreateDirectory(Path.Combine(directory, "media"));
            File.WriteAllText(Path.Combine(directory, "media", "data.bin"), "hello");
        });

        var result = _service.ValidateConversion(source, output, verifyChecksums: true);

        Assert.True(result.Passed);
        Assert.Empty(result.Issues);
        Assert.Equal(result.SourceFileCount, result.OutputFileCount);
    }

    [Fact]
    public void MissingFileFails()
    {
        var source = CreateSource("missing");
        var output = PackFrom("missing-output", static directory =>
        {
            File.WriteAllText(Path.Combine(directory, "default.xbe"), "fake xbe content");
        });

        var result = _service.ValidateConversion(source, output, verifyChecksums: false);

        Assert.False(result.Passed);
        Assert.Contains(result.Issues, static issue => issue.Type == ValidationIssueType.MissingInOutput);
    }

    [Fact]
    public void DifferentContentOfTheSameSizeFailsWithChecksums()
    {
        var source = CreateSource("content", payload: "aaaa");
        var output = PackFrom("content-output", static directory =>
        {
            File.WriteAllText(Path.Combine(directory, "default.xbe"), "fake xbe content");
            Directory.CreateDirectory(Path.Combine(directory, "media"));
            File.WriteAllText(Path.Combine(directory, "media", "data.bin"), "bbbb");
        });

        var result = _service.ValidateConversion(source, output, verifyChecksums: true);

        Assert.False(result.Passed);
        Assert.Contains(result.Issues, static issue => issue.Type == ValidationIssueType.ChecksumMismatch);
    }

    [Fact]
    public void RemovedSystemUpdateFolderIsIgnoredWhenRequested()
    {
        var source = CreateSource("systemupdate", includeSystemUpdate: true);
        var output = PackFrom("systemupdate-output", static directory =>
        {
            File.WriteAllText(Path.Combine(directory, "default.xbe"), "fake xbe content");
            Directory.CreateDirectory(Path.Combine(directory, "media"));
            File.WriteAllText(Path.Combine(directory, "media", "data.bin"), "hello");
        });

        var strict = _service.ValidateConversion(source, output, verifyChecksums: false);
        Assert.False(strict.Passed);
        Assert.Contains(strict.Issues, static issue =>
            issue.Path.Contains("$SystemUpdate", StringComparison.OrdinalIgnoreCase));

        var filtered = _service.ValidateConversion(source, output, verifyChecksums: false,
            ignoreSystemUpdate: true);
        Assert.True(filtered.Passed);
        Assert.Empty(filtered.Issues);
    }
}
