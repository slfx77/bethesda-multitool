using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Fallout 4-family <c>BNDS</c> (Bendable Spline) base object. Unlike an ordinary static,
///     a BNDS has no MODL/NIF: the engine generates its visible tube from this definition and
///     the placed REFR's <c>XBSD</c> parameters.
/// </summary>
public sealed record BendableSplineRecord
{
    /// <summary>FormID of the BNDS base object.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID from EDID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Object bounds from OBND.</summary>
    public ObjectBounds? Bounds { get; init; }

    /// <summary>Authored DNAM defaults used to tessellate and animate placements.</summary>
    public BendableSplineDefinitionData? Data { get; init; }

    /// <summary>TXST FormID from TNAM.</summary>
    public uint? TextureSetFormId { get; init; }

    /// <summary>Offset in the source where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was decoded from big-endian data.</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     The fixed 32-byte BNDS DNAM payload documented by the Fallout 4 and Fallout 76 record
///     definitions. The raw U16 tile-mode value is retained rather than collapsed to a bool so
///     malformed or future authored values are not silently rewritten by the parser.
/// </summary>
public sealed record BendableSplineDefinitionData
{
    public float DefaultTileCount { get; init; }

    public ushort DefaultSliceCount { get; init; }

    public ushort TilesRelativeToLengthRaw { get; init; }

    public bool TilesRelativeToLength => TilesRelativeToLengthRaw != 0;

    public Vector4 DefaultColor { get; init; }

    public float WindSensibility { get; init; }

    public float WindFlexibility { get; init; }
}
