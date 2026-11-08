using Serilog;
using XISOSharp;
using XISOSharp.Models;
using XISOStudio.Interfaces;

namespace XISOStudio.Services;

/// <summary>
/// Compares a converted image against its source through <see cref="XisoValidator"/> and
/// applies the application's one deliberate transformation: a removed <c>$SystemUpdate</c>
/// folder must not count as a missing-file failure.
/// </summary>
public sealed class ConversionValidationService : IConversionValidationService
{
    /// <summary>Folder the conversion filter removes when the user enables "Skip $SystemUpdate".</summary>
    private const string SystemUpdateFolder = "$SystemUpdate";

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversionValidationService"/> class.
    /// </summary>
    /// <param name="logger">Logger used for diagnostics.</param>
    public ConversionValidationService(ILogger logger)
    {
        _logger = logger.ForContext<ConversionValidationService>();
    }

    /// <inheritdoc/>
    public ValidationResult ValidateConversion(string sourcePath, string outputPath, bool verifyChecksums,
        bool ignoreSystemUpdate = false)
    {
        var result = XisoValidator.ValidateConversion(sourcePath, outputPath, verifyChecksums);

        if (!ignoreSystemUpdate || result.Passed) return result;

        // The filter removes $SystemUpdate entries by design. Every other difference still
        // fails the conversion, so filter only those missing paths and re-derive the verdict.
        var issues = result.Issues.Where(static issue => !IsRemovedSystemUpdateEntry(issue)).ToList();
        if (issues.Count == result.Issues.Count) return result;

        _logger.Debug("Ignored {Count} expected $SystemUpdate difference(s) during validation",
            result.Issues.Count - issues.Count);

        return result with { Passed = issues.Count == 0, Issues = issues };
    }

    /// <summary>
    /// Returns true for a missing-output entry that lives inside the removed
    /// <c>$SystemUpdate</c> folder.
    /// </summary>
    /// <param name="issue">Validation issue to classify.</param>
    /// <returns><c>true</c> when the issue is an expected <c>$SystemUpdate</c> removal.</returns>
    private static bool IsRemovedSystemUpdateEntry(ValidationIssue issue)
    {
        if (issue.Type != ValidationIssueType.MissingInOutput) return false;

        var path = issue.Path.Replace('\\', '/').TrimStart('/');
        return path.Equals(SystemUpdateFolder, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(SystemUpdateFolder + "/", StringComparison.OrdinalIgnoreCase);
    }
}
