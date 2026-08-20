using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Disposition.Policies;

/// <summary>
///     First-priority diagnostic policy that leaves selected DMP overrides master-pure.
///     The planner validates that each requested FormID is a real override before invoking
///     the policy; the guard here protects direct policy use as well.
/// </summary>
public sealed class DiagnosticKeepMasterDispositionPolicy : IFirstPriorityDispositionPolicy
{
    private readonly ImmutableHashSet<uint> _formIds;

    public DiagnosticKeepMasterDispositionPolicy(IEnumerable<uint> formIds)
    {
        ArgumentNullException.ThrowIfNull(formIds);
        _formIds = formIds.ToImmutableHashSet();
    }

    public IReadOnlySet<string> RecordTypes { get; } = new HashSet<string>(StringComparer.Ordinal);

    public DispositionDecision? Decide(CatalogEntry entry)
    {
        var formId = entry.MasterFormId ?? entry.DmpFormId;
        if (formId is not uint id || !_formIds.Contains(id))
        {
            return null;
        }

        if (entry.Source != SourceKind.DmpOverride || entry.Master is null)
        {
            throw new InvalidOperationException(
                $"Diagnostic keep-master FormID 0x{id:X8} is not a DMP override with a master record.");
        }

        return new DispositionDecision
        {
            Disposition = RecordDisposition.KeepMaster,
            Provenance = new PlanProvenance
            {
                PolicyId = "DiagnosticKeepMasterDispositionPolicy",
                Reason = $"Diagnostic requested master-pure FormID 0x{id:X8}."
            }
        };
    }
}
