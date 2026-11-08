using XISOStudio.Interfaces;
using Serilog;
using GodHeader = StfsSharp.GodHeader;

namespace XISOStudio.Services;

/// <summary>
/// Opens an <see cref="IImageExplorer"/> for a supported image path: single-file STFS
/// packages (XBLA/XBLIG) with StfsSharp, ZAR archives with ZArchiveSharp, CHD images with
/// CHDSharp (decompressed on demand), and everything else (plain ISO, CISO, CCI, GOD/SVOD)
/// with XISOSharp.
/// </summary>
public static class ImageExplorerFactory
{
    /// <summary>
    /// Opens an image explorer appropriate for the format of the specified file.
    /// </summary>
    /// <param name="imagePath">Path of the image to open.</param>
    /// <param name="logger">Optional logger used to record failures.</param>
    /// <returns>An explorer for the image, selected by its file extension.</returns>
    public static IImageExplorer Open(string imagePath, ILogger? logger = null)
    {
        try
        {
            var resolvedPath = ResolveContainerPath(imagePath);

            // Single-file STFS packages (XBLA/XBLIG) are extensionless, so probe their
            // XContent header before the extension-based dispatch.
            if (StfsSharp.StfsPackage.IsStfsPackage(resolvedPath))
                return new StfsImageExplorer(resolvedPath, logger);

            var extension = Path.GetExtension(resolvedPath);
            if (extension.Equals(".zar", StringComparison.OrdinalIgnoreCase))
                return new ZarImageExplorer(resolvedPath, logger);

            if (extension.Equals(".chd", StringComparison.OrdinalIgnoreCase))
                return new ChdImageExplorer(resolvedPath, logger);

            return new XisoImageExplorer(resolvedPath, logger);
        }
        catch (Exception ex)
        {
            // Invalid, missing, or unreadable user-selected files are expected conditions,
            // so the failure is logged below the automatic bug-report threshold.
            logger?.Information(ex, "Failed to open an image explorer: {Message}",
                HostIoErrorDescriber.TryDescribe(ex, "opening the image", imagePath) ?? ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Resolves a GOD/SVOD <c>.data</c> directory or a parent folder holding exactly
    /// one GOD header to the canonical header path. Other paths are returned unchanged.
    /// </summary>
    private static string ResolveContainerPath(string imagePath)
    {
        if (File.Exists(imagePath) || !Directory.Exists(imagePath))
        {
            return imagePath;
        }

        if (GodHeader.TryReadVolume(imagePath, out var volume))
        {
            return volume.HeaderPath;
        }

        var headers = Directory.EnumerateFiles(imagePath, "*", SearchOption.TopDirectoryOnly)
            .Where(static file => GodHeader.IsGodContainer(file))
            .Take(2)
            .ToList();
        return headers.Count == 1 ? headers[0] : imagePath;
    }
}