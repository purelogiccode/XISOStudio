namespace XISOStudio.Tests.Fixtures;

/// <summary>
/// One file in a synthetic STFS package fixture.
/// </summary>
/// <param name="Path">Relative path using <c>/</c> separators.</param>
/// <param name="Data">File contents.</param>
internal sealed record SyntheticStfsFile(string Path, byte[] Data);
