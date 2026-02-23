namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     File Location Table from the save file header (110 bytes / 0x6E).
///     Provides byte offsets and counts for all body sections.
///     Offsets are absolute from the start of the save payload (the FO3SAVEGAME magic).
///     Layout (verified empirically across 12 saves):
///     Bytes 0-35:   9 × uint32 — defined fields below
///     Bytes 36-107: 18 × uint32 — reserved (always zero in FNV)
///     Bytes 108-109: 1 × uint16 — reserved (always zero)
///     Total: 110 bytes (0x6E)
///     GlobalDataTable1Count is always 12 (types 0-11).
///     GlobalDataTable2Count is always 1 (type 1000 = NVSE).
///     UnknownCount is always 0.
/// </summary>
public sealed class FileLocationTable
{
    /// <summary>Size of the File Location Table in bytes.</summary>
    public const int Size = 0x6E;

    public uint RefIdArrayCountOffset { get; init; }
    public uint UnknownTableOffset { get; init; }
    public uint GlobalDataTable1Offset { get; init; }
    public uint ChangedFormsOffset { get; init; }
    public uint GlobalDataTable2Offset { get; init; }
    public uint GlobalDataTable1Count { get; init; }
    public uint GlobalDataTable2Count { get; init; }
    public uint ChangedFormsCount { get; init; }
    public uint UnknownCount { get; init; }
}
