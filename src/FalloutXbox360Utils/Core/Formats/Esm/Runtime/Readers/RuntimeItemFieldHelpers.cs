using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Field-reading helpers for item runtime structs: weapon base/combat/critical fields
///     and container content traversal. Used by <see cref="RuntimeItemReader" />.
/// </summary>
internal sealed class RuntimeItemFieldHelpers
{
    private readonly RuntimeMemoryContext _context;
    private readonly RuntimeItemLayouts _layouts;

    internal RuntimeItemFieldHelpers(RuntimeMemoryContext context, RuntimeItemLayouts layouts)
    {
        _context = context;
        _layouts = layouts;
    }

    #region Weapon Helper Methods

    internal (int Value, int Health, float Weight, short Damage, byte ClipSize)
        ReadWeaponBaseClassFields(byte[] buffer)
    {
        var value = RuntimeMemoryContext.ReadInt32BE(buffer, _layouts.WeapValueOffset);
        var health = RuntimeMemoryContext.ReadInt32BE(buffer, _layouts.WeapHealthOffset);
        var weight = BinaryUtils.ReadFloatBE(buffer, _layouts.WeapWeightOffset);
        var damage = (short)BinaryUtils.ReadUInt16BE(buffer, _layouts.WeapDamageOffset);
        var clipSize = buffer[_layouts.WeapClipRoundsOffset];

        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        if (health < 0 || health > 100000)
        {
            health = 0;
        }

        if (!RuntimeMemoryContext.IsNormalFloat(weight) || weight < 0 || weight > 500)
        {
            weight = 0;
        }

        if (damage < 0 || damage > 10000)
        {
            damage = 0;
        }

        return (value, health, weight, damage, clipSize);
    }

    internal (WeaponType WeaponType, uint AnimationType, float Speed, float Reach,
        float MinSpread, float Spread, float MinRange, float MaxRange,
        byte VatsChance, float ActionPoints, float ShotsPerSec) ReadWeaponCombatFields(byte[] buffer)
    {
        // animationType is stored as uint8 at the first byte of a 4-byte aligned field
        var animTypeByte = buffer[_layouts.WeapAnimTypeOffset];
        var animationType = animTypeByte <= 20 ? animTypeByte : 0u;

        var speed = BinaryUtils.ReadFloatBE(buffer, _layouts.WeapSpeedOffset);
        var reach = BinaryUtils.ReadFloatBE(buffer, _layouts.WeapReachOffset);

        if (!RuntimeMemoryContext.IsNormalFloat(speed) || speed < 0 || speed > 100)
        {
            speed = 1.0f;
        }

        if (!RuntimeMemoryContext.IsNormalFloat(reach) || reach < 0 || reach > 1000)
        {
            reach = 0;
        }

        // Animation type byte maps directly to WeaponType enum
        var weaponType = animTypeByte <= 11 ? (WeaponType)animTypeByte : 0;

        var dataStart = _layouts.WeapDataStart;
        var minSpread =
            RuntimeMemoryContext.ReadValidatedFloat(buffer, dataStart + RuntimeItemLayouts.DnamMinSpreadRelOffset, 0,
                1000);
        var spread = RuntimeMemoryContext.ReadValidatedFloat(buffer,
            dataStart + RuntimeItemLayouts.DnamSpreadRelOffset, 0, 1000);
        var minRange =
            RuntimeMemoryContext.ReadValidatedFloat(buffer, dataStart + RuntimeItemLayouts.DnamMinRangeRelOffset, 0,
                100000);
        var maxRange =
            RuntimeMemoryContext.ReadValidatedFloat(buffer, dataStart + RuntimeItemLayouts.DnamMaxRangeRelOffset, 0,
                100000);
        var actionPoints =
            RuntimeMemoryContext.ReadValidatedFloat(buffer, dataStart + RuntimeItemLayouts.DnamActionPointsRelOffset,
                0, 1000);
        var shotsPerSec =
            RuntimeMemoryContext.ReadValidatedFloat(buffer, dataStart + RuntimeItemLayouts.DnamShotsPerSecRelOffset,
                0, 1000);

        var vatsChance = buffer[dataStart + RuntimeItemLayouts.DnamVatsChanceRelOffset];
        if (vatsChance > 100)
        {
            vatsChance = 0;
        }

        return (weaponType, animationType, speed, reach, minSpread, spread,
            minRange, maxRange, vatsChance, actionPoints, shotsPerSec);
    }

    internal (short Damage, float Chance) ReadWeaponCriticalFields(byte[] buffer)
    {
        var damage = (short)BinaryUtils.ReadUInt16BE(buffer, _layouts.WeapCritDamageOffset);
        var chance = BinaryUtils.ReadFloatBE(buffer, _layouts.WeapCritChanceOffset);

        if (!RuntimeMemoryContext.IsNormalFloat(chance) || chance < 0 || chance > 100)
        {
            chance = 0;
        }

        if (damage < 0 || damage > 10000)
        {
            damage = 0;
        }

        return (damage, chance);
    }

    #endregion

    #region Container Helper Methods

    /// <summary>
    ///     Read container contents from TESContainer tList at +120/+124.
    ///     Reuses the same ContainerObject reading logic as NPC inventory.
    /// </summary>
    internal List<InventoryItem> ReadContainerContents(byte[] buffer)
    {
        var items = new List<InventoryItem>();

        // Read inline first node
        var firstDataPtr = BinaryUtils.ReadUInt32BE(buffer, _layouts.ContContentsDataOffset);
        var firstNextPtr = BinaryUtils.ReadUInt32BE(buffer, _layouts.ContContentsNextOffset);

        // Process inline first item
        var firstItem = ReadContainerObject(firstDataPtr);
        if (firstItem != null)
        {
            items.Add(firstItem);
        }

        // Follow chain of _Node (8 bytes each: data ptr + next ptr)
        var nextVA = firstNextPtr;
        var visited = new HashSet<uint>();
        while (nextVA != 0 && items.Count < RuntimeMemoryContext.MaxListItems && !visited.Contains(nextVA))
        {
            visited.Add(nextVA);
            var nodeFileOffset = _context.VaToFileOffset(nextVA);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuf = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuf == null)
            {
                break;
            }

            var dataPtr = BinaryUtils.ReadUInt32BE(nodeBuf);
            var nextPtr = BinaryUtils.ReadUInt32BE(nodeBuf, 4);

            var item = ReadContainerObject(dataPtr);
            if (item != null)
            {
                items.Add(item);
            }

            nextVA = nextPtr;
        }

        return items;
    }

    /// <summary>
    ///     Follow a ContainerObject* pointer to read { count(int32 BE), pItem(TESForm*) }.
    ///     Returns an InventoryItem or null.
    /// </summary>
    private InventoryItem? ReadContainerObject(uint containerObjectVA)
    {
        if (containerObjectVA == 0)
        {
            return null;
        }

        var fileOffset = _context.VaToFileOffset(containerObjectVA);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = _context.ReadBytes(fileOffset.Value, 8);
        if (buf == null)
        {
            return null;
        }

        var count = RuntimeMemoryContext.ReadInt32BE(buf, 0);
        var pItem = BinaryUtils.ReadUInt32BE(buf, 4);

        // Validate count (reasonable range for inventory)
        if (count <= 0 || count > 100000)
        {
            return null;
        }

        // Follow pItem to read the item's FormID
        var itemFormId = _context.FollowPointerVaToFormId(pItem);
        if (itemFormId == null)
        {
            return null;
        }

        return new InventoryItem(itemFormId.Value, count);
    }

    #endregion
}
