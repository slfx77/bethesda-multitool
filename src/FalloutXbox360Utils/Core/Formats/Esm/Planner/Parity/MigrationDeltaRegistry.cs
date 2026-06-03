using System.Collections.Immutable;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     Authoritative list of <see cref="MigrationDelta" /> entries. Parity tests consult
///     <see cref="IsTolerated" /> when a record-type byte diff appears between the planner
///     and legacy pipelines.
/// </summary>
public sealed class MigrationDeltaRegistry
{
    public ImmutableArray<MigrationDelta> Deltas { get; }

    public MigrationDeltaRegistry(ImmutableArray<MigrationDelta> deltas)
    {
        Deltas = deltas;
    }

    /// <summary>
    ///     The default registry shipped with the build. Starts empty at the Tier 6.6
    ///     baseline; new entries land here when a user-approved planner-vs-legacy byte
    ///     diff needs to pass the parity harness.
    /// </summary>
    public static MigrationDeltaRegistry Default { get; } =
        new(ImmutableArray<MigrationDelta>.Empty);

    /// <summary>
    ///     True when at least one registered delta covers this (record type, optional
    ///     FormID) pair. Used by the parity harness to decide whether to tolerate a byte
    ///     diff in a record type's GRUP output.
    /// </summary>
    public bool IsTolerated(string recordType, uint? formId = null)
    {
        if (string.IsNullOrEmpty(recordType))
        {
            return false;
        }

        foreach (var delta in Deltas)
        {
            if (!delta.RecordTypes.Contains(recordType))
            {
                continue;
            }

            if (delta.FormIdScope is null)
            {
                return true;
            }

            if (formId is uint id && delta.FormIdScope(id))
            {
                return true;
            }
        }

        return false;
    }
}
