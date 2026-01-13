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

        // Find header string (ends with newline)
        var newlinePos = Array.IndexOf(data, (byte)0x0A, 0, Math.Min(60, data.Length));
        if (newlinePos < 0) return null;

        info.HeaderString = Encoding.ASCII.GetString(data, 0, newlinePos);
        var pos = newlinePos + 1;

        // Binary version (always LE)
        info.BinaryVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        pos += 4;

        // Endian byte
        info.IsBigEndian = data[pos] == 0;
        pos += 1;

        // User version (always LE)
        info.UserVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        pos += 4;

        // Num blocks (always LE)
        var numBlocks = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        info.BlockCount = (int)numBlocks;
        pos += 4;

        // Check for Bethesda header
        if (IsBethesdaVersion(info.BinaryVersion, info.UserVersion))
        {
            // BS Version (always LE)
            info.BsVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            pos += 4;

            // Skip ShortStrings (author, process script, export script)
            for (var i = 0; i < 3; i++)
            {
                var len = data[pos];
                pos += 1 + len;
            }
        }
        else
        {
            // Non-Bethesda version - we only support Bethesda versions for full conversion
            // Return minimal info so caller can check endianness
            return info;
        }

        // NumBlockTypes (follows endian byte)
        var numBlockTypes = info.IsBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
        pos += 2;

        // Block type names (SizedStrings)
        for (var i = 0; i < numBlockTypes; i++)
        {
            if (pos + 4 > data.Length) return null;

            var strLen = info.IsBigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
                : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            pos += 4;

            // Sanity check: string length should be reasonable (< 256) and fit in buffer
            if (strLen > 256 || pos + strLen > data.Length) return null;

            info.BlockTypeNames.Add(Encoding.ASCII.GetString(data, pos, (int)strLen));
            pos += (int)strLen;
        }

        // Block type indices
        var blockTypeIndices = new ushort[numBlocks];
        for (var i = 0; i < numBlocks; i++)
        {
            blockTypeIndices[i] = info.IsBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            pos += 2;
        }

        // Block sizes
        var blockSizes = new uint[numBlocks];
        for (var i = 0; i < numBlocks; i++)
        {
            blockSizes[i] = info.IsBigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
                : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            pos += 4;
        }

        // Parse string table
        pos = ParseStringTable(data, pos, info.IsBigEndian, info.Strings);

        // Num groups
        var numGroups = info.IsBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        pos += 4;
        pos += (int)numGroups * 4;

        // Build block list
        for (var i = 0; i < numBlocks; i++)
        {
            var block = new BlockInfo
            {
                Index = i,
                TypeIndex = blockTypeIndices[i],
                TypeName = blockTypeIndices[i] < info.BlockTypeNames.Count
                    ? info.BlockTypeNames[blockTypeIndices[i]]
                    : "Unknown",
                Size = (int)blockSizes[i],
                DataOffset = pos
            };
            info.Blocks.Add(block);
            pos += (int)blockSizes[i];
        }

        return info;
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
