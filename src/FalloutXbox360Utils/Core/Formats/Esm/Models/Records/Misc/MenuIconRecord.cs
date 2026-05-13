namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Menu Icon (MICN) record. Stand-alone UI texture referenced by message boxes
///     and other menu screens (mirrors the per-record ICON subrecord seen elsewhere).
///     PDB struct: BGSMenuIcon (52 bytes, FormType 0x05). Only own-class field is
///     TextureName at +44.
/// </summary>
public record MenuIconRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }

    /// <summary>Texture path (ICON subrecord / TextureName BSStringT at +44).</summary>
    public string? IconPath { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
