using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Esm.Subrecords;

/// <summary>
///     Decodes the four physically supported TES4-family CTDA body widths without inferring absent
///     tail fields. Subrecord signature and length-header parsing remain the caller's responsibility.
/// </summary>
internal static class ConditionSubrecordDecoder
{
    internal static bool IsSupportedBodyLength(int length)
    {
        return length is 20 or 24 or 28 or 32;
    }

    internal static ConditionSubrecord Decode(ReadOnlySpan<byte> body, long offset, bool isBigEndian)
    {
        if (!TryDecode(body, offset, isBigEndian, out var condition))
        {
            throw new ArgumentException(
                "A CTDA body must be exactly 20, 24, 28, or 32 bytes.", nameof(body));
        }

        return condition;
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> body,
        long offset,
        bool isBigEndian,
        out ConditionSubrecord condition)
    {
        condition = null!;
        if (!IsSupportedBodyLength(body.Length))
        {
            return false;
        }

        var comparisonRawBits = ReadUInt32(body[4..8], isBigEndian);
        var functionIndex = ReadUInt16(body[8..10], isBigEndian);
        var param1 = ReadUInt32(body[12..16], isBigEndian);
        var param2 = ReadUInt32(body[16..20], isBigEndian);
        uint? runOn = body.Length >= 24 ? ReadUInt32(body[20..24], isBigEndian) : null;
        uint? referenceStorage = body.Length >= 28 ? ReadUInt32(body[24..28], isBigEndian) : null;
        int? parameter3 = body.Length >= 32 ? ReadInt32(body[28..32], isBigEndian) : null;

        condition = new ConditionSubrecord(
            body[0],
            comparisonRawBits,
            functionIndex,
            param1,
            param2,
            offset,
            runOn,
            referenceStorage,
            parameter3);
        return true;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        return isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data)
            : BinaryPrimitives.ReadUInt16LittleEndian(data);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        return isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        return isBigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data)
            : BinaryPrimitives.ReadInt32LittleEndian(data);
    }
}
