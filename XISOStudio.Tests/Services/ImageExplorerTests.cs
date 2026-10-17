using XISOStudio.Models;
using XISOStudio.Services;
using XISOSharp;
using Xunit;
using ZArchiveSharp;

namespace XISOStudio.Tests.Services;

/// <summary>Tests listing and extracting entries from XISO, CSO, ZAR, and CHD images via <c>ImageExplorerFactory</c>.</summary>
public sealed class ImageExplorerTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"ImageExplorerTests_{Guid.NewGuid():N}");

    public ImageExplorerTests()
    {
        Directory.CreateDirectory(_tempRoot);
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

    private string CreateXiso(string name = "game.iso")
    {
        var sourceDir = Path.Combine(_tempRoot, "source");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "default.xbe"), "fake xbe content");
        var mediaDir = Path.Combine(sourceDir, "media");
        Directory.CreateDirectory(mediaDir);
        File.WriteAllBytes(Path.Combine(mediaDir, "data.bin"), [1, 2, 3, 4, 5]);

        var isoPath = Path.Combine(_tempRoot, name);
        Assert.Equal(0, XisoWriter.PackFromDirectory(sourceDir, isoPath));
        return isoPath;
    }

    private string CreateCiso()
    {
        var isoPath = CreateXiso();
        var csoPath = Path.Combine(_tempRoot, "game.cso");
        Assert.Equal(0, CisoWriter.CompressToCso(isoPath, csoPath));
        return csoPath;
    }

    private string CreateZar()
    {
        var isoPath = CreateXiso();
        var zarPath = Path.Combine(_tempRoot, "game.zar");
        Assert.True(XisoZarchive.CreateZar(isoPath, zarPath, quiet: true));
        return zarPath;
    }

    private string CreateChd()
    {
        var isoPath = CreateXiso();
        var chdPath = Path.Combine(_tempRoot, "game.chd");
        return ChdTestHelper.CreateDvdChd(isoPath, chdPath);
    }

    private void AssertImageExplorerWorks(string imagePath)
    {
        using var explorer = ImageExplorerFactory.Open(imagePath);

        var root = explorer.ListChildren("/");
        Assert.Contains(root, static e => e is { Name: "default.xbe", IsDirectory: false, Size: > 0 });
        Assert.Contains(root, static e => e is { Name: "media", IsDirectory: true, Size: 0 });

        var media = explorer.ListChildren("/media");
        var data = Assert.Single(media);
        Assert.Equal("data.bin", data.Name);
        Assert.False(data.IsDirectory);
        Assert.Equal(5, data.Size);
        Assert.Equal("/media/data.bin", data.FullPath);

        var dest = Path.Combine(_tempRoot, "out", "data.bin");
        explorer.CopyOut("/media/data.bin", dest);
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(dest));
    }

    [Fact]
    public void XisoImageExplorerListsAndCopiesFiles()
    {
        AssertImageExplorerWorks(CreateXiso());
    }

    [Fact]
    public void XisoImageExplorerOpensWhileImageIsHeldOpenForWriting()
    {
        // Reproduces the reported "the process cannot access the file because it is
        // being used by another process" failure: another process (sync client,
        // antivirus, in-progress copy) keeps the image open for writing and only
        // shares reads. The explorer is a pure reader and must coexist with such
        // writers; opening with FileShare.Read only fails with a sharing violation.
        var isoPath = CreateXiso();
        using var writer = new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        using var explorer = ImageExplorerFactory.Open(isoPath);

        var root = explorer.ListChildren("/");
        Assert.Contains(root, static e => e is { Name: "default.xbe", IsDirectory: false });

        var dest = Path.Combine(_tempRoot, "in-use-out", "data.bin");
        explorer.CopyOut("/media/data.bin", dest);
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(dest));
    }

    [Fact]
    public void XisoImageExplorerOpensSplitCisoWhilePartIsHeldOpenForWriting()
    {
        // Report follow-up: the image share mode must reach split *.1.cso part sets
        // too (XISOSharp 1.4.3). A writer (sync client, in-progress copy) keeps the
        // first part open for write and shares reads only; with XISOSharp 1.4.2 the
        // part files were opened read-shared regardless of the caller's request, so
        // this failed with a sharing violation on Windows.
        var sourceDir = Path.Combine(_tempRoot, "split-source");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "default.xbe"), "fake xbe content");
        var blob = new byte[300_000];
        new Random(7).NextBytes(blob);
        File.WriteAllBytes(Path.Combine(sourceDir, "blob.bin"), blob);
        var isoPath = Path.Combine(_tempRoot, "split-game.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(sourceDir, isoPath));

        var csoPath = Path.Combine(_tempRoot, "split-game.cso");
        Assert.Equal(0, CisoWriter.CompressToCso(isoPath, csoPath, level: 9, splitBytes: 64 * 1024));
        var firstPart = Path.Combine(_tempRoot, "split-game.1.cso");
        Assert.True(File.Exists(firstPart));
        Assert.True(File.Exists(Path.Combine(_tempRoot, "split-game.2.cso")), "expected at least two parts");

        using var writer = new FileStream(firstPart, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var explorer = ImageExplorerFactory.Open(firstPart);

        var root = explorer.ListChildren("/");
        Assert.Contains(root, static e => e is { Name: "default.xbe", IsDirectory: false });

        var dest = Path.Combine(_tempRoot, "split-out", "blob.bin");
        explorer.CopyOut("/blob.bin", dest);
        Assert.Equal(blob, File.ReadAllBytes(dest));
    }

    [Fact]
    public void CisoImageExplorerListsAndCopiesFiles()
    {
        AssertImageExplorerWorks(CreateCiso());
    }

    [Fact]
    public void ZarImageExplorerListsAndCopiesFiles()
    {
        AssertImageExplorerWorks(CreateZar());
    }

    [Fact]
    public void ChdImageExplorerListsAndCopiesFiles()
    {
        AssertImageExplorerWorks(CreateChd());
    }

    [Fact]
    public void ChdImageExplorerCopiesDirectoriesRecursively()
    {
        var chdPath = CreateChd();
        using var explorer = ImageExplorerFactory.Open(chdPath);

        var dest = Path.Combine(_tempRoot, "media-out");
        explorer.CopyOut("/media", dest);

        Assert.True(File.Exists(Path.Combine(dest, "data.bin")));
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(Path.Combine(dest, "data.bin")));
    }

    [Fact]
    public void ChdImageExplorerMissingPathThrows()
    {
        var chdPath = CreateChd();
        using var explorer = ImageExplorerFactory.Open(chdPath);

        Assert.Throws<InvalidDataException>(() => explorer.ListChildren("/missing"));
        Assert.Throws<InvalidDataException>(() => explorer.CopyOut("/missing", Path.Combine(_tempRoot, "x")));
    }

    [Fact]
    public void ChdImageExplorerInvalidChdThrows()
    {
        var badChd = Path.Combine(_tempRoot, "bad.chd");
        File.WriteAllText(badChd, "not a chd");

        Assert.Throws<InvalidDataException>(() => ImageExplorerFactory.Open(badChd));
    }

    [Fact]
    public void ChdImageExplorerNonDvdChdThrows()
    {
        var isoPath = CreateXiso();
        var rawChd = Path.Combine(_tempRoot, "raw.chd");
        ChdTestHelper.CreateRawChd(isoPath, rawChd);

        Assert.Throws<InvalidDataException>(() => ImageExplorerFactory.Open(rawChd));
    }

    [Fact]
    public void ZarImageExplorerCopiesDirectoriesRecursively()
    {
        var zarPath = CreateZar();
        using var explorer = ImageExplorerFactory.Open(zarPath);

        var dest = Path.Combine(_tempRoot, "media-out");
        explorer.CopyOut("/media", dest);

        Assert.True(File.Exists(Path.Combine(dest, "data.bin")));
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(Path.Combine(dest, "data.bin")));
    }

    [Fact]
    public void ZarImageExplorerMissingPathThrows()
    {
        var zarPath = CreateZar();
        using var explorer = ImageExplorerFactory.Open(zarPath);

        Assert.Throws<InvalidDataException>(() => explorer.ListChildren("/missing"));
        Assert.Throws<InvalidDataException>(() => explorer.CopyOut("/missing", Path.Combine(_tempRoot, "x")));
    }

    [Fact]
    public void ZarImageExplorerInvalidArchiveThrows()
    {
        var badZar = Path.Combine(_tempRoot, "bad.zar");
        File.WriteAllText(badZar, "not a zar");

        Assert.Throws<InvalidDataException>(() => ImageExplorerFactory.Open(badZar));
    }

    [Theory]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("media\\sub/", "/media/sub")]
    [InlineData("media", "/media")]
    public void ImagePathsNormalizeReturnsCanonicalPath(string input, string expected)
    {
        Assert.Equal(expected, ImagePaths.Normalize(input));
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/media", "/")]
    [InlineData("/media/sub", "/media")]
    public void ImagePathsGetParentReturnsParent(string input, string expected)
    {
        Assert.Equal(expected, ImagePaths.GetParent(input));
    }

    private static void AssertRootListing(string imagePath)
    {
        using var explorer = ImageExplorerFactory.Open(imagePath);

        var root = explorer.ListChildren("/");
        Assert.Contains(root,
            static e => e is { Name: "default.xbe", FullPath: "/default.xbe", IsDirectory: false, Size: > 0 });
        Assert.Contains(root,
            static e => e is { Name: "media", FullPath: "/media", IsDirectory: true, Size: 0 });
    }

    private static void AssertNestedListing(string imagePath)
    {
        using var explorer = ImageExplorerFactory.Open(imagePath);

        var media = explorer.ListChildren("/media");
        var data = Assert.Single(media);
        Assert.Equal(new ImageEntry("data.bin", "/media/data.bin", false, 5), data);
    }

    private void AssertCopyOutOverwritesExistingFile(string imagePath)
    {
        using var explorer = ImageExplorerFactory.Open(imagePath);

        var dest = Path.Combine(_tempRoot, "overwrite", "data.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, "stale content");

        explorer.CopyOut("/media/data.bin", dest);

        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(dest));
    }

    private void AssertCopyOutCreatesDestinationDirectories(string imagePath)
    {
        using var explorer = ImageExplorerFactory.Open(imagePath);

        var dest = Path.Combine(_tempRoot, "created", "deeper", "data.bin");
        explorer.CopyOut("/media/data.bin", dest);

        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(dest));
    }

    private void AssertCaseInsensitivePaths(string imagePath)
    {
        using var explorer = ImageExplorerFactory.Open(imagePath);

        var media = explorer.ListChildren("/MEDIA");
        Assert.Single(media);

        var dest = Path.Combine(_tempRoot, "case", "DATA.BIN");
        explorer.CopyOut("/Media/DATA.BIN", dest);

        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(dest));
    }

    private void AssertPathTraversalIsRejected(string imagePath)
    {
        using var explorer = ImageExplorerFactory.Open(imagePath);

        Assert.Throws<InvalidDataException>(() => explorer.ListChildren("/../"));

        var escaped = Path.Combine(_tempRoot, "escaped.txt");
        Assert.Throws<InvalidDataException>(() => explorer.CopyOut("/../escaped.txt", escaped));
        Assert.False(File.Exists(escaped));
    }

    private static void AssertEmptyPathListsRoot(string imagePath)
    {
        using var explorer = ImageExplorerFactory.Open(imagePath);

        var root = explorer.ListChildren("");
        Assert.Contains(root, static e => string.Equals(e.Name, "default.xbe", StringComparison.Ordinal));
        var nullPath = explorer.ListChildren(null!);
        Assert.Equal(root.Count, nullPath.Count);
    }

    private static void AssertDisposeIsIdempotent(string imagePath)
    {
        var explorer = ImageExplorerFactory.Open(imagePath);
        explorer.Dispose();
        explorer.Dispose();
    }

    private static void AssertDisposeReleasesFileHandle(string imagePath)
    {
        var explorer = ImageExplorerFactory.Open(imagePath);
        explorer.Dispose();

        File.Delete(imagePath);
        Assert.False(File.Exists(imagePath));
    }

    private string CreateCraftedZar(string entryPath, string name = "crafted.zar")
    {
        var zarPath = Path.Combine(_tempRoot, name);
        using var output = File.Create(zarPath);
        using var writer = new ZArchiveWriter(output);
        var slash = entryPath.LastIndexOf('/');
        if (slash > 0)
        {
            Assert.True(writer.MakeDir(entryPath[..slash], recursive: true));
        }

        Assert.True(writer.StartNewFile(entryPath));
        writer.AppendData("unsafe"u8);
        writer.Finalize();

        return zarPath;
    }

    [Fact]
    public void XisoImageExplorerRootListingIncludesCanonicalPaths()
    {
        AssertRootListing(CreateXiso());
    }

    [Fact]
    public void XisoImageExplorerNestedListingHasExpectedEntry()
    {
        AssertNestedListing(CreateXiso());
    }

    [Fact]
    public void CisoImageExplorerListsNestedDirectory()
    {
        AssertNestedListing(CreateCiso());
    }

    [Fact]
    public void ZarImageExplorerRootListingIncludesCanonicalPaths()
    {
        AssertRootListing(CreateZar());
    }

    [Fact]
    public void ZarImageExplorerNestedListingHasExpectedEntry()
    {
        AssertNestedListing(CreateZar());
    }

    [Fact]
    public void ChdImageExplorerRootListingIncludesCanonicalPaths()
    {
        AssertRootListing(CreateChd());
    }

    [Fact]
    public void ChdImageExplorerNestedListingHasExpectedEntry()
    {
        AssertNestedListing(CreateChd());
    }

    [Fact]
    public void XisoImageExplorerInvalidPathThrows()
    {
        using var explorer = ImageExplorerFactory.Open(CreateXiso());

        Assert.Throws<InvalidDataException>(() => explorer.ListChildren("/missing"));
        Assert.Throws<InvalidDataException>(() => explorer.CopyOut("/missing", Path.Combine(_tempRoot, "x")));
    }

    [Fact]
    public void CisoImageExplorerInvalidPathThrows()
    {
        using var explorer = ImageExplorerFactory.Open(CreateCiso());

        Assert.Throws<InvalidDataException>(() => explorer.ListChildren("/missing"));
    }

    [Fact]
    public void XisoImageExplorerListingFileAsDirectoryThrows()
    {
        using var explorer = ImageExplorerFactory.Open(CreateXiso());

        Assert.Throws<InvalidDataException>(() => explorer.ListChildren("/default.xbe"));
    }

    [Fact]
    public void ZarImageExplorerListingFileAsDirectoryThrows()
    {
        using var explorer = ImageExplorerFactory.Open(CreateZar());

        Assert.Throws<InvalidDataException>(() => explorer.ListChildren("/default.xbe"));
    }

    [Fact]
    public void ChdImageExplorerListingFileAsDirectoryThrows()
    {
        using var explorer = ImageExplorerFactory.Open(CreateChd());

        Assert.Throws<InvalidDataException>(() => explorer.ListChildren("/default.xbe"));
    }

    [Fact]
    public void XisoImageExplorerCopyOutOverwritesExistingFile()
    {
        AssertCopyOutOverwritesExistingFile(CreateXiso());
    }

    [Fact]
    public void ZarImageExplorerCopyOutOverwritesExistingFile()
    {
        AssertCopyOutOverwritesExistingFile(CreateZar());
    }

    [Fact]
    public void ChdImageExplorerCopyOutOverwritesExistingFile()
    {
        AssertCopyOutOverwritesExistingFile(CreateChd());
    }

    [Fact]
    public void XisoImageExplorerCopyOutCreatesMissingDirectories()
    {
        AssertCopyOutCreatesDestinationDirectories(CreateXiso());
    }

    [Fact]
    public void ZarImageExplorerCopyOutCreatesMissingDirectories()
    {
        AssertCopyOutCreatesDestinationDirectories(CreateZar());
    }

    [Fact]
    public void ChdImageExplorerCopyOutCreatesMissingDirectories()
    {
        AssertCopyOutCreatesDestinationDirectories(CreateChd());
    }

    [Fact]
    public void XisoImageExplorerPathsAreCaseInsensitive()
    {
        AssertCaseInsensitivePaths(CreateXiso());
    }

    [Fact]
    public void ZarImageExplorerPathsAreCaseInsensitive()
    {
        AssertCaseInsensitivePaths(CreateZar());
    }

    [Fact]
    public void ChdImageExplorerPathsAreCaseInsensitive()
    {
        AssertCaseInsensitivePaths(CreateChd());
    }

    [Fact]
    public void XisoImageExplorerRejectsPathTraversal()
    {
        AssertPathTraversalIsRejected(CreateXiso());
    }

    [Fact]
    public void ZarImageExplorerRejectsPathTraversal()
    {
        AssertPathTraversalIsRejected(CreateZar());
    }

    [Fact]
    public void ChdImageExplorerRejectsPathTraversal()
    {
        AssertPathTraversalIsRejected(CreateChd());
    }

    [Fact]
    public void ZarImageExplorerSkipsReservedDeviceNameEntry()
    {
        var zarPath = CreateCraftedZar("dir/CON");
        using var explorer = ImageExplorerFactory.Open(zarPath);

        // The device name must never be exposed to the UI: listed names become paths.
        var listed = explorer.ListChildren("/dir");
        Assert.DoesNotContain(listed, static e => string.Equals(e.Name, "CON", StringComparison.Ordinal));

        var dest = Path.Combine(_tempRoot, "con-out");
        explorer.CopyOut("/dir", dest);
        Assert.True(Directory.Exists(dest));
        Assert.Empty(Directory.GetFiles(dest, "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(_tempRoot, "CON")));
    }

    [Fact]
    public void ZarImageExplorerSkipsParentDirectoryEntry()
    {
        var zarPath = CreateCraftedZar("dir/..");
        using var explorer = ImageExplorerFactory.Open(zarPath);

        // ".." must not be listed (the UI would navigate to it) nor copied out.
        var listed = explorer.ListChildren("/dir");
        Assert.DoesNotContain(listed, static e => string.Equals(e.Name, "..", StringComparison.Ordinal));

        var dest = Path.Combine(_tempRoot, "dotdot-out");
        explorer.CopyOut("/dir", dest);
        Assert.True(Directory.Exists(dest));
        Assert.Empty(Directory.GetFiles(dest, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ZarImageExplorerSkipsAlternateDataStreamEntry()
    {
        var zarPath = CreateCraftedZar("dir/file:stream");
        using var explorer = ImageExplorerFactory.Open(zarPath);

        // A colon would create an NTFS alternate data stream instead of a file.
        var listed = explorer.ListChildren("/dir");
        Assert.DoesNotContain(listed, static e => string.Equals(e.Name, "file:stream", StringComparison.Ordinal));

        var dest = Path.Combine(_tempRoot, "ads-out");
        explorer.CopyOut("/dir", dest);
        Assert.True(Directory.Exists(dest));
        Assert.Empty(Directory.GetFiles(dest, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("CON ")]
    [InlineData("NUL.")]
    [InlineData("COM1 ")]
    [InlineData("LPT9.")]
    public void ZarImageExplorerSkipsDeviceNameWithTrailingDotsOrSpaces(string name)
    {
        // Win32 strips trailing dots/spaces, so these still resolve to the device.
        var zarPath = CreateCraftedZar("dir/" + name);
        using var explorer = ImageExplorerFactory.Open(zarPath);

        var listed = explorer.ListChildren("/dir");
        Assert.DoesNotContain(listed, e => string.Equals(e.Name, name, StringComparison.Ordinal));

        var dest = Path.Combine(_tempRoot, "device-out");
        explorer.CopyOut("/dir", dest);
        Assert.True(Directory.Exists(dest));
        Assert.Empty(Directory.GetFiles(dest, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ZarImageExplorerRejectsExcessiveDirectoryNesting()
    {
        const int depth = 1100;
        var deepPath = string.Join('/', Enumerable.Repeat("d", depth));
        var zarPath = Path.Combine(_tempRoot, "deep.zar");
        using (var output = File.Create(zarPath))
        using (var writer = new ZArchiveWriter(output))
        {
            Assert.True(writer.MakeDir(deepPath, recursive: true));
            Assert.True(writer.StartNewFile(deepPath + "/f.bin"));
            writer.AppendData("deep"u8);
            writer.Finalize();
        }

        using var explorer = ImageExplorerFactory.Open(zarPath);

        // A crafted deep tree must fail catchably instead of overflowing the stack. The
        // depth cap throws InvalidDataException; macOS limits paths to ~1024 bytes, so the
        // walk can hit a path-too-long error first, which is also an acceptable catchable
        // failure. Unrelated I/O errors (permissions, disk full) must still fail the test.
        // ReSharper disable once AccessToDisposedClosure
        var exception = Record.Exception(() => explorer.CopyOut("/d", Path.Combine(_tempRoot, "deep-out")));
        var isNestingCap = exception is InvalidDataException dataEx &&
                           dataEx.Message.Contains("nesting", StringComparison.OrdinalIgnoreCase);
        var isPathTooLong = exception is PathTooLongException ||
                            (exception is IOException ioEx &&
                             ioEx.Message.Contains("too long", StringComparison.OrdinalIgnoreCase));
        Assert.True(isNestingCap || isPathTooLong,
            $"Expected the nesting cap or a path-too-long failure, got {exception?.GetType().Name ?? "no exception"}: {exception?.Message}");
    }

    [Fact]
    public void XisoImageExplorerInvalidIsoThrows()
    {
        var badIso = Path.Combine(_tempRoot, "bad.iso");
        File.WriteAllText(badIso, "not an xiso");

        Assert.Throws<XisoFormatException>(() => ImageExplorerFactory.Open(badIso));
    }

    [Fact]
    public void XisoImageExplorerMissingFileThrows()
    {
        Assert.Throws<FileNotFoundException>(() => ImageExplorerFactory.Open(Path.Combine(_tempRoot, "missing.iso")));
    }

    [Fact]
    public void ZarImageExplorerMissingFileThrows()
    {
        Assert.Throws<InvalidDataException>(() => ImageExplorerFactory.Open(Path.Combine(_tempRoot, "missing.zar")));
    }

    [Fact]
    public void ChdImageExplorerMissingFileThrows()
    {
        Assert.Throws<InvalidDataException>(() => ImageExplorerFactory.Open(Path.Combine(_tempRoot, "missing.chd")));
    }

    [Fact]
    public void XisoImageExplorerEmptyPathListsRoot()
    {
        AssertEmptyPathListsRoot(CreateXiso());
    }

    [Fact]
    public void ZarImageExplorerEmptyPathListsRoot()
    {
        AssertEmptyPathListsRoot(CreateZar());
    }

    [Fact]
    public void ChdImageExplorerEmptyPathListsRoot()
    {
        AssertEmptyPathListsRoot(CreateChd());
    }

    [Fact]
    public void XisoImageExplorerDisposeIsIdempotent()
    {
        AssertDisposeIsIdempotent(CreateXiso());
    }

    [Fact]
    public void ZarImageExplorerDisposeIsIdempotent()
    {
        AssertDisposeIsIdempotent(CreateZar());
    }

    [Fact]
    public void ChdImageExplorerDisposeIsIdempotent()
    {
        AssertDisposeIsIdempotent(CreateChd());
    }

    [Fact]
    public void XisoImageExplorerThrowsAfterDispose()
    {
        var isoPath = CreateXiso();
        var explorer = ImageExplorerFactory.Open(isoPath);
        explorer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => explorer.ListChildren("/"));
        Assert.Throws<ObjectDisposedException>(() =>
            explorer.CopyOut("/default.xbe", Path.Combine(_tempRoot, "after-dispose.bin")));
    }

    [Fact]
    public void XisoImageExplorerDisposeReleasesFileHandle()
    {
        AssertDisposeReleasesFileHandle(CreateXiso());
    }

    [Fact]
    public void ZarImageExplorerDisposeReleasesFileHandle()
    {
        AssertDisposeReleasesFileHandle(CreateZar());
    }

    [Fact]
    public void ChdImageExplorerDisposeReleasesFileHandle()
    {
        AssertDisposeReleasesFileHandle(CreateChd());
    }
}