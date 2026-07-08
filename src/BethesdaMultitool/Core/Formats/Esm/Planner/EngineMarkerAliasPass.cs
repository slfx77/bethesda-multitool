using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition;
using BethesdaMultitool.Core.Formats.Esm.Plugin;

namespace BethesdaMultitool.Core.Formats.Esm.Planner;

/// <summary>
///     Engine default-object marker dedup for the planner top-level (ports legacy
///     <c>TryEncodeNewTopLevelRecord</c>'s aliasing). PortalMarker (0x20), OcclusionMarker
///     (0x17), RoomMarker (0x1F), etc. are engine-only forms with no authoring record in any
///     ESM, so a DMP capture of one looks like a brand-new prototype record. Emitting it as a
///     duplicate STAT re-bases the cell's structural REFRs (XPOD portals / XOCP occlusion
///     planes) onto the duplicate, breaking the room/portal graph — the Gomorrah01 black-void
///     class. Instead: suppress the duplicate and alias its source FormID to the canonical
///     engine marker, so every base remap lands on the real marker.
/// </summary>
internal static class EngineMarkerAliasPass
{
    public static IReadOnlyList<(CatalogEntry Entry, DispositionDecision Decision)> Apply(
        IReadOnlyList<(CatalogEntry Entry, DispositionDecision Decision)> decisions,
        out ImmutableDictionary<uint, uint> markerAliases)
    {
        ImmutableDictionary<uint, uint>.Builder? aliases = null;
        List<(CatalogEntry, DispositionDecision)>? rewritten = null;

        for (var i = 0; i < decisions.Count; i++)
        {
            var (entry, decision) = decisions[i];
            if (decision.Disposition != RecordDisposition.New
                || entry.DmpFormId is not { } sourceFormId
                || entry.Model is not StaticRecord stat
                || !EngineDefaultObjectMarkers.TryGetCanonicalFormId(stat.EditorId, out var canonicalFormId))
            {
                rewritten?.Add(decisions[i]);
                continue;
            }

            if (rewritten is null)
            {
                rewritten = new List<(CatalogEntry, DispositionDecision)>(decisions.Count);
                for (var j = 0; j < i; j++)
                {
                    rewritten.Add(decisions[j]);
                }
            }

            rewritten.Add((entry, decision with
            {
                Disposition = RecordDisposition.Skip,
                Provenance = new PlanProvenance
                {
                    PolicyId = "EngineMarkerAliasPass",
                    Reason = $"Engine default-object marker '{stat.EditorId}' aliased to canonical " +
                             $"0x{canonicalFormId:X8}; duplicate record suppressed."
                }
            }));

            if (sourceFormId != 0 && sourceFormId != canonicalFormId)
            {
                aliases ??= ImmutableDictionary.CreateBuilder<uint, uint>();
                aliases[sourceFormId] = canonicalFormId;
            }
        }

        markerAliases = aliases?.ToImmutable() ?? ImmutableDictionary<uint, uint>.Empty;
        return rewritten ?? decisions;
    }
}
