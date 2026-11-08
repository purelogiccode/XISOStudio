using XISOSharp.Models;

namespace XISOStudio.Interfaces;

/// <summary>
/// Compares a converted image against its source: every file must exist in both trees with
/// the same size and, when requested, the same SHA-256 hash. Supports ISO, split ISO, CISO,
/// CCI and ZAR on either side (CHD is verified through CHDSharp instead).
/// </summary>
public interface IConversionValidationService
{
    /// <summary>
    /// Validates <paramref name="outputPath"/> against <paramref name="sourcePath"/>.
    /// </summary>
    /// <param name="sourcePath">Path of the original image.</param>
    /// <param name="outputPath">Path of the converted image.</param>
    /// <param name="verifyChecksums">
    /// When <c>true</c>, hashes every file with SHA-256 in addition to comparing paths and sizes.
    /// </param>
    /// <param name="ignoreSystemUpdate">
    /// When <c>true</c>, files missing under <c>$SystemUpdate</c> are not reported: the caller
    /// deliberately removed that folder from the output.
    /// </param>
    /// <returns>The comparison result; <see cref="ValidationResult.Passed"/> is <c>false</c> on any mismatch.</returns>
    ValidationResult ValidateConversion(string sourcePath, string outputPath, bool verifyChecksums,
        bool ignoreSystemUpdate = false);
}
