namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Static Collection (SCOL) record. Groups multiple instances of one or more STAT
///     bases under a single record; each part references one STAT and carries a packed
///     list of per-instance placements (position + rotation + scale).
/// </summary>
public record StaticCollectionRecord
{
    /// <summary>FormID of the SCOL record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Model file path (MODL subrecord; optional for SCOL).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Texture hash data from MODT subrecord (opaque bytes — engine validates).</summary>
    public byte[]? TextureHashData { get; init; }

    /// <summary>Object bounds (OBND subrecord; optional for SCOL).</summary>
    public ObjectBounds? Bounds { get; init; }

    /// <summary>Parts in stream order. Each part is one ONAM (base STAT) + one DATA (placements).</summary>
    public List<StaticCollectionPart> Parts { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     One part of a Static Collection — a single base STAT plus its packed placement list.
/// </summary>
public record StaticCollectionPart
{
    /// <summary>FormID of the base STAT this part instantiates (ONAM subrecord).</summary>
    public uint OnamFormId { get; init; }

    /// <summary>Placements for this part (DATA subrecord — 28 bytes per placement).</summary>
    public List<StaticCollectionPlacement> Placements { get; init; } = [];
}

/// <summary>
///     A single per-instance placement inside a SCOL part: 7 floats = X, Y, Z, RotX, RotY, RotZ, Scale.
/// </summary>
public readonly record struct StaticCollectionPlacement(
    float X, float Y, float Z, float RotX, float RotY, float RotZ, float Scale);
