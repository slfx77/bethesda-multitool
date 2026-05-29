namespace FalloutXbox360Utils.Core.Formats.Esm.Planner;

/// <summary>
///     Plan-level metadata. The writer reads <see cref="NextObjectId" /> when emitting the
///     TES4 HEDR subrecord; the rest is for diagnostics + audit.
/// </summary>
public sealed record PlanMetadata
{
    /// <summary>
    ///     The smallest local FormID that was <i>not</i> allocated to any
    ///     <see cref="RecordDisposition.New" /> record. Goes into TES4 HEDR's
    ///     <c>NextObjectId</c> field so GECK / xEdit pick up allocation from the right place.
    /// </summary>
    public required uint NextObjectId { get; init; }

    /// <summary>
    ///     Path to the master ESM the plan was built against. Stored for audit; the writer
    ///     does not need it (the master byte stream comes via <c>MasterRecordIndex</c>).
    /// </summary>
    public string? MasterPath { get; init; }

    /// <summary>
    ///     The record-type set the planner was asked to handle on this run.
    ///     Mirror of <c>PluginBuildOptions.PlannerEnabledRecordTypes</c>. Empty during
    ///     Tier 0 (planner runs but produces no records); grows as tiers ship.
    /// </summary>
    public required System.Collections.Immutable.ImmutableHashSet<string> PlannerCoverage { get; init; }
}
