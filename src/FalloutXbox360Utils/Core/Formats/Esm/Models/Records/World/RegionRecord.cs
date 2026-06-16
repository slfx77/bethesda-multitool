namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Region (REGN) record. Defines a worldspace area that triggers ambient
///     content (sounds, weather, spawns) when the player is inside. PDB struct:
///     TESRegion (72 bytes, FormType 0x37).
/// </summary>
public record RegionRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }

    /// <summary>Owning worldspace FormID (WNAM subrecord / pWorldSpace at +48).</summary>
    public uint WorldspaceFormId { get; init; }

    /// <summary>EmittanceColor RGB (NiColor at +60 — first 3 floats × 255).</summary>
    public byte EmittanceColorR { get; init; }

    public byte EmittanceColorG { get; init; }
    public byte EmittanceColorB { get; init; }

    /// <summary>Number of RDAT region-data tuples in the ESM-side record.</summary>
    public int DataBlockCount { get; init; }

    /// <summary>
    ///     RDAT region-data tuples in stream order. Each block is the 8-byte RDAT
    ///     header (type + flags/priority/reserved as a single uint32) plus the
    ///     typed payload subrecord(s) that follow it (RDOT/RDMP/RDGS/RDMD/RDSD/RDWT,
    ///     depending on Type). Captured as opaque payload bytes — the encoder
    ///     re-emits them verbatim, sidestepping per-type schema work.
    /// </summary>
    public List<RegionDataBlock> DataBlocks { get; init; } = [];

    /// <summary>
    ///     Per-region weather overrides decoded from RDWT payloads (12 bytes/entry:
    ///     Weather FormID, Chance, Global FormID — layout confirmed by the Xbox→PC
    ///     converter schema). A derived projection over <see cref="DataBlocks" />; the
    ///     encoder still round-trips the raw bytes, so this is read-only viewer/UI data
    ///     (feeds the weather selection in P4).
    /// </summary>
    public IReadOnlyList<RegionWeatherType> WeatherTypes { get; init; } = [];

    /// <summary>
    ///     Grass GRAS FormIDs decoded from RDGS payloads (8 bytes/entry: Grass FormID +
    ///     4 unused bytes — fopdoc layout, PROVISIONAL pending P6 confirmation). A derived
    ///     projection over <see cref="DataBlocks" />; the encoder still round-trips the raw
    ///     bytes. Feeds grass scatter in P6 (secondary to LTEX GrassFormIds).
    /// </summary>
    public IReadOnlyList<uint> GrassFormIds { get; init; } = [];

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     One RDWT weather-type entry: a candidate weather for this region with a
///     selection chance and an optional GLOB FormID that gates it.
/// </summary>
public readonly record struct RegionWeatherType(uint WeatherFormId, uint Chance, uint GlobalFormId);

/// <summary>
///     One RDAT region-data tuple. Header is 8 bytes: uint32 Type + uint32 Flags
///     (Flags packs the priority byte + reserved bytes per FNV layout; captured
///     as a single uint32 for verbatim round-trip).
/// </summary>
public readonly record struct RegionDataBlock(
    uint Type,
    uint Flags,
    List<RegionSubrecord> Payload);

/// <summary>
///     A single typed subrecord that follows an RDAT header (RDOT/RDMP/RDGS/RDMD/RDSD/RDWT).
///     Captured verbatim — neither the parser nor the encoder interprets the bytes.
/// </summary>
public readonly record struct RegionSubrecord(string Signature, byte[] Bytes);
