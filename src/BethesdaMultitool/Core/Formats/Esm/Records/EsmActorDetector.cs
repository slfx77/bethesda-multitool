using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Records;

/// <summary>
///     Detects and parses actor-related ESM subrecord types (ACBS, CTDA)
///     from byte arrays during memory dump scanning.
/// </summary>
internal static class EsmActorDetector
{
    private static readonly BethesdaGame[] GamesWithExplicitConditionTables =
    [
        BethesdaGame.Oblivion,
        BethesdaGame.FalloutNewVegas,
        BethesdaGame.Skyrim,
        BethesdaGame.Fallout4,
        BethesdaGame.Fallout76,
        BethesdaGame.Starfield
    ];

    #region Actor Base (ACBS)

    internal static void TryAddActorBaseSubrecord(byte[] data, int i, int dataLength, List<ActorBaseSubrecord> records)
    {
        if (i + 30 > dataLength) // 4 sig + 2 len + 24 data
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 24) // ACBS is exactly 24 bytes
        {
            return;
        }

        // Try little-endian first
        var acbs = TryParseActorBaseData(data, i + 6, i, false);
        if (acbs != null)
        {
            records.Add(acbs);
            return;
        }

        // Try big-endian
        acbs = TryParseActorBaseData(data, i + 6, i, true);
        if (acbs != null)
        {
            records.Add(acbs);
        }
    }

    internal static void TryAddActorBaseSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<ActorBaseSubrecord> records)
    {
        if (i + 30 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 24)
        {
            return;
        }

        // Try little-endian first
        var acbs = TryParseActorBaseData(data, i + 6, baseOffset + i, false);
        if (acbs != null)
        {
            records.Add(acbs);
            return;
        }

        // Try big-endian
        acbs = TryParseActorBaseData(data, i + 6, baseOffset + i, true);
        if (acbs != null)
        {
            records.Add(acbs);
        }
    }

    private static ActorBaseSubrecord? TryParseActorBaseData(byte[] data, int offset, long recordOffset,
        bool isBigEndian)
    {
        uint flags;
        ushort fatigueBase, barterGold, calcMin, calcMax, speedMultiplier, templateFlags;
        short level, dispositionBase;
        float karmaAlignment;

        if (isBigEndian)
        {
            flags = BinaryUtils.ReadUInt32BE(data, offset);
            fatigueBase = BinaryUtils.ReadUInt16BE(data, offset + 4);
            barterGold = BinaryUtils.ReadUInt16BE(data, offset + 6);
            level = (short)BinaryUtils.ReadUInt16BE(data, offset + 8);
            calcMin = BinaryUtils.ReadUInt16BE(data, offset + 10);
            calcMax = BinaryUtils.ReadUInt16BE(data, offset + 12);
            speedMultiplier = BinaryUtils.ReadUInt16BE(data, offset + 14);
            karmaAlignment = BinaryUtils.ReadFloatBE(data, offset + 16);
            dispositionBase = (short)BinaryUtils.ReadUInt16BE(data, offset + 20);
            templateFlags = BinaryUtils.ReadUInt16BE(data, offset + 22);
        }
        else
        {
            flags = BinaryUtils.ReadUInt32LE(data, offset);
            fatigueBase = BinaryUtils.ReadUInt16LE(data, offset + 4);
            barterGold = BinaryUtils.ReadUInt16LE(data, offset + 6);
            level = (short)BinaryUtils.ReadUInt16LE(data, offset + 8);
            calcMin = BinaryUtils.ReadUInt16LE(data, offset + 10);
            calcMax = BinaryUtils.ReadUInt16LE(data, offset + 12);
            speedMultiplier = BinaryUtils.ReadUInt16LE(data, offset + 14);
            karmaAlignment = BinaryUtils.ReadFloatLE(data, offset + 16);
            dispositionBase = (short)BinaryUtils.ReadUInt16LE(data, offset + 20);
            templateFlags = BinaryUtils.ReadUInt16LE(data, offset + 22);
        }

        // Validate actor base data
        if (!IsValidActorBaseData(flags, fatigueBase, level, speedMultiplier, karmaAlignment))
        {
            return null;
        }

        return new ActorBaseSubrecord(flags, fatigueBase, barterGold, level, calcMin, calcMax,
            speedMultiplier, karmaAlignment, dispositionBase, templateFlags, recordOffset, isBigEndian);
    }

    private static bool IsValidActorBaseData(uint flags, ushort fatigueBase, short level, ushort speedMultiplier,
        float karmaAlignment)
    {
        // Validate flags - some bits should not be set
        if ((flags & 0xFFF00000) != 0)
        {
            return false;
        }

        // Fatigue base should be reasonable (0-1000)
        if (fatigueBase > 1000)
        {
            return false;
        }

        // Level should be reasonable (-128 to 255 for leveled, 1-100 for fixed)
        if (level < -128 || level > 255)
        {
            return false;
        }

        // Speed multiplier should be reasonable (0-500)
        if (speedMultiplier > 500)
        {
            return false;
        }

        // Karma alignment is a float -1.0 to +1.0
        if (float.IsNaN(karmaAlignment) || float.IsInfinity(karmaAlignment) ||
            karmaAlignment < -2.0f || karmaAlignment > 2.0f)
        {
            return false;
        }

        return true;
    }

    #endregion

    #region Conditions (CTDA)

    internal static void TryAddConditionSubrecord(byte[] data, int i, int dataLength, List<ConditionSubrecord> records)
    {
        TryAddConditionSubrecordCore(data, i, dataLength, i, records);
    }

    internal static void TryAddConditionSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<ConditionSubrecord> records)
    {
        TryAddConditionSubrecordCore(data, i, dataLength, baseOffset + i, records);
    }

    private static void TryAddConditionSubrecordCore(
        byte[] data,
        int i,
        int dataLength,
        long recordOffset,
        List<ConditionSubrecord> records)
    {
        if (dataLength < 0 || dataLength > data.Length || i < 0 || i > dataLength - 6)
        {
            return;
        }

        bool isBigEndian;
        if (data.AsSpan(i, 4).SequenceEqual("CTDA"u8))
        {
            isBigEndian = false;
        }
        else if (data.AsSpan(i, 4).SequenceEqual("ADTC"u8))
        {
            isBigEndian = true;
        }
        else
        {
            return;
        }

        var bodyLength = isBigEndian
            ? BinaryUtils.ReadUInt16BE(data, i + 4)
            : BinaryUtils.ReadUInt16LE(data, i + 4);
        if (!ConditionSubrecordDecoder.IsSupportedBodyLength(bodyLength) ||
            bodyLength > dataLength - (i + 6))
        {
            return;
        }

        var body = data.AsSpan(i + 6, bodyLength);
        if (!ConditionSubrecordDecoder.TryDecode(body, recordOffset, isBigEndian, out var condition) ||
            !IsPlausibleCondition(condition))
        {
            return;
        }

        records.Add(condition);
    }

    private static bool IsPlausibleCondition(ConditionSubrecord condition)
    {
        // This blind scanner has no game identity. Preserve the historical coarse range for low
        // indices, but require every higher value to occur in at least one supported game's explicit
        // raw CTDA map. This admits sparse TES4/xOBSE, Skyrim/SKSE, and FO76 indices without turning
        // the full UInt16 range into blind-carving candidates.
        if (condition.FunctionIndex > 1000 && !IsKnownHighConditionIndex(condition.FunctionIndex))
        {
            return false;
        }

        // A UseGlobal comparison stores raw GLOB FormID bits, so interpreting those bits as a float
        // can legitimately produce NaN or infinity. Finiteness is meaningful only for numeric storage.
        return condition.UsesGlobalComparison || float.IsFinite(condition.ComparisonValue);
    }

    private static bool IsKnownHighConditionIndex(ushort functionIndex)
    {
        foreach (var game in GamesWithExplicitConditionTables)
        {
            if (ScriptFunctionTables.For(game).GetConditionFunction(functionIndex) is not null)
            {
                return true;
            }
        }

        return false;
    }

    #endregion
}
