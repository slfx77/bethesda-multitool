using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Converts Xbox 360 NIF files to PC-compatible format.
///     Handles endian conversion and BSPackedAdditionalGeometryData decompression.
/// </summary>
/// <remarks>
///     Xbox 360 NIFs use BSPackedAdditionalGeometryData to store vertex data as half-floats
///     in a GPU-optimized interleaved format. PC NIFs store this data as full floats
///     directly in NiTriStripsData/NiTriShapeData blocks.
/// </remarks>
public sealed class NifConverter
{
    private readonly NifGeometryExtractor _geometryExtractor;
    private readonly bool _verbose;

    public NifConverter(bool verbose = false)
    {
        _verbose = verbose;
        _geometryExtractor = new NifGeometryExtractor(verbose);
    }

    /// <summary>
    ///     Converts an Xbox 360 NIF file to PC format.
    /// </summary>
    public ConversionResult Convert(byte[] data)
    {
        try
        {
            var sourceInfo = NifParser.Parse(data);
            if (sourceInfo == null)
                return new ConversionResult
                {
                    Success = false,
                    ErrorMessage = "Failed to parse NIF header"
                };

            if (!sourceInfo.IsBigEndian)
                return new ConversionResult
                {
                    Success = true,
                    OutputData = data,
                    SourceInfo = sourceInfo,
                    OutputInfo = sourceInfo,
                    ErrorMessage = "File is already little-endian (PC format)"
                };

            var packedBlocks = sourceInfo.Blocks
                .Where(b => b.TypeName == "BSPackedAdditionalGeometryData")
                .ToList();

            if (packedBlocks.Count == 0) return ConvertEndianOnly(data, sourceInfo);

            return ConvertWithGeometryUnpacking(data, sourceInfo, packedBlocks);
        }
        catch (Exception ex)
        {
            return new ConversionResult
            {
                Success = false,
                ErrorMessage = $"Conversion failed: {ex.Message}"
            };
        }
    }

    private static ConversionResult ConvertEndianOnly(byte[] data, NifInfo sourceInfo)
    {
        var output = new byte[data.Length];
        var blockRemap = CreateIdentityRemap(sourceInfo.BlockCount);

        var outPos = NifWriter.WriteHeader(data, output, sourceInfo, [], []);

        foreach (var block in sourceInfo.Blocks) outPos = NifWriter.WriteBlock(data, output, outPos, block, blockRemap);

        outPos = NifWriter.WriteFooter(data, output, outPos, sourceInfo, blockRemap);

        if (outPos < output.Length) Array.Resize(ref output, outPos);

        return new ConversionResult
        {
            Success = true,
            OutputData = output,
            SourceInfo = sourceInfo,
            OutputInfo = NifParser.Parse(output)
        };
    }

    private ConversionResult ConvertWithGeometryUnpacking(
        byte[] data, NifInfo sourceInfo, List<BlockInfo> packedBlocks)
    {
        var geometryDataByBlock = ExtractAllPackedGeometry(data, packedBlocks);
        var geometryBlocksToExpand = AnalyzeGeometryBlocks(data, sourceInfo, geometryDataByBlock);
        var vertexMapByGeometryBlock = ExtractVertexMaps(data, sourceInfo, geometryBlocksToExpand);
        var output = BuildConvertedOutput(data, sourceInfo, packedBlocks, geometryBlocksToExpand, geometryDataByBlock, vertexMapByGeometryBlock);

        return new ConversionResult
        {
            Success = true,
            OutputData = output,
            SourceInfo = sourceInfo,
            OutputInfo = NifParser.Parse(output)
        };
    }

    private Dictionary<int, PackedGeometryData> ExtractAllPackedGeometry(
        byte[] data, List<BlockInfo> packedBlocks)
    {
        var result = new Dictionary<int, PackedGeometryData>();

        foreach (var block in packedBlocks)
        {
            var packedData = _geometryExtractor.Extract(data, block);
            if (packedData != null)
            {
                result[block.Index] = packedData;
                if (_verbose)
                    Console.WriteLine(
                        $"Extracted geometry from block {block.Index}: {packedData.NumVertices} vertices");
            }
        }

        return result;
    }

    private Dictionary<int, GeometryBlockExpansion> AnalyzeGeometryBlocks(
        byte[] data, NifInfo sourceInfo, Dictionary<int, PackedGeometryData> geometryDataByBlock)
    {
        var result = new Dictionary<int, GeometryBlockExpansion>();

        foreach (var block in sourceInfo.Blocks.Where(b => b.TypeName is "NiTriStripsData" or "NiTriShapeData"))
        {
            var expansion = AnalyzeGeometryBlock(data, block, geometryDataByBlock);
            if (expansion != null)
            {
                result[block.Index] = expansion;
                if (_verbose)
                    Console.WriteLine(
                        $"Block {block.Index} ({block.TypeName}) will expand by {expansion.SizeIncrease} bytes");
            }
        }

        return result;
    }

