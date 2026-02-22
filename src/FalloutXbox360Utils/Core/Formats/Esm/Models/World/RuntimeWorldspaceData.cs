namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Runtime worldspace data extracted from a TESWorldSpace struct in an Xbox 360 memory dump.
///     Contains the cell map (grid → cell FormID mapping) and persistent cell identification.
/// </summary>
public record RuntimeWorldspaceData
{
    /// <summary>FormID of the TESWorldSpace.</summary>
    public uint FormId { get; init; }

    /// <summary>FormID of the persistent cell (from pPersistentCell pointer).</summary>
    public uint? PersistentCellFormId { get; init; }

    /// <summary>FormID of the parent worldspace (from pParentWorld pointer).</summary>
    public uint? ParentWorldFormId { get; init; }

    /// <summary>All cells in this worldspace's pCellMap hash table.</summary>
    public List<RuntimeCellMapEntry> Cells { get; init; } = [];
}
