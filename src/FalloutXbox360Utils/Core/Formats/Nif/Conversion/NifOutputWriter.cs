// NIF converter - Output writing and in-place conversion methods

using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Nif.Geometry;
using FalloutXbox360Utils.Core.Formats.Nif.Skinning;
using FalloutXbox360Utils.Core.Utils;
using static FalloutXbox360Utils.Core.Formats.Nif.Conversion.NifEndianUtils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Conversion;

/// <summary>
///     Handles the output phase of NIF conversion: writing converted headers, blocks, and footers.
///     Supports both in-place conversion (no size changes) and full rebuild with expanded/stripped blocks.
/// </summary>
internal sealed class NifOutputWriter(NifConversionState state)
{
    private static readonly Logger Log = Logger.Instance;

    private readonly NifConversionState _state = state;

    /// <summary>
    ///     Write the converted output with expanded geometry and removed packed blocks.
    /// </summary>
    public void WriteConvertedOutput(byte[] input, byte[] output, NifInfo info, int[] blockRemap)
    {
        // Simple case: no expansions, no blocks to strip, and no new strings -> do in-place conversion
        if (_state.CanUseInPlaceConversion)
        {
            Array.Copy(input, output, input.Length);
            ConvertInPlace(output, info, blockRemap);
            return;
        }

        // Complex case: rebuild the file with new block sizes
        Log.Debug(
            $"  Rebuilding file: removing {_state.BlocksToStrip.Count} packed blocks, expanding {_state.GeometryExpansions.Count} geometry, {_state.SkinPartitionExpansions.Count} skin partition blocks, adding {_state.NewStrings.Count} strings");

        var schemaConverter = new NifSchemaConverter(
            _state.Schema,
            info.BinaryVersion,
            (int)info.UserVersion,
            (int)info.BsVersion);

        // Write header with updated block counts and sizes
        var outPos = WriteConvertedHeader(input, output, info);

        // Write each block
        foreach (var block in info.Blocks)
        {
            if (_state.BlocksToStrip.Contains(block.Index))
            {
                continue;
            }

            var blockStartPos = outPos;
            var (newPos, expectedSize) = WriteBlockByType(input, output, outPos, block, schemaConverter, blockRemap);
            outPos = newPos;

            var actualSize = outPos - blockStartPos;
            if (actualSize != expectedSize)
            {
                Log.Debug(
                    $"  BLOCK SIZE MISMATCH: Block {block.Index} ({block.TypeName}) wrote {actualSize} bytes, expected {expectedSize}");
            }
        }

        // Write footer with remapped indices
        outPos = WriteConvertedFooter(input, output, outPos, info, blockRemap);

        Log.Debug($"  Final output size: {outPos} (buffer size: {output.Length})");
    }

    private (int newPos, int expectedSize) WriteBlockByType(
        byte[] input,
        byte[] output,
        int outPos,
        BlockInfo block,
        NifSchemaConverter schemaConverter,
        int[] blockRemap)
    {
        if (_state.GeometryExpansions.TryGetValue(block.Index, out var expansion))
        {
            var pos = WriteExpandedGeometryBlockWithMaps(input, output, outPos, block, expansion);
            return (pos, expansion.NewSize);
        }

        if (_state.HavokExpansions.TryGetValue(block.Index, out var havokExpansion))
        {
            var pos = NifGeometryWriter.WriteExpandedHavokBlock(input, output, outPos, block);
            return (pos, havokExpansion.NewSize);
        }

        if (_state.SkinPartitionExpansions.TryGetValue(block.Index, out var skinPartExpansion))
        {
            var packedData = _state.SkinPartitionToPackedData[block.Index];
            var pos = NifSkinPartitionExpander.WriteExpanded(skinPartExpansion.ParsedData, packedData, output, outPos);
            return (pos, skinPartExpansion.NewSize);
        }

        // Regular block - copy and convert endianness
        var newPos = WriteConvertedBlock(input, output, outPos, block, schemaConverter, blockRemap);
        return (newPos, block.Size);
    }