    private static GeometryBlockExpansion? AnalyzeGeometryBlock(
        byte[] data, BlockInfo block, Dictionary<int, PackedGeometryData> geometryDataByBlock)
    {
        var pos = block.DataOffset + 4; // Skip groupId

        var numVertices = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
        pos += 4; // numVertices + flags

        var hasVertices = data[pos++];
        if (hasVertices != 0) pos += numVertices * 12;

        var bsDataFlags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
        pos += 2;

        var hasNormals = data[pos++];
        if (hasNormals != 0)
        {
            pos += numVertices * 12;
            if ((bsDataFlags & 4096) != 0) pos += numVertices * 24;
        }

        pos += 16; // center + radius

        var hasVertexColors = data[pos++];
        if (hasVertexColors != 0) pos += numVertices * 16;

        var numUVSets = bsDataFlags & 1;
        if (numUVSets != 0) pos += numVertices * 8;

        pos += 2; // consistency

        var additionalDataRef = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
        if (additionalDataRef < 0 ||
            !geometryDataByBlock.TryGetValue(additionalDataRef, out var packedData)) return null;

        // For NiTriShapeData with HasTriangles=0, we still want to expand vertex/normal/UV data
        // The triangles will come from NiSkinPartition for skinned meshes
        if (block.TypeName == "NiTriShapeData")
        {
            pos += 4; // skip additionalData ref
            var numTriangles = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
            pos += 2;
            pos += 4; // numTrianglePoints
            var hasTriangles = data[pos];

            if (hasTriangles == 0 && numTriangles > 0)
            {
                Console.WriteLine($"    Note: Block {block.Index} (NiTriShapeData) has HasTriangles=0");
                Console.WriteLine($"    Will expand vertex/normal/UV data; triangles from NiSkinPartition");
            }
        }

        var sizeIncrease = CalculateSizeIncrease(hasVertices, hasNormals, numUVSets, numVertices, packedData);
        if (sizeIncrease == 0) return null;

        return new GeometryBlockExpansion
        {
            BlockIndex = block.Index,
            PackedBlockIndex = additionalDataRef,
            OriginalSize = block.Size,
            NewSize = block.Size + sizeIncrease,
            SizeIncrease = sizeIncrease
        };
    }

    private static int CalculateSizeIncrease(
        byte hasVertices, byte hasNormals, int numUVSets, int numVertices, PackedGeometryData packedData)
    {
        var sizeIncrease = 0;

        if (hasVertices == 0 && packedData.Positions != null) sizeIncrease += numVertices * 12;

        if (hasNormals == 0 && packedData.Normals != null)
        {
            sizeIncrease += numVertices * 12;
            if (packedData.Tangents != null) sizeIncrease += numVertices * 12;
            if (packedData.Bitangents != null) sizeIncrease += numVertices * 12;
        }

        if (numUVSets == 0 && packedData.UVs != null) sizeIncrease += numVertices * 8;

        return sizeIncrease;
    }

