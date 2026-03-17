namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Runtime worldspace data extracted from a TESWorldSpace struct in an Xbox 360 memory dump.
///     Contains the cell map (grid → cell FormID mapping) and persistent cell identification.
/// </summary>
public record RuntimeWorldspaceData
{
    /// <summary>FormID of the TESWorldSpace.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID captured from the runtime worldspace entry when available.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name captured from TESFullName when available.</summary>
    public string? FullName { get; init; }

    /// <summary>FormID of the persistent cell (from pPersistentCell pointer).</summary>
    public uint? PersistentCellFormId { get; init; }

    /// <summary>FormID of the parent worldspace (from pParentWorld pointer).</summary>
    public uint? ParentWorldFormId { get; init; }

    /// <summary>Climate FormID from the runtime TESWorldSpace.</summary>
    public uint? ClimateFormId { get; init; }

    /// <summary>Water FormID from the runtime TESWorldSpace.</summary>
    public uint? WaterFormId { get; init; }

    /// <summary>Default land height from the runtime TESWorldSpace.</summary>
    public float? DefaultLandHeight { get; init; }

    /// <summary>Default water height from the runtime TESWorldSpace.</summary>
    public float? DefaultWaterHeight { get; init; }

    /// <summary>Map usable width from the runtime TESWorldSpace.</summary>
    public int? MapUsableWidth { get; init; }

    /// <summary>Map usable height from the runtime TESWorldSpace.</summary>
    public int? MapUsableHeight { get; init; }

    /// <summary>Map NW cell X from the runtime TESWorldSpace.</summary>
    public short? MapNWCellX { get; init; }

    /// <summary>Map NW cell Y from the runtime TESWorldSpace.</summary>
    public short? MapNWCellY { get; init; }

    /// <summary>Map SE cell X from the runtime TESWorldSpace.</summary>
    public short? MapSECellX { get; init; }

    /// <summary>Map SE cell Y from the runtime TESWorldSpace.</summary>
    public short? MapSECellY { get; init; }

    /// <summary>World bounds minimum X from the runtime TESWorldSpace.</summary>
    public float? BoundsMinX { get; init; }

    /// <summary>World bounds minimum Y from the runtime TESWorldSpace.</summary>
    public float? BoundsMinY { get; init; }

    /// <summary>World bounds maximum X from the runtime TESWorldSpace.</summary>
    public float? BoundsMaxX { get; init; }

    /// <summary>World bounds maximum Y from the runtime TESWorldSpace.</summary>
    public float? BoundsMaxY { get; init; }

    /// <summary>Encounter zone FormID from the runtime TESWorldSpace.</summary>
    public uint? EncounterZoneFormId { get; init; }

    /// <summary>Runtime file offset of the TESWorldSpace when available.</summary>
    public long Offset { get; init; }

    /// <summary>All cells in this worldspace's pCellMap hash table.</summary>
    public List<RuntimeCellMapEntry> Cells { get; init; } = [];
}
