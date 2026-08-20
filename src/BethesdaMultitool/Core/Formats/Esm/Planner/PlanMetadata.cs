using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Formats.Esm.Planner;

/// <summary>
///     Plan-level metadata. The writer reads <see cref="NextObjectId" /> when emitting the
///     TES4 HEDR subrecord; the rest is for diagnostics + audit.
/// </summary>
public sealed record PlanMetadata
{
    /// <summary>
    ///     The smallest local FormID whose allocator slot has not been consumed. This
    ///     includes explicit non-emitting <see cref="FormIdReservation" /> holes as well as
    ///     live <see cref="RecordDisposition.New" /> records. Goes into TES4 HEDR's
    ///     <c>NextObjectId</c> field so GECK / xEdit continue after the true high-water mark.
    /// </summary>
    public required uint NextObjectId { get; init; }

    /// <summary>
    ///     Path to the master ESM the plan was built against. Stored for audit; the writer
    ///     does not need it (the master byte stream comes via <c>MasterRecordIndex</c>).
    /// </summary>
    public string? MasterPath { get; init; }

    /// <summary>
    ///     The registered record-type catalog used to build this plan. Production fills it
    ///     from <c>PlannedEncoders.KnownRecordTypes</c>; synthetic plans may provide a subset
    ///     to exercise bounded writer and reference-policy behavior.
    /// </summary>
    public required ImmutableHashSet<string> PlannerCoverage { get; init; }
}
