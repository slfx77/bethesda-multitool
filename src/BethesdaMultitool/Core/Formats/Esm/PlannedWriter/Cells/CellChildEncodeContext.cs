using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Shared inputs for per-child encoding in the planner cell writer: the plan's
///     remap/validity data, the master record dictionary and index, and the (optional)
///     pipeline stats sink for drop accounting.
/// </summary>
internal sealed record CellChildEncodeContext(
    EmitPlan Plan,
    IReadOnlyDictionary<uint, ParsedMainRecord> MasterByFormId,
    HashSet<uint> ValidFormIds,
    PluginBuildOptions Options,
    ConversionPipelineStats? Stats,
    MasterRecordIndex? MasterIndex,
    IReadOnlySet<uint> MasterRefFormIds,
    IReadOnlyDictionary<uint, string>? DmpBaseTypes);

/// <summary>
///     Per-cell mutable state accumulated while encoding one cell's children: the
///     merge-mode classification, marker-drop policy, coverage of master refs by emitted
///     overrides, and the count of genuine (DMP-sourced) children that survived encoding.
/// </summary>
internal sealed class CellEncodeState
{
    public required uint CellFormId { get; init; }
    public required CellMergeMode Mode { get; init; }
    public required bool IsMasterAnchored { get; init; }
    public required bool IsInterior { get; init; }
    public required bool DropRenderCullingMarkers { get; init; }

    /// <summary>Master ref FormIDs covered by an emitted override in this cell.</summary>
    public HashSet<uint> CoveredMasterRefFormIds { get; } = [];

    /// <summary>DMP-sourced child records that survived encoding (excludes carry-forwards).</summary>
    public int GenuineChildCount { get; set; }

    /// <summary>Of the genuine children, how many are NAVM records.</summary>
    public int GenuineNavmCount { get; set; }

    /// <summary>Emitted FormIDs of this cell's NAVM children (discarded on suppression).</summary>
    public List<uint> EmittedNavmFormIds { get; } = [];
}
