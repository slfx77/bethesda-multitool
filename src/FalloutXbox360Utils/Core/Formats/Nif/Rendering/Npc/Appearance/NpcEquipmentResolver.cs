namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;

internal sealed class NpcEquipmentResolver
{
    private readonly IReadOnlyDictionary<uint, ArmoScanEntry> _armors;
    private readonly IReadOnlyDictionary<uint, List<uint>> _leveledItems;

    internal NpcEquipmentResolver(
        IReadOnlyDictionary<uint, ArmoScanEntry> armors,
        IReadOnlyDictionary<uint, List<uint>> leveledItems)
    {
        _armors = armors;
        _leveledItems = leveledItems;
    }

    internal List<EquippedItem>? Resolve(List<uint>? inventoryFormIds, bool isFemale)
    {
        if (inventoryFormIds is not { Count: > 0 })
        {
            return null;
        }

        var slotToArmor = new Dictionary<uint, (uint BipedFlags, string MeshPath)>();

        foreach (var formId in inventoryFormIds)
        {
            var armor = ResolveArmor(formId);
            if (armor == null)
            {
                continue;
            }

            var meshPath = SelectMeshPath(armor, isFemale);
            if (meshPath == null)
            {
                continue;
            }

            for (var bit = 0; bit < 20; bit++)
            {
                var slot = 1u << bit;
                if ((armor.BipedFlags & slot) != 0)
                {
                    slotToArmor.TryAdd(slot, (armor.BipedFlags, meshPath));
                }
            }
        }

        if (slotToArmor.Count == 0)
        {
            return null;
        }

        var seenMeshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var equippedItems = new List<EquippedItem>();

        foreach (var (_, armorInfo) in slotToArmor)
        {
            if (seenMeshes.Add(armorInfo.MeshPath))
            {
                equippedItems.Add(new EquippedItem
                {
                    BipedFlags = armorInfo.BipedFlags,
                    MeshPath = NpcAppearancePathDeriver.AsMeshPath(
                        armorInfo.MeshPath)!
                });
            }
        }

        return equippedItems.Count > 0 ? equippedItems : null;
    }

    private ArmoScanEntry? ResolveArmor(uint formId, int depth = 0)
    {
        if (_armors.TryGetValue(formId, out var armor))
        {
            return armor;
        }

        if (depth > 5 || !_leveledItems.TryGetValue(formId, out var entries))
        {
            return null;
        }

        foreach (var entryFormId in entries)
        {
            var resolved = ResolveArmor(entryFormId, depth + 1);
            if (resolved != null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? SelectMeshPath(ArmoScanEntry armor, bool isFemale)
    {
        if (!isFemale)
        {
            return armor.MaleBipedModelPath;
        }

        return armor.FemaleBipedModelPath ?? armor.MaleBipedModelPath;
    }
}
