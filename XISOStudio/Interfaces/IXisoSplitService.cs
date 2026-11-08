namespace XISOStudio.Interfaces;

/// <summary>
/// Splits a plain ISO into sector-aligned parts for FAT32/FATX drives and reassembles
/// split sets.
/// </summary>
public interface IXisoSplitService
{
    /// <summary>
    /// Splits <paramref name="imagePath"/> into <c>&lt;base&gt;.1.iso</c>, <c>.2.iso</c>, ….
    /// </summary>
    /// <param name="imagePath">Path of the plain ISO/XISO image to split.</param>
    /// <param name="outputBase">Output path base without the part suffix.</param>
    /// <param name="partSizeBytes">Maximum size of one part in bytes.</param>
    /// <param name="token">Token that cancels the operation.</param>
    /// <returns>The paths of the created parts.</returns>
    Task<IReadOnlyList<string>> SplitAsync(string imagePath, string outputBase, long partSizeBytes,
        CancellationToken token = default);

    /// <summary>
    /// Reassembles a split set starting at <paramref name="firstPartPath"/> and audits the
    /// result.
    /// </summary>
    /// <param name="firstPartPath">Path of the first part (for example <c>game.1.iso</c>).</param>
    /// <param name="outputPath">Path of the joined ISO.</param>
    /// <param name="token">Token that cancels the operation.</param>
    /// <returns>The path of the joined image.</returns>
    Task<string> JoinAsync(string firstPartPath, string outputPath, CancellationToken token = default);
}
