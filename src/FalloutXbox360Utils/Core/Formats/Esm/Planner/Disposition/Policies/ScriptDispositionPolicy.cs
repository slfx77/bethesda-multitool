using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition.Policies;

/// <summary>
///     Per-SCPT policy: refuses to emit an Override when the DMP-captured script would
///     downgrade master's compiled bytecode. Ports the decision half of legacy
///     <c>PluginBuilder.TryEncodeProvenScriptBearingOverride</c> (the encoding half moves
///     into the planned writer in Tier 6.5).
/// </summary>
/// <remarks>
///     <para>
///         The proto/early-build runtime sometimes captures populated Variables and
///         ReferencedObjects but truncated CompiledData — for VCGTutorialSCRIPT (Doc Mitchell
///         intro) proto has 0 bytes vs master's 2151; emitting the override neuters the
///         tutorial flow ("Travel onwards" → SPECIAL menu instead of advancing). This policy
///         enforces "proto SCDA must be at least as large as master's" before allowing
///         Override; otherwise we keep master intact.
///     </para>
///     <para>
///         The policy returns <c>null</c> when no decision applies (non-override entries or
///         non-truncated SCDA), letting the default disposition policy choose Override for
///         genuine DMP overrides.
///     </para>
/// </remarks>
public sealed class ScriptDispositionPolicy : IDispositionPolicy
{
    public IReadOnlySet<string> RecordTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "SCPT",
    };

    public DispositionDecision? Decide(CatalogEntry entry)
    {
        if (entry.Source != SourceKind.DmpOverride
            || entry.Model is not ScriptRecord script
            || entry.Master is null)
        {
            return null;
        }

        var protoCompiledSize = script.CompiledData?.Length ?? 0;
        var masterScda = entry.Master.Subrecords.FirstOrDefault(s => s.Signature == "SCDA");
        var masterCompiledSize = masterScda?.Data.Length ?? 0;
        if (masterCompiledSize > 0 && protoCompiledSize < masterCompiledSize)
        {
            return new DispositionDecision
            {
                Disposition = RecordDisposition.KeepMaster,
                Provenance = new PlanProvenance
                {
                    PolicyId = "ScriptDispositionPolicy.RefuseDowngrade",
                    Reason =
                        $"Proto SCDA {protoCompiledSize}B < master {masterCompiledSize}B; "
                        + "would downgrade compiled bytecode.",
                },
            };
        }

        return null;
    }
}
