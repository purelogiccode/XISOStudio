using XISOStudio.Services;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests supported file extension checks in <c>SupportedFiles</c>.</summary>
public class SupportedFilesTests
{
    [Theory]
    [InlineData("game.iso")]
    [InlineData("game.ISO")]
    [InlineData("games.zip")]
    [InlineData("games.7z")]
    [InlineData("games.rar")]
    [InlineData(@"C:\folder\game.iso")]
    public void IsConvertibleSupportedExtensionsReturnsTrue(string path)
    {
        Assert.True(SupportedFiles.IsConvertible(path));
    }

    [Theory]
    [InlineData("game.bin")]
    [InlineData("game.txt")]
    [InlineData("game.iso.bak")]
    [InlineData("game")]
    public void IsConvertibleUnsupportedExtensionsReturnsFalse(string path)
    {
        Assert.False(SupportedFiles.IsConvertible(path));
    }

    [Theory]
    [InlineData("game.iso")]
    [InlineData("GAME.ISO")]
    [InlineData(@"C:\folder\game.iso")]
    public void IsIsoIsoFilesReturnsTrue(string path)
    {
        Assert.True(SupportedFiles.IsIso(path));
    }

    [Theory]
    [InlineData("game.zip")]
    [InlineData("game.xiso")]
    public void IsIsoNonIsoFilesReturnsFalse(string path)
    {
        Assert.False(SupportedFiles.IsIso(path));
    }

    [Theory]
    [InlineData("game.iso")]
    [InlineData("GAME.ISO")]
    [InlineData(@"C:\folder\game.iso")]
    [InlineData("game.cso")]
    [InlineData("GAME.CSO")]
    [InlineData("game.1.cso")]
    [InlineData(@"C:\folder\game.1.cso")]
    [InlineData("my.game.1.cso")]
    [InlineData("game.cci")]
    [InlineData("GAME.CCI")]
    [InlineData("game.1.cci")]
    [InlineData(@"C:\folder\game.1.cci")]
    [InlineData("game.zar")]
    [InlineData("GAME.ZAR")]
    [InlineData(@"C:\folder\game.zar")]
    [InlineData("game.chd")]
    [InlineData("GAME.CHD")]
    [InlineData(@"C:\folder\game.chd")]
    public void IsTestableSupportedImagesReturnsTrue(string path)
    {
        Assert.True(SupportedFiles.IsTestable(path));
    }

    [Theory]
    [InlineData("game.2.cso")]
    [InlineData("game.10.cso")]
    [InlineData(@"C:\folder\game.2.cso")]
    [InlineData("game.zip")]
    [InlineData("game.xiso")]
    [InlineData("game.bin")]
    [InlineData("game")]
    public void IsTestableUnsupportedFilesReturnsFalse(string path)
    {
        Assert.False(SupportedFiles.IsTestable(path));
    }

    [Theory]
    [InlineData("game.2.iso")]
    [InlineData("game.2.ISO")]
    [InlineData("game.10.iso")]
    [InlineData("game.2.zar")]
    [InlineData("game.2.ZAR")]
    [InlineData("game.2.chd")]
    [InlineData("game.2.CHD")]
    [InlineData(@"C:\folder\my.game.12.iso")]
    [InlineData("game.2.cci")]
    [InlineData("game.10.CCI")]
    public void IsTestableNumericSuffixOnNonCisoImageReturnsTrue(string path)
    {
        // Only CISO always treats ".N.cso" as a continuation part. A standalone ISO,
        // ZAR, CHD or CCI whose name ends in a number must not be mistaken for one.
        Assert.True(SupportedFiles.IsTestable(path));
    }

    [Theory]
    [InlineData("game.ISO")]
    [InlineData("game.Iso")]
    [InlineData("game.ZIP")]
    [InlineData("game.Zip")]
    [InlineData("game.7Z")]
    [InlineData("game.RAR")]
    [InlineData("game.Rar")]
    [InlineData("/tmp/game.7z")]
    public void IsConvertibleAllExtensionsAreCaseInsensitive(string path)
    {
        Assert.True(SupportedFiles.IsConvertible(path));
    }

    [Theory]
    [InlineData("game.cso")]
    [InlineData("GAME.CSO")]
    [InlineData("game.zar")]
    [InlineData("GAME.ZAR")]
    [InlineData("game.chd")]
    [InlineData("GAME.CHD")]
    [InlineData("game.xbe")]
    [InlineData("game.1.cso")]
    public void IsConvertibleImageOnlyExtensionsReturnFalse(string path)
    {
        Assert.False(SupportedFiles.IsConvertible(path));
    }

    [Theory]
    [InlineData("/tmp/GAME.Iso")]
    [InlineData(@"C:\folder\Game.iSO")]
    [InlineData("game.ISO")]
    public void IsIsoIsCaseInsensitiveAcrossDirectories(string path)
    {
        Assert.True(SupportedFiles.IsIso(path));
    }

