namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion;

/// <summary>
///     Index of records and groups for conversion.
///     Built during first pass through the file.
/// </summary>
public sealed class ConversionIndex
{
    /// <summary>World records by order found.</summary>
    public List<WorldEntry> Worlds { get; } = [];

    /// <summary>All cells indexed by FormID.</summary>
    public Dictionary<uint, CellEntry> CellsById { get; } = [];

    /// <summary>Exterior cells grouped by parent world FormID.</summary>
    public Dictionary<uint, List<CellEntry>> ExteriorCellsByWorld { get; } = [];

    /// <summary>
    ///     Worldspace persistent cell (the CELL record that lives directly under each WRLD's World Children group).
    ///     In PC files this appears before exterior cell block/sub-block groups.
    /// </summary>
    public Dictionary<uint, CellEntry> WorldPersistentCellsByWorld { get; } = [];

    /// <summary>Interior cells (no parent world).</summary>
    public List<CellEntry> InteriorCells { get; } = [];

    /// <summary>Cell child groups (Persistent/Temporary/VWD) indexed by (cellId, grupType).</summary>
    public Dictionary<(uint cellId, int type), List<GrupEntry>> CellChildGroups { get; } = [];
}
