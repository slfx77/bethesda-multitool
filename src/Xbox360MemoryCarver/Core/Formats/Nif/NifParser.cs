using System.Buffers.Binary;
using System.Text;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Parses NIF file headers and block structures.
/// </summary>
internal static class NifParser
{
    /// <summary>
    ///     Parse NIF file and extract header/block information.
    /// </summary>
    public static NifInfo? Parse(byte[] data)
    {
        if (data.Length < 50) return null;

        var info = new NifInfo();
        var pos = ParseHeaderString(data, info);
        if (pos < 0) return null;

        pos = ParseVersionInfo(data, pos, info);
        if (!IsBethesdaVersion(info.BinaryVersion,
                info.UserVersion)) return info; // Return minimal info for non-Bethesda files

        pos = ParseBethesdaHeader(data, pos, info);
        var numBlockTypes = ReadUInt16(data, pos, info.IsBigEndian);
        pos += 2;

        pos = ParseBlockTypeNames(data, pos, numBlockTypes, info);
        if (pos < 0) return null;

        var (blockTypeIndices, blockSizes) = ParseBlockMetadata(data, pos, info.BlockCount, info.IsBigEndian);
        pos += info.BlockCount * 6; // 2 bytes for type index + 4 bytes for size

        pos = ParseStringTable(data, pos, info.IsBigEndian, info.Strings);
        pos = SkipGroups(data, pos, info.IsBigEndian);

        BuildBlockList(info, blockTypeIndices, blockSizes, pos);
        return info;
    }

    private static int ParseHeaderString(byte[] data, NifInfo info)
    {
        var newlinePos = Array.IndexOf(data, (byte)0x0A, 0, Math.Min(60, data.Length));
        if (newlinePos < 0) return -1;

        info.HeaderString = Encoding.ASCII.GetString(data, 0, newlinePos);
        return newlinePos + 1;
    }

    private static int ParseVersionInfo(byte[] data, int pos, NifInfo info)
    {
        info.BinaryVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        info.IsBigEndian = data[pos + 4] == 0;
        info.UserVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 5));
        info.BlockCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 9));
        return pos + 13;
    }

    private static int ParseBethesdaHeader(byte[] data, int pos, NifInfo info)
    {
        info.BsVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        pos += 4;

        // Skip ShortStrings (author, process script, export script)
        for (var i = 0; i < 3; i++) pos += 1 + data[pos];

        return pos;
    }

    private static int ParseBlockTypeNames(byte[] data, int pos, int numBlockTypes, NifInfo info)
    {
        for (var i = 0; i < numBlockTypes; i++)
        {
            if (pos + 4 > data.Length) return -1;

            var strLen = ReadUInt32(data, pos, info.IsBigEndian);
            pos += 4;

            if (strLen > 256 || pos + strLen > data.Length) return -1;

            info.BlockTypeNames.Add(Encoding.ASCII.GetString(data, pos, (int)strLen));
            pos += (int)strLen;
        }

        return pos;
    }

    private static (ushort[] typeIndices, uint[] sizes) ParseBlockMetadata(
        byte[] data, int pos, int numBlocks, bool isBigEndian)
    {
        var blockTypeIndices = new ushort[numBlocks];
        for (var i = 0; i < numBlocks; i++) blockTypeIndices[i] = ReadUInt16(data, pos + i * 2, isBigEndian);

        var sizePos = pos + numBlocks * 2;
        var blockSizes = new uint[numBlocks];
        for (var i = 0; i < numBlocks; i++) blockSizes[i] = ReadUInt32(data, sizePos + i * 4, isBigEndian);

        return (blockTypeIndices, blockSizes);
    }

    private static int SkipGroups(byte[] data, int pos, bool isBigEndian)
    {
        var numGroups = ReadUInt32(data, pos, isBigEndian);
        return pos + 4 + (int)numGroups * 4;
    }

    private static void BuildBlockList(NifInfo info, ushort[] blockTypeIndices, uint[] blockSizes, int dataStart)
    {
        var pos = dataStart;
        for (var i = 0; i < info.BlockCount; i++)
        {
            info.Blocks.Add(new BlockInfo
            {
                Index = i,
                TypeIndex = blockTypeIndices[i],
                TypeName = blockTypeIndices[i] < info.BlockTypeNames.Count
                    ? info.BlockTypeNames[blockTypeIndices[i]]
                    : "Unknown",
                Size = (int)blockSizes[i],
                DataOffset = pos
            });
            pos += (int)blockSizes[i];
        }
    }

    private static ushort ReadUInt16(byte[] data, int pos, bool isBigEndian)
    {
        return isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
    }

    private static uint ReadUInt32(byte[] data, int pos, bool isBigEndian)
    {
        return isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
    }

    private static int ParseStringTable(byte[] data, int pos, bool isBigEndian, List<string> strings)
    {
        // Num strings
        var numStrings = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        pos += 4;

        // Max string length (skip)
        pos += 4;

        // Strings
        for (var i = 0; i < numStrings; i++)
        {
            var strLen = isBigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
                : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            pos += 4;

            var str = Encoding.ASCII.GetString(data, pos, (int)strLen);
            strings.Add(str);
            pos += (int)strLen;
        }

        return pos;
    }

    public static bool IsBethesdaVersion(uint binaryVersion, uint userVersion)
    {
        return binaryVersion == 0x14020007 && (userVersion == 11 || userVersion == 12);
    }
}
