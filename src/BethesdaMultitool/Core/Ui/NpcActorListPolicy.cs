using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;

namespace BethesdaMultitool.Core.Ui;

/// <summary>Identifies which actor family is visible in the Actors browser list.</summary>
internal enum NpcActorKind
{
    Npc,
    Creature
}

/// <summary>Pure filtering and count-label policy for the tabbed NPC/creature browser.</summary>
internal static class NpcActorListPolicy
{
    internal static List<NpcListItem> Filter(
        IEnumerable<NpcListItem> actors,
        NpcActorKind actorKind,
        bool namedOnly,
        string? searchText)
    {
        ArgumentNullException.ThrowIfNull(actors);

        return actors
            .Where(actor => actor.IsCreature == (actorKind == NpcActorKind.Creature))
            .Where(actor => !namedOnly || !string.IsNullOrEmpty(actor.FullName))
            .Where(actor =>
                string.IsNullOrEmpty(searchText) ||
                actor.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                (actor.EditorId?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true) ||
                $"0x{actor.FormId:X8}".Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .OrderBy(actor => actor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string BuildSelectionCountText(
        IReadOnlyCollection<NpcListItem> filteredActors,
        IReadOnlyCollection<NpcListItem> allActors,
        NpcActorKind actorKind)
    {
        ArgumentNullException.ThrowIfNull(filteredActors);
        ArgumentNullException.ThrowIfNull(allActors);

        var familyCount = allActors.Count(actor => actor.IsCreature == (actorKind == NpcActorKind.Creature));
        var selectedCount = filteredActors.Count(actor => actor.IsSelected);
        var noun = ResolveNoun(actorKind, filteredActors.Count);
        var filterNote = familyCount != filteredActors.Count
            ? $" (of {familyCount})"
            : string.Empty;

        return selectedCount > 0
            ? $"{filteredActors.Count} {noun}{filterNote} — {selectedCount} selected"
            : $"{filteredActors.Count} {noun}{filterNote}";
    }

    private static string ResolveNoun(NpcActorKind actorKind, int count)
    {
        if (actorKind == NpcActorKind.Creature)
        {
            return count == 1 ? "creature" : "creatures";
        }

        return count == 1 ? "NPC" : "NPCs";
    }
}
