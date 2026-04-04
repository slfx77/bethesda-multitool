namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

/// <summary>
///     Parsed Eyes (EYES) record.
///     Eye types available for NPC character generation.
/// </summary>
public record EyesRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }

    /// <summary>Eye texture path from ICON subrecord.</summary>
    public string? TexturePath { get; init; }

    /// <summary>Eye flags from DATA subrecord.</summary>
    public byte Flags { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }

    /// <summary>Whether this eye type can be chosen by players.</summary>
    public bool IsPlayable => (Flags & 0x01) != 0;
}
