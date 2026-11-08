using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Interactivity;
using Avalonia.Threading;
using XISOStudio.Models;
using XISOStudio.Services;

namespace XISOStudio;

/// <summary>Selectable file lists for the conversion and test views, including folder scans and selection commands.</summary>
[SuppressMessage("ReSharper", "UnusedMember.Local",
    Justification =
        "XAML event handlers are resolved by the Avalonia markup compiler, which ReSharper does not link across partial class files.")]
[SuppressMessage("ReSharper", "UnusedParameter.Local",
    Justification = "Parameters are required by XAML event handler signatures (sender, event args).")]
public partial class MainWindow
{
    /// <summary>Files discovered for batch conversion.</summary>
    private readonly ObservableCollection<FileItem> _conversionFiles = new();

    /// <summary>Files discovered for batch integrity testing.</summary>
    private readonly ObservableCollection<FileItem> _testFiles = new();

    /// <summary>Files discovered for batch decompression.</summary>
    private readonly ObservableCollection<FileItem> _decompressFiles = new();

    // A scan generation per file list: a refresh only mutates its list while it is still
    // the newest scan for that list, so an older, slower scan cannot clear or repopulate
    // the list after a newer one (e.g. when the user changes the source folder quickly).
    /// <summary>Guards the per-list scan generation counters.</summary>
    private readonly Lock _fileListRefreshLock = new();

    /// <summary>Newest scan generation for each file list.</summary>
    private readonly Dictionary<ObservableCollection<FileItem>, int> _fileListRefreshGeneration = new();

    /// <summary>
    ///     Binds the selectable file lists to their DataGrids. Called once from the constructor.
    /// </summary>
    private void InitializeFileLists()
    {
        ConversionFilesDataGrid.ItemsSource = _conversionFiles;
        TestFilesDataGrid.ItemsSource = _testFiles;
        DecompressFilesDataGrid.ItemsSource = _decompressFiles;
    }

    /// <summary>Refreshes the affected file list when the search-subfolders option changes.</summary>
    /// <param name="sender">The check box that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void SearchSubfoldersCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized) return;

