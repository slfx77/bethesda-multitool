namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     One standalone Starfield CUR3 record. Retail Starfield authors CUR3 as an <c>EDID,REFL</c>
///     full object; no inheritance or DIFF representation has been observed. A failed strict
///     decode remains explicit instead of being replaced with a zero or identity curve.
/// </summary>
public sealed record StarfieldCurve3DRecord
{
    /// <summary>CUR3 FormID.</summary>
    public uint FormId { get; init; }

    /// <summary>EDID, when present.</summary>
    public string? EditorId { get; init; }

    /// <summary>The exact decoded three-axis curve, or null when strict decoding failed.</summary>
    public StarfieldCurve3DDefinition? Definition { get; init; }

    /// <summary>Strict decode failure, or null for a valid definition.</summary>
    public string? DecodeFailure { get; init; }

    /// <summary>Offset in the source plugin where the CUR3 record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the enclosing record was detected as big-endian.</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Exact reflected <c>BGSCurve3DForm -&gt; BSFloat3DCurve</c> axes. This is source data only:
///     it deliberately does not define sampling, interpolation, concentration normalization, or
///     any water-shader equation.
/// </summary>
public sealed record StarfieldCurve3DDefinition(
    StarfieldFloatCurve XCurve,
    StarfieldFloatCurve YCurve,
    StarfieldFloatCurve ZCurve);

/// <summary>
///     Lossless typed preservation of one reflected <c>BSFloatCurve</c> USER/LIST pair. The six
///     scalar bounds/defaults, authored type/edge strings, interpolation flag, trailing serializer
///     word, and ordered controls are retained exactly. Raw bodies remain available so future
///     format work does not have to reconstruct unknown serializer metadata from interpreted data.
/// </summary>
public sealed record StarfieldFloatCurve
{
    public float MaxInput { get; init; }
    public float MinInput { get; init; }
    public float InputDistance { get; init; }
    public float MaxValue { get; init; }
    public float MinValue { get; init; }
    public float DefaultValue { get; init; }

    /// <summary>Authored <c>Type</c> metadata, such as retail's <c>CubicSpline</c>.</summary>
    public string CurveType { get; init; } = string.Empty;

    /// <summary>Authored <c>Edge</c> metadata, such as retail's <c>Clamp</c>.</summary>
    public string EdgeMode { get; init; } = string.Empty;

    public bool IsSampleInterpolating { get; init; }

    /// <summary>
    ///     The four-byte word following the authored USER metadata. It is one for every audited
    ///     retail axis, but its runtime meaning is intentionally not inferred.
    /// </summary>
    public uint SerializedControlListMarker { get; init; }

    /// <summary>Ordered, unresampled source control points.</summary>
    public IReadOnlyList<StarfieldFloatCurveControl> Controls { get; init; } = [];

    /// <summary>
    ///     Exact USER serializer payload after its declared/serialized type tokens. This contains
    ///     the six float bit patterns, both length-prefixed strings, Bool, and trailing word.
    /// </summary>
    public byte[] RawSerializedMetadata { get; init; } = [];

    /// <summary>Exact LIST body, including element-type token and declared count.</summary>
    public byte[] RawControlListBody { get; init; } = [];
}

/// <summary>One source-authored <c>BSFloatCurve::Control</c>, retained in serialized order.</summary>
public sealed record StarfieldFloatCurveControl(float Input, float Value);
