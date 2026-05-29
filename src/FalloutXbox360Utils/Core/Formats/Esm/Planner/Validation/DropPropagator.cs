using System.Collections.Immutable;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Validation;

/// <summary>
///     Propagates record-level <see cref="RecordDisposition.Skip" /> dispositions through
///     the plan: when the planner skips a record, references to it from other records become
///     dangling and must be re-resolved against the policy. One pass — phase D was already
///     transitive across the resolver, so a single propagation step suffices in practice.
/// </summary>
public static class DropPropagator
{
    /// <summary>
    ///     Walks <paramref name="records" /> once and re-marks any reference whose final
    ///     FormID belongs to a skipped record. The output replaces the input;
    ///     <c>PlanValidator</c> calls this before topological ordering.
    /// </summary>
    public static ImmutableArray<RecordPlan> Propagate(ImmutableArray<RecordPlan> records)
    {
        var skippedFormIds = new HashSet<uint>();
        foreach (var record in records)
        {
            if (record.Disposition == RecordDisposition.Skip)
            {
                skippedFormIds.Add(record.FormId);
            }
        }

        if (skippedFormIds.Count == 0)
        {
            return records;
        }

        var builder = ImmutableArray.CreateBuilder<RecordPlan>(records.Length);
        foreach (var record in records)
        {
            if (record.Disposition == RecordDisposition.Skip)
            {
                builder.Add(record);
                continue;
            }

            var rebuilt = RebuildReferences(record, skippedFormIds);
            builder.Add(rebuilt);
        }

        return builder.ToImmutable();
    }

    private static RecordPlan RebuildReferences(RecordPlan record, HashSet<uint> skipped)
    {
        var changed = false;
        var refs = ImmutableArray.CreateBuilder<ResolvedRef>(record.References.Length);

        foreach (var resolved in record.References)
        {
            if (resolved.Action == ResolvedRefAction.Resolved
                && resolved.FinalFormId is { } final
                && skipped.Contains(final))
            {
                changed = true;
                refs.Add(resolved with
                {
                    Action = ResolvedRefAction.DropSubrecord,
                    FinalFormId = null,
                    Reason = $"Cascade: original target 0x{final:X8} was Skip-dispositioned upstream.",
                });
                continue;
            }

            refs.Add(resolved);
        }

        return changed ? record with { References = refs.ToImmutable() } : record;
    }
}
