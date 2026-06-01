namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Texture Set (TXST) record.
///     Defines a set of textures (diffuse, normal, glow, etc.) used by objects and terrain.
/// </summary>
public record TextureSetRecord
{
    /// <summary>FormID of the texture set record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Object bounds (OBND subrecord).</summary>
    public ObjectBounds? Bounds { get; init; }

    /// <summary>Diffuse/color map texture path (TX00).</summary>
    public string? DiffuseTexture { get; init; }

    /// <summary>Normal/bump map texture path (TX01).</summary>
    public string? NormalTexture { get; init; }

    /// <summary>Environment mask/reflection texture path (TX02).</summary>
    public string? EnvironmentTexture { get; init; }

    /// <summary>Glow/emissive map texture path (TX03).</summary>
    public string? GlowTexture { get; init; }

    /// <summary>Parallax/height map texture path (TX04).</summary>
    public string? ParallaxTexture { get; init; }

    /// <summary>Environment/cube map texture path (TX05).</summary>
    public string? EnvironmentMapTexture { get; init; }

    /// <summary>
    ///     Decal data (DODT subrecord, 36 bytes). Present on TXSTs that emit decals — blood
    ///     splatter, bullet impacts, weather-driven decals, etc. Pure terrain texture sets
    ///     typically have no DODT. Layout matches fopdoc / xEdit / SubrecordCellAndMiscSchemas.
    /// </summary>
    public TxstDecalData? DecalData { get; init; }

    /// <summary>Texture set flags (DNAM, 2 bytes).</summary>
    public ushort Flags { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Decal-data payload (TXST DODT subrecord, 36 bytes). Drives the in-engine decal
///     rendering — random size range, surface depth, shininess, parallax pass count, and
///     ARGB tint. Components match the field order in
///     <see cref="FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema.SubrecordCellAndMiscSchemas" />.
/// </summary>
public record TxstDecalData
{
    public float MinWidth { get; init; }
    public float MaxWidth { get; init; }
    public float MinHeight { get; init; }
    public float MaxHeight { get; init; }
    public float Depth { get; init; }
    public float Shininess { get; init; }
    public float ParallaxScale { get; init; }
    public byte ParallaxPasses { get; init; }
    public byte Flags { get; init; }
    public uint ColorArgb { get; init; }
}
