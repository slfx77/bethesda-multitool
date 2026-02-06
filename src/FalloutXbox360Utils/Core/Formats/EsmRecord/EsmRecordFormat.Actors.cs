using FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

public sealed partial class EsmRecordFormat
{
    #region Actor Base Data (ACBS)

    private static void TryAddActorBaseSubrecord(byte[] data, int i, int dataLength, List<ActorBaseSubrecord> records)
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

    private static void TryAddActorBaseSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
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

    #region Condition Data (CTDA)

    private static void TryAddConditionSubrecord(byte[] data, int i, int dataLength, List<ConditionSubrecord> records)
    {
        // CTDA is typically 24 or 28 bytes
        if (i + 30 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 24 && len != 28)
        {
            return;
        }

        var condition = TryParseCondition(data, i + 6, i, false);
        if (condition != null)
        {
            records.Add(condition);
            return;
        }

        condition = TryParseCondition(data, i + 6, i, true);
        if (condition != null)
        {
            records.Add(condition);
        }
    }

    private static void TryAddConditionSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<ConditionSubrecord> records)
    {
        if (i + 30 > dataLength)
        {
            return;
        }

        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 24 && len != 28)
        {
            return;
        }

        var condition = TryParseCondition(data, i + 6, baseOffset + i, false);
        if (condition != null)
        {
            records.Add(condition);
            return;
        }

        condition = TryParseCondition(data, i + 6, baseOffset + i, true);
        if (condition != null)
        {
            records.Add(condition);
        }
    }

    private static ConditionSubrecord? TryParseCondition(byte[] data, int offset, long recordOffset, bool isBigEndian)
    {
        // CTDA structure (24 bytes):
        // Offset 0: type (1 byte)
        // Offset 1: unused (3 bytes) - byte 1 is operator
        // Offset 4: compValue (4 bytes float)
        // Offset 8: functionIndex (2 bytes)
        // Offset 10: unused (2 bytes)
        // Offset 12: param1 (4 bytes)
        // Offset 16: param2 (4 bytes)
        // Offset 20: runOnType (4 bytes) - optional

        byte conditionType = data[offset];
        byte operatorVal = data[offset + 1];
        float compValue;
        ushort functionIndex;
        uint param1;
        uint param2;

        if (isBigEndian)
        {
            compValue = BinaryUtils.ReadFloatBE(data, offset + 4);
            functionIndex = BinaryUtils.ReadUInt16BE(data, offset + 8);
            param1 = BinaryUtils.ReadUInt32BE(data, offset + 12);
            param2 = BinaryUtils.ReadUInt32BE(data, offset + 16);
        }
        else
        {
            compValue = BinaryUtils.ReadFloatLE(data, offset + 4);
            functionIndex = BinaryUtils.ReadUInt16LE(data, offset + 8);
            param1 = BinaryUtils.ReadUInt32LE(data, offset + 12);
            param2 = BinaryUtils.ReadUInt32LE(data, offset + 16);
        }

        // Validate function index (should be < 1000 for known functions)
        if (functionIndex > 1000)
        {
            return null;
        }

        // Validate comparison value
        if (float.IsNaN(compValue) || float.IsInfinity(compValue))
        {
            return null;
        }

        return new ConditionSubrecord(conditionType, operatorVal, compValue, functionIndex, param1, param2, recordOffset);
    }

    #endregion
}
