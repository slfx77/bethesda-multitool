namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

/// <summary>
///     Voice Type (VTYP) record. Identifies a voice asset bucket used by NPC dialogue.
///     PDB struct: BGSVoiceType (44 bytes, FormType 0x5D).
/// </summary>
public record VoiceTypeRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }

    /// <summary>VOICE_TYPE_DATA byte at +40 (DNAM subrecord). Bit 0 = allow default dialogue, bit 1 = female.</summary>
    public byte Flags { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
