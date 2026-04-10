using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
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
        return ResolveWeaponEntry(itemFormId)?.ModelPath;
    }

    public WeaponRestriction GetWeaponRestriction(uint? combatStyleFormId)
    {
        if (combatStyleFormId is { } id &&
            _index.CombatStyles.TryGetValue(id, out var csty))
        {
            return csty.Restriction;
        }

        return WeaponRestriction.None;
    }

    public WeapScanEntry? ResolveWeaponEntry(uint itemFormId)
    {
        if (_index.Weapons.TryGetValue(itemFormId, out var weapon))
        {
            return weapon;
        }

        return ResolveLeveledWeaponEntry(itemFormId, 3);
    }

    /// <summary>
    ///     Collects every reachable weapon for a single inventory FormId, expanding
    ///     leveled item lists. Used by selection scoring (vs <see cref="ResolveWeaponEntry" />
    ///     which returns the first match for backward compat).
    /// </summary>
    public void CollectWeaponEntries(uint itemFormId, List<WeapScanEntry> sink)
    {
        CollectWeaponEntries(itemFormId, sink, 5);
    }

    private void CollectWeaponEntries(uint formId, List<WeapScanEntry> sink, int maxDepth)
    {
        if (maxDepth <= 0)
        {
            return;
        }

        if (_index.Weapons.TryGetValue(formId, out var weapon))
        {
            sink.Add(weapon);
            return;
        }

        if (_index.LeveledItems.TryGetValue(formId, out var entries))
        {
            foreach (var entryFormId in entries)
            {
                CollectWeaponEntries(entryFormId, sink, maxDepth - 1);
            }
        }
    }

    private WeapScanEntry? ResolveLeveledWeaponEntry(uint formId, int maxDepth)
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
                return weapon;
            }

            var nested = ResolveLeveledWeaponEntry(entryFormId, maxDepth - 1);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }
}
