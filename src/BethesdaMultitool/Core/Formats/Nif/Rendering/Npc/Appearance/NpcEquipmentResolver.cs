using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;

/// <summary>Resolves the armor/clothing an NPC wears, from ESM inventory or runtime BipedAnim slots captured in a DMP.</summary>
internal sealed class NpcEquipmentResolver
{
    private readonly IReadOnlyDictionary<uint, ArmaAddonScanEntry> _armorAddons;
    private readonly IReadOnlyDictionary<uint, ArmoScanEntry> _armors;
    private readonly IReadOnlyDictionary<uint, List<uint>> _formLists;
    private readonly BethesdaGame _game;
    private readonly IReadOnlyDictionary<uint, List<uint>> _leveledItems;

    internal NpcEquipmentResolver(
        IReadOnlyDictionary<uint, ArmoScanEntry> armors,
        IReadOnlyDictionary<uint, ArmaAddonScanEntry> armorAddons,
        IReadOnlyDictionary<uint, List<uint>> formLists,
        IReadOnlyDictionary<uint, List<uint>> leveledItems,
        BethesdaGame game = BethesdaGame.Unknown)
    {
        _armors = armors;
        _armorAddons = armorAddons;
        _formLists = formLists;
        _leveledItems = leveledItems;
        _game = game;
    }