    private int WriteExpandedGeometryBlockWithMaps(
        byte[] input,
        byte[] output,
        int outPos,
        BlockInfo block,
        GeometryBlockExpansion expansion)
    {
        var packedData = _state.PackedGeometryByBlock[expansion.PackedBlockIndex];

        // Check if this geometry block has a vertex map for skinned mesh remapping
        ushort[]? vertexMap = null;
        ushort[]? triangles = null;

        if (_state.GeometryToSkinPartition.TryGetValue(block.Index, out var skinPartitionIndex))
        {
            _state.VertexMaps.TryGetValue(skinPartitionIndex, out vertexMap);
            _state.SkinPartitionTriangles.TryGetValue(skinPartitionIndex, out triangles);
            LogVertexMapAndTriangles(block.Index, skinPartitionIndex, vertexMap, triangles);
        }

        // For NiTriStripsData without skin partition, use triangles extracted from strips
        if (triangles == null && _state.GeometryStripTriangles.TryGetValue(block.Index, out var stripTriangles))
        {
            triangles = stripTriangles;
            Log.Debug($"    Block {block.Index}: Using {triangles.Length / 3} triangles from NiTriStripsData strips");
        }

        return NifGeometryWriter.WriteExpandedGeometryBlock(input, output, outPos, block, packedData, vertexMap, triangles);
    }

    private static void LogVertexMapAndTriangles(int blockIndex, int skinPartitionIndex, ushort[]? vertexMap,
        ushort[]? triangles)
    {
        if (vertexMap != null)
        {
            Log.Debug(
                $"    Block {blockIndex}: Using vertex map from skin partition {skinPartitionIndex}, length={vertexMap.Length}");
        }

        if (triangles != null)
        {
            Log.Debug(
                $"    Block {blockIndex}: Using {triangles.Length / 3} triangles from skin partition {skinPartitionIndex}");
        }
    }

    /// <summary>
    ///     Write the converted header to output with updated block counts and sizes.
    /// </summary>
    private int WriteConvertedHeader(byte[] input, byte[] output, NifInfo info)
    {
        // Copy header string including newline
        var newlinePos = Array.IndexOf(input, (byte)0x0A, 0, 60);
        Array.Copy(input, 0, output, 0, newlinePos + 1);
        var srcPos = newlinePos + 1;
        var outPos = newlinePos + 1;

        // Write fixed header fields
        (srcPos, outPos) = WriteHeaderFixedFields(input, output, srcPos, outPos, info);

        // BS Header (Bethesda specific)
        var bsVersion = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(srcPos));
        (srcPos, outPos) = WriteBsHeader(input, output, srcPos, outPos, bsVersion);

        // Write block type information
        (srcPos, outPos) = WriteBlockTypeInfo(input, output, srcPos, outPos);

        // Write block type indices and sizes
        (srcPos, outPos) = WriteBlockIndicesAndSizes(output, srcPos, outPos, info);

        // Write strings including new node name strings
        (srcPos, outPos) = WriteStringsSection(input, output, srcPos, outPos);

        // Write groups section
        outPos = WriteGroupsSection(input, output, srcPos, outPos);

