using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Aggregated cross-dump record index: maps record types to FormIDs to per-dump formatted text.
/// </summary>
internal sealed class CrossDumpRecordIndex
{
    /// <summary>Ordered list of dump snapshots (sorted by date).</summary>
    internal List<DumpSnapshot> Dumps { get; } = [];

    /// <summary>
    ///     RecordType -> FormID -> (dumpIndex -> RecordReport).
    ///     Canonical structured comparison data used by HTML, JSON, and CSV output.
    ///     Populated by <see cref="CrossDumpAggregator.Aggregate" />.
     /// </summary>
    internal Dictionary<string, Dictionary<uint, Dictionary<int, RecordReport>>> StructuredRecords { get; } = [];

    /// <summary>
    ///     RecordType -> FormID -> GroupKey.
    ///     Used to split record type pages into sub-tables (e.g., cells by worldspace/interior).
    /// </summary>
    internal Dictionary<string, Dictionary<uint, string>> RecordGroups { get; } = [];

    /// <summary>
    ///     FormID -> (GridX, GridY) for cell records with grid coordinates.
    ///     Used to generate CSS grid tile maps per worldspace.
    /// </summary>
    internal Dictionary<uint, (int X, int Y)> CellGridCoords { get; } = [];

    /// <summary>
    ///     GroupKey -> (GridX, GridY) -> LandHeightmap for the latest available heightmap per cell.
    ///     Keyed by worldspace group name to avoid cross-worldspace coordinate collisions.
    /// </summary>
    internal Dictionary<string, Dictionary<(int X, int Y), LandHeightmap>> CellHeightmaps { get; } = [];

    /// <summary>
    ///     Raw LAND records from ESM files, used for generating complete heightmap images.
    ///     Populated directly from EsmRecordScanResult for reliable positioning.
    /// </summary>
    internal List<ExtractedLandRecord> EsmLandRecords { get; } = [];

    /// <summary>WorldspaceFormId -> EditorId mapping for LAND record worldspace resolution.</summary>
    internal Dictionary<uint, string> LandWorldspaceMap { get; } = [];

    /// <summary>
    ///     RecordType -> FormID -> key/value metadata for display (quest name, topic name, speaker, etc.).
    ///     Kept separate from <see cref="StructuredRecords" /> to avoid touching the report model or delta encoding.
    /// </summary>
    internal Dictionary<string, Dictionary<uint, Dictionary<string, string>>> RecordMetadata { get; } = [];
}
