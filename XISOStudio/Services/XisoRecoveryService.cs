using Serilog;
using XISOSharp;
using XISOSharp.Models;
using XISOStudio.Interfaces;

namespace XISOStudio.Services;

/// <summary>
/// Repairs and salvages damaged images with <see cref="XisoReader.Repair"/> and
/// <see cref="XisoReader.Salvage"/>.
/// </summary>
public sealed class XisoRecoveryService : IXisoRecoveryService
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="XisoRecoveryService"/> class.
    /// </summary>
    /// <param name="logger">Logger used for diagnostics.</param>
    public XisoRecoveryService(ILogger logger)
    {
        _logger = logger.ForContext<XisoRecoveryService>();
    }

    /// <inheritdoc/>
    public Task<RepairResult> RepairAsync(string imagePath, bool dryRun, CancellationToken token = default)
    {
        return Task.Run(() =>
        {
            _logger.Information("{Action} repairs on '{ImagePath}'...",
                dryRun ? "Previewing" : "Applying", imagePath);

            // The application always keeps the recoverable original: a failed or undesirable
            // repair must never be the only copy the user has.
            var result = XisoReader.Repair(imagePath, createBackup: true, dryRun);

            _logger.Information(
                "Repair {Outcome}: {FixedCount} issue(s) fixed, {RemainingCount} remaining.",
                result.Success ? "succeeded" : "did not fully succeed", result.Fixed.Count, result.Remaining.Count);

            foreach (var issue in result.Fixed)
            {
                _logger.Information("  fixed: {Issue:l}", issue);
            }

            foreach (var issue in result.Remaining)
            {
                _logger.Information("  remaining: {Issue:l}", issue);
            }

            if (!string.IsNullOrEmpty(result.BackupPath))
            {
                _logger.Information("Backup written to '{BackupPath}'.", result.BackupPath);
            }

            return result;
        }, token);
    }

    /// <inheritdoc/>
    public Task<SalvageResult> SalvageAsync(string imagePath, string? outputPath,
        CancellationToken token = default)
    {
        return Task.Run(() =>
        {
            _logger.Information("Salvaging '{ImagePath}' into a fresh ISO...", imagePath);

            var result = XisoReader.Salvage(imagePath, outputPath);

            _logger.Information(
                "Salvage {Outcome}: {CopiedCount} entries carried, {SkippedCount} skipped, output '{OutputPath}'.",
                result.Success ? "succeeded" : "produced an image that still fails the audit",
                result.Copied.Count, result.Skipped.Count, result.OutputPath);

            foreach (var skipped in result.Skipped)
            {
                _logger.Information("  skipped: {Reason:l}", skipped);
            }

            foreach (var issue in result.OutputIssues)
            {
                _logger.Information("  output issue: {Issue:l}", issue);
            }

            return result;
        }, token);
    }
}
