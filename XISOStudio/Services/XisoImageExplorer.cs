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
    /// image in keep-open mode.
    /// </summary>
    /// <param name="imagePath">Path of the ISO or CISO image to explore.</param>
    /// <param name="logger">Optional logger used for diagnostics.</param>
    internal XisoImageExplorer(string imagePath, ILogger? logger = null)
    {
        _logger = logger?.ForContext<XisoImageExplorer>();
        _explorer = new XisoExplorer(imagePath, new XisoExplorerOptions { KeepOpen = true });
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
            _logger?.Error(ex, "Failed to list '{InternalPath}' in the image", internalPath);
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
            _logger?.Error(ex, "Failed to copy out '{InternalPath}' from the image", internalPath);
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _explorer.Dispose();
    }
}