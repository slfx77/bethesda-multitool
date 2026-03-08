namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;

internal sealed class NpcInventoryResolver
{
    private readonly IReadOnlyDictionary<uint, NpcScanEntry> _npcs;
    private readonly IReadOnlyDictionary<uint, List<uint>> _leveledNpcs;

    internal NpcInventoryResolver(
        IReadOnlyDictionary<uint, NpcScanEntry> npcs,
        IReadOnlyDictionary<uint, List<uint>> leveledNpcs)
    {
        _npcs = npcs;
        _leveledNpcs = leveledNpcs;
    }

    internal List<uint>? ResolveInventoryFormIds(NpcScanEntry npc)
    {
        if (npc.InventoryFormIds is { Count: > 0 })
        {
            return npc.InventoryFormIds;
        }

        if (npc.TemplateFormId == null || (npc.TemplateFlags & 0x0100) == 0)
        {
            return null;
        }

        return ResolveInventoryFromTemplate(npc.TemplateFormId.Value, 0);
    }

    private List<uint>? ResolveInventoryFromTemplate(uint templateId, int depth)
    {
        if (depth > 5)
        {
            return null;
        }

        if (_npcs.TryGetValue(templateId, out var templateNpc))
        {
            if (templateNpc.InventoryFormIds is { Count: > 0 })
            {
                return templateNpc.InventoryFormIds;
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
                var inventory = ResolveInventoryFormIds(leveledNpc);
                if (inventory != null)
                {
                    return inventory;
                }
            }
        }

        return null;
    }
}