        return outPos;
    }

    private (int srcPos, int outPos) WriteHeaderFixedFields(byte[] input, byte[] output, int srcPos, int outPos,
        NifInfo info)
    {
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
        var newBlockCount = info.BlockCount - _state.BlocksToStrip.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), (uint)newBlockCount);
        srcPos += 4;
        outPos += 4;

        return (srcPos, outPos);
    }

    private static (int srcPos, int outPos) WriteBsHeader(byte[] input, byte[] output, int srcPos, int outPos,
        uint bsVersion)
    {
        // BS Version (4 bytes) - already LE
        Array.Copy(input, srcPos, output, outPos, 4);
        srcPos += 4;
        outPos += 4;

        // Author string (1 byte length + chars)
        (srcPos, outPos) = CopyLengthPrefixedString(input, output, srcPos, outPos);

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
            (srcPos, outPos) = CopyLengthPrefixedString(input, output, srcPos, outPos);
        }

        // Export Script
        (srcPos, outPos) = CopyLengthPrefixedString(input, output, srcPos, outPos);

        // Max Filepath if bsVersion >= 103
        if (bsVersion >= 103)
        {
            (srcPos, outPos) = CopyLengthPrefixedString(input, output, srcPos, outPos);
        }

        return (srcPos, outPos);
    }

    private static (int srcPos, int outPos) CopyLengthPrefixedString(byte[] input, byte[] output, int srcPos,
        int outPos)
    {
        var strLen = input[srcPos];
        Array.Copy(input, srcPos, output, outPos, 1 + strLen);
        return (srcPos + 1 + strLen, outPos + 1 + strLen);
    }

    private static (int srcPos, int outPos) WriteBlockTypeInfo(byte[] input, byte[] output, int srcPos, int outPos)
    {
        // Num Block Types (ushort) - convert BE to LE
        var numBlockTypes = BinaryUtils.ReadUInt16BE(input, srcPos);
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

        return (srcPos, outPos);
    }

    private (int srcPos, int outPos) WriteBlockIndicesAndSizes(byte[] output, int srcPos, int outPos, NifInfo info)
    {
        // Block type indices (ushort[numBlocks]) - skip removed blocks, convert BE to LE
        foreach (var block in info.Blocks)
        {
            if (!_state.BlocksToStrip.Contains(block.Index))
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), block.TypeIndex);
                outPos += 2;
            }

            srcPos += 2;
        }

        // Block sizes (uint[numBlocks]) - skip removed, update expanded, convert BE to LE
        foreach (var block in info.Blocks)
        {
            if (!_state.BlocksToStrip.Contains(block.Index))
            {
                var size = _state.GetBlockOutputSize(block);
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), (uint)size);
                outPos += 4;
            }

            srcPos += 4;
        }

        return (srcPos, outPos);
    }

    private (int srcPos, int outPos) WriteStringsSection(byte[] input, byte[] output, int srcPos, int outPos)
    {
        // Num strings (uint) - add new strings count
        var numStrings = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos));
        var newNumStrings = numStrings + (uint)_state.NewStrings.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), newNumStrings);
        srcPos += 4;
        outPos += 4;

        // Max string length (uint) - update if we have longer strings
        var maxStrLen = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos));
        maxStrLen = Math.Max(maxStrLen, (uint)_state.NewStrings.Select(s => s.Length).DefaultIfEmpty(0).Max());
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
        foreach (var str in _state.NewStrings)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), (uint)str.Length);
            outPos += 4;
            Encoding.ASCII.GetBytes(str, output.AsSpan(outPos));
            outPos += str.Length;
        }

        return (srcPos, outPos);
    }

    private static int WriteGroupsSection(byte[] input, byte[] output, int srcPos, int outPos)
    {
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
            // Fallback: bulk swap
        {
            BulkSwap32(output, outPos, block.Size);
        }

        // Restore node name if we have one from the palette
        if (_state.NodeNameStringIndices.TryGetValue(block.Index, out var stringIndex))
        {
            // The Name field is at offset 0 for NiNode/BSFadeNode (first field after AVObject inheritance)
            // It's a StringIndex which is a 4-byte int
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outPos), stringIndex);
            Log.Debug(
                $"    Block {block.Index} ({block.TypeName}): Set Name to string index {stringIndex} ('{_state.NodeNamesByBlock[block.Index]}')");
        }

        return outPos + block.Size;
    }

    /// <summary>
    ///     Convert the NIF in place (no size changes).
    /// </summary>
    public void ConvertInPlace(byte[] buf, NifInfo info, int[] blockRemap)
    {
        // Convert header
        ConvertHeader(buf, info);

        // Create schema converter
        var schemaConverter = new NifSchemaConverter(
            _state.Schema,
            info.BinaryVersion,
            (int)info.UserVersion,
            (int)info.BsVersion);

        // Convert each block using schema
        foreach (var block in info.Blocks)
        {
            if (_state.BlocksToStrip.Contains(block.Index))
            {
                Log.Debug($"  Block {block.Index}: {block.TypeName} - skipping (will be removed)");
                continue;
            }

            Log.Debug($"  Block {block.Index}: {block.TypeName} at offset {block.DataOffset:X}, size {block.Size}");

            if (!schemaConverter.TryConvert(buf, block.DataOffset, block.Size, block.TypeName, blockRemap))
            {
                // Fallback: bulk swap all 4-byte values (may break some data)
                Log.Debug("    -> Using fallback bulk swap");
                BulkSwap32(buf, block.DataOffset, block.Size);
            }
        }

        // Convert footer
        ConvertFooter(buf, info);
    }

    /// <summary>
    ///     Convert header endianness.
    /// </summary>
    private static void ConvertHeader(byte[] buf, NifInfo info)
    {
        // The header string and version are always little-endian
        // Only the endian byte needs to change from 0 (BE) to 1 (LE)

        // Find the endian byte position (after header string + binary version)
        var pos = info.HeaderString.Length + 1 + 4; // +1 for newline, +4 for binary version

        // Change endian byte from 0 to 1
        if (buf[pos] == 0)
        {
            buf[pos] = 1;
        }

        // Swap header fields
        SwapHeaderFields(buf, info);
    }

    /// <summary>
    ///     Convert footer endianness.
    /// </summary>
    private static void ConvertFooter(byte[] buf, NifInfo info)
    {
        // Footer is at the end of the file after all blocks
        // Structure: Num Roots (uint) + Root indices (int[Num Roots])

        // Calculate footer position
        var lastBlock = info.Blocks[^1];
        var footerPos = lastBlock.DataOffset + lastBlock.Size;

        if (footerPos + 4 > buf.Length)
        {
            return;
        }

        // Swap num roots
        SwapUInt32InPlace(buf, footerPos);
        var numRoots = BinaryUtils.ReadUInt32LE(buf, footerPos);
        footerPos += 4;

        // Swap root indices
        for (var i = 0; i < numRoots && footerPos + 4 <= buf.Length; i++)
        {
            SwapUInt32InPlace(buf, footerPos);
            footerPos += 4;
        }
    }

    /// <summary>
    ///     Swap all header fields from big-endian to little-endian.
    /// </summary>
    private static void SwapHeaderFields(byte[] buf, NifInfo info)
    {
        // Position after header string + newline + binary version + endian byte
        var pos = info.HeaderString.Length + 1 + 4 + 1;

        // User version (4 bytes) - already LE in Bethesda
        pos += 4;

        // Num blocks (4 bytes) - already LE in Bethesda
        pos += 4;

        // BS Header (Bethesda specific)
        // BS Version (4 bytes) - already LE
        var bsVersion = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos, 4));
        pos += 4;

        // Author string (1 byte length + chars)
        var authorLen = buf[pos];
        pos += 1 + authorLen;

        // Unknown int if bsVersion > 130
        if (bsVersion > 130)
        {
            pos += 4;
        }

        // Process Script if bsVersion < 131
        if (bsVersion < 131)
        {
            var psLen = buf[pos];
            pos += 1 + psLen;
        }

        // Export Script
        var esLen = buf[pos];
        pos += 1 + esLen;

        // Max Filepath if bsVersion >= 103
        if (bsVersion >= 103)
        {
            var mfLen = buf[pos];
            pos += 1 + mfLen;
        }

        // Now we're at Num Block Types (ushort) - needs swap
        SwapUInt16InPlace(buf, pos);
        var numBlockTypes = BinaryUtils.ReadUInt16LE(buf, pos);
        pos += 2;

        // Block type strings (SizedString: uint length + chars)
        for (var i = 0; i < numBlockTypes; i++)
        {
            SwapUInt32InPlace(buf, pos);
            var strLen = BinaryUtils.ReadUInt32LE(buf, pos);
            pos += 4 + (int)strLen;
        }

        // Block type indices (ushort[numBlocks])
        for (var i = 0; i < info.BlockCount; i++)
        {
            SwapUInt16InPlace(buf, pos);
            pos += 2;
        }

        // Block sizes (uint[numBlocks])
        for (var i = 0; i < info.BlockCount; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // Num strings (uint)
        SwapUInt32InPlace(buf, pos);
        var numStrings = BinaryUtils.ReadUInt32LE(buf, pos);
        pos += 4;

        // Max string length (uint)
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // Strings (SizedString: uint length + chars)
        for (var i = 0; i < numStrings; i++)
        {
            SwapUInt32InPlace(buf, pos);
            var strLen = BinaryUtils.ReadUInt32LE(buf, pos);
            pos += 4 + (int)strLen;
        }

        // Num groups (uint)
        SwapUInt32InPlace(buf, pos);
        var numGroups = BinaryUtils.ReadUInt32LE(buf, pos);
        pos += 4;

        // Groups (uint[numGroups])
        for (var i = 0; i < numGroups; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }
    }
}
