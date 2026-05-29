using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     One entry in the cell-phase A catalog. Same shape as the top-level
///     <see cref="CatalogEntry" /> but specialized for cells: the master side is a
///     <see cref="PcEsmCellContext" /> (grouping context, not a full record) and the DMP
///     side is a typed <see cref="CellRecord" /> model.
/// </summary>
public sealed record CellCatalogEntry
{
    /// <summary>The master / DMP FormID of this cell.</summary>
    public required uint CellFormId { get; init; }

    /// <summary>Where this entry came from.</summary>
    public required SourceKind Source { get; init; }

    /// <summary>
    ///     The master cell's grouping context — needed so the writer can place the cell
    ///     under the right Block / Sub-Block GRUP. Null for <see cref="SourceKind.DmpNew" />
    ///     (the planner synthesizes a context from grid coords).
    /// </summary>
    public PcEsmCellContext? MasterContext { get; init; }

    /// <summary>
    ///     The parsed master CELL record bytes (header + subrecords). Used by KeepMaster
    ///     and Override paths. Null for <see cref="SourceKind.DmpNew" />.
    /// </summary>
    public ParsedMainRecord? MasterRecord { get; init; }

    /// <summary>
    ///     The DMP-captured CELL model. Null for <see cref="SourceKind.MasterOnly" />.
    /// </summary>
    public CellRecord? DmpModel { get; init; }
}
