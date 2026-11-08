using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Serilog;
using XISOSharp;
using XISOSharp.Models;
using XISOStudio.Interfaces;
using XISOStudio.Models;
using XISOStudio.Services;

namespace XISOStudio;

/// <summary>Image explorer view: browsing, extracting, and dragging files out of an open image.</summary>
[SuppressMessage("ReSharper", "UnusedMember.Local",
    Justification =
        "XAML event handlers are resolved by the Avalonia markup compiler, which ReSharper does not link across partial class files.")]
[SuppressMessage("ReSharper", "UnusedParameter.Local",
    Justification = "Parameters are required by XAML event handler signatures (sender, event args).")]
public partial class MainWindow
{
    // Drag-drop state tracking
    /// <summary>Pointer position where the current drag gesture started.</summary>
    private Point _dragStartPoint;

    /// <summary>Pointer arguments captured when the drag gesture started.</summary>
    private PointerPressedEventArgs? _dragPointerArgs;

    /// <summary>Indicates that a drag operation is in progress.</summary>
    private bool _isDragging;

    /// <summary>Minimum pointer movement in pixels required to start a drag.</summary>
    private const double MinimumDragDistance = 4;

    /// <summary>Decoded XBE preview currently shown, or <c>null</c>.</summary>
    private Bitmap? _xbePreviewBitmap;

    /// <summary>Hashes computed for the open image, or an empty list.</summary>
    private IReadOnlyList<ImageMetadataDetail> _currentHashes = [];

    /// <summary>Stream backing <see cref="_xbePreviewBitmap"/>; kept alive while the bitmap is displayed.</summary>
    private MemoryStream? _xbePreviewStream;

