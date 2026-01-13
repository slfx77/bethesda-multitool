// NIF converter - Output writing methods

using System.Buffers.Binary;
using System.Text;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

internal sealed partial class NifConverter
{
    /// <summary>
    ///     Write the converted output with expanded geometry and removed packed blocks.
    /// </summary>
    private void WriteConvertedOutput(byte[] input, byte[] output, NifInfo info, int[] blockRemap)
    {
        // Simple case: no expansions, no blocks to strip, and no new strings -> do in-place conversion
        if (_blocksToStrip.Count == 0 && _geometryExpansions.Count == 0 && _havokExpansions.Count == 0 &&
            _skinPartitionExpansions.Count == 0 && _newStrings.Count == 0)
        {
            Array.Copy(input, output, input.Length);
            ConvertInPlace(output, info, blockRemap);
            return;
        }

        // Complex case: rebuild the file with new block sizes
        Log.Debug($"  Rebuilding file: removing {_blocksToStrip.Count} packed blocks, expanding {_geometryExpansions.Count} geometry, {_skinPartitionExpansions.Count} skin partition blocks, adding {_newStrings.Count} strings");

        var schemaConverter = new NifSchemaConverter(
            _schema,
            info.BinaryVersion,
            (int)info.UserVersion,
            (int)info.BsVersion);

        // Write header with updated block counts and sizes
        var outPos = WriteConvertedHeader(input, output, info);

        // Write each block
        foreach (var block in info.Blocks)
        {
            if (_blocksToStrip.Contains(block.Index))
            {
                // Skip packed blocks entirely
                continue;
            }

            var blockStartPos = outPos;
            var expectedSize = block.Size;

            if (_geometryExpansions.TryGetValue(block.Index, out var expansion))
            {
                expectedSize = expansion.NewSize;
                // Expand geometry block with unpacked data
                var packedData = _packedGeometryByBlock[expansion.PackedBlockIndex];

                // Check if this geometry block has a vertex map for skinned mesh remapping
                ushort[]? vertexMap = null;
                ushort[]? triangles = null;
                if (_geometryToSkinPartition.TryGetValue(block.Index, out var skinPartitionIndex))
                {
                    _vertexMaps.TryGetValue(skinPartitionIndex, out vertexMap);
                    _skinPartitionTriangles.TryGetValue(skinPartitionIndex, out triangles);
                    if (vertexMap != null)
                    {
                        Log.Debug($"    Block {block.Index}: Using vertex map from skin partition {skinPartitionIndex}, length={vertexMap.Length}");
                    }

                    if (triangles != null)
                    {
                        Log.Debug($"    Block {block.Index}: Using {triangles.Length / 3} triangles from skin partition {skinPartitionIndex}");
                    }
                }

                // For NiTriStripsData without skin partition, use triangles extracted from strips
                if (triangles == null && _geometryStripTriangles.TryGetValue(block.Index, out var stripTriangles))
                {
                    triangles = stripTriangles;
                    Log.Debug($"    Block {block.Index}: Using {triangles.Length / 3} triangles from NiTriStripsData strips");
                }

                outPos = WriteExpandedGeometryBlock(input, output, outPos, block, packedData, vertexMap,
                    triangles);
            }
            else if (_havokExpansions.TryGetValue(block.Index, out var havokExpansion))
            {
                expectedSize = havokExpansion.NewSize;
                // Expand Havok collision block with decompressed vertices
                outPos = WriteExpandedHavokBlock(input, output, outPos, block);
            }
            else if (_skinPartitionExpansions.TryGetValue(block.Index, out var skinPartExpansion))
            {
                expectedSize = skinPartExpansion.NewSize;
                // Expand NiSkinPartition block with bone weights/indices
                var packedData = _skinPartitionToPackedData[block.Index];
                outPos = NifSkinPartitionExpander.WriteExpanded(skinPartExpansion.ParsedData, packedData, output,
                    outPos);
            }
            else
            {
                // Regular block - copy and convert endianness
                outPos = WriteConvertedBlock(input, output, outPos, block, schemaConverter, blockRemap);
            }

            var actualSize = outPos - blockStartPos;
            if (actualSize != expectedSize)
            {
                Log.Debug($"  BLOCK SIZE MISMATCH: Block {block.Index} ({block.TypeName}) wrote {actualSize} bytes, expected {expectedSize}");
            }
        }

        // Write footer with remapped indices
        outPos = WriteConvertedFooter(input, output, outPos, info, blockRemap);

        Log.Debug($"  Final output size: {outPos} (buffer size: {output.Length})");
    }

