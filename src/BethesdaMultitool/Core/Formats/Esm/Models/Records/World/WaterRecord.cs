namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

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

    /// <summary>
    ///     Oblivion's authored per-water surface/detail texture (TNAM). This is distinct from
    ///     WATER000's global animated NormalMap, which comes from the INI-selected
    ///     <c>textures\water\water00..31.dds</c> sequence.
    /// </summary>
    public string? SurfaceTexture { get; init; }

    /// <summary>
    ///     All authored water normal/surface textures in source order. Skyrim repeats NNAM three
    ///     times (one per normal layer), while FO4-family records contribute NAM2/NAM3/NAM4.
    ///     Oblivion TNAM is deliberately excluded because it is the separate detail input, not a
    ///     normal source. <see cref="NoiseTexture" /> remains the
    ///     first-entry compatibility projection.
    /// </summary>
    public IReadOnlyList<string> NormalTextures { get; init; } = Array.Empty<string>();

    /// <summary>
    ///     Skyrim's obsolete repeated NNAM texture set when a record also supplies the active
    ///     NAM2/NAM3/NAM4 set. Kept separately so parsing remains lossless without accidentally
    ///     binding the obsolete set to the three active shader samplers.
    /// </summary>
    public IReadOnlyList<string> LegacyNormalTextures { get; init; } = Array.Empty<string>();

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