    /// <summary>Prompts for an Xbox image and opens it in the explorer.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void BrowseExplorerFile_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select an Xbox image to explore",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Xbox images")
                        { Patterns = Options },
                    new FilePickerFileType("Xbox ISO") { Patterns = OptionsArray },
                    new FilePickerFileType("Compressed ISO") { Patterns = OptionsArray0 },
                    new FilePickerFileType("CCI image") { Patterns = OptionsArray4 },
                    new FilePickerFileType("ZAR archive") { Patterns = OptionsArray1 },
                    new FilePickerFileType("CHD image") { Patterns = OptionsArray2 },
                    new FilePickerFileType("GOD/SVOD header or other") { Patterns = OptionsArray3 },
                    new FilePickerFileType("All files") { Patterns = OptionsArray3 }
                ]
            });

            if (files.Count == 0) return;

            var selectedPath = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(selectedPath)) return;

            ExplorerFilePathTextBox.Text = selectedPath;
            await InitializeExplorerAsync(selectedPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method BrowseExplorerFile_ClickAsync");
        }
    }

    /// <summary>Prompts for a GOD/SVOD <c>.data</c> folder or a folder holding one GOD header.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void BrowseExplorerFolder_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select a GOD/SVOD .data folder or its parent folder",
                AllowMultiple = false
            });

            if (folders.Count == 0) return;

            var selectedPath = folders[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(selectedPath)) return;

            ExplorerFilePathTextBox.Text = selectedPath;
            await InitializeExplorerAsync(selectedPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method BrowseExplorerFolder_ClickAsync");
        }
    }

    /// <summary>Opens the image and loads its root directory, retiring any previous explorer.</summary>
    /// <param name="imagePath">Path of the image or archive to open.</param>
    private async Task InitializeExplorerAsync(string imagePath)
    {
        IImageExplorer? previous;
        lock (_explorerLock)
        {
            previous = _explorer;
            // Clear the reference before opening: if the factory throws, the field must
            // not keep pointing at the explorer that was just disposed.
            _explorer = null;
        }

        // Dispose the previous explorer only after any background copy-out using it finishes.
        RetireExplorer(previous);
        ResetExplorerDetails();

        try
        {
            var explorer = ImageExplorerFactory.Open(imagePath, _logger);
            lock (_explorerLock)
            {
                _explorer = explorer;
            }

            await LoadDirectoryAsync("/");
            await LoadExplorerMetadataAsync();
        }
        catch (Exception ex) when (ImageErrorClassifier.IsMissingApplicationComponentError(ex))
        {
            lock (_explorerLock)
            {
                _explorer = null;
            }

            // A missing or damaged application assembly is an installation problem, not an
            // image-input problem or an application defect. Keep it below the automatic
            // bug-report threshold and give the user a way to fix it.
            _logger.Information(ex, "A required image engine component could not be loaded: {Message}", ex.Message);
            await ShowErrorSafeAsync(
                "XISO Studio could not load one of its image engine components (XISOSharp, ZArchiveSharp, or CHDSharp). " +
                "The application files may be incomplete or damaged. Please reinstall XISO Studio from the official release and try again.\n\n" +
                $"Details: {ex.Message}");

            // The grid would otherwise keep showing the previous (now disposed) image.
            ExplorerDataGrid.ItemsSource = null;
            _currentInternalPath = "/";
            UpdateExplorerUiState();
        }
        catch (Exception ex) when (ImageErrorClassifier.IsUserInputImageError(ex))
        {
            lock (_explorerLock)
            {
                _explorer = null;
            }

            // Invalid, missing, or unreadable user-selected files are expected conditions,
            // so they are logged below the automatic bug-report threshold.
            _logger.Information(ex, "The selected file is not a supported Xbox image: {Message}", ex.Message);
            await ShowErrorSafeAsync(
                "The selected file is not a valid Xbox or Xbox 360 image, or it cannot be read. " +
                "Please select an Xbox ISO, XISO, CSO, CCI, CHD, ZAR, GOD/SVOD container (header or .data folder), " +
                $"or an XBLA/XBLIG STFS package.\n\nDetails: {ex.Message}");

            // The grid would otherwise keep showing the previous (now disposed) image.
            ExplorerDataGrid.ItemsSource = null;
            _currentInternalPath = "/";
            UpdateExplorerUiState();
        }
        catch (Exception ex) when (PathHelper.IsFileInUseError(ex))
        {
            lock (_explorerLock)
            {
                _explorer = null;
            }

            // The image itself is fine but another program holds it open. That is an
            // expected environmental condition, so it stays below the automatic
            // bug-report threshold and the user gets actionable guidance.
            _logger.Information(ex, "The image is in use by another process: {Message}", ex.Message);
            await ShowErrorSafeAsync(
                "The selected image is currently in use by another program. " +
                "Close any application that may have it open (mounting tool, antivirus, download manager, another window) and try again.\n\n" +
                $"Details: {ex.Message}");

            // The grid would otherwise keep showing the previous (now disposed) image.
            ExplorerDataGrid.ItemsSource = null;
            _currentInternalPath = "/";
            UpdateExplorerUiState();
        }
        catch (Exception ex)
        {
            lock (_explorerLock)
            {
                _explorer = null;
            }

            _logger.Error(ex, "Failed to read image: {Message}", ex.Message);
            await ShowErrorSafeAsync($"Failed to read image: {ex.Message}");

            // The grid would otherwise keep showing the previous (now disposed) image.
            ExplorerDataGrid.ItemsSource = null;
            _currentInternalPath = "/";
            UpdateExplorerUiState();
        }
    }

    /// <summary>
    ///     Runs <paramref name="action" /> on the current explorer while holding it in use,
    ///     so opening another image or closing the window cannot dispose it mid-operation.
    ///     The action runs on the thread pool because explorer reads (hunk decompression,
    ///     archive reads) are synchronous and can take seconds. Returns false when no
    ///     explorer is open.
    /// </summary>
    /// <param name="action">Action to run against the open explorer.</param>
    /// <param name="token">Token that cancels the queued work.</param>
    /// <returns><c>true</c> when the action ran; <c>false</c> when no explorer is open.</returns>
    private async Task<bool> UseExplorerAsync(Action<IImageExplorer> action, CancellationToken token)
    {
        await _explorerUseLock.WaitAsync(token);
        try
        {
            IImageExplorer? explorer;
            lock (_explorerLock)
            {
                explorer = _explorer;
            }

            if (explorer == null) return false;

            await Task.Run(() => action(explorer), token);
            return true;
        }
        finally
        {
            _explorerUseLock.Release();
        }
    }

    /// <summary>
    ///     Disposes <paramref name="explorer" /> after any in-flight copy-out that is still
    ///     using it has finished, without blocking the caller.
    /// </summary>
    /// <param name="explorer">Explorer to dispose, or <c>null</c> to do nothing.</param>
    private void RetireExplorer(IImageExplorer? explorer)
    {
        if (explorer == null) return;

        _ = Task.Run(async () =>
        {
            // Retirement must always complete, so it deliberately ignores cancellation.
            await _explorerUseLock.WaitAsync(CancellationToken.None);
            try
            {
                explorer.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Error disposing the image explorer");
            }
            finally
            {
                _explorerUseLock.Release();
            }
        }, CancellationToken.None);
    }

    /// <summary>Lists a directory of the open image and binds it to the explorer grid.</summary>
    /// <param name="internalPath">Directory path within the image (<c>"/"</c> for the root).</param>
    private async Task LoadDirectoryAsync(string internalPath)
    {
        CancellationToken token;
        try
        {
            token = _explorerCts.Token;
        }
        catch (ObjectDisposedException ex)
        {
            // The window is shutting down.
            _logger.Debug(ex, "Skipping directory load because the window is shutting down");
            return;
        }

        IReadOnlyList<ImageEntry> entries;
        try
        {
            // Explorer reads (hunk decompression, archive reads) can take seconds, so run
            // them off the UI thread while holding the explorer in use; opening another
            // image or closing the window cannot dispose it mid-read.
            IReadOnlyList<ImageEntry>? listed = null;
            if (!await UseExplorerAsync(explorer => listed = explorer.ListChildren(internalPath), token))
            {
                return;
            }

            entries = listed ?? [];
        }
        catch (OperationCanceledException ex)
        {
            _logger.Debug(ex, "Loading directory {InternalPath} was canceled", internalPath);
            return;
        }
        catch (Exception ex) when (ImageErrorClassifier.IsUserInputImageError(ex))
        {
            // A corrupt or unreadable image is an expected condition, not an application defect.
            _logger.Information(ex, "The image could not be read while loading {InternalPath}", internalPath);
            await ShowErrorSafeAsync($"The image appears to be corrupt or unreadable. Details: {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error loading directory: {InternalPath}", internalPath);
            await ShowErrorSafeAsync($"Error loading directory: {ex.Message}");
            return;
        }

        var uiItems = entries.Select(static e => new XisoExplorerItem
        {
            Name = e.Name,
            IsDirectory = e.IsDirectory,
            SizeFormatted = e.IsDirectory ? "" : Formatter.FormatBytes(e.Size),
            Entry = e
        }).OrderByDescending(static i => i.IsDirectory)
            .ThenBy(static i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ExplorerDataGrid.ItemsSource = uiItems;
        _currentInternalPath = ImagePaths.Normalize(internalPath);
        UpdateExplorerUiState();
    }

    /// <summary>Updates the explorer path text and the enabled state of the up button.</summary>
    private void UpdateExplorerUiState()
    {
        ExplorerUpButton.IsEnabled = !string.Equals(_currentInternalPath, "/", StringComparison.Ordinal);
        ExplorerPathTextBlock.Text = _currentInternalPath;
    }

    /// <summary>
    ///     Reads image-wide metadata for the details panel and seeds the XBE
    ///     preview path from the root executable.
    /// </summary>
    private async Task LoadExplorerMetadataAsync()
    {
        CancellationToken token;
        try
        {
            token = _explorerCts.Token;
        }
        catch (ObjectDisposedException ex)
        {
            _logger.Debug(ex, "Skipping metadata load because the window is shutting down");
            return;
        }

        ImageMetadata? metadata = null;
        try
        {
            if (!await UseExplorerAsync(explorer => metadata = explorer.GetMetadata(), token))
            {
                return;
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.Debug(ex, "Loading image metadata was canceled");
            return;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read image metadata");
            metadata = ImageMetadata.Unavailable("", ExplorerFilePathTextBox.Text ?? "", 0,
                $"Metadata could not be read: {ex.Message}");
        }

        if (metadata is null)
        {
            return;
        }

        ApplyMetadata(metadata);
    }

    /// <summary>Binds <see cref="ImageMetadata"/> to the explorer details panel.</summary>
    /// <param name="metadata">Metadata to display.</param>
    private void ApplyMetadata(ImageMetadata metadata)
    {
        ImageFormatValueTextBlock.Text = string.IsNullOrEmpty(metadata.Format) ? "Unknown" : metadata.Format;
        ImageSizeValueTextBlock.Text = metadata.Length > 0 ? Formatter.FormatBytes(metadata.Length) : "Unknown";
        ImageKindValueTextBlock.Text = string.IsNullOrEmpty(metadata.Kind) ? "Unknown" : metadata.Kind;
        ImageExecutableValueTextBlock.Text = string.IsNullOrEmpty(metadata.ExecutablePath) ? "—" : metadata.ExecutablePath;
        ImagePathValueTextBlock.Text = metadata.Path;

        TitleNameValueTextBlock.Text = string.IsNullOrEmpty(metadata.TitleName) ? "—" : metadata.TitleName;
        TitleIdValueTextBlock.Text = string.IsNullOrEmpty(metadata.TitleIdHex) ? "—" : metadata.TitleIdHex;
        RegionValueTextBlock.Text = string.IsNullOrEmpty(metadata.Region) ? "—" : metadata.Region;
        DiscValueTextBlock.Text = metadata.DiscNumber == 0
            ? "—"
            : metadata.DiscCount > 0 ? $"{metadata.DiscNumber} of {metadata.DiscCount}" : metadata.DiscNumber.ToString();
        VersionValueTextBlock.Text = metadata.Version == 0 ? "—" : $"0x{metadata.Version:X8}";
        PublisherValueTextBlock.Text = string.IsNullOrEmpty(metadata.Publisher) ? "—" : metadata.Publisher;
        DeveloperValueTextBlock.Text = string.IsNullOrEmpty(metadata.Developer) ? "—" : metadata.Developer;
        GenreValueTextBlock.Text = string.IsNullOrEmpty(metadata.Genre) ? "—" : metadata.Genre;
        DescriptionValueTextBlock.Text = string.IsNullOrEmpty(metadata.Description) ? "—" : metadata.Description;

        ImageDetailsItemsControl.ItemsSource = metadata.Details;

        XbePreviewPathTextBox.Text = string.IsNullOrWhiteSpace(metadata.ExecutablePath)
            ? "/default.xbe"
            : metadata.ExecutablePath;
        XbePreviewStatusTextBlock.Text = metadata.UnavailableReason ?? "";

        if (metadata.TitleThumbnail is { Length: > 0 } thumbnail)
        {
            ApplyTitleThumbnail(thumbnail);
        }
    }

    /// <summary>
    ///     Renders the raw XDBF thumbnail of an Xbox 360 title (usually PNG or JPEG) in the
    ///     preview box, replacing any previous preview.
    /// </summary>
    /// <param name="thumbnail">Raw image bytes from <c>XexTitleInfo.Thumbnail</c>.</param>
    private void ApplyTitleThumbnail(byte[] thumbnail)
    {
        try
        {
            var stream = new MemoryStream(thumbnail, writable: false);
            var bitmap = new Bitmap(stream);
            DisposeXbePreview();
            _xbePreviewStream = stream;
            _xbePreviewBitmap = bitmap;
            XbePreviewImage.Source = bitmap;
            XbePreviewStatusTextBlock.Text = "Xbox 360 title thumbnail";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException
                                       or IOException)
        {
            // The thumbnail is optional decoration; an undecodable payload must not hide
            // the rest of the metadata or produce a bug report.
            _logger.Information(ex, "Could not decode the title thumbnail: {Message}", ex.Message);
        }
    }

    /// <summary>
    ///     Extracts the whole open image into a folder chosen by the user, optionally skipping
    ///     files that were already extracted.
    /// </summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void ExtractAllButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        CancellationToken token;
        try
        {
            token = _explorerCts.Token;
        }
        catch (ObjectDisposedException ex)
        {
            _logger.Debug(ex, "Skipping extraction because the window is shutting down");
            return;
        }

        if (_explorer is null)
        {
            UpdateStatus("Open an image before extracting.");
            return;
        }

        var destination = await SelectFolderAsync("Select the folder that receives the extracted files");
        if (string.IsNullOrEmpty(destination)) return;

        var skipExisting = ExtractAllSkipExistingCheckBox.IsChecked == true;
        ExtractAllButton.IsEnabled = false;
        UpdateStatus("Extracting image...");
        _logger.Information("Extracting the image contents to '{Folder}'...", destination);

        var extracted = 0;
        var progress = new Progress<ProgressInfo>(info =>
        {
            if (info.Type != ProgressInfoType.FileAdded) return;

            extracted++;
            if (extracted % 50 == 0) UpdateStatus($"Extracting: {extracted:N0} file(s)...");
        });

        try
        {
            var result = 0;
            if (!await UseExplorerAsync(
                    explorer => result = explorer.ExtractAll(destination, skipExisting, progress, token), token))
            {
                UpdateStatus("Open an image before extracting.");
                return;
            }

            if (result != 0)
            {
                _logger.Information("Extraction finished with result code {ResultCode}.", result);
                UpdateStatus("Extraction finished with errors.");
            }
            else
            {
                _logger.Information("Extracted {Count} file(s) to '{Folder}'.", extracted, destination);
                UpdateStatus("Extraction complete.");
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.Debug(ex, "Image extraction was canceled");
            UpdateStatus("Extraction canceled.");
        }
        catch (Exception ex) when (ImageErrorClassifier.IsUserInputImageError(ex)
                                   || ImageErrorClassifier.IsEnvironmentalIoError(ex))
        {
            _logger.Information(ex, "Could not extract the image: {Message}", ex.Message);
            await ShowErrorSafeAsync("The image contents could not be extracted:\n\n" + ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to extract the image");
            await ShowErrorSafeAsync($"Failed to extract the image: {ex.Message}");
        }
        finally
        {
            ExtractAllButton.IsEnabled = true;
        }
    }

    /// <summary>
    ///     Splits the open plain ISO into sector-aligned parts sized for FAT32/FATX drives.
    /// </summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void SplitImageButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        if (_isOperationRunning) return;

        var imagePath = ExplorerFilePathTextBox.Text;
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath) || !SupportedFiles.IsIso(imagePath))
        {
            await ShowWarningSafeAsync(
                "Open a plain .iso/.xiso image to split it. Compressed containers must be decompressed first.");
            return;
        }

        var outputFolder = await SelectFolderAsync("Select the folder that receives the split parts");
        if (string.IsNullOrEmpty(outputFolder)) return;

        SplitImageButton.IsEnabled = false;
        try
        {
            var outputBase = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(imagePath));
            var parts = await _xisoSplitService.SplitAsync(imagePath, outputBase,
                XisoSplitter.DefaultPartSizeBytes, _explorerCts.Token);
            UpdateStatus($"Split into {parts.Count} part(s).");
        }
        catch (OperationCanceledException ex)
        {
            _logger.Debug(ex, "Image split was canceled");
            UpdateStatus("Split canceled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.Information(ex, "Could not split '{ImagePath}': {Reason}", imagePath, ex.Message);
            await ShowErrorSafeAsync("The image could not be split:\n\n" + ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to split '{ImagePath}'", imagePath);
            await ShowErrorSafeAsync($"Failed to split the image: {ex.Message}");
        }
        finally
        {
            SplitImageButton.IsEnabled = true;
        }
    }

    /// <summary>
    ///     Reassembles a split set starting at a chosen <c>.1.iso</c> part and opens the result.
    /// </summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void JoinImageButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        if (_isOperationRunning) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select the first part of the split image (game.1.iso)",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Split ISO first part") { Patterns = ["*.1.iso"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        });

        if (files.Count == 0) return;

        var firstPartPath = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(firstPartPath)) return;

        var stem = Path.GetFileNameWithoutExtension(firstPartPath);
        if (stem.EndsWith(".1", StringComparison.OrdinalIgnoreCase)) stem = stem[..^2];

        var directory = Path.GetDirectoryName(firstPartPath);
        if (string.IsNullOrEmpty(directory)) return;

        var outputPath = Path.Combine(directory, stem + ".iso");
        if (File.Exists(outputPath))
        {
            var overwrite = await _messageBoxService.ShowAsync(
                $"'{Path.GetFileName(outputPath)}' already exists. Replace it with the joined image?",
                "Join split image", UiMessageBoxButton.YesNo, UiMessageBoxImage.Warning);
            if (overwrite != UiMessageBoxResult.Yes) return;

            try
            {
                File.Delete(outputPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Information(ex, "Could not replace the existing joined image '{OutputPath}'", outputPath);
                await ShowErrorSafeAsync("The existing output file could not be replaced:\n\n" + ex.Message);
                return;
            }
        }

        JoinImageButton.IsEnabled = false;
        try
        {
            var joinedPath = await _xisoSplitService.JoinAsync(firstPartPath, outputPath, _explorerCts.Token);
            UpdateStatus($"Joined image written to '{joinedPath}'.");

            ExplorerFilePathTextBox.Text = joinedPath;
            await InitializeExplorerAsync(joinedPath);
        }
        catch (OperationCanceledException ex)
        {
            _logger.Debug(ex, "Image join was canceled");
            UpdateStatus("Join canceled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.Information(ex, "Could not join '{FirstPart}': {Reason}", firstPartPath, ex.Message);
            await ShowErrorSafeAsync("The split image could not be joined:\n\n" + ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to join '{FirstPart}'", firstPartPath);
            await ShowErrorSafeAsync($"Failed to join the split image: {ex.Message}");
        }
        finally
        {
            JoinImageButton.IsEnabled = true;
        }
    }

    /// <summary>
    ///     Computes and displays the checksums of the open image. Hashing is explicit because
    ///     it reads the whole (decompressed) image and can take a while.
    /// </summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void ComputeHashesButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        var imagePath = ExplorerFilePathTextBox.Text;
        if (string.IsNullOrWhiteSpace(imagePath) || _explorer is null)
        {
            ImageHashesStatusTextBlock.Text = "Open an image before computing hashes.";
            return;
        }

        CancellationToken token;
        try
        {
            token = _explorerCts.Token;
        }
        catch (ObjectDisposedException ex)
        {
            _logger.Debug(ex, "Skipping hash computation because the window is shutting down");
            return;
        }

        ComputeHashesButton.IsEnabled = false;
        CopyHashesButton.IsEnabled = false;
        ImageHashesItemsControl.ItemsSource = null;
        ImageHashesStatusTextBlock.Text = "Computing hashes...";

        try
        {
            var hashes = await _imageHashService.ComputeAsync(imagePath, token);

            // The user may have opened another image while the hashes were being read.
            if (!string.Equals(ExplorerFilePathTextBox.Text, imagePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _currentHashes = BuildHashRows(hashes);
            ImageHashesItemsControl.ItemsSource = _currentHashes;
            CopyHashesButton.IsEnabled = _currentHashes.Count > 0;
            ImageHashesStatusTextBlock.Text = hashes.Note ?? "";
        }
        catch (OperationCanceledException ex)
        {
            _logger.Debug(ex, "Image hash computation was canceled");
            ImageHashesStatusTextBlock.Text = "Hash computation canceled.";
        }
        catch (Exception ex) when (ImageErrorClassifier.IsUserInputImageError(ex)
                                   || ImageErrorClassifier.IsEnvironmentalIoError(ex))
        {
            _logger.Information(ex, "Could not compute the image hashes: {Message}", ex.Message);
            ImageHashesStatusTextBlock.Text = $"Hashes unavailable: {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to compute the image hashes");
            ImageHashesStatusTextBlock.Text = "Hashes could not be computed.";
        }
        finally
        {
            ComputeHashesButton.IsEnabled = true;
        }
    }

    /// <summary>Copies every computed hash to the clipboard.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void CopyHashesButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        if (_currentHashes.Count == 0) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            ImageHashesStatusTextBlock.Text = "The clipboard is not available.";
            return;
        }

        var text = string.Join(Environment.NewLine,
            _currentHashes.Select(static row => $"{row.Label}: {row.Value}"));
        await clipboard.SetValueAsync(DataFormat.Text, text);
        ImageHashesStatusTextBlock.Text = "Hashes copied to the clipboard.";
    }

    /// <summary>Converts the computed hashes into display rows, omitting unavailable values.</summary>
    /// <param name="hashes">Hashes to render.</param>
    /// <returns>The display rows.</returns>
    private static IReadOnlyList<ImageMetadataDetail> BuildHashRows(ImageHashes hashes)
    {
        var rows = new List<ImageMetadataDetail>();
        if (!string.IsNullOrEmpty(hashes.Crc32)) rows.Add(new ImageMetadataDetail("CRC-32", hashes.Crc32));
        if (!string.IsNullOrEmpty(hashes.Md5)) rows.Add(new ImageMetadataDetail("MD5", hashes.Md5));
        if (!string.IsNullOrEmpty(hashes.Sha1)) rows.Add(new ImageMetadataDetail("SHA-1", hashes.Sha1));
        if (!string.IsNullOrEmpty(hashes.Sha256)) rows.Add(new ImageMetadataDetail("SHA-256", hashes.Sha256));
        if (!string.IsNullOrEmpty(hashes.ImageSha3256))
        {
            rows.Add(new ImageMetadataDetail("SHA3-256", hashes.ImageSha3256));
        }

        if (!string.IsNullOrEmpty(hashes.TreeSha3256))
        {
            rows.Add(new ImageMetadataDetail("Tree SHA3-256", hashes.TreeSha3256));
        }

        return rows;
    }

    /// <summary>Clears the details panel and disposes the current preview.</summary>
    private void ResetExplorerDetails()
    {
        DisposeXbePreview();
        ImageFormatValueTextBlock.Text = "";
        ImageSizeValueTextBlock.Text = "";
        ImageKindValueTextBlock.Text = "";
        ImageExecutableValueTextBlock.Text = "";
        ImagePathValueTextBlock.Text = "";
        TitleNameValueTextBlock.Text = "";
        TitleIdValueTextBlock.Text = "";
        RegionValueTextBlock.Text = "";
        DiscValueTextBlock.Text = "";
        VersionValueTextBlock.Text = "";
        PublisherValueTextBlock.Text = "";
        DeveloperValueTextBlock.Text = "";
        GenreValueTextBlock.Text = "";
        DescriptionValueTextBlock.Text = "";
        ImageDetailsItemsControl.ItemsSource = null;
        _currentHashes = [];
        ImageHashesItemsControl.ItemsSource = null;
        ImageHashesStatusTextBlock.Text = "";
        CopyHashesButton.IsEnabled = false;
        ComputeHashesButton.IsEnabled = true;
        XbePreviewStatusTextBlock.Text = "";
        XbePreviewPathTextBox.Text = "/default.xbe";
    }

    /// <summary>Reads and renders the selected XBE asset for the open image.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void LoadXbePreviewButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var internalPath = XbePreviewPathTextBox.Text;
            if (string.IsNullOrWhiteSpace(internalPath))
            {
                XbePreviewStatusTextBlock.Text = "Enter the image-internal path of an XBE file (for example /default.xbe).";
                return;
            }

            var kind = XbePreviewKindComboBox.SelectedIndex switch
            {
                1 => XbeImageKind.Title,
                2 => XbeImageKind.Save,
                _ => XbeImageKind.Logo
            };
            var kindName = kind.ToString();

            CancellationToken token;
            try
            {
                token = _explorerCts.Token;
            }
            catch (ObjectDisposedException ex)
            {
                _logger.Debug(ex, "Skipping XBE preview because the window is shutting down");
                return;
            }

            XbeImage? image = null;
            if (!await UseExplorerAsync(explorer => image = explorer.GetXbeImage(internalPath, kind), token))
            {
                XbePreviewStatusTextBlock.Text = "Open an image before loading a preview.";
                return;
            }

            if (image is null)
            {
                DisposeXbePreview();
                XbePreviewStatusTextBlock.Text =
                    $"{kindName} image not found in '{internalPath}'. Original-Xbox XBEs usually provide logo, title and save images; Xbox 360 XEX files do not.";
                return;
            }

            if (image.Png is not { Length: > 0 })
            {
                DisposeXbePreview();
                XbePreviewStatusTextBlock.Text = image.Width > 0
                    ? $"{kindName} image is {image.Width}×{image.Height} ({image.PixelFormat}) but could not be decoded to PNG."
                    : $"{kindName} image ({image.PixelFormat}) could not be decoded.";
                return;
            }

            var stream = new MemoryStream(image.Png, writable: false);
            var bitmap = new Bitmap(stream);
            DisposeXbePreview();
            _xbePreviewStream = stream;
            _xbePreviewBitmap = bitmap;
            XbePreviewImage.Source = bitmap;
            XbePreviewStatusTextBlock.Text = image.Width > 0
                ? $"{kindName}: {image.SectionName} — {image.Width}×{image.Height} ({image.PixelFormat})"
                : $"{kindName}: {image.SectionName} ({image.PixelFormat})";
        }
        catch (OperationCanceledException ex)
        {
            _logger.Debug(ex, "Loading the XBE preview was canceled");
        }
        catch (Exception ex) when (ImageErrorClassifier.IsUserInputImageError(ex)
                                   || ImageErrorClassifier.IsEnvironmentalIoError(ex))
        {
            _logger.Information(ex, "Could not load the XBE preview: {Message}", ex.Message);
            XbePreviewStatusTextBlock.Text = $"Preview unavailable: {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load the XBE preview");
            await ShowErrorSafeAsync($"Failed to load the XBE preview: {ex.Message}");
        }
    }

    /// <summary>Releases the current preview bitmap and its backing stream.</summary>
    private void DisposeXbePreview()
    {
        XbePreviewImage.Source = null;
        _xbePreviewBitmap?.Dispose();
        _xbePreviewBitmap = null;
        _xbePreviewStream?.Dispose();
        _xbePreviewStream = null;
    }

    /// <summary>Opens the double-clicked directory or extracts and opens the selected file.</summary>
    /// <param name="sender">The grid that raised the event.</param>
    /// <param name="e">The tap event data.</param>
    private async void ExplorerDataGrid_DoubleTappedAsync(object? sender, TappedEventArgs e)
    {
        try
        {
            if (ExplorerDataGrid.SelectedItem is not XisoExplorerItem item) return;

            if (item.IsDirectory)
            {
                await LoadDirectoryAsync(item.Entry.FullPath);
            }
            else
            {
                // Open the file with the default application
                await OpenFileFromImageAsync(item.Entry, item.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in method ExplorerDataGrid_DoubleTappedAsync");
        }
    }

    /// <summary>Extracts a file to a temporary folder and opens it with the default application.</summary>
    /// <param name="entry">Entry describing the file inside the image.</param>
    /// <param name="fileName">Name used for the extracted temporary file.</param>
    private async Task OpenFileFromImageAsync(ImageEntry entry, string fileName)
    {
        CancellationToken token;
        try
        {
            // Capture the token before queueing the background work: the window may be
            // closed (and the source disposed) before the task actually starts.
            token = _explorerCts.Token;
        }
        catch (ObjectDisposedException ex)
        {
            // The window is shutting down.
            _logger.Debug(ex, "Skipping file extraction because the window is shutting down");
            return;
        }

        await Task.Run(async () =>
        {
            string? tempFolder = null;
            var extracted = false;
            try
            {
                tempFolder = ResolveExplorerTempDirectory(entry.Size, "ImageExplorer");
                Directory.CreateDirectory(tempFolder);
                var tempPath = Path.Combine(tempFolder, fileName);

                // Extract file to temp location while the explorer is held in use.
                if (!await UseExplorerAsync(explorer => explorer.CopyOut(entry.FullPath, tempPath), token))
                {
                    return;
                }

                extracted = true;

                // Open with default application on UI thread
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        using var process = Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
                    }
                    catch (Win32Exception ex)
                    {
                        // No application is registered for this file type (Xbox
                        // executables, game assets) or the shell cannot launch one.
                        // The extraction itself succeeded, so this is an expected
                        // condition: keep it below the automatic bug-report threshold
                        // and take the user to the extracted file instead.
                        _logger.Information(ex,
                            "Windows could not open {TempPath} with a default application; revealing the file in the file manager instead",
                            tempPath);
                        RevealInFileManager(tempPath);
                        ShowErrorSafe(
                            "Windows does not have a default application to open this file type.\n\n" +
                            $"The file was extracted to:\n{tempPath}\n\n" +
                            "Open it from that folder with a suitable application.");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Failed to open extracted file: {TempPath}", tempPath);
                        ShowErrorSafe($"Failed to open file: {ex.Message}");
                    }
                });

                // Schedule delayed cleanup of the extracted file after the viewer had a
                // chance to open it.
                ScheduleTempFolderCleanup(tempFolder, TimeSpan.FromSeconds(30));
            }
            catch (OperationCanceledException)
            {
                _logger.Debug("Extracting and opening file from image was canceled: {FileName}", fileName);
            }
            catch (Exception ex) when (ImageErrorClassifier.IsEnvironmentalIoError(ex))
            {
                // Disk-full, access-denied, and unreadable-path failures are environmental,
                // not defects, so they are logged below the automatic bug-report threshold.
                _logger.Information(ex, "Could not extract and open the file from the image: {FileName}", fileName);
                await ShowErrorSafeAsync($"Could not extract and open the file: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to extract and open file from image: {FileName}", fileName);
                await ShowErrorSafeAsync($"Failed to extract and open file: {ex.Message}");
            }
            finally
            {
                // A partial or canceled extraction leaves files no other process can use;
                // remove them so they do not accumulate in the temp folder.
                if (!extracted && tempFolder != null)
                {
                    ScheduleTempFolderCleanup(tempFolder, TimeSpan.FromSeconds(5));
                }
            }
        }, token);
    }

    /// <summary>
    ///     Shows an extracted file in the platform file manager as a best-effort fallback
    ///     when Windows has no default application for it.
    /// </summary>
    /// <param name="filePath">File to reveal.</param>
    private void RevealInFileManager(string filePath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
                { UseShellExecute = true });
                return;
            }

            // Linux/macOS: open the containing folder with the system file manager.
            var folder = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(folder))
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not reveal {FilePath} in the file manager", filePath);
        }
    }

    /// <summary>
    ///     Deletes a temporary extraction folder after a delay, so a process that is still
    ///     reading the extracted files (file viewer, drag-and-drop target) is not disturbed.
    ///     Failures are logged and left for the startup cleanup.
    /// </summary>
    /// <param name="tempFolder">Temporary folder to delete.</param>
    /// <param name="delay">Delay before the folder is deleted.</param>
    private void ScheduleTempFolderCleanup(string tempFolder, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not delete explorer temp folder: {TempFolder}", tempFolder);
            }
        }, CancellationToken.None);
    }

    /// <summary>Records the pointer position that may start a drag gesture.</summary>
    /// <param name="sender">The grid that raised the event.</param>
    /// <param name="e">The pointer event data.</param>
    private void ExplorerDataGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only the primary button can start a file drag; storing a right- or middle-click
        // would let the next pointer move start a drag that the user never initiated.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragPointerArgs = null;
            return;
        }

        _dragStartPoint = e.GetPosition(null);
        _dragPointerArgs = e;
    }

    /// <summary>Ends the drag gesture candidate when the primary button is released.</summary>
    /// <param name="sender">The grid that raised the event.</param>
    /// <param name="e">The pointer event data.</param>
    private void ExplorerDataGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragPointerArgs = null;
    }

    /// <summary>Ends the drag gesture candidate when the pointer capture is lost.</summary>
    /// <param name="sender">The grid that raised the event.</param>
    /// <param name="e">The capture event data.</param>
    private void ExplorerDataGrid_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _dragPointerArgs = null;
    }

    /// <summary>
    /// Builds the pointer-pressed event handed to the platform drag source. The control
    /// that raised the original press can be detached from the visual tree by DataGrid
    /// virtualization or an items refresh before the drag threshold is crossed; X11
    /// rejects such a trigger event because it cannot find the owning top level.
    /// </summary>
    /// <param name="current">Current pointer-move event used for the fallback coordinates.</param>
    /// <returns>The trigger event, or <c>null</c> when the gesture is no longer valid.</returns>
    private PointerPressedEventArgs? CreateDragTriggerEvent(PointerEventArgs current)
    {
        var pressed = _dragPointerArgs;
        if (pressed is null) return null;

        if (GetTopLevel(pressed.Source as Visual) is not null) return pressed;

        // Re-root the trigger at the window, which is always attached to a top level.
        if (GetTopLevel(this) is null) return null;

        return new PointerPressedEventArgs(
            this,
            pressed.Pointer,
            this,
            current.GetPosition(this),
            pressed.Timestamp,
            pressed.GetCurrentPoint(this).Properties,
            pressed.KeyModifiers,
            pressed.ClickCount);
    }

    /// <summary>Starts a file drag once the pointer moved far enough with the left button held.</summary>
    /// <param name="sender">The grid that raised the event.</param>
    /// <param name="e">The pointer event data.</param>
    private async void ExplorerDataGrid_PointerMovedAsync(object? sender, PointerEventArgs e)
    {
        try
        {
            if (_isDragging || _dragPointerArgs is null) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                // The button was released without a PointerReleased event reaching the
                // grid (for example after a capture change), so the gesture is stale.
                _dragPointerArgs = null;
                return;
            }

            var currentPosition = e.GetPosition(null);
            var diff = _dragStartPoint - currentPosition;

            // Check if the pointer has moved enough to start a drag operation
            if (Math.Abs(diff.X) < MinimumDragDistance && Math.Abs(diff.Y) < MinimumDragDistance) return;

            // Get selected file items (not directories)
            var selectedItems = ExplorerDataGrid.SelectedItems?
                .Cast<XisoExplorerItem>()
                .Where(static i => !i.IsDirectory)
                .ToList();

            if (selectedItems is not { Count: > 0 }) return;

            var triggerEvent = CreateDragTriggerEvent(e);
            if (triggerEvent is null) return;

            // One drag gesture per press: do not reuse the stored event after this attempt.
            _dragPointerArgs = null;

            CancellationToken token;
            try
            {
                token = _explorerCts.Token;
            }
            catch (ObjectDisposedException ex)
            {
                // The window is shutting down.
                _logger.Debug(ex, "Skipping drag operation because the window is shutting down");
                return;
            }

            string? tempFolder = null;
            var dragStarted = false;
            try
            {
                _isDragging = true;
                // Extract files to temp folder for drag operation
                var totalSize = selectedItems.Sum(static i => i.Entry.Size);
                var folder = ResolveExplorerTempDirectory(totalSize, "ImageExplorer_DragDrop");
                tempFolder = folder;
                Directory.CreateDirectory(folder);

                var tempFiles = new List<string>();

                // Perform extraction while the explorer is held in use.
                var copied = await UseExplorerAsync(explorer =>
                {
                    foreach (var item in selectedItems)
                    {
                        var tempPath = Path.Combine(folder, item.Name);
                        explorer.CopyOut(item.Entry.FullPath, tempPath);
                        tempFiles.Add(tempPath);
                    }
                }, token);

                if (!copied) return;

                // Start drag operation with the file drop list
                var topLevel = GetTopLevel(this);
                if (topLevel is not null && tempFiles.Count > 0)
                {
                    var data = new DataTransfer();
                    var addedCount = 0;
                    foreach (var tempFile in tempFiles)
                    {
                        var storageItem = await topLevel.StorageProvider.TryGetFileFromPathAsync(tempFile);
                        if (storageItem is not null)
                        {
                            data.Add(DataTransferItem.CreateFile(storageItem));
                            addedCount++;
                        }
                    }

                    if (addedCount > 0)
                    {
                        dragStarted = true;
                        await DragDrop.DoDragDropAsync(triggerEvent, data, DragDropEffects.Copy);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Debug("Drag operation canceled");
            }
            catch (Exception ex) when (ImageErrorClassifier.IsEnvironmentalIoError(ex))
            {
                // Disk-full, access-denied, and unreadable-path failures are environmental,
                // not defects, so they are logged below the automatic bug-report threshold.
                _logger.Information(ex, "Could not prepare files for drag operation: {Message}", ex.Message);
                await ShowErrorSafeAsync($"Could not prepare files for drag operation: {ex.Message}");
            }
            catch (Exception ex) when (ImageErrorClassifier.IsUserInputImageError(ex))
            {
                // Truncated or corrupt entries are expected input problems, not defects.
                _logger.Information(ex, "Could not read the selected files for the drag operation: {Message}",
                    ex.Message);
                await ShowErrorSafeAsync($"Could not read the selected files from the image: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to prepare files for drag operation");
                await ShowErrorSafeAsync($"Failed to prepare files for drag operation: {ex.Message}");
            }
            finally
            {
                // The drop target (for example File Explorer) copies the dropped files
                // asynchronously after the drop returns, so deleting immediately can
                // truncate the copy. Keep the files for a while and let the startup
                // cleanup collect anything left behind after a crash.
                if (tempFolder != null)
                {
                    ScheduleTempFolderCleanup(tempFolder,
                        dragStarted ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5));
                }

                _isDragging = false;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Drag operation failed");
            await ShowErrorSafeAsync($"Drag operation failed: {ex.Message}");
        }
    }

    /// <summary>File picker patterns matching every file.</summary>
    private static readonly string[] OptionsArray3 = ["*"];

    /// <summary>File picker patterns matching CHD images.</summary>
    private static readonly string[] OptionsArray2 = ["*.chd"];

    /// <summary>File picker patterns matching ZAR archives.</summary>
    private static readonly string[] OptionsArray1 = ["*.zar"];

    /// <summary>File picker patterns matching CSO images.</summary>
    private static readonly string[] OptionsArray0 = ["*.cso"];

    /// <summary>File picker patterns matching CCI/CCIM images.</summary>
    private static readonly string[] OptionsArray4 = ["*.cci"];

    /// <summary>File picker patterns matching ISO images.</summary>
    private static readonly string[] OptionsArray = ["*.iso", "*.xiso", "*.img"];

    /// <summary>File picker patterns matching all supported Xbox images.</summary>
    private static readonly string[] Options =
        ["*.iso", "*.xiso", "*.img", "*.cso", "*.cci", "*.zar", "*.chd"];

    /// <summary>Navigates to the parent directory of the explorer view.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void ExplorerUpButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (string.Equals(_currentInternalPath, "/", StringComparison.Ordinal)) return;

            await LoadDirectoryAsync(ImagePaths.GetParent(_currentInternalPath));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in method ExplorerUpButton_Click");
        }
    }

    /// <summary>Chooses and creates a temporary folder with enough free space for an extraction.</summary>
    /// <param name="requiredSize">Number of bytes the extraction needs.</param>
    /// <param name="tempSubfolder">Subfolder name used under the chosen drive's temp path.</param>
    /// <returns>Path of the created temporary folder.</returns>
    private string ResolveExplorerTempDirectory(long requiredSize, string tempSubfolder)
    {
        // Reuse the shared resolver: it applies the same safety buffer and drive selection as
        // the batch pipeline, never falls back to a drive known to be full, and creates the
        // folder so an unwritable drive is skipped before the extraction starts.
        return PathHelper.ResolveTempDirectory(requiredSize, tempSubfolder, _diskMonitorService);
    }
}