    [Theory]
    [InlineData("game.ISO")]
    [InlineData("game.Iso")]
    [InlineData("game.ZAR")]
    [InlineData("game.Zar")]
    [InlineData("game.zAr")]
    [InlineData("game.CHD")]
    [InlineData("game.Chd")]
    [InlineData("game.CSO")]
    [InlineData("game.Cso")]
    [InlineData("game.CCI")]
    [InlineData("game.Cci")]
    [InlineData(@"C:\Folder\GAME.ZAR")]
    public void IsTestableAllExtensionsAreCaseInsensitive(string path)
    {
        Assert.True(SupportedFiles.IsTestable(path));
    }

    [Theory]
    [InlineData("game.2.cso")]
    [InlineData("game.3.CSO")]
    [InlineData("game.10.cso")]
    [InlineData("game.99.cso")]
    [InlineData("my.game.2.cso")]
    [InlineData(@"C:\folder\my.game.12.cso")]
    [InlineData("game.1000000.cso")]
    public void IsTestableSplitContinuationPartsReturnFalse(string path)
    {
        Assert.False(SupportedFiles.IsTestable(path));
    }

    [Theory]
    [InlineData("game.1.cso")]
    [InlineData("GAME.1.CSO")]
    [InlineData("game.0.cso")]
    [InlineData("game.01.cso")]
    [InlineData("game.-1.cso")]
    [InlineData("game.abc.cso")]
    [InlineData("game..cso")]
    [InlineData("my.game.1.cso")]
    [InlineData(@"C:\folder\game.1.cso")]
    public void IsTestableFirstOrNonNumericSplitPartsReturnTrue(string path)
    {
        Assert.True(SupportedFiles.IsTestable(path));
    }

    [Fact]
    public void IsTestableSplitPartNumberBeyondIntRangeIsTreatedAsFirstPart()
    {
        Assert.True(SupportedFiles.IsTestable("game.2147483648.cso"));
        Assert.True(SupportedFiles.IsTestable("game.99999999999.cso"));
    }

    [Fact]
    public void IsTestableSplitPartNumberWithinIntRangeIsTreatedAsContinuationPart()
    {
        Assert.False(SupportedFiles.IsTestable("game.2147483647.cso"));
        Assert.False(SupportedFiles.IsTestable("game.1000.cso"));
    }

    [Fact]
    public void IsTestableCciContinuationPartWithFirstPartReturnsFalse()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "game.1.cci"), string.Empty);
        File.WriteAllText(Path.Combine(directory, "game.2.cci"), string.Empty);

        try
        {
            Assert.True(SupportedFiles.IsTestable(Path.Combine(directory, "game.1.cci")));
            Assert.False(SupportedFiles.IsTestable(Path.Combine(directory, "game.2.cci")));
            Assert.False(SupportedFiles.IsTestable(Path.Combine(directory, "game.10.cci")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IsTestableStandaloneNumericCciReturnsTrue()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "game.2.cci"), string.Empty);

        try
        {
            Assert.True(SupportedFiles.IsTestable(Path.Combine(directory, "game.2.cci")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("game.bin")]
    [InlineData("game.txt")]
    [InlineData("game.xiso")]
    [InlineData("game.zip")]
    [InlineData("game.7z")]
    [InlineData("game.rar")]
    [InlineData("game.cso.bak")]
    [InlineData("game.iso.tmp")]
    [InlineData("game.2")]
    [InlineData("folder.iso/file")]
    public void IsTestableUnsupportedExtensionsAndPathsReturnFalse(string path)
    {
        Assert.False(SupportedFiles.IsTestable(path));
    }

    [Theory]
    [InlineData("game.iso")]
    [InlineData("game.cso")]
    [InlineData("game.1.cso")]
    [InlineData("game.2.cso")]
    [InlineData("GAME.2.CSO")]
    [InlineData(@"C:\folder\my.game.12.cso")]
    [InlineData("game.cci")]
    [InlineData("game.1.cci")]
    [InlineData("game.2.cci")]
    [InlineData("GAME.2.CCI")]
    [InlineData(@"C:\folder\my.game.12.cci")]
    [InlineData("game.zar")]
    [InlineData("game.chd")]
    public void IsImageAllImageExtensionsIncludingSplitContinuationPartsReturnTrue(string path)
    {
        Assert.True(SupportedFiles.IsImage(path));
    }

    [Theory]
    [InlineData("game.zip")]
    [InlineData("game.7z")]
    [InlineData("game.rar")]
    [InlineData("game.bin")]
    [InlineData("game.xiso")]
    [InlineData("game")]
    public void IsImageNonImageFilesReturnFalse(string path)
    {
        Assert.False(SupportedFiles.IsImage(path));
    }

    [Fact]
    public void IsConvertibleNullThrowsNullReferenceException()
    {
        Assert.Throws<NullReferenceException>(static () => SupportedFiles.IsConvertible(null!));
    }

    [Fact]
    public void IsIsoNullThrowsNullReferenceException()
    {
        Assert.Throws<NullReferenceException>(static () => SupportedFiles.IsIso(null!));
    }

    [Fact]
    public void IsTestableNullThrowsNullReferenceException()
    {
        Assert.Throws<NullReferenceException>(static () => SupportedFiles.IsTestable(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void SupportedFileChecksEmptyOrWhitespaceReturnFalse(string path)
    {
        Assert.False(SupportedFiles.IsConvertible(path));
        Assert.False(SupportedFiles.IsIso(path));
        Assert.False(SupportedFiles.IsTestable(path));
    }
}