using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Low-level endian swap utilities for NIF conversion.
/// </summary>
internal static class NifEndianUtils
{
    public static void SwapUInt32InPlace(byte[] buf, int pos)
    {
        (buf[pos], buf[pos + 1], buf[pos + 2], buf[pos + 3]) =
            (buf[pos + 3], buf[pos + 2], buf[pos + 1], buf[pos]);
    }

    public static void SwapUInt16InPlace(byte[] buf, int pos)
    {
        (buf[pos], buf[pos + 1]) = (buf[pos + 1], buf[pos]);
    }

    public static void BulkSwap4InPlace(byte[] buf, int pos, int size)
    {
        var end = pos + size;
        while (pos + 4 <= end)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }
    }

    public static void RemapBlockRefInPlace(byte[] buf, int pos, int[] blockRemap)
    {
        var refIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos));
        if (refIdx >= 0 && refIdx < blockRemap.Length)
        {
            var newIdx = blockRemap[refIdx];
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos), newIdx);
        }
    }

    /// <summary>
    ///     Convert NiAVObject base fields in-place. Returns new position or -1 on error.
    /// </summary>
    public static int ConvertNiAVObjectInPlace(byte[] buf, int pos, int end, int[] blockRemap)
    {
        // nameIdx
        if (pos + 4 > end) return -1;

        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // numExtraDataList
        if (pos + 4 > end) return -1;

        SwapUInt32InPlace(buf, pos);
        var numExtra = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // extraData refs
        for (var i = 0; i < numExtra && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        // controllerRef
        if (pos + 4 > end) return -1;

        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        // flags
        if (pos + 4 > end) return -1;

        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // translation (3 floats)
        for (var i = 0; i < 3 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // rotation (9 floats - 3x3 matrix)
        for (var i = 0; i < 9 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // scale
        if (pos + 4 > end) return -1;

        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // numProperties
        if (pos + 4 > end) return -1;

        SwapUInt32InPlace(buf, pos);
        var numProps = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // property refs
        for (var i = 0; i < numProps && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        // collisionRef
        if (pos + 4 > end) return -1;

        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        return pos;
    }

    /// <summary>
    ///     Convert NiObjectNET base fields (nameIdx, extras, controller).
    /// </summary>
    public static int ConvertNiObjectNETInPlace(byte[] buf, int pos, int end, int[] blockRemap)
    {
        // nameIdx
        if (pos + 4 > end) return -1;

        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // numExtraDataList
        if (pos + 4 > end) return -1;

        SwapUInt32InPlace(buf, pos);
        var numExtra = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // extraData refs
        for (var i = 0; i < numExtra && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        // controllerRef
        if (pos + 4 > end) return -1;

        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        return pos;
    }
}
