using XISOStudio.Interfaces;
using Serilog;

namespace XISOStudio.Services;

/// <summary>
/// Opens an <see cref="IImageExplorer"/> for a supported image path: ZAR archives
/// are read with ZArchiveSharp, CHD images with CHDSharp (decompressed on demand),
/// and everything else (plain ISO, CISO) with XISOSharp.
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
            var extension = Path.GetExtension(imagePath);

            if (extension.Equals(".zar", StringComparison.OrdinalIgnoreCase))
                return new ZarImageExplorer(imagePath, logger);

            if (extension.Equals(".chd", StringComparison.OrdinalIgnoreCase))
                return new ChdImageExplorer(imagePath, logger);

            return new XisoImageExplorer(imagePath, logger);
        }
        catch (Exception ex)
        {
            logger?.Error(ex, "Failed to open an image explorer: {Message}", ex.Message);
            throw;
        }
    }
}