using XISOSharp.Models;

namespace XISOStudio.Interfaces;

/// <summary>
/// Repairs safely patchable XISO defects in place and rebuilds readable images from corrupt
/// ones through the XISOSharp recovery APIs.
/// </summary>
public interface IXisoRecoveryService
{
    /// <summary>
    /// Repairs the audit's safely patchable issues (reserved attribute bits, a missing
    /// optimized tag, separators in names) in place. Always writes an <c>.old</c> backup.
    /// </summary>
    /// <param name="imagePath">Path of the plain ISO/XISO image to repair.</param>
    /// <param name="dryRun">When <c>true</c>, previews the fixes without writing.</param>
    /// <param name="token">Token that cancels the operation.</param>
    /// <returns>The repair outcome, including the fixed and remaining issue lists.</returns>
    Task<RepairResult> RepairAsync(string imagePath, bool dryRun, CancellationToken token = default);

    /// <summary>
    /// Rebuilds every reachable entry from a corrupt image into a fresh ISO. The source is
    /// never modified and CISO/CCI input is accepted.
    /// </summary>
    /// <param name="imagePath">Path of the damaged image.</param>
    /// <param name="outputPath">Destination ISO, or <c>null</c> for <c>&lt;stem&gt;.salvaged.iso</c>.</param>
    /// <param name="token">Token that cancels the operation.</param>
    /// <returns>The salvage outcome, including the copied/skipped counts and output verdict.</returns>
    Task<SalvageResult> SalvageAsync(string imagePath, string? outputPath,
        CancellationToken token = default);
}
