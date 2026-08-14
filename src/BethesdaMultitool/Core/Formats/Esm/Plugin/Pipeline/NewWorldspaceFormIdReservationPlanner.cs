using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;

/// <summary>
///     Preserves the allocator slots historically consumed by the retired pre-encoded
///     new-WRLD path. The planner now owns the real WRLD allocation and bytes; these slots
///     are reservations only and must never enter a source alias or liveness set.
/// </summary>
internal static class NewWorldspaceFormIdReservationPlanner
{
    internal const string PolicyId = "LegacyLayoutReservation.NewWorldspaceWithCells";

    internal static ImmutableArray<FormIdReservation> Reserve(
        RecordCollection dmpRecords,
        NewVsOverrideClassifier classifier,
        FormIdAllocator allocator,
        IReadOnlyDictionary<uint, uint> existingSourceAliases)
    {
        ArgumentNullException.ThrowIfNull(dmpRecords);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(existingSourceAliases);

        // Preserve PreEncodeNewWorldspacesWithCells's candidate construction and the source
        // worldspace-list iteration order exactly. WrldEncoder.EncodeNew always contributes
        // EDID (empty when necessary), so its former Subrecords.Count gate was tautological.
        var worldspacesWithCells = dmpRecords.Cells
            .Select(static cell => cell.WorldspaceFormId)
            .Where(static worldspaceFormId => worldspaceFormId.HasValue)
            .Select(static worldspaceFormId => worldspaceFormId!.Value)
            .ToHashSet();
        if (worldspacesWithCells.Count == 0)
        {
            return [];
        }

        var reservations = ImmutableArray.CreateBuilder<FormIdReservation>();
        foreach (var worldspace in dmpRecords.Worldspaces)
        {
            if (classifier.IsOverride(worldspace.FormId))
            {
                continue;
            }

            if (existingSourceAliases.TryGetValue(worldspace.FormId, out var masterAlias)
                && classifier.IsOverride(masterAlias))
            {
                continue;
            }

            if (!worldspacesWithCells.Contains(worldspace.FormId))
            {
                continue;
            }

            reservations.Add(new FormIdReservation
            {
                FormId = allocator.Allocate(),
                SourceFormId = worldspace.FormId,
                RecordType = "WRLD",
                PolicyId = PolicyId,
            });
        }

        return reservations.ToImmutable();
    }
}
