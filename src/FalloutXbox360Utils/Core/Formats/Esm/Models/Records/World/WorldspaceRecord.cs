namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed Worldspace from memory dump.
/// </summary>
public record WorldspaceRecord
{
    /// <summary>FormID of the WRLD record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID (e.g., "WastelandNV").</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Parent worldspace FormID (if any).</summary>
    public uint? ParentWorldspaceFormId { get; init; }

    /// <summary>Climate FormID (CNAM subrecord).</summary>
    public uint? ClimateFormId { get; init; }

    /// <summary>Water FormID (NAM2 subrecord).</summary>
    public uint? WaterFormId { get; init; }

    /// <summary>Default land height from DNAM subrecord.</summary>
    public float? DefaultLandHeight { get; init; }

    /// <summary>Default water height from DNAM subrecord (fallback for cells with sentinel XCLW).</summary>
    public float? DefaultWaterHeight { get; init; }

    /// <summary>Map usable width from MNAM subrecord (WORLD_MAP_DATA).</summary>
    public int? MapUsableWidth { get; init; }

    /// <summary>Map usable height from MNAM subrecord (WORLD_MAP_DATA).</summary>
    public int? MapUsableHeight { get; init; }

    /// <summary>Map NW corner cell X from MNAM subrecord.</summary>
    public short? MapNWCellX { get; init; }

    /// <summary>Map NW corner cell Y from MNAM subrecord.</summary>
    public short? MapNWCellY { get; init; }

    /// <summary>Map SE corner cell X from MNAM subrecord.</summary>
    public short? MapSECellX { get; init; }

    /// <summary>Map SE corner cell Y from MNAM subrecord.</summary>
    public short? MapSECellY { get; init; }

    /// <summary>World bounds minimum X from NAM0 subrecord.</summary>
    public float? BoundsMinX { get; init; }

    /// <summary>World bounds minimum Y from NAM0 subrecord.</summary>
    public float? BoundsMinY { get; init; }

    /// <summary>World bounds maximum X from NAM9 subrecord.</summary>
    public float? BoundsMaxX { get; init; }

    /// <summary>World bounds maximum Y from NAM9 subrecord.</summary>
    public float? BoundsMaxY { get; init; }

    /// <summary>Encounter zone FormID (XEZN subrecord).</summary>
    public uint? EncounterZoneFormId { get; init; }

    /// <summary>Cells belonging to this worldspace.</summary>
    public List<CellRecord> Cells { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
