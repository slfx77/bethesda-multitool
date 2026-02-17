namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

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

    /// <summary>Texture set flags (DNAM, 2 bytes).</summary>
    public ushort Flags { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
