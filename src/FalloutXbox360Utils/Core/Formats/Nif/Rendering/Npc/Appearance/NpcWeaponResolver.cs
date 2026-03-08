using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;

internal sealed class NpcWeaponResolver
{
    private static readonly HashSet<WeaponType> NonRenderableWeaponTypes =
    [
        WeaponType.GrenadeThrow,
        WeaponType.LandMine,
        WeaponType.MinePlacement
    ];

    private readonly IReadOnlyDictionary<uint, WeapScanEntry> _weapons;
    private readonly IReadOnlyDictionary<uint, List<uint>> _leveledItems;

    internal NpcWeaponResolver(
        IReadOnlyDictionary<uint, WeapScanEntry> weapons,
        IReadOnlyDictionary<uint, List<uint>> leveledItems)
    {
        _weapons = weapons;
        _leveledItems = leveledItems;
    }

    internal EquippedWeapon? Resolve(List<uint>? inventoryFormIds)
    {
        if (inventoryFormIds is not { Count: > 0 })
        {
            return null;
        }

        WeapScanEntry? bestWeapon = null;
        foreach (var formId in inventoryFormIds)
        {
            var weapon = ResolveWeaponCandidate(formId);
            if (weapon?.ModelPath == null)
            {
                continue;
            }

            if (bestWeapon == null || weapon.Damage > bestWeapon.Damage)
            {
                bestWeapon = weapon;
            }
        }

        if (bestWeapon?.ModelPath == null)
        {
            return null;
        }

        return new EquippedWeapon
        {
            WeaponType = bestWeapon.WeaponType,
            MeshPath = NpcAppearancePathDeriver.AsMeshPath(bestWeapon.ModelPath)!
        };
    }

    private WeapScanEntry? ResolveWeaponCandidate(uint formId, int depth = 0)
    {
        if (_weapons.TryGetValue(formId, out var weapon))
        {
            return IsRenderable(weapon) ? weapon : null;
        }

        if (depth > 5 || !_leveledItems.TryGetValue(formId, out var entries))
        {
            return null;
        }

        WeapScanEntry? bestWeapon = null;
        foreach (var entryFormId in entries)
        {
            var resolved = ResolveWeaponCandidate(entryFormId, depth + 1);
            if (resolved != null &&
                (bestWeapon == null || resolved.Damage > bestWeapon.Damage))
            {
                bestWeapon = resolved;
            }
        }

        return bestWeapon;
    }

    private static bool IsRenderable(WeapScanEntry weapon)
    {
        return !NonRenderableWeaponTypes.Contains(weapon.WeaponType);
    }
}
