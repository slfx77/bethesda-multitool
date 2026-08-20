using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;

/// <summary>Resolves an NPC's inventory items, expanding leveled-list references into concrete item FormIDs.</summary>
internal sealed class NpcInventoryResolver
{
    private readonly IReadOnlyDictionary<uint, List<uint>> _leveledNpcs;
    private readonly IReadOnlyDictionary<uint, NpcScanEntry> _npcs;

    internal NpcInventoryResolver(
        IReadOnlyDictionary<uint, NpcScanEntry> npcs,
        IReadOnlyDictionary<uint, List<uint>> leveledNpcs)
    {
        _npcs = npcs;
        _leveledNpcs = leveledNpcs;
    }

    internal List<InventoryItem>? ResolveInventoryItems(NpcScanEntry npc)
    {
        if (npc.InventoryItems is { Count: > 0 })
        {
            return npc.InventoryItems;
        }

        if (npc.TemplateFormId == null || (npc.TemplateFlags & 0x0100) == 0)
        {
            return null;
        }

        return ResolveInventoryFromTemplate(npc.TemplateFormId.Value, 0);
    }

    private List<InventoryItem>? ResolveInventoryFromTemplate(uint templateId, int depth)
    {
        if (depth > 5)
        {
            return null;
        }

        if (_npcs.TryGetValue(templateId, out var templateNpc))
        {
            if (templateNpc.InventoryItems is { Count: > 0 })
            {
                return templateNpc.InventoryItems;
            }

            if (templateNpc.TemplateFormId != null &&
                (templateNpc.TemplateFlags & 0x0100) != 0)
            {
                return ResolveInventoryFromTemplate(
                    templateNpc.TemplateFormId.Value,
                    depth + 1);
            }

            return null;
        }

        if (!_leveledNpcs.TryGetValue(templateId, out var leveledNpcEntries))
        {
            return null;
        }

        foreach (var entryId in leveledNpcEntries)
        {
            if (_npcs.TryGetValue(entryId, out var leveledNpc))
            {
                var inventory = ResolveInventoryItems(leveledNpc);
                if (inventory != null)
                {
                    return inventory;
                }
            }
        }

        return null;
    }
}
