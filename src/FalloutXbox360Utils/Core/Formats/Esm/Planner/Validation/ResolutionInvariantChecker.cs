using System.Collections.Immutable;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Validation;

/// <summary>
///     Asserts the planner's "every non-Resolved reference carries a Reason" invariant.
///     If this ever fires the planner code has a bug — a phase produced an ambiguous output
///     without explaining why.
/// </summary>
public static class ResolutionInvariantChecker
{
    public static IEnumerable<PlanDiagnostic> Check(ImmutableArray<RecordPlan> records)
    {
        foreach (var record in records)
        {
            foreach (var resolved in record.References)
            {
                if (resolved.Action == ResolvedRefAction.Resolved)
                {
                    if (!resolved.FinalFormId.HasValue)
                    {
                        yield return new PlanDiagnostic
                        {
                            Kind = PlanDiagnosticKind.Warning,
                            Phase = "Validation",
                            Code = "invariant.resolved-missing-final",
                            RecordType = record.Type,
                            FormId = record.FormId,
                            Message =
                                $"ResolvedRef for {record.Type} 0x{record.FormId:X8} field {resolved.FieldPath} " +
                                "has Action=Resolved but FinalFormId is null.",
                        };
                    }

                    continue;
                }

                if (string.IsNullOrEmpty(resolved.Reason))
                {
                    yield return new PlanDiagnostic
                    {
                        Kind = PlanDiagnosticKind.Warning,
                        Phase = "Validation",
                        Code = "invariant.non-resolved-missing-reason",
                        RecordType = record.Type,
                        FormId = record.FormId,
                        Message =
                            $"ResolvedRef for {record.Type} 0x{record.FormId:X8} field {resolved.FieldPath} " +
                            $"has Action={resolved.Action} but no Reason.",
                    };
                }
            }
        }
    }
}