    /// <summary>
    ///     Extracts VertexMaps from NiSkinPartition blocks for skinned meshes.
    /// </summary>
    /// <remarks>
    ///     For skinned meshes, BSPackedAdditionalGeometryData stores vertices in partition order,
    ///     not mesh order. The VertexMap from NiSkinPartition is needed to remap vertices
    ///     to the correct positions.
    /// </remarks>
    private Dictionary<int, ushort[]> ExtractVertexMaps(
        byte[] data, NifInfo sourceInfo, Dictionary<int, GeometryBlockExpansion> geometryBlocksToExpand)
    {
        var result = new Dictionary<int, ushort[]>();

        // Find all NiSkinPartition blocks
        var skinPartitionBlocks = sourceInfo.Blocks
            .Where(b => b.TypeName == "NiSkinPartition")
            .ToDictionary(b => b.Index, b => b);

        if (_verbose)
            Console.WriteLine($"  ExtractVertexMaps: Found {skinPartitionBlocks.Count} NiSkinPartition blocks");

        if (skinPartitionBlocks.Count == 0)
            return result;

        // Find all BSDismemberSkinInstance/NiSkinInstance blocks and their partition refs
        var skinInstanceBlocks = sourceInfo.Blocks
            .Where(b => b.TypeName is "BSDismemberSkinInstance" or "NiSkinInstance")
            .ToList();

        if (_verbose)
            Console.WriteLine($"  ExtractVertexMaps: Found {skinInstanceBlocks.Count} skin instance blocks");

        // Build a map from geometry data index to skin partition
        foreach (var geomExpansion in geometryBlocksToExpand)
        {
            var geomBlockIndex = geomExpansion.Key;

            if (_verbose)
                Console.WriteLine($"  ExtractVertexMaps: Processing geometry block {geomBlockIndex}");

            // Find the NiTriShape that owns this geometry data
            var ownerShape = FindOwnerShape(data, sourceInfo, geomBlockIndex);
            if (ownerShape == null)
            {
                if (_verbose)
                    Console.WriteLine($"    Could not find owner shape for geometry block {geomBlockIndex}");
                continue;
            }

            if (_verbose)
                Console.WriteLine($"    Found owner shape: block {ownerShape.Index} ({ownerShape.TypeName})");

            // Find the skin instance from the shape
            var skinInstanceRef = FindSkinInstanceRef(data, ownerShape, geomBlockIndex, sourceInfo.IsBigEndian);
            if (_verbose)
                Console.WriteLine($"    Skin instance ref: {skinInstanceRef}");

            if (skinInstanceRef < 0 || !skinInstanceBlocks.Any(b => b.Index == skinInstanceRef))
            {
                if (_verbose)
                    Console.WriteLine($"    Skin instance ref {skinInstanceRef} not found in skin instance blocks");
                continue;
            }

            var skinInstanceBlock = skinInstanceBlocks.First(b => b.Index == skinInstanceRef);

            // Find the NiSkinPartition ref from the skin instance
            var skinPartitionRef = NifSkinPartitionParser.FindSkinPartitionRef(
                data, skinInstanceBlock.DataOffset, sourceInfo.IsBigEndian);

            if (_verbose)
                Console.WriteLine($"    Skin partition ref: {skinPartitionRef}");

            if (skinPartitionRef < 0 || !skinPartitionBlocks.TryGetValue(skinPartitionRef, out var skinPartitionBlock))
            {
                if (_verbose)
                    Console.WriteLine($"    Skin partition ref {skinPartitionRef} not found");
                continue;
            }

            // Extract the VertexMap
            var vertexMap = NifSkinPartitionParser.ExtractVertexMap(
                data, skinPartitionBlock.DataOffset, skinPartitionBlock.Size, sourceInfo.IsBigEndian);

            if (vertexMap != null)
            {
                result[geomBlockIndex] = vertexMap;
                if (_verbose)
                    Console.WriteLine($"  Found VertexMap for geometry block {geomBlockIndex}: {vertexMap.Length} entries");
            }
            else
            {
                if (_verbose)
                    Console.WriteLine($"    Failed to extract VertexMap from skin partition block {skinPartitionBlock.Index}");
            }
        }

        return result;
    }

