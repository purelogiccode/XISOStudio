using Serilog;
using XISOSharp;
using XISOStudio.Interfaces;

namespace XISOStudio.Services;

/// <summary>
/// Splits and joins plain ISO images through <see cref="XisoReader.SplitXiso"/> and
/// <see cref="XisoReader.JoinSplitXiso"/>.
/// </summary>
public sealed class XisoSplitService : IXisoSplitService
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="XisoSplitService"/> class.
    /// </summary>
    /// <param name="logger">Logger used for diagnostics.</param>
    public XisoSplitService(ILogger logger)
    {
        _logger = logger.ForContext<XisoSplitService>();
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> SplitAsync(string imagePath, string outputBase, long partSizeBytes,
        CancellationToken token = default)
    {
        return Task.Run(() =>
        {
            _logger.Information("Splitting '{ImagePath}' into parts of {PartSize}...", imagePath,
                Formatter.FormatBytes(partSizeBytes));

            var parts = XisoReader.SplitXiso(imagePath, outputBase, partSizeBytes, token);

            _logger.Information("Split '{ImagePath}' into {PartCount} part(s).", imagePath, parts.Count);
            foreach (var part in parts)
            {
                _logger.Information("  {PartPath}", part);
            }

            return parts;
        }, token);
    }

    /// <inheritdoc/>
    public Task<string> JoinAsync(string firstPartPath, string outputPath, CancellationToken token = default)
    {
        return Task.Run(() =>
        {
            _logger.Information("Joining split image starting at '{FirstPart}' into '{OutputPath}'...",
                firstPartPath, outputPath);

            var joinedPath = XisoReader.JoinSplitXiso(firstPartPath, outputPath, token);

            var audit = XisoReader.AuditXiso(joinedPath, requireOptimizedTag: false);
            if (audit.IsValid)
            {
                _logger.Information(
                    "Joined image passed validation ({FilesChecked} files, {DirsChecked} directories).",
                    audit.FilesChecked, audit.DirsChecked);
            }
            else
            {
                // The join itself succeeded; the assembled image is reported, not deleted.
                _logger.Information("Joined image failed structural validation: {Issues}",
                    string.Join("; ", audit.Issues));
            }

            return joinedPath;
        }, token);
    }
}
