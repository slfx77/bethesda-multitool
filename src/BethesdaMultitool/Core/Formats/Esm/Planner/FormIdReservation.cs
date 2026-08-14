namespace BethesdaMultitool.Core.Formats.Esm.Planner;

/// <summary>
///     One explicitly catalogued early-policy FormID reservation that must not be treated
///     as a live record identity. This audit shape is intentionally non-exhaustive: later
///     fail-closed planner passes can suppress other already-allocated records.
/// </summary>
public sealed record FormIdReservation
{
    /// <summary>The plugin-range FormID whose allocator slot was consumed.</summary>
    public required uint FormId { get; init; }

    /// <summary>The captured source identity that caused the reservation.</summary>
    public required uint SourceFormId { get; init; }

    /// <summary>The four-character record signature associated with the reservation.</summary>
    public required string RecordType { get; init; }

    /// <summary>Stable policy code explaining why the slot is reserved but not live.</summary>
    public required string PolicyId { get; init; }
}
