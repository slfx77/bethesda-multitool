using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition.Policies;

/// <summary>
///     Skips engine-owned runtime-state FormIDs (Player NPC, PlayerRef ACHR, world-clock
///     globals, etc.). Ports legacy <see cref="RuntimeStateRecordPolicy" /> to the planner's
///     policy-chain shape. Type-agnostic: runs before
///     <see cref="DefaultDispositionPolicy" /> in the fallback chain and returns null when
///     the entry's FormID isn't a runtime-state value, letting the default decide.
/// </summary>
public sealed class RuntimeStatePolicy : IDispositionPolicy
{
    public IReadOnlySet<string> RecordTypes { get; } = new HashSet<string>(StringComparer.Ordinal);

    public DispositionDecision? Decide(CatalogEntry entry)
    {
        var formId = entry.MasterFormId ?? entry.DmpFormId;
        if (formId is not uint id || !RuntimeStateRecordPolicy.IsRuntimeStateFormId(id))
        {
            return null;
        }

        return new DispositionDecision
        {
            Disposition = RecordDisposition.Skip,
            Provenance = new PlanProvenance
            {
                PolicyId = "RuntimeStatePolicy",
                Reason = $"FormID 0x{id:X8} is engine-owned runtime state; never emit as a plugin record.",
            },
        };
    }
}
