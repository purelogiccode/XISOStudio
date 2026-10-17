namespace XISOStudio.Models;

/// <summary>
/// A progress update reported during a batch conversion or test operation. Individual
/// properties are optional: each report carries only the values that changed (a log line,
/// a status update or updated counters).
/// </summary>
public class BatchOperationProgress
{
    /// <summary>Gets or sets a log line to append to the UI log.</summary>
    public string? LogMessage { get; set; }

    /// <summary>Gets or sets the status text shown while a file is processed.</summary>
    public string? StatusText { get; set; }

    /// <summary>Gets or sets the total number of files in the batch.</summary>
    public int? TotalFiles { get; set; }

    /// <summary>Gets or sets the number of files processed so far.</summary>
    public int? ProcessedCount { get; set; }

    /// <summary>Gets or sets the number of successfully processed files.</summary>
    public int? SuccessCount { get; init; }

    /// <summary>Gets or sets the number of failed files.</summary>
    public int? FailedCount { get; init; }

    /// <summary>Gets or sets the number of skipped files.</summary>
    public int? SkippedCount { get; init; }

    /// <summary>Gets or sets the number of files detected as invalid images.</summary>
    public int? InvalidIsoCount { get; set; }

    /// <summary>Gets or sets the drive letter currently being read from or written to.</summary>
    public string? CurrentDrive { get; set; }

    /// <summary>Gets or sets a failed file path to add to the failure list.</summary>
    public string? FailedPathToAdd { get; set; }
}