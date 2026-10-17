using XISOStudio.Models;
using Xunit;

namespace XISOStudio.Tests.Models;

/// <summary>Tests <c>FileItem</c> property defaults, change notifications, and file size formatting.</summary>
public class FileItemTests
{
    [Fact]
    public void IsSelectedDefaultsToTrue()
    {
        var item = new FileItem();

        Assert.True(item.IsSelected);
    }

    [Fact]
    public void IsSelectedRaisesPropertyChanged()
    {
        var item = new FileItem();
        var raised = false;
        item.PropertyChanged += (_, e) =>
            raised = string.Equals(e.PropertyName, nameof(FileItem.IsSelected), StringComparison.Ordinal);

        item.IsSelected = false;

        Assert.True(raised);
        Assert.False(item.IsSelected);
    }

    [Fact]
    public void IsSelectedSameValueDoesNotRaisePropertyChanged()
    {
        var item = new FileItem();
        var raised = false;
        item.PropertyChanged += (_, _) => raised = true;

        item.IsSelected = true;

        Assert.False(raised);
    }

    [Fact]
    public void FileSizeSetterUpdatesDisplaySize()
    {
        var item = new FileItem { FileSize = 1024 };

        Assert.Equal("1 KB", item.DisplaySize);
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(1099511627776, "1 TB")]
    public void FileSizeFormatsDisplaySize(long bytes, string expected)
    {
        var item = new FileItem { FileSize = bytes };

        Assert.Equal(expected, item.DisplaySize);
    }

    [Fact]
    public void FileNameSameValueDoesNotRaisePropertyChanged()
    {
        var item = new FileItem { FileName = "game.iso" };
        var raised = false;
        item.PropertyChanged += (_, _) => raised = true;

        item.FileName = "game.iso";

        Assert.False(raised);
    }

    [Fact]
    public void FullPathRaisesPropertyChanged()
    {
        var item = new FileItem();
        var raised = false;
        item.PropertyChanged += (_, e) =>
            raised = string.Equals(e.PropertyName, nameof(FileItem.FullPath), StringComparison.Ordinal);

        item.FullPath = @"C:\games\game.iso";

        Assert.True(raised);
    }

    [Fact]
    public void FileNameRaisesPropertyChanged()
    {
        var item = new FileItem();
        var raised = false;
        item.PropertyChanged += (_, e) =>
            raised = string.Equals(e.PropertyName, nameof(FileItem.FileName), StringComparison.Ordinal);

        item.FileName = "game.iso";

        Assert.True(raised);
        Assert.Equal("game.iso", item.FileName);
    }

    [Fact]
    public void DisplaySizeRaisesPropertyChanged()
    {
        var item = new FileItem();
        var raised = false;
        item.PropertyChanged += (_, e) =>
            raised = string.Equals(e.PropertyName, nameof(FileItem.DisplaySize), StringComparison.Ordinal);

        item.DisplaySize = "1.5 GB";

        Assert.True(raised);
        Assert.Equal("1.5 GB", item.DisplaySize);
    }

    [Fact]
    public void DisplaySizeSameValueDoesNotRaisePropertyChanged()
    {
        var item = new FileItem { DisplaySize = "1 KB" };
        var raised = false;
        item.PropertyChanged += (_, _) => raised = true;

        item.DisplaySize = "1 KB";

        Assert.False(raised);
    }

    [Fact]
    public void FileSizeRaisesPropertyChangedForFileSizeAndDisplaySize()
    {
        var item = new FileItem();
        var changedProperties = new List<string?>();
        item.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        item.FileSize = 2048;

        Assert.Contains(nameof(FileItem.FileSize), changedProperties, StringComparer.Ordinal);
        Assert.Contains(nameof(FileItem.DisplaySize), changedProperties, StringComparer.Ordinal);
    }

    [Fact]
    public void FileSizeSameValueDoesNotRaisePropertyChanged()
    {
        var item = new FileItem { FileSize = 1024 };
        var raised = false;
        item.PropertyChanged += (_, _) => raised = true;

        item.FileSize = 1024;

        Assert.False(raised);
    }

    [Fact]
    public void DefaultValuesAreEmptyAndZero()
    {
        var item = new FileItem();

        Assert.Equal(string.Empty, item.FileName);
        Assert.Equal(string.Empty, item.FullPath);
        Assert.Equal(string.Empty, item.DisplaySize);
        Assert.Equal(0, item.FileSize);
    }

    [Fact]
    public void FileSizeZeroOnFreshInstanceFormatsAsZeroBytes()
    {
        var item = new FileItem { FileSize = 0 };

        Assert.Equal("0 B", item.DisplaySize);
    }

    [Fact]
    public void FileSizeResetToZeroFormatsAsZeroBytes()
    {
        var item = new FileItem { FileSize = 1024 };

        item.FileSize = 0;

        Assert.Equal("0 B", item.DisplaySize);
    }

    [Theory]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1025, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048575, "1024 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741823, "1024 MB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(1099511627775, "1024 GB")]
    [InlineData(1099511627776, "1 TB")]
    [InlineData(1125899906842624, "1024 TB")]
    [InlineData(long.MaxValue, "8388608 TB")]
    [InlineData(-5, "-5 B")]
    [InlineData(-1024, "-1024 B")]
    public void FileSizeEdgeCasesFormatDisplaySize(long bytes, string expected)
    {
        var item = new FileItem { FileSize = bytes };

        Assert.Equal(expected, item.DisplaySize);
    }
}