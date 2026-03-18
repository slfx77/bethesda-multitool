namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Parsed Hair (HAIR) record.
///     Hair styles available for NPC character generation.
/// </summary>
public record HairRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }

    /// <summary>Hair mesh model path from MODL subrecord.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Hair texture path from ICON subrecord.</summary>
    public string? TexturePath { get; init; }

    /// <summary>Hair flags from DATA subrecord.</summary>
    public byte Flags { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }

    /// <summary>Whether this hair style can be chosen by players.</summary>
    public bool IsPlayable => (Flags & 0x01) != 0;
}
