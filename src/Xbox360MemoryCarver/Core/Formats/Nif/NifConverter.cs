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
        var output = BuildConvertedOutput(data, sourceInfo, packedBlocks, geometryBlocksToExpand, geometryDataByBlock);

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

    private static byte[] BuildConvertedOutput(
        byte[] data,
        NifInfo sourceInfo,
        List<BlockInfo> packedBlocks,
        Dictionary<int, GeometryBlockExpansion> geometryBlocksToExpand,
        Dictionary<int, PackedGeometryData> geometryDataByBlock)
    {
        var packedBlockIndices = new HashSet<int>(packedBlocks.Select(b => b.Index));
        var (output, blockRemap) = AllocateOutput(data, sourceInfo, packedBlocks, geometryBlocksToExpand);

        var outPos = NifWriter.WriteHeader(data, output, sourceInfo, packedBlockIndices, geometryBlocksToExpand);

        foreach (var block in sourceInfo.Blocks)
        {
            if (packedBlockIndices.Contains(block.Index)) continue;

            if (geometryBlocksToExpand.TryGetValue(block.Index, out var expansion))
            {
                var packedData = geometryDataByBlock[expansion.PackedBlockIndex];
                outPos = NifGeometryWriter.WriteExpandedBlock(data, output, outPos, block, packedData);
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
