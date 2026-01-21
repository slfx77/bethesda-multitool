using System.Buffers.Binary;

namespace EsmAnalyzer.Helpers;

internal static class EsmBinary
{
    public static ushort ReadUInt16(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    }

    public static uint ReadUInt32(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }

    public static int ReadInt32(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
    }

    public static float ReadSingle(byte[] data, int offset, bool bigEndian)
    {
        var value = ReadInt32(data, offset, bigEndian);
        return BitConverter.Int32BitsToSingle(value);
    }
}