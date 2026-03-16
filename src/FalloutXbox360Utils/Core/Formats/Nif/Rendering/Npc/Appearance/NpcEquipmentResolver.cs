using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;

internal sealed class NpcEquipmentResolver
{
    private readonly IReadOnlyDictionary<uint, ArmaAddonScanEntry> _armorAddons;
    private readonly IReadOnlyDictionary<uint, ArmoScanEntry> _armors;
    private readonly IReadOnlyDictionary<uint, List<uint>> _formLists;
    private readonly IReadOnlyDictionary<uint, List<uint>> _leveledItems;

    internal NpcEquipmentResolver(
        IReadOnlyDictionary<uint, ArmoScanEntry> armors,
        IReadOnlyDictionary<uint, ArmaAddonScanEntry> armorAddons,
        IReadOnlyDictionary<uint, List<uint>> formLists,
        IReadOnlyDictionary<uint, List<uint>> leveledItems)
    {
        _armors = armors;
        _armorAddons = armorAddons;
        _formLists = formLists;
        _leveledItems = leveledItems;
    }

    internal List<EquippedItem>? Resolve(List<InventoryItem>? inventoryItems, bool isFemale)
    {
        if (inventoryItems is not { Count: > 0 })
        {
            return null;
        }

        var slotToArmor = new Dictionary<uint, ResolvedArmorChoice>();

        foreach (var inventoryItem in inventoryItems)
        {
            if (inventoryItem.Count <= 0)
            {
                continue;
            }

            var armor = ResolveArmor(inventoryItem.ItemFormId);
            if (armor == null)
            {
                continue;
            }

            if (!HasRenderableVisual(armor, isFemale))
            {
                continue;
            }

            var choice = new ResolvedArmorChoice(inventoryItem.ItemFormId, armor);
            for (var bit = 0; bit < 20; bit++)
            {
                var slot = 1u << bit;
                if ((armor.BipedFlags & slot) != 0)
                {
                    slotToArmor.TryAdd(slot, choice);
                }
            }
        }

        if (slotToArmor.Count == 0)
        {
            return null;
        }

        var seenMeshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var equippedItems = new List<EquippedItem>();
        var emittedArmorFormIds = new HashSet<uint>();

        foreach (var (_, armorChoice) in slotToArmor)
        {
            if (!emittedArmorFormIds.Add(armorChoice.FormId))
            {
                continue;
            }

            AddArmorVisuals(
                armorChoice.Armor,
                isFemale,
                seenMeshes,
                equippedItems);
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

    private void AddArmorVisuals(
        ArmoScanEntry armor,
        bool isFemale,
        HashSet<string> seenMeshes,
        List<EquippedItem> equippedItems)
    {
        AddEquippedItem(
            SelectMeshPath(armor, isFemale),
            armor.BipedFlags,
            armor.IsPowerArmor,
            seenMeshes,
            equippedItems);

        if (!armor.BipedModelListFormId.HasValue ||
            !_formLists.TryGetValue(armor.BipedModelListFormId.Value, out var addonFormIds))
        {
            return;
        }

        foreach (var addonFormId in addonFormIds)
        {
            if (!_armorAddons.TryGetValue(addonFormId, out var addon))
            {
                continue;
            }

            AddEquippedItem(
                SelectAddonMeshPath(addon, isFemale),
                addon.BipedFlags,
                armor.IsPowerArmor,
                seenMeshes,
                equippedItems);
        }
    }

    private static void AddEquippedItem(
        string? meshPath,
        uint bipedFlags,
        bool isPowerArmor,
        HashSet<string> seenMeshes,
        List<EquippedItem> equippedItems)
    {
        if (meshPath == null || !seenMeshes.Add(meshPath))
        {
            return;
        }

        var normalizedPath = NpcAppearancePathDeriver.AsMeshPath(meshPath);
        if (normalizedPath == null)
        {
            return;
        }

        equippedItems.Add(new EquippedItem
        {
            BipedFlags = bipedFlags,
            IsPowerArmor = isPowerArmor,
            AttachmentMode = ResolveAttachmentMode(bipedFlags),
            MeshPath = normalizedPath
        });
    }

    private static EquipmentAttachmentMode ResolveAttachmentMode(uint bipedFlags)
    {
        if ((bipedFlags & 0x40) != 0)
        {
            return EquipmentAttachmentMode.LeftWristRigid;
        }

        var hasLeftHand = (bipedFlags & 0x08) != 0;
        var hasRightHand = (bipedFlags & 0x10) != 0;

        if (hasLeftHand && !hasRightHand)
        {
            return EquipmentAttachmentMode.LeftWristRigid;
        }

        if (hasRightHand && !hasLeftHand)
        {
            return EquipmentAttachmentMode.RightWristRigid;
        }

        return EquipmentAttachmentMode.None;
    }

    private bool HasRenderableVisual(ArmoScanEntry armor, bool isFemale)
    {
        if (SelectMeshPath(armor, isFemale) != null)
        {
            return true;
        }

        if (!armor.BipedModelListFormId.HasValue ||
            !_formLists.TryGetValue(armor.BipedModelListFormId.Value, out var addonFormIds))
        {
            return false;
        }

        foreach (var addonFormId in addonFormIds)
        {
            if (_armorAddons.TryGetValue(addonFormId, out var addon) &&
                SelectAddonMeshPath(addon, isFemale) != null)
            {
                return true;
            }
        }

        return false;
    }

    private static string? SelectMeshPath(ArmoScanEntry armor, bool isFemale)
    {
        if (!isFemale)
        {
            return armor.MaleBipedModelPath;
        }

        return armor.FemaleBipedModelPath ?? armor.MaleBipedModelPath;
    }

    private static string? SelectAddonMeshPath(ArmaAddonScanEntry addon, bool isFemale)
    {
        if (!isFemale)
        {
            return addon.MaleModelPath;
        }

        return addon.FemaleModelPath ?? addon.MaleModelPath;
    }

    private readonly record struct ResolvedArmorChoice(uint FormId, ArmoScanEntry Armor);
}
