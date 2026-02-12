using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Detects and parses dialogue-related ESM subrecord types (NAM1 response text, TRDT response data)
///     from byte arrays during memory dump scanning.
/// </summary>
internal static class EsmDialogueDetector
{
    #region Response Text (NAM1)

    internal static void TryAddResponseTextSubrecord(byte[] data, int i, int dataLength,
        List<ResponseTextSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        // Try little-endian first (PC format), then big-endian (Xbox 360 format)
        var lenLe = BinaryUtils.ReadUInt16LE(data, i + 4);
        var lenBe = BinaryUtils.ReadUInt16BE(data, i + 4);

        ushort len;
        if (lenLe > 0 && lenLe <= 2048 && i + 6 + lenLe <= dataLength)
        {
            len = lenLe;
        }
        else if (lenBe > 0 && lenBe <= 2048 && i + 6 + lenBe <= dataLength)
        {
            len = lenBe;
        }
        else
        {
            return;
        }

        // Extract null-terminated string
        var text = EsmStringUtils.ReadNullTermString(data, i + 6, len);
        if (IsValidDialogueText(text))
        {
            records.Add(new ResponseTextSubrecord(text, i));
        }
    }

    internal static void TryAddResponseTextSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<ResponseTextSubrecord> records)
    {
        if (i + 6 > dataLength)
        {
            return;
        }

        var lenLe = BinaryUtils.ReadUInt16LE(data, i + 4);
        var lenBe = BinaryUtils.ReadUInt16BE(data, i + 4);

        ushort len;
        if (lenLe > 0 && lenLe <= 2048 && i + 6 + lenLe <= dataLength)
        {
            len = lenLe;
        }
        else if (lenBe > 0 && lenBe <= 2048 && i + 6 + lenBe <= dataLength)
        {
            len = lenBe;
        }
        else
        {
            return;
        }

        var text = EsmStringUtils.ReadNullTermString(data, i + 6, len);
        if (IsValidDialogueText(text))
        {
            records.Add(new ResponseTextSubrecord(text, baseOffset + i));
        }
    }

    private static bool IsValidDialogueText(string text)
    {
        // For debugging: accept any non-empty text
        return !string.IsNullOrEmpty(text) && text.Length >= 2;
    }

    #endregion

    #region Response Data (TRDT)

    internal static void TryAddResponseDataSubrecord(byte[] data, int i, int dataLength,
        List<ResponseDataSubrecord> records)
    {
        // TRDT is 20 bytes: emotionType(4) + emotionValue(4) + unused(4) + responseNumber(1) + unused(3) + soundFile(4)
        if (i + 26 > dataLength) // 4 sig + 2 len + 20 data
        {
            return;
        }

        // Check both endianness for length (20 = 0x0014)
        var lenLe = BinaryUtils.ReadUInt16LE(data, i + 4);
        var lenBe = BinaryUtils.ReadUInt16BE(data, i + 4);
        if (lenLe != 20 && lenBe != 20)
        {
            return;
        }

        var trdt = TryParseResponseData(data, i + 6, i);
        if (trdt != null)
        {
            records.Add(trdt);
        }
    }

    internal static void TryAddResponseDataSubrecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<ResponseDataSubrecord> records)
    {
        if (i + 26 > dataLength)
        {
            return;
        }

        var lenLe = BinaryUtils.ReadUInt16LE(data, i + 4);
        var lenBe = BinaryUtils.ReadUInt16BE(data, i + 4);
        if (lenLe != 20 && lenBe != 20)
        {
            return;
        }

        var trdt = TryParseResponseData(data, i + 6, baseOffset + i);
        if (trdt != null)
        {
            records.Add(trdt);
        }
    }

    private static ResponseDataSubrecord? TryParseResponseData(byte[] data, int offset, long recordOffset)
    {
        // Try little-endian first (more common in memory)
        var emotionType = BinaryUtils.ReadUInt32LE(data, offset);
        var emotionValue = BinaryUtils.ReadInt32LE(data, offset + 4);
        var responseNumber = data[offset + 12];

        // Validate emotion type (0-8 are valid emotion types in Fallout NV)
        if (emotionType <= 8 && emotionValue >= -100 && emotionValue <= 100)
        {
            return new ResponseDataSubrecord(emotionType, emotionValue, responseNumber, recordOffset);
        }

        // Try big-endian
        emotionType = BinaryUtils.ReadUInt32BE(data, offset);
        emotionValue = (int)BinaryUtils.ReadUInt32BE(data, offset + 4);

        if (emotionType <= 8 && emotionValue >= -100 && emotionValue <= 100)
        {
            return new ResponseDataSubrecord(emotionType, emotionValue, responseNumber, recordOffset);
        }

        return null;
    }

    #endregion
}
