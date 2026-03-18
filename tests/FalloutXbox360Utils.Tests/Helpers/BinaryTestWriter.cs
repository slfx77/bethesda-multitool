using System.Text;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     Shared low-level byte-writing helpers for constructing synthetic test data.
///     Replaces duplicate private helper methods across 9+ test files.
/// </summary>
internal static class BinaryTestWriter
{
    /// <summary>Write a 4-char ASCII signature in little-endian byte order.</summary>
    public static void WriteSig(byte[] buf, int offset, string sig)
    {
        buf[offset] = (byte)sig[0];
        buf[offset + 1] = (byte)sig[1];
        buf[offset + 2] = (byte)sig[2];
        buf[offset + 3] = (byte)sig[3];
    }

    /// <summary>Write a 4-char ASCII signature in big-endian (reversed) byte order.</summary>
    public static void WriteSigBE(byte[] buf, int offset, string sig)
    {
        buf[offset] = (byte)sig[3];
        buf[offset + 1] = (byte)sig[2];
        buf[offset + 2] = (byte)sig[1];
        buf[offset + 3] = (byte)sig[0];
    }

    /// <summary>Write a uint32 in little-endian.</summary>
    public static void WriteUInt32LE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    /// <summary>Write a uint32 in big-endian.</summary>
    public static void WriteUInt32BE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)((value >> 24) & 0xFF);
        buf[offset + 1] = (byte)((value >> 16) & 0xFF);
        buf[offset + 2] = (byte)((value >> 8) & 0xFF);
        buf[offset + 3] = (byte)(value & 0xFF);
    }

    /// <summary>Write a uint16 in little-endian.</summary>
    public static void WriteUInt16LE(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>Write a uint16 in big-endian.</summary>
    public static void WriteUInt16BE(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)((value >> 8) & 0xFF);
        buf[offset + 1] = (byte)(value & 0xFF);
    }

    /// <summary>Write a big-endian int32 into a byte array.</summary>
    public static void WriteInt32BE(byte[] data, int offset, int value)
    {
        WriteUInt32BE(data, offset, (uint)value);
    }

    /// <summary>Write a big-endian float into a byte array.</summary>
    public static void WriteFloatBE(byte[] data, int offset, float value)
    {
        var bits = BitConverter.SingleToUInt32Bits(value);
        WriteUInt32BE(data, offset, bits);
    }

    /// <summary>Write a null-terminated ASCII string into a byte array.</summary>
    public static void WriteAsciiString(byte[] data, int offset, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        bytes.CopyTo(data, offset);
        data[offset + bytes.Length] = 0;
    }

    /// <summary>Write a big-endian uint32 via BinaryWriter.</summary>
    public static void WriteBigEndianUInt32(BinaryWriter bw, uint value)
    {
        bw.Write((byte)((value >> 24) & 0xFF));
        bw.Write((byte)((value >> 16) & 0xFF));
        bw.Write((byte)((value >> 8) & 0xFF));
        bw.Write((byte)(value & 0xFF));
    }
}