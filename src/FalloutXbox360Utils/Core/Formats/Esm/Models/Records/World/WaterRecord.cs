namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Water (WATR) record.
///     Defines water properties including visuals, sounds, and damage.
/// </summary>
public record WaterRecord
{
    /// <summary>FormID of the water record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name (FULL subrecord).</summary>
    public string? FullName { get; init; }

    /// <summary>Noise texture path (NNAM subrecord).</summary>
    public string? NoiseTexture { get; init; }

    /// <summary>Opacity (ANAM subrecord).</summary>
    public byte Opacity { get; init; }

    /// <summary>Water flags (FNAM subrecord).</summary>
    public byte[]? WaterFlags { get; init; }

    /// <summary>Sound FormID (SNAM subrecord).</summary>
    public uint? SoundFormId { get; init; }

    /// <summary>Damage per second (DATA subrecord, 2 bytes).</summary>
    public ushort Damage { get; init; }

    /// <summary>Visual properties from DNAM subrecord (196 bytes, parsed via schema).</summary>
    public Dictionary<string, object?>? VisualProperties { get; init; }

    /// <summary>Related water data from GNAM subrecord.</summary>
    public Dictionary<string, object?>? RelatedWater { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