    private static BlockInfo? FindOwnerShape(byte[] data, NifInfo sourceInfo, int geomDataBlockIndex)
    {
        // Find the NiTriShape/NiTriStrips that references this geometry data block
        foreach (var block in sourceInfo.Blocks.Where(b => b.TypeName is "NiTriShape" or "NiTriStrips"))
        {
            // NiTriShape/NiTriStrips structure has Data ref at offset 44 (after NiAVObject fields)
            // Actually the position depends on the exact structure. Let's look for the ref.
            // In the NIF format, the Data ref comes after:
            // - NiObjectNET: name(4), extraData count(4), extraData refs, controller ref(4)
            // - NiAVObject: flags(2), translation(12), rotation(36), scale(4), numProps(4), props refs, collisionObject ref(4)
            // This is complex, so let's scan for the block reference instead.

            // Actually, for Gamebryo NIFs, we can look at the end of the block for the data ref
            // NiTriShape ends with: ... properties[], collision ref, data ref, skin instance ref
            // Let's scan backward from the skin instance to find the data ref

            var pos = block.DataOffset;
            var blockEnd = block.DataOffset + block.Size;

            // Scan the block for the geometry data reference
            while (pos < blockEnd - 4)
            {
                var refValue = sourceInfo.IsBigEndian
                    ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos))
                    : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));

                if (refValue == geomDataBlockIndex)
                    return block;

                pos++;
            }
        }

        return null;
    }

    private static int FindSkinInstanceRef(byte[] data, BlockInfo shapeBlock, int geomDataBlockIndex, bool bigEndian)
    {
        // NiTriShape/NiTriStrips structure ends with:
        // ... CollisionObject ref, Data ref, SkinInstance ref, NumMaterials, ...
        // Find where the Data ref is and read the SkinInstance ref after it

        var pos = shapeBlock.DataOffset;
        var blockEnd = shapeBlock.DataOffset + shapeBlock.Size;

        // Scan for the Data ref (geomDataBlockIndex), then read the next int32 as SkinInstance ref
        while (pos < blockEnd - 8) // Need at least 8 bytes: Data ref + SkinInstance ref
        {
            var refValue = bigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos))
                : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));

            if (refValue == geomDataBlockIndex)
            {
                // Found Data ref, next int32 is SkinInstance ref
                return bigEndian
                    ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos + 4))
                    : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 4));
            }

            pos++;
        }

        return -1;
    }

    private static byte[] BuildConvertedOutput(
        byte[] data,
        NifInfo sourceInfo,
        List<BlockInfo> packedBlocks,
        Dictionary<int, GeometryBlockExpansion> geometryBlocksToExpand,
        Dictionary<int, PackedGeometryData> geometryDataByBlock,
        Dictionary<int, ushort[]> vertexMapByGeometryBlock)
    {
        var packedBlockIndices = new HashSet<int>(packedBlocks.Select(b => b.Index));
        var (output, blockRemap) = AllocateOutput(data, sourceInfo, packedBlocks, geometryBlocksToExpand);

        var outPos = NifWriter.WriteHeader(data, output, sourceInfo, packedBlockIndices, geometryBlocksToExpand);

        foreach (var block in sourceInfo.Blocks)
        {
            if (packedBlockIndices.Contains(block.Index)) continue;

            var blockStartPos = outPos;

            if (geometryBlocksToExpand.TryGetValue(block.Index, out var expansion))
            {
                var packedData = geometryDataByBlock[expansion.PackedBlockIndex];
                vertexMapByGeometryBlock.TryGetValue(block.Index, out var vertexMap);
                outPos = NifGeometryWriter.WriteExpandedBlock(data, output, outPos, block, packedData, vertexMap, verbose: true);

                var actualWritten = outPos - blockStartPos;
                if (actualWritten != expansion.NewSize)
                    Console.WriteLine($"  WARNING: Block {block.Index} expected {expansion.NewSize} bytes, wrote {actualWritten}");
            }
            else
            {
                outPos = NifWriter.WriteBlock(data, output, outPos, block, blockRemap);
            }
        }

        outPos = NifWriter.WriteFooter(data, output, outPos, sourceInfo, blockRemap);

        if (outPos < output.Length) Array.Resize(ref output, outPos);

        return output;
    }

    private static (byte[] output, int[] blockRemap) AllocateOutput(
        byte[] data,
        NifInfo sourceInfo,
        List<BlockInfo> packedBlocks,
        Dictionary<int, GeometryBlockExpansion> geometryBlocksToExpand)
    {
        var packedBlockIndices = new HashSet<int>(packedBlocks.Select(b => b.Index));

        // Calculate header size changes:
        // - Each removed block loses 2 bytes (block type index) + 4 bytes (block size) = 6 bytes
        // - Removed block type string loses 4 bytes (length) + string length
        var headerSizeDelta = -(packedBlocks.Count * 6);

        // Calculate removed block type strings
        var usedTypeIndices = new HashSet<int>();
        foreach (var block in sourceInfo.Blocks)
            if (!packedBlockIndices.Contains(block.Index))
                usedTypeIndices.Add(block.TypeIndex);

        for (var i = 0; i < sourceInfo.BlockTypeNames.Count; i++)
            if (!usedTypeIndices.Contains(i))
                // This block type is no longer used - subtract its string from header
                // (4 bytes for length prefix + string content)
                headerSizeDelta -= 4 + sourceInfo.BlockTypeNames[i].Length;

        long totalBlockSizeDelta = 0;
        foreach (var block in sourceInfo.Blocks)
            if (packedBlockIndices.Contains(block.Index))
                totalBlockSizeDelta -= block.Size;
            else if (geometryBlocksToExpand.TryGetValue(block.Index, out var expansion))
                totalBlockSizeDelta += expansion.SizeIncrease;

        var output = new byte[data.Length + headerSizeDelta + totalBlockSizeDelta];

        var blockRemap = new int[sourceInfo.BlockCount];
        var newBlockIndex = 0;
        for (var i = 0; i < sourceInfo.BlockCount; i++)
            blockRemap[i] = packedBlockIndices.Contains(i) ? -1 : newBlockIndex++;

        return (output, blockRemap);
    }

    private static int[] CreateIdentityRemap(int count)
    {
        var remap = new int[count];
        for (var i = 0; i < count; i++) remap[i] = i;
        return remap;
    }
}
