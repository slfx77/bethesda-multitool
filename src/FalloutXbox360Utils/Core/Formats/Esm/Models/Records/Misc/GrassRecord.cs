namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Grass (GRAS) record. Referenced by <see cref="LandscapeTextureRecord.GrassFormIds" />
///     via LTEX <c>GNAM</c> subrecords — each LTEX may list grass meshes the engine scatters
///     across terrain quadrants painted with that texture.
/// </summary>
public record GrassRecord
{
    public uint FormId { get; init; }

    public string? EditorId { get; init; }

    public ObjectBounds? Bounds { get; init; }

    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Model bound radius (MODB subrecord, single float).</summary>
    public float? ModelBound { get; init; }

    /// <summary>Model texture data (MODT subrecord, opaque binary blob — unparsed).</summary>
    public byte[]? ModelTextureData { get; init; }

    /// <summary>Grass-engine parameters (DATA subrecord, 32 bytes).</summary>
    public GrassData? Data { get; init; }

    public long Offset { get; init; }

    public bool IsBigEndian { get; init; }
}

/// <summary>
///     GRAS DATA payload (32 bytes). Mirrors the
///     <see cref="FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema.SubrecordCellAndMiscSchemas" />
///     DATA/GRAS schema and fopdoc.
/// </summary>
public record GrassData
{
    /// <summary>Maximum density per cell quadrant.</summary>
    public byte Density { get; init; }

    /// <summary>Minimum terrain slope (degrees) where grass spawns.</summary>
    public byte MinSlope { get; init; }

    /// <summary>Maximum terrain slope (degrees) where grass spawns.</summary>
    public byte MaxSlope { get; init; }

    /// <summary>Vertical offset from water surface (in units).</summary>
    public ushort UnitsFromWaterAmount { get; init; }

    /// <summary>Above/at-or-below water sentinel (per fopdoc enum).</summary>
    public uint UnitsFromWaterType { get; init; }

    /// <summary>Random XY position jitter (units).</summary>
    public float PositionRange { get; init; }

    /// <summary>Random Z height variation.</summary>
    public float HeightRange { get; init; }

    /// <summary>Random colour tint variation.</summary>
    public float ColorRange { get; init; }

    /// <summary>Wind wave period (seconds).</summary>
    public float WavePeriod { get; init; }

    /// <summary>Per-bit grass flags (see fopdoc).</summary>
    public byte Flags { get; init; }
}
