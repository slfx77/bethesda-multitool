using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Writes converted NIF headers, blocks, and footers.
/// </summary>
internal static class NifWriter
{
    /// <summary>
    ///     Write the converted header to output.
    /// </summary>
    public static int WriteHeader(
        byte[] data,
        byte[] output,
        NifInfo sourceInfo,
        HashSet<int> packedBlockIndices,
        Dictionary<int, GeometryBlockExpansion> geometryBlocksToExpand,
        Dictionary<int, HavokBlockExpansion>? havokBlocksToExpand = null)
    {
        havokBlocksToExpand ??= [];

        var pos = 0;
        var outPos = 0;

        // Copy header string including newline
        var newlinePos = Array.IndexOf(data, (byte)0x0A, 0, 60);
        Array.Copy(data, 0, output, 0, newlinePos + 1);
        pos = newlinePos + 1;
        outPos = newlinePos + 1;

        // Binary version (already LE)
        Array.Copy(data, pos, output, outPos, 4);
        pos += 4;
        outPos += 4;

        // Write endian byte as LE
        output[outPos] = 1;
        pos += 1;
        outPos += 1;

        // User version (already LE)
        Array.Copy(data, pos, output, outPos, 4);
        pos += 4;
        outPos += 4;

        // Write new block count (LE)
        var newBlockCount = sourceInfo.BlockCount - packedBlockIndices.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), (uint)newBlockCount);
        pos += 4;
        outPos += 4;

        // BS header if present
        if (sourceInfo.BsVersion > 0)
        {
            Array.Copy(data, pos, output, outPos, 4);
            pos += 4;
            outPos += 4;

            for (var i = 0; i < 3; i++)
            {
                var len = data[pos];
                Array.Copy(data, pos, output, outPos, 1 + len);
                pos += 1 + len;
                outPos += 1 + len;
            }
        }

        // Build block type remap: old index -> new index (or -1 if removed)
        // This removes the "BSPackedAdditionalGeometryData" block type if it's no longer used
        var blockTypeRemap = BuildBlockTypeRemap(sourceInfo, packedBlockIndices);
        var newBlockTypeCount = blockTypeRemap.Values.Count(v => v >= 0);

        // NumBlockTypes (write new count)
        var numBlockTypes = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), (ushort)newBlockTypeCount);
        pos += 2;
        outPos += 2;

        // Block type names (skip removed types)
        for (var i = 0; i < numBlockTypes; i++)
        {
            var strLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            pos += 4;

            if (blockTypeRemap[i] >= 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), strLen);
                outPos += 4;
                Array.Copy(data, pos, output, outPos, (int)strLen);
                outPos += (int)strLen;
            }

            pos += (int)strLen;
        }

        // Block type indices (remapped)
        foreach (var block in sourceInfo.Blocks)
        {
            if (!packedBlockIndices.Contains(block.Index))
            {
                var newTypeIndex = blockTypeRemap[block.TypeIndex];
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), (ushort)newTypeIndex);
                outPos += 2;
            }

            pos += 2;
        }

        // Block sizes
        foreach (var block in sourceInfo.Blocks)
        {
            if (!packedBlockIndices.Contains(block.Index))
            {
                var size = (uint)block.Size;
                if (geometryBlocksToExpand.TryGetValue(block.Index, out var geomExpansion))
                    size = (uint)geomExpansion.NewSize;
                else if (havokBlocksToExpand.TryGetValue(block.Index, out var havokExpansion))
                    size = (uint)havokExpansion.NewSize;
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), size);
                outPos += 4;
            }

            pos += 4;
        }

        // String table
        outPos = CopyStringTable(data, output, pos, outPos);

        return outPos;
    }

    /// <summary>
    ///     Builds a mapping from old block type indices to new indices.
    ///     Types that are no longer used (like BSPackedAdditionalGeometryData) get -1.
    /// </summary>
    private static Dictionary<int, int> BuildBlockTypeRemap(NifInfo sourceInfo, HashSet<int> packedBlockIndices)
    {
        // Find which block types are still in use after removing packed blocks
        var usedTypeIndices = new HashSet<int>();
        foreach (var block in sourceInfo.Blocks)
            if (!packedBlockIndices.Contains(block.Index))
                usedTypeIndices.Add(block.TypeIndex);

        // Build remap: old index -> new index (or -1 if not used)
        var remap = new Dictionary<int, int>();
        var newIndex = 0;
        for (var i = 0; i < sourceInfo.BlockTypeNames.Count; i++)
            if (usedTypeIndices.Contains(i))
                remap[i] = newIndex++;
            else
                remap[i] = -1;

        return remap;
    }

    private static int CopyStringTable(byte[] data, byte[] output, int pos, int outPos)
    {
        // Num strings
        var numStrings = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numStrings);
        pos += 4;
        outPos += 4;

        // Max string length
        var maxStrLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), maxStrLen);
        pos += 4;
        outPos += 4;

        // Strings
        for (var i = 0; i < numStrings; i++)
        {
            var strLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), strLen);
            pos += 4;
            outPos += 4;

            Array.Copy(data, pos, output, outPos, (int)strLen);
            pos += (int)strLen;
            outPos += (int)strLen;
        }

        // Num groups
        var numGroups = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numGroups);
        pos += 4;
        outPos += 4;

        // Group sizes
        for (var i = 0; i < numGroups; i++)
        {
            var groupSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), groupSize);
            pos += 4;
            outPos += 4;
        }

        return outPos;
    }

    /// <summary>
    ///     Write a regular block with proper endian conversion.
    /// </summary>
    public static int WriteBlock(byte[] data, byte[] output, int outPos, BlockInfo block, int[] blockRemap)
    {
        Array.Copy(data, block.DataOffset, output, outPos, block.Size);
        NifBlockConverters.ConvertBlockInPlace(output, outPos, block.Size, block.TypeName, blockRemap);
        return outPos + block.Size;
    }

    /// <summary>
    ///     Write the footer with remapped block references.
    /// </summary>
    public static int WriteFooter(byte[] data, byte[] output, int outPos, NifInfo sourceInfo, int[] blockRemap)
    {
        var footerPos = sourceInfo.Blocks.Count > 0
            ? sourceInfo.Blocks[^1].DataOffset + sourceInfo.Blocks[^1].Size
            : data.Length;

        if (footerPos >= data.Length) return outPos;

        var numRoots = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(footerPos));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numRoots);
        footerPos += 4;
        outPos += 4;

        for (var i = 0; i < numRoots; i++)
        {
            var rootIdx = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(footerPos));
            var newRootIdx = rootIdx >= 0 && rootIdx < blockRemap.Length ? blockRemap[rootIdx] : rootIdx;
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outPos), newRootIdx);
            footerPos += 4;
            outPos += 4;
        }

        return outPos;
    }
}
