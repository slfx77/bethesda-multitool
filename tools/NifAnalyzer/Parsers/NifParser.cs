using System.Buffers.Binary;
using System.Text;
using NifAnalyzer.Models;
using static NifAnalyzer.Utils.BinaryHelpers;

namespace NifAnalyzer.Parsers;

/// <summary>
/// Parses NIF file headers.
/// </summary>
internal static class NifParser
{
    public static NifInfo Parse(byte[] data)
    {
        var info = new NifInfo();
        int pos = 0;

        // Read version string until newline
        int nl = Array.IndexOf(data, (byte)0x0A, 0, Math.Min(128, data.Length));
        if (nl < 0) throw new InvalidDataException("No newline in header");
        info.VersionString = Encoding.ASCII.GetString(data, 0, nl);
        pos = nl + 1;

        // Binary version (ALWAYS little-endian in header)
        info.Version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)); pos += 4;

        // Endian flag (0 = big, 1 = little) for version >= 20.0.0.4
        info.IsBigEndian = data[pos] == 0;
        pos += 1;

        // User version (ALWAYS little-endian)
        info.UserVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)); pos += 4;

        // Num blocks (ALWAYS little-endian)
        info.NumBlocks = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)); pos += 4;

        // BS version (ALWAYS little-endian) if Bethesda format (user version >= 10)
        if (info.UserVersion >= 10)
        {
            info.BsVersion = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)); pos += 4;
        }

        // Skip export info strings (3 ShortStrings)
        for (int i = 0; i < 3; i++)
        {
            int len = data[pos++];
            pos += len;
        }

        // From here, endianness matters (NumBlockTypes onward)
        // Num block types
        int numBlockTypes = ReadUInt16(data, pos, info.IsBigEndian); pos += 2;

        // Block type names (SizedStrings)
        info.BlockTypes = new List<string>(numBlockTypes);
        for (int i = 0; i < numBlockTypes; i++)
        {
            int len = (int)ReadUInt32(data, pos, info.IsBigEndian); pos += 4;
            info.BlockTypes.Add(Encoding.ASCII.GetString(data, pos, len));
            pos += len;
        }

        // Block type indices
        info.BlockTypeIndices = new ushort[info.NumBlocks];
        for (int i = 0; i < info.NumBlocks; i++)
        {
            info.BlockTypeIndices[i] = ReadUInt16(data, pos, info.IsBigEndian);
            pos += 2;
        }

        // Block sizes
        info.BlockSizes = new uint[info.NumBlocks];
        for (int i = 0; i < info.NumBlocks; i++)
        {
            info.BlockSizes[i] = ReadUInt32(data, pos, info.IsBigEndian);
            pos += 4;
        }

        // String table
        info.NumStrings = (int)ReadUInt32(data, pos, info.IsBigEndian); pos += 4;
        int maxStringLen = (int)ReadUInt32(data, pos, info.IsBigEndian); pos += 4;

        info.Strings = new List<string>(info.NumStrings);
        for (int i = 0; i < info.NumStrings; i++)
        {
            int len = (int)ReadUInt32(data, pos, info.IsBigEndian); pos += 4;
            info.Strings.Add(Encoding.ASCII.GetString(data, pos, len));
            pos += len;
        }

        // Groups
        int numGroups = (int)ReadUInt32(data, pos, info.IsBigEndian); pos += 4;
        pos += numGroups * 4;

        info.BlockDataOffset = pos;
        return info;
    }
}
