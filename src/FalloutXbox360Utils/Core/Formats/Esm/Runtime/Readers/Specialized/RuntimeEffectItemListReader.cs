using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Shared runtime reader for BSSimpleList&lt;EffectItem*&gt; payloads.
/// </summary>
internal static class RuntimeEffectItemListReader
{
    private const int EffectItemSize = 24;
    private const byte MagicEffectFormType = 0x10;

    internal static List<EnchantmentEffect> Read(
        RuntimeMemoryContext context,
        byte[] structBuffer,
        int listOffset,
        int maxItems = RuntimeMemoryContext.MaxListItems)
    {
        var result = new List<EnchantmentEffect>();

        foreach (var itemVa in context.WalkInlineBSSimpleListItemPointers(structBuffer, listOffset, maxItems))
        {
            if (TryReadEffectItem(context, itemVa) is { } effect)
            {
                result.Add(effect);
            }
        }

        return result;
    }

    private static EnchantmentEffect? TryReadEffectItem(RuntimeMemoryContext context, uint itemVa)
    {
        var itemOffset = context.VaToFileOffset(itemVa);
        if (itemOffset == null)
        {
            return null;
        }

        var buffer = context.ReadBytes(itemOffset.Value, EffectItemSize);
        if (buffer == null)
        {
            return null;
        }

        // Most captures point to the full EffectItem:
        //   pSetting, magnitude, area, duration, target, actorValue.
        if (TryReadLayout(context, buffer, 0, 4) is { } standard)
        {
            return standard;
        }

        // Some ALCH captures point at EffectItemData rather than the containing item:
        //   magnitude, area, duration, target, actorValue, pSetting.
        return TryReadLayout(context, buffer, 20, 0);
    }

    private static EnchantmentEffect? TryReadLayout(
        RuntimeMemoryContext context,
        byte[] buffer,
        int settingPointerOffset,
        int dataOffset)
    {
        if (settingPointerOffset + 4 > buffer.Length || dataOffset + 20 > buffer.Length)
        {
            return null;
        }

        var settingVa = BinaryUtils.ReadUInt32BE(buffer, settingPointerOffset);
        var settingFormId = context.FollowPointerVaToFormId(settingVa, MagicEffectFormType);
        if (settingFormId is not > 0)
        {
            return null;
        }

        var magnitude = GameStatNormalizer.EffectMagnitude(buffer.AsSpan(dataOffset, 4), true);
        var area = BinaryUtils.ReadUInt32BE(buffer, dataOffset + 4);
        var duration = BinaryUtils.ReadUInt32BE(buffer, dataOffset + 8);
        var target = BinaryUtils.ReadUInt32BE(buffer, dataOffset + 12);
        var actorValue = unchecked((int)BinaryUtils.ReadUInt32BE(buffer, dataOffset + 16));

        if (!GameStatNormalizer.IsPlausibleEffectArea(area) ||
            !GameStatNormalizer.IsPlausibleEffectDuration(duration) ||
            !GameStatNormalizer.IsPlausibleEffectTarget(target) ||
            !GameStatNormalizer.IsPlausibleActorValue(actorValue))
        {
            return null;
        }

        return new EnchantmentEffect
        {
            EffectFormId = settingFormId.Value,
            Magnitude = magnitude,
            Area = area,
            Duration = duration,
            Type = target,
            ActorValue = actorValue
        };
    }
}
