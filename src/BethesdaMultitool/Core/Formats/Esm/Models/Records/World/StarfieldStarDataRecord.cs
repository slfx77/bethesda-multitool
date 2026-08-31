namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     One Starfield Creation Engine 2 <c>STDT</c> record. The envelope deliberately projects only
///     the four routing fields whose types have been established. Large presentation payloads such
///     as <c>PCCC</c> remain opaque and no stellar-rendering equations are inferred from them.
/// </summary>
public sealed record StarfieldStarDataRecord
{
    /// <summary>STDT FormID.</summary>
    public uint FormId { get; init; }

    /// <summary>EDID, when authored.</summary>
    public string? EditorId { get; init; }

    /// <summary>
    ///     Strict typed projection of the established routing fields. Null means decoding failed;
    ///     inspect <see cref="DecodeFailure" /> rather than treating the record as authored zeroes.
    /// </summary>
    public StarfieldStarDataRouting? Routing { get; init; }

    /// <summary>Strict subrecord-framing or typed-field failure, or null for a valid projection.</summary>
    public string? DecodeFailure { get; init; }

    /// <summary>Offset in the source plugin where the STDT record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the enclosing record was detected as big-endian.</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Lossless nullable projection of the established STDT routing fields. Null means the field
///     was omitted; zero means an explicit zero was authored. <see cref="SystemId" /> is a scalar
///     identifier and must never be load-order rebased. The other three values are FormIDs.
/// </summary>
public sealed record StarfieldStarDataRouting
{
    /// <summary><c>DNAM</c>: scalar star-system identifier. Authored zero is valid (Sol).</summary>
    public uint? SystemId { get; init; }

    /// <summary><c>SNAM</c>: optional binary-companion STDT FormID.</summary>
    public uint? BinaryStarFormId { get; init; }

    /// <summary><c>PNAM</c>: optional SUNP FormID.</summary>
    public uint? SunPresetFormId { get; init; }

    /// <summary><c>HNAM</c>: optional TODD FormID.</summary>
    public uint? TimeOfDayDataFormId { get; init; }
}
