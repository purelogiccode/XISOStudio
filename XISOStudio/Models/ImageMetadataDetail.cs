namespace XISOStudio.Models;

/// <summary>
/// One label/value row in the explorer's container-details section (for example
/// <c>Disc layout</c> / <c>GLOBAL (XGD2)</c> or <c>SHA-1</c> / a hex digest).
/// </summary>
/// <param name="Label">Short field name shown in the left column.</param>
/// <param name="Value">Human-readable value shown in the right column.</param>
public sealed record ImageMetadataDetail(string Label, string Value);
