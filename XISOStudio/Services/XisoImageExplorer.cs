using XISOStudio.Interfaces;
using XISOStudio.Models;
using XISOSharp;
using Serilog;
using XISOSharp.Models;

namespace XISOStudio.Services;

/// <summary>
/// <see cref="IImageExplorer"/> over a plain ISO or CISO (<c>.cso</c>, including
/// split <c>.1.cso</c> part sets) image, backed by <see cref="XisoExplorer"/> in
/// keep-open mode (one image handle for the explorer's lifetime).
/// </summary>
internal sealed class XisoImageExplorer : IImageExplorer
{
    private readonly XisoExplorer _explorer;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="XisoImageExplorer"/> class and opens the
    /// image in keep-open mode, sharing reads and writes so an image held open by another
    /// process (sync client, antivirus, another reader) can still be browsed.
    /// </summary>
    /// <param name="imagePath">Path of the ISO or CISO image to explore.</param>
    /// <param name="logger">Optional logger used for diagnostics.</param>
    internal XisoImageExplorer(string imagePath, ILogger? logger = null)
    {
        _logger = logger?.ForContext<XisoImageExplorer>();
        _explorer = new XisoExplorer(imagePath, new XisoExplorerOptions
        {
            KeepOpen = true,
            // The explorer only reads. Widening the share mode is what lets it coexist
            // with writers (cloud sync clients, antivirus scanners, download managers)
            // that keep the image open while this running copy is being browsed.
            Share = FileShare.ReadWrite
        });
    }

    /// <inheritdoc/>
    public IReadOnlyList<ImageEntry> ListChildren(string internalPath)
    {
        try
        {
            return _explorer.ListChildren(internalPath)
                .Select(static node => new ImageEntry(node.Name, node.FullPath, node.IsDirectory, node.Size))
                .ToList();
        }
        catch (Exception ex)
        {
            // Expected input problems (corrupt image) and environmental failures (device
            // removed) are handled by the caller and must stay below the automatic
            // bug-report threshold.
            if (ImageErrorClassifier.IsUserInputImageError(ex) || ImageErrorClassifier.IsEnvironmentalIoError(ex))
            {
                _logger?.Information(ex, "Could not list '{InternalPath}' in the image: {Reason}", internalPath,
                    ex.Message);
            }
            else
            {
                _logger?.Error(ex, "Failed to list '{InternalPath}' in the image", internalPath);
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public void CopyOut(string internalPath, string destPath)
    {
        try
        {
            _explorer.CopyOut(internalPath, destPath);
        }
        catch (Exception ex)
        {
            // Disk-full, access-denied, and corrupt/truncated entries are expected
            // conditions: report them to the user but not as application defects.
            if (ImageErrorClassifier.IsUserInputImageError(ex) || ImageErrorClassifier.IsEnvironmentalIoError(ex))
            {
                _logger?.Information(ex, "Could not copy out '{InternalPath}' from the image: {Reason}", internalPath,
                    ex.Message);
            }
            else
            {
                _logger?.Error(ex, "Failed to copy out '{InternalPath}' from the image", internalPath);
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _explorer.Dispose();
    }
}