        if (ReferenceEquals(sender, SearchSubfoldersConversionCheckBox))
        {
            _ = RefreshConversionFileListAsync();
        }
        else if (ReferenceEquals(sender, SearchSubfoldersTestCheckBox))
        {
            _ = RefreshTestFileListAsync();
        }
        else if (ReferenceEquals(sender, SearchSubfoldersDecompressCheckBox))
        {
            _ = RefreshDecompressFileListAsync();
        }
    }

    /// <summary>Selects every file in the conversion list.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void SelectAllConversion_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var file in _conversionFiles) file.IsSelected = true;
    }

    /// <summary>Deselects every file in the conversion list.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void DeselectAllConversion_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var file in _conversionFiles) file.IsSelected = false;
    }

    /// <summary>Selects every file in the test list.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void SelectAllTest_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var file in _testFiles) file.IsSelected = true;
    }

    /// <summary>Deselects every file in the test list.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void DeselectAllTest_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var file in _testFiles) file.IsSelected = false;
    }

    /// <summary>Selects every file in the decompression list.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void SelectAllDecompress_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var file in _decompressFiles) file.IsSelected = true;
    }

    /// <summary>Deselects every file in the decompression list.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void DeselectAllDecompress_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var file in _decompressFiles) file.IsSelected = false;
    }

    /// <summary>Gets the conversion files the user selected.</summary>
    /// <returns>The full paths of the selected conversion files.</returns>
    private IReadOnlyList<string> GetSelectedConversionFiles()
    {
        return _conversionFiles.Where(static f => f.IsSelected).Select(static f => f.FullPath).ToList();
    }

    /// <summary>Gets the test files the user selected.</summary>
    /// <returns>The full paths of the selected test files.</returns>
    private IReadOnlyList<string> GetSelectedTestFiles()
    {
        return _testFiles.Where(static f => f.IsSelected).Select(static f => f.FullPath).ToList();
    }

    /// <summary>Gets the decompression files the user selected.</summary>
    /// <returns>The full paths of the selected decompression files.</returns>
    private IReadOnlyList<string> GetSelectedDecompressFiles()
    {
        return _decompressFiles.Where(static f => f.IsSelected).Select(static f => f.FullPath).ToList();
    }

    /// <summary>Rescans the conversion input folder and repopulates the conversion list.</summary>
    private Task RefreshConversionFileListAsync()
    {
        return LoadFileListAsync(ConversionInputFolderTextBox.Text,
            SearchSubfoldersConversionCheckBox.IsChecked == true, _conversionFiles, SupportedFiles.IsConvertible,
            "conversion");
    }

    /// <summary>Rescans the test input folder and repopulates the test list.</summary>
    private Task RefreshTestFileListAsync()
    {
        return LoadFileListAsync(TestInputFolderTextBox.Text, SearchSubfoldersTestCheckBox.IsChecked == true,
            _testFiles, SupportedFiles.IsTestable, "testing");
    }

    /// <summary>Rescans the decompression input folder and repopulates the decompression list.</summary>
    private Task RefreshDecompressFileListAsync()
    {
        return LoadFileListAsync(DecompressInputFolderTextBox.Text,
            SearchSubfoldersDecompressCheckBox.IsChecked == true, _decompressFiles,
            SupportedFiles.IsDecompressible, "decompression");
    }

    /// <summary>
    ///     Scans the input folder and repopulates the given list with every supported file,
    ///     adding items in small chunks so the UI stays responsive with thousands of files.
    /// </summary>
    /// <param name="inputFolder">Folder to scan; an empty or missing folder clears the list.</param>
    /// <param name="searchSubfolders">Whether to recurse into subfolders.</param>
    /// <param name="target">List to repopulate.</param>
    /// <param name="filter">Predicate selecting the supported files.</param>
    /// <param name="purpose">Label used in log messages ("conversion" or "testing").</param>
    private async Task LoadFileListAsync(string? inputFolder, bool searchSubfolders,
        ObservableCollection<FileItem> target, Func<string, bool> filter, string purpose)
    {
        int generation;
        lock (_fileListRefreshLock)
        {
            generation = _fileListRefreshGeneration.TryGetValue(target, out var current) ? current + 1 : 1;
            _fileListRefreshGeneration[target] = generation;
        }

        try
        {
            if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder))
            {
                if (IsFileListRefreshCurrent(target, generation)) target.Clear();
                return;
            }

            var files = await Task.Run(() =>
            {
                var enumerationOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = searchSubfolders,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
                };

                return Directory.GetFiles(inputFolder, "*.*", enumerationOptions)
                    .Where(filter)
                    .Select(file => new FileItem
                    {
                        FileName = Path.GetRelativePath(inputFolder, file),
                        FullPath = file,
                        FileSize = GetFileSizeSafe(file)
                    })
                    .ToList();
            });

            if (!IsFileListRefreshCurrent(target, generation)) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsFileListRefreshCurrent(target, generation)) target.Clear();
            });

            const int chunkSize = 100;
            for (var i = 0; i < files.Count; i += chunkSize)
            {
                if (!IsFileListRefreshCurrent(target, generation)) return;

                var chunk = files.Skip(i).Take(chunkSize).ToList();
                await Dispatcher.UIThread.InvokeAsync(
                    () =>
                    {
                        // Re-check on the UI thread: a newer scan may have started after the
                        // check above but before this callback ran.
                        if (!IsFileListRefreshCurrent(target, generation)) return;
                        foreach (var item in chunk) target.Add(item);
                    },
                    DispatcherPriority.Background);
            }

            _logger.Information("Loaded {FileCount} file(s) for {Purpose} from {InputFolder}", files.Count, purpose,
                inputFolder);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not load the file list for {Purpose} from {InputFolder}", purpose, inputFolder);
        }
    }

    /// <summary>
    ///     Returns true when <paramref name="generation" /> is still the newest scan for
    ///     <paramref name="target" />; results from an older, slower scan must be discarded.
    /// </summary>
    /// <param name="target">File list whose newest scan generation is checked.</param>
    /// <param name="generation">Scan generation to test.</param>
    /// <returns><c>true</c> when the generation is still the newest scan for the list.</returns>
    private bool IsFileListRefreshCurrent(ObservableCollection<FileItem> target, int generation)
    {
        lock (_fileListRefreshLock)
        {
            return _fileListRefreshGeneration.TryGetValue(target, out var current) && current == generation;
        }
    }

    /// <summary>
    ///     Returns the file length, or 0 when the file vanished, is locked, or is otherwise
    ///     unreadable: one such file must not abort the whole folder scan.
    /// </summary>
    /// <param name="path">File whose length is read.</param>
    /// <returns>The file length in bytes, or 0 when it cannot be read.</returns>
    private long GetFileSizeSafe(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.Debug(ex, "Could not read the size of {FilePath}; treating it as 0", path);
            return 0;
        }
    }
}