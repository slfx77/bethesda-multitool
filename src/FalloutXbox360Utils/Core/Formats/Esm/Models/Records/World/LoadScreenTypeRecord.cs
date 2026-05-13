namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Load Screen Type (LSCT) record. Defines the visual layout of a load screen
///     (position, fonts, stats panel orientation). PDB struct: TESLoadScreenType
///     (128 bytes, FormType 0x6E). Single 88-byte LoadScreenType_Data block at +40.
/// </summary>
public record LoadScreenTypeRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }

    /// <summary>Layout fields from DATA subrecord (88 bytes, schema-parsed).</summary>
    public Dictionary<string, object?>? LayoutData { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
