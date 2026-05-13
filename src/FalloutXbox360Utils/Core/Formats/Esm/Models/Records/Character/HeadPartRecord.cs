namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

/// <summary>
///     Head Part (HDPT) record. Assigns a NIF mesh to a head slot for character creation.
///     PDB struct: BGSHeadPart (96 bytes, FormType 0x09).
/// </summary>
public record HeadPartRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public string? ModelPath { get; init; }
    public byte[]? TextureHashData { get; init; }

    /// <summary>BGSHeadPart-specific flags (DATA subrecord, 1 byte). Bit 0 = playable.</summary>
    public byte Flags { get; init; }

    /// <summary>FormIDs of additional head parts that this part includes (HNAM list).</summary>
    public List<uint> ExtraParts { get; init; } = [];

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