    /// <summary>
    ///     Write the converted header to output with updated block counts and sizes.
    /// </summary>
    private int WriteConvertedHeader(byte[] input, byte[] output, NifInfo info)
    {
        var srcPos = 0;
        var outPos = 0;

        // Copy header string including newline
        var newlinePos = Array.IndexOf(input, (byte)0x0A, 0, 60);
        Array.Copy(input, 0, output, 0, newlinePos + 1);
        srcPos = newlinePos + 1;
        outPos = newlinePos + 1;

        // Binary version (4 bytes) - already LE in Bethesda files
        Array.Copy(input, srcPos, output, outPos, 4);
        srcPos += 4;
        outPos += 4;

        // Endian byte: change from 0 (BE) to 1 (LE)
        output[outPos] = 1;
        srcPos += 1;
        outPos += 1;

        // User version (4 bytes) - already LE
        Array.Copy(input, srcPos, output, outPos, 4);
        srcPos += 4;
        outPos += 4;

        // Num blocks (4 bytes) - write new count (LE)
        var newBlockCount = info.BlockCount - _blocksToStrip.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), (uint)newBlockCount);
        srcPos += 4;
        outPos += 4;

        // BS Header (Bethesda specific)
        // BS Version (4 bytes) - already LE
        Array.Copy(input, srcPos, output, outPos, 4);
        var bsVersion = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(srcPos));
        srcPos += 4;
        outPos += 4;

        // Author string (1 byte length + chars)
        var authorLen = input[srcPos];
        Array.Copy(input, srcPos, output, outPos, 1 + authorLen);
        srcPos += 1 + authorLen;
        outPos += 1 + authorLen;

        // Unknown int if bsVersion > 130
        if (bsVersion > 130)
        {
            Array.Copy(input, srcPos, output, outPos, 4);
            srcPos += 4;
            outPos += 4;
        }

        // Process Script if bsVersion < 131
        if (bsVersion < 131)
        {
            var psLen = input[srcPos];
            Array.Copy(input, srcPos, output, outPos, 1 + psLen);
            srcPos += 1 + psLen;
            outPos += 1 + psLen;
        }

        // Export Script
        var esLen = input[srcPos];
        Array.Copy(input, srcPos, output, outPos, 1 + esLen);
        srcPos += 1 + esLen;
        outPos += 1 + esLen;

        // Max Filepath if bsVersion >= 103
        if (bsVersion >= 103)
        {
            var mfLen = input[srcPos];
            Array.Copy(input, srcPos, output, outPos, 1 + mfLen);
            srcPos += 1 + mfLen;
            outPos += 1 + mfLen;
        }

        // Num Block Types (ushort) - convert BE to LE
        var numBlockTypes = ReadUInt16BE(input, srcPos);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numBlockTypes);
        srcPos += 2;
        outPos += 2;

        // Block type strings (SizedString: uint length BE + chars)
        for (var i = 0; i < numBlockTypes; i++)
        {
            var strLen = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), strLen);
            srcPos += 4;
            outPos += 4;

            Array.Copy(input, srcPos, output, outPos, (int)strLen);
            srcPos += (int)strLen;
            outPos += (int)strLen;
        }

        // Block type indices (ushort[numBlocks]) - skip removed blocks, convert BE to LE
        foreach (var block in info.Blocks)
        {
            if (!_blocksToStrip.Contains(block.Index))
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), block.TypeIndex);
                outPos += 2;
            }

            srcPos += 2;
        }

        // Block sizes (uint[numBlocks]) - skip removed, update expanded, convert BE to LE
        foreach (var block in info.Blocks)
        {
            if (!_blocksToStrip.Contains(block.Index))
            {
                var size = (uint)block.Size;
                if (_geometryExpansions.TryGetValue(block.Index, out var expansion))
                {
                    size = (uint)expansion.NewSize;
                }
                else if (_havokExpansions.TryGetValue(block.Index, out var havokExpansion))
                {
                    size = (uint)havokExpansion.NewSize;
                }
                else if (_skinPartitionExpansions.TryGetValue(block.Index, out var skinPartExpansion))
                {
                    size = (uint)skinPartExpansion.NewSize;
                }

                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), size);
                outPos += 4;
            }

            srcPos += 4;
        }

        // Num strings (uint) - add new strings count
        var numStrings = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos));
        var newNumStrings = numStrings + (uint)_newStrings.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), newNumStrings);
        srcPos += 4;
        outPos += 4;

        // Max string length (uint) - update if we have longer strings
        var maxStrLen = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos));
        foreach (var str in _newStrings)
        {
            if (str.Length > maxStrLen)
            {
                maxStrLen = (uint)str.Length;
            }
        }

        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), maxStrLen);
        srcPos += 4;
        outPos += 4;

        // Strings (SizedString: uint length BE + chars) - copy original strings
        for (var i = 0; i < numStrings; i++)
        {
            var strLen = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), strLen);
            srcPos += 4;
            outPos += 4;

            Array.Copy(input, srcPos, output, outPos, (int)strLen);
            srcPos += (int)strLen;
            outPos += (int)strLen;
        }

        // Write new strings for node names
        foreach (var str in _newStrings)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), (uint)str.Length);
            outPos += 4;
            Encoding.ASCII.GetBytes(str, output.AsSpan(outPos));
            outPos += str.Length;
        }

        // Num groups (uint) - convert BE to LE
        var numGroups = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numGroups);
        srcPos += 4;
        outPos += 4;

        // Groups (uint[numGroups]) - convert BE to LE
        for (var i = 0; i < numGroups; i++)
        {
            var groupSize = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), groupSize);
            srcPos += 4;
            outPos += 4;
        }

        return outPos;
    }

    /// <summary>
    ///     Write the footer with remapped block indices.
    /// </summary>
    private static int WriteConvertedFooter(byte[] input, byte[] output, int outPos, NifInfo info, int[] blockRemap)
    {
        // Calculate footer position in source
        var lastBlock = info.Blocks[^1];
        var footerPos = lastBlock.DataOffset + lastBlock.Size;

        if (footerPos + 4 > input.Length)
        {
            return outPos;
        }

        // numRoots (uint BE -> LE)
        var numRoots = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(footerPos));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numRoots);
        footerPos += 4;
        outPos += 4;

        // root indices (int[numRoots] BE -> LE with remapping)
        for (var i = 0; i < numRoots && footerPos + 4 <= input.Length; i++)
        {
            var rootIdx = BinaryPrimitives.ReadInt32BigEndian(input.AsSpan(footerPos));
            var newRootIdx = rootIdx >= 0 && rootIdx < blockRemap.Length ? blockRemap[rootIdx] : rootIdx;
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outPos), newRootIdx);
            footerPos += 4;
            outPos += 4;
        }

        return outPos;
    }

    /// <summary>
    ///     Write a regular block (copy and convert endianness).
    /// </summary>
    private int WriteConvertedBlock(byte[] input, byte[] output, int outPos, BlockInfo block,
        NifSchemaConverter schemaConverter, int[] blockRemap)
    {
        // Copy block data
        Array.Copy(input, block.DataOffset, output, outPos, block.Size);

        // Convert using schema
        if (!schemaConverter.TryConvert(output, outPos, block.Size, block.TypeName, blockRemap))
        {
            // Fallback: bulk swap
            BulkSwap32(output, outPos, block.Size);
        }

        // Restore node name if we have one from the palette
        if (_nodeNameStringIndices.TryGetValue(block.Index, out var stringIndex))
        {
            // The Name field is at offset 0 for NiNode/BSFadeNode (first field after AVObject inheritance)
            // It's a StringIndex which is a 4-byte int
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outPos), stringIndex);
            Log.Debug($"    Block {block.Index} ({block.TypeName}): Set Name to string index {stringIndex} ('{_nodeNamesByBlock[block.Index]}')");
        }

        return outPos + block.Size;
    }
}
