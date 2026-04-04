using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Stable façade for NPC appearance scanning and resolution.
/// </summary>
internal sealed class NpcAppearanceResolver
{
    private readonly NpcAppearanceFactory _appearanceFactory;
    private readonly NpcAppearanceIndex _index;

    private NpcAppearanceResolver(NpcAppearanceIndex index)
    {
        _index = index;
        _appearanceFactory = new NpcAppearanceFactory(index);
    }

    public int NpcCount => _index.Npcs.Count;

    public int CreatureCount => _index.Creatures.Count;

    public int RaceCount => _index.Races.Count;

    public static NpcAppearanceResolver Build(byte[] esmData, bool bigEndian)
    {
        var index = NpcAppearanceIndexBuilder.Build(esmData, bigEndian);
        return new NpcAppearanceResolver(index);
    }

    public NpcAppearance? ResolveHeadOnly(uint formId, string pluginName)
    {
        if (!_index.Npcs.TryGetValue(formId, out var npc))
        {
            return null;
        }

        return _appearanceFactory.Build(formId, npc, pluginName);
    }

    public List<NpcAppearance> ResolveAllHeadOnly(
        string pluginName,
        bool filterNamed = false)
    {
        var results = new List<NpcAppearance>();
        foreach (var (formId, npc) in _index.Npcs)
        {
            if (filterNamed && string.IsNullOrEmpty(npc.FullName))
            {
                continue;
            }

            results.Add(_appearanceFactory.Build(formId, npc, pluginName));
        }

        return results;
    }

    public NpcAppearance ResolveFromDmpRecord(
        NpcRecord npcRecord,
        string pluginName,
        NpcWeaponResolver.RuntimeWeaponSelection? runtimeWeaponSelection = null)
    {
        return _appearanceFactory.BuildFromDmpRecord(npcRecord, pluginName, runtimeWeaponSelection);
    }

    public IReadOnlyDictionary<uint, NpcScanEntry> GetAllNpcs()
    {
        return _index.Npcs;
    }

    public IReadOnlyDictionary<uint, CreatureScanEntry> GetAllCreatures()
    {
        return _index.Creatures;
    }

    public IReadOnlyDictionary<uint, RaceScanEntry> GetAllRaces()
    {
        return _index.Races;
    }

    public bool TryGetNpc(uint formId, out NpcScanEntry npc)
    {
        return _index.Npcs.TryGetValue(formId, out npc!);
    }

    public string? ResolveWeaponMeshPath(uint itemFormId)
    {
        // Direct weapon lookup
        if (_index.Weapons.TryGetValue(itemFormId, out var weapon))
        {
            return weapon.ModelPath;
        }

        // Walk leveled item lists to find a weapon (max depth 3 to avoid cycles)
        return ResolveLeveledWeapon(itemFormId, 3);
    }

    private string? ResolveLeveledWeapon(uint formId, int maxDepth)
    {
        if (maxDepth <= 0)
        {
            return null;
        }

        if (!_index.LeveledItems.TryGetValue(formId, out var entries))
        {
            return null;
        }

        foreach (var entryFormId in entries)
        {
            if (_index.Weapons.TryGetValue(entryFormId, out var weapon))
            {
                return weapon.ModelPath;
            }

            var nested = ResolveLeveledWeapon(entryFormId, maxDepth - 1);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }
}
