using System.Collections.Immutable;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     One intentional byte difference between legacy and planner output, registered with
///     a user-approved rationale. The parity harness consults
///     <see cref="MigrationDeltaRegistry" /> when a record-type byte diff appears; if the
///     diff matches a registered delta the harness passes (with a note); otherwise it fails.
/// </summary>
/// <remarks>
///     The companion human-readable log is [docs/planner/migration-deltas.md]. The C# entries
///     here are authoritative for tests; <see cref="MigrationDeltaRegistry" /> ships with the
///     same set the markdown documents.
/// </remarks>
public sealed record MigrationDelta
{
    /// <summary>Stable identifier in the form <c>DELTA-001</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Tier label that introduced the delta, e.g. <c>"6.2"</c>, <c>"6.3"</c>.</summary>
    public required string Tier { get; init; }

    /// <summary>Record types this delta covers (uppercase 4-char signatures).</summary>
    public required ImmutableHashSet<string> RecordTypes { get; init; }

    /// <summary>One-line reason; the markdown carries the full prose.</summary>
    public required string Reason { get; init; }

    /// <summary>User signoff date (UTC).</summary>
    public required DateOnly ApprovalDate { get; init; }

    /// <summary>
    ///     Optional FormID-scope predicate. When non-null, only diffs whose record FormID
    ///     satisfies the predicate are tolerated; when null, every FormID of the listed
    ///     record types is tolerated.
    /// </summary>
    public Func<uint, bool>? FormIdScope { get; init; }
}
