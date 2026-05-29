using System.Collections.Immutable;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Validation;

/// <summary>
///     Phase E — drop-propagation + containment ordering + invariant assertion.
///     Output is the final immutable plan suitable for the writer.
/// </summary>
public sealed class PlanValidator
{
    /// <summary>
    ///     Run all three validation steps and return the ordered record list + any
    ///     diagnostics surfaced along the way.
    /// </summary>
    public (ImmutableArray<RecordPlan> Records, ImmutableArray<PlanDiagnostic> Diagnostics) Validate(
        ImmutableArray<RecordPlan> records)
    {
        var propagated = DropPropagator.Propagate(records);
        var diagnostics = ResolutionInvariantChecker.Check(propagated).ToImmutableArray();

        var emitted = ImmutableArray.CreateBuilder<RecordPlan>(propagated.Length);
        foreach (var record in propagated)
        {
            if (record.Disposition == RecordDisposition.Skip)
            {
                continue;
            }

            emitted.Add(record);
        }

        var ordered = ContainmentOrderer.Order(emitted.ToImmutable());
        return (ordered, diagnostics);
    }
}
