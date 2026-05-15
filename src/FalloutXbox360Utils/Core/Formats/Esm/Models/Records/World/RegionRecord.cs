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

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
