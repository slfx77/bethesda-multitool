using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Allocation;

/// <summary>
///     Converts known planner-New records that cannot safely serialize into explicit
///     reservations. Allocation has already happened so established downstream ordinals
///     remain stable, but the source aliases and allocated IDs are removed from liveness
///     before any reference is resolved.
/// </summary>
internal static class PlannedNonEmissionReservationPass
{
    internal static Result Apply(
        IReadOnlyList<(CatalogEntry Entry, DispositionDecision Decision)> decisions,
        IReadOnlyDictionary<uint, uint> topLevelAllocations,
        ImmutableDictionary<uint, uint> sourceToEmitted,
        ImmutableHashSet<uint> emittedFormIds)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(topLevelAllocations);

        var rewritten = decisions.ToArray();
        var liveAliases = sourceToEmitted.ToBuilder();
        var liveFormIds = emittedFormIds.ToBuilder();
        var reservations = ImmutableArray.CreateBuilder<FormIdReservation>();
        var diagnostics = ImmutableArray.CreateBuilder<PlanDiagnostic>();

        // AVIF is unconditional: FNV's actor-value table is engine-owned and loading a
        // plugin-new AVIF crashes. Remove these first so SCOL reachability cannot treat an
        // AVIF reservation as a live ONAM target.
        for (var index = 0; index < rewritten.Length; index++)
        {
            var (entry, decision) = rewritten[index];
            if (entry.Type != "AVIF" || decision.Disposition != RecordDisposition.New)
            {
                continue;
            }

            Reserve(
                rewritten,
                index,
                entry,
                decision,
                topLevelAllocations,
                liveAliases,
                liveFormIds,
                reservations,
                diagnostics,
                "PlannedNonEmissionReservationPass.NewAvif",
                "allocation.reserve.avif-engine-owned",
                "FNV actor values are engine-owned; a plugin-new AVIF crashes during load.");
        }

        // ScolEncoder intentionally declines a new SCOL when it has neither a baked model
        // nor any ONAM part reachable in the final liveness set. Settle that outcome here,
        // after AVIF pruning but still before reference resolution.
        var aliasesAfterAvifReservations = liveAliases.ToImmutable();
        var formIdsAfterAvifReservations = liveFormIds.ToImmutable();
        for (var index = 0; index < rewritten.Length; index++)
        {
            var (entry, decision) = rewritten[index];
            if (entry.Type != "SCOL"
                || decision.Disposition != RecordDisposition.New
                || entry.Model is not StaticCollectionRecord scol
                || ScolEncoder.CanEmitNew(
                    scol,
                    formIdsAfterAvifReservations,
                    formIdsAfterAvifReservations,
                    aliasesAfterAvifReservations))
            {
                continue;
            }

            Reserve(
                rewritten,
                index,
                entry,
                decision,
                topLevelAllocations,
                liveAliases,
                liveFormIds,
                reservations,
                diagnostics,
                "PlannedNonEmissionReservationPass.UnemittableScol",
                "allocation.reserve.scol-no-renderable-content",
                "SCOL has no baked model and no ONAM part whose target is live.");
        }

        return new Result(
            rewritten,
            liveAliases.ToImmutable(),
            liveFormIds.ToImmutable(),
            reservations
                .OrderBy(static reservation => reservation.FormId)
                .ToImmutableArray(),
            diagnostics.ToImmutable());
    }

    private static void Reserve(
        (CatalogEntry Entry, DispositionDecision Decision)[] rewritten,
        int index,
        CatalogEntry entry,
        DispositionDecision decision,
        IReadOnlyDictionary<uint, uint> topLevelAllocations,
        ImmutableDictionary<uint, uint>.Builder liveAliases,
        ImmutableHashSet<uint>.Builder liveFormIds,
        ImmutableArray<FormIdReservation>.Builder reservations,
        ImmutableArray<PlanDiagnostic>.Builder diagnostics,
        string policyId,
        string diagnosticCode,
        string reason)
    {
        if (entry.DmpFormId is not { } sourceFormId
            || !topLevelAllocations.TryGetValue(sourceFormId, out var reservedFormId))
        {
            throw new InvalidOperationException(
                $"Planner-New {entry.Type} 0x{entry.DmpFormId ?? 0:X8} reached reservation "
                + "policy without a top-level allocation.");
        }

        for (var otherIndex = 0; otherIndex < rewritten.Length; otherIndex++)
        {
            if (otherIndex == index)
            {
                continue;
            }

            var (otherEntry, otherDecision) = rewritten[otherIndex];
            if (otherDecision.Disposition == RecordDisposition.New
                && otherEntry.DmpFormId == sourceFormId)
            {
                throw new InvalidOperationException(
                    $"Cannot reserve {entry.Type} source 0x{sourceFormId:X8}: planner-New "
                    + $"{otherEntry.Type} owns the same source-keyed allocation. Per-record "
                    + "allocation identity is required before either entry can be suppressed safely.");
            }
        }

        if (sourceFormId != reservedFormId && liveFormIds.Contains(sourceFormId))
        {
            throw new InvalidOperationException(
                $"Cannot reserve {entry.Type} source 0x{sourceFormId:X8}: the source FormID is "
                + "independently live. Removing its allocation alias would let reference "
                + "resolution fall back to the raw source identity and bind dependents to "
                + "an unrelated live record.");
        }

        if (!liveAliases.TryGetValue(sourceFormId, out var liveTarget)
            || liveTarget != reservedFormId)
        {
            throw new InvalidOperationException(
                $"Reservation candidate {entry.Type} 0x{sourceFormId:X8} expected live alias "
                + $"0x{reservedFormId:X8}, found 0x{liveTarget:X8}.");
        }

        liveAliases.Remove(sourceFormId);
        liveFormIds.Remove(reservedFormId);
        rewritten[index] = (entry, decision with
        {
            Disposition = RecordDisposition.Skip,
            Provenance = new PlanProvenance
            {
                PolicyId = policyId,
                Reason = reason
            }
        });
        reservations.Add(new FormIdReservation
        {
            FormId = reservedFormId,
            SourceFormId = sourceFormId,
            RecordType = entry.Type,
            PolicyId = policyId
        });
        diagnostics.Add(new PlanDiagnostic
        {
            Kind = PlanDiagnosticKind.Warning,
            Phase = "Allocation",
            Code = diagnosticCode,
            RecordType = entry.Type,
            FormId = sourceFormId,
            Message = $"Reserved 0x{reservedFormId:X8} for non-emitting {entry.Type} "
                      + $"source 0x{sourceFormId:X8}: {reason}",
            Metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["source-form-id"] = $"0x{sourceFormId:X8}",
                ["reserved-form-id"] = $"0x{reservedFormId:X8}",
                ["policy-id"] = policyId
            }
        });
    }

    internal sealed record Result(
        IReadOnlyList<(CatalogEntry Entry, DispositionDecision Decision)> Decisions,
        ImmutableDictionary<uint, uint> SourceToEmitted,
        ImmutableHashSet<uint> EmittedFormIds,
        ImmutableArray<FormIdReservation> Reservations,
        ImmutableArray<PlanDiagnostic> Diagnostics);
}
