namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     STFS header information parsed from the container.
/// </summary>
internal sealed record StfsHeaderInfo(
    string Magic,
    uint ContentType,
    uint MetadataVersion,
    byte BlockSeparation,
    int FileTableBlockCount,
    int FileTableBlockNumber,
    int TotalAllocatedBlocks,
    int TotalUnallocatedBlocks)
{
    /// <summary>Whether the STFS header appears valid for a save game.</summary>
    public bool IsValidSaveGame => Magic.StartsWith("CON", StringComparison.Ordinal) ||
                                   Magic.StartsWith("LIVE", StringComparison.Ordinal) ||
                                   Magic.StartsWith("PIRS", StringComparison.Ordinal);
}