    internal List<EquippedItem>? Resolve(
        List<InventoryItem>? inventoryItems,
        bool isFemale,
        ResolutionMode mode = ResolutionMode.StaticDefault)
    {
        if (inventoryItems is not { Count: > 0 })
        {
            return null;
        }

        var armorChoices = ResolveRenderableArmorChoices(inventoryItems, isFemale);
        var useOblivionDefaultWornSelection =
            _game == BethesdaGame.Oblivion && mode == ResolutionMode.StaticDefault;
        if (useOblivionDefaultWornSelection)
        {
            armorChoices = SelectBestOblivionArmorChoices(armorChoices);
        }

        var slotToArmor = new Dictionary<uint, ResolvedArmorChoice>();
        foreach (var choice in armorChoices)
        {
            for (var bit = 0; bit < 20; bit++)
            {
                var slot = 1u << bit;
                if ((choice.Armor.BipedFlags & slot) != 0)
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
        IEnumerable<ResolvedArmorChoice> choicesToEmit =
            useOblivionDefaultWornSelection || mode == ResolutionMode.AuthoritativeWorn
                ? armorChoices
                : slotToArmor.Values;

        foreach (var armorChoice in choicesToEmit)
        {
            if (!emittedArmorFormIds.Add(armorChoice.FormId))
            {
                continue;
            }

            AddArmorVisuals(
                armorChoice,
                isFemale,
                slotToArmor,
                seenMeshes,
                equippedItems);
        }

        return equippedItems.Count > 0 ? equippedItems : null;
    }

    private List<ResolvedArmorChoice> ResolveRenderableArmorChoices(
        List<InventoryItem> inventoryItems,
        bool isFemale)
    {
        var choices = new List<ResolvedArmorChoice>();

        for (var inventoryIndex = 0; inventoryIndex < inventoryItems.Count; inventoryIndex++)
        {
            var inventoryItem = inventoryItems[inventoryIndex];
            if (inventoryItem.Count <= 0)
            {
                continue;
            }

            var armor = ResolveArmor(inventoryItem.ItemFormId);
            if (armor == null || armor.BipedFlags == 0 || !HasRenderableVisual(armor, isFemale))
            {
                continue;
            }

            choices.Add(new ResolvedArmorChoice(
                inventoryItem.ItemFormId,
                armor,
                inventoryIndex));
        }

        return choices;
    }

    private static List<ResolvedArmorChoice> SelectBestOblivionArmorChoices(
        List<ResolvedArmorChoice> candidates)
    {
        uint claimedBipedFlags = 0;
        var selected = new List<ResolvedArmorChoice>();
        // The classic engine initializes worn items in ascending biped-slot order.
        // This is significant for an upper-body garment that also owns the lower
        // slot: it must claim both before a separately authored pair of pants is
        // considered. Selecting candidates globally reverses that outcome for
        // Reynald Jemane because his pants precede his two-slot outfit in CNTO.
        for (var bit = 0; bit < 20; bit++)
        {
            var slot = 1u << bit;
            if ((claimedBipedFlags & slot) != 0)
            {
                continue;
            }

            ResolvedArmorChoice? best = null;
            foreach (var candidate in candidates)
            {
                if ((candidate.Armor.BipedFlags & slot) == 0 ||
                    (candidate.Armor.BipedFlags & claimedBipedFlags) != 0)
                {
                    continue;
                }

                if (!best.HasValue || CompareOblivionCandidates(candidate, best.Value) < 0)
                {
                    best = candidate;
                }
            }

            if (!best.HasValue)
            {
                continue;
            }

            selected.Add(best.Value);
            claimedBipedFlags |= best.Value.Armor.BipedFlags;
        }

        selected.Sort(static (left, right) => left.InventoryIndex.CompareTo(right.InventoryIndex));
        return selected;
    }

    private static int CompareOblivionCandidates(
        ResolvedArmorChoice left,
        ResolvedArmorChoice right)
    {
        var comparison = left.Armor.IsClothing.CompareTo(right.Armor.IsClothing);
        if (comparison != 0)
        {
            return comparison;
        }

        if (left.Armor.IsClothing)
        {
            comparison = right.Armor.BaseValue.CompareTo(left.Armor.BaseValue);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        else
        {
            comparison = right.Armor.BaseArmorRating.CompareTo(left.Armor.BaseArmorRating);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.InventoryIndex.CompareTo(right.InventoryIndex);
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
        ResolvedArmorChoice armorChoice,
        bool isFemale,
        Dictionary<uint, ResolvedArmorChoice> slotToArmor,
        HashSet<string> seenMeshes,
        List<EquippedItem> equippedItems)
    {
        var armor = armorChoice.Armor;
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

            // BipedModelList addons are fill-in visuals (e.g. an outfit's bare hands).
            // A dedicated armor equipped on the same biped slot supersedes them — the
            // vault suit's lefthand.nif must not render under the Pip-Boy glove.
            if (IsAddonSlotOwnedByOtherArmor(addon.BipedFlags, armorChoice.FormId, slotToArmor))
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

    private static bool IsAddonSlotOwnedByOtherArmor(
        uint addonBipedFlags,
        uint armorFormId,
        Dictionary<uint, ResolvedArmorChoice> slotToArmor)
    {
        for (var bit = 0; bit < 20; bit++)
        {
            var slot = 1u << bit;
            if ((addonBipedFlags & slot) != 0 &&
                slotToArmor.TryGetValue(slot, out var owner) &&
                owner.FormId != armorFormId)
            {
                return true;
            }
        }

        return false;
    }

    private void AddEquippedItem(
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
            AttachmentMode = ResolveAttachmentMode(bipedFlags, _game),
            MeshPath = normalizedPath
        });
    }

    private static EquipmentAttachmentMode ResolveAttachmentMode(uint bipedFlags, BethesdaGame game)
    {
        // Oblivion BMDT bit 13 is the shield slot. Shields are rigid world models;
        // skinning them as ordinary body armor leaves their vertices at model origin (the feet).
        if (game == BethesdaGame.Oblivion && (bipedFlags & 0x2000) != 0)
        {
            return EquipmentAttachmentMode.LeftWristRigid;
        }

        // Only classify as wrist-rigid when the item covers exclusively hand/wrist
        // slots. Items that also cover body slots (chest, legs, etc.) get None.
        const uint handSlotsMask = 0x08 | 0x10 | 0x40; // left hand, right hand, Pip-Boy
        var nonHandSlots = bipedFlags & ~handSlotsMask;
        if (nonHandSlots != 0)
        {
            return EquipmentAttachmentMode.None;
        }

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

    /// <summary>
    ///     Worn-armor FormIDs read from a memory dump's runtime BipedAnim slots
    ///     for a specific actor instance. Mirrors
    ///     <see cref="NpcWeaponResolver.RuntimeWeaponSelection" />.
    /// </summary>
    internal readonly record struct RuntimeEquipmentSelection(
        bool HasRuntimeTarget,
        uint? ActorRefFormId,
        IReadOnlyList<uint>? WornArmorFormIds);

    internal enum ResolutionMode
    {
        StaticDefault,
        AuthoritativeWorn
    }

    private readonly record struct ResolvedArmorChoice(
        uint FormId,
        ArmoScanEntry Armor,
        int InventoryIndex);
}
