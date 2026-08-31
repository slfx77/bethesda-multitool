namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>Which proven top-level worldspace-list representation a Starfield PNDT carries.</summary>
public enum StarfieldPlanetDataPayloadKind
{
    /// <summary>No valid top-level CNAM or EOVR representation was established.</summary>
    Unknown,

    /// <summary>A complete authored CNAM list whose entries are 20-byte coordinate/WRLD tuples.</summary>
    Master,

    /// <summary>An ordered EOVR delta list whose entries append a one-byte operation to each tuple.</summary>
    Override
}

/// <summary>The two proven operation bytes authored by a PNDT EOVR list.</summary>
public enum StarfieldPlanetWorldspaceOperation : byte
{
    Removed = 0,
    Added = 1
}

/// <summary>
///     One top-level PNDT coordinate/WRLD tuple. Coordinates are stored by their exact IEEE-754
///     bit patterns so tuple identity does not collapse signed zero or distinct NaN payloads.
/// </summary>
/// <remarks>
///     <see cref="WorldspaceFormId" /> is a FormID. The coordinate bits are authored scalar data.
/// </remarks>
public readonly record struct StarfieldPlanetWorldspaceEntry(
    long LatitudeRawBits,
    long LongitudeRawBits,
    uint WorldspaceFormId)
{
    public StarfieldPlanetWorldspaceEntry(
        double latitude,
        double longitude,
        uint worldspaceFormId)
        : this(
            BitConverter.DoubleToInt64Bits(latitude),
            BitConverter.DoubleToInt64Bits(longitude),
            worldspaceFormId)
    {
    }

    public double Latitude => BitConverter.Int64BitsToDouble(LatitudeRawBits);
    public double Longitude => BitConverter.Int64BitsToDouble(LongitudeRawBits);
}

/// <summary>One authored EOVR operation, retained in source order.</summary>
public readonly record struct StarfieldPlanetWorldspaceDelta(
    StarfieldPlanetWorldspaceEntry Entry,
    StarfieldPlanetWorldspaceOperation Operation);

/// <summary>
///     Proven 16-byte body INAM. Only <see cref="AtmosphereFormId" /> is a FormID. The meanings of
///     the three finite scalar floats are intentionally left unassigned until independently proven.
/// </summary>
public sealed record StarfieldPlanetAtmosphereData(
    uint AtmosphereFormId,
    float UnknownFloat0,
    float UnknownFloat1,
    float UnknownFloat2);

/// <summary>
///     Proven marker-delimited PNDT body subset. <see cref="SystemId" />,
///     <see cref="ParentPlanetId" />, and <see cref="PlanetId" /> are scalar identifiers, not
///     FormIDs, and must never be passed through load-order FormID rebasing.
/// </summary>
public sealed record StarfieldPlanetBodyData(
    byte CnamRawValue,
    uint SystemId,
    uint ParentPlanetId,
    uint PlanetId,
    StarfieldPlanetAtmosphereData Atmosphere);

/// <summary>
///     Bounded decoded PNDT data. This represents authored plugin structure only; it does not claim
///     a Creation Engine 2 runtime planet-selection or atmosphere-selection algorithm.
/// </summary>
public sealed record StarfieldPlanetDataRecord
{
    /// <summary>PNDT FormID. Production load-order grouping and inverse WRLD lookup use this identity.</summary>
    public uint FormId { get; init; }

    /// <summary>EDID, when present.</summary>
    public string? EditorId { get; init; }

    /// <summary>Offset in the source plugin where the PNDT record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the enclosing record was detected as big-endian.</summary>
    public bool IsBigEndian { get; init; }

    public StarfieldPlanetDataPayloadKind PayloadKind { get; init; }

    /// <summary>Complete top-level CNAM entries for a <see cref="StarfieldPlanetDataPayloadKind.Master" />.</summary>
    public IReadOnlyList<StarfieldPlanetWorldspaceEntry> MasterWorldspaces { get; init; } =
        Array.Empty<StarfieldPlanetWorldspaceEntry>();

    /// <summary>Ordered top-level EOVR operations for an override record.</summary>
    public IReadOnlyList<StarfieldPlanetWorldspaceDelta> WorldspaceOverrides { get; init; } =
        Array.Empty<StarfieldPlanetWorldspaceDelta>();

    /// <summary>
    ///     Raw bits of an optional four-byte top-level GNAM scan multiplier. This field is unrelated
    ///     to the marker-delimited 12-byte GNAM identifiers and is not projected into them.
    /// </summary>
    public uint? TopLevelGnamRawBits { get; init; }

    /// <summary>The single complete BDST/BDED-delimited body, or null on decode failure.</summary>
    public StarfieldPlanetBodyData? Body { get; init; }

    /// <summary>Strict framing or typed-field failure; non-null records are malformed.</summary>
    public string? DecodeFailure { get; init; }
}
