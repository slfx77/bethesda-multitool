using System.Buffers.Binary;
using System.Text;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Converts Xbox 360 NIF files to PC-compatible format.
///     Handles endian conversion and BSPackedAdditionalGeometryData decompression.
/// </summary>
/// <remarks>
///     Xbox 360 NIFs use BSPackedAdditionalGeometryData to store vertex data as half-floats
///     in a GPU-optimized interleaved format. PC NIFs store this data as full floats
///     directly in NiTriStripsData/NiTriShapeData blocks.
/// 
///     This converter:
///     1. Converts endianness (BE to LE)
///     2. Extracts half-float geometry from BSPackedAdditionalGeometryData
///     3. Converts to full floats and injects into geometry data blocks
///     4. Removes BSPackedAdditionalGeometryData blocks
/// </remarks>
public sealed class NifConverter
{
    private readonly bool _verbose;

    public NifConverter(bool verbose = false)
    {
        _verbose = verbose;
    }

    /// <summary>
    ///     Result of a NIF conversion operation.
    /// </summary>
    public sealed class ConversionResult
    {
        public required bool Success { get; init; }
        public byte[]? OutputData { get; init; }
        public string? ErrorMessage { get; init; }
        public NifInfo? SourceInfo { get; init; }
        public NifInfo? OutputInfo { get; init; }
    }

    /// <summary>
    ///     Information about a NIF file.
    /// </summary>
    public sealed class NifInfo
    {
        public string HeaderString { get; set; } = "";
        public uint BinaryVersion { get; set; }
        public bool IsBigEndian { get; set; }
        public uint UserVersion { get; set; }
        public uint BsVersion { get; set; }
        public int BlockCount { get; set; }
        public List<BlockInfo> Blocks { get; } = [];
        public List<string> BlockTypeNames { get; } = [];
    }

    /// <summary>
    ///     Information about a single block.
    /// </summary>
    public sealed class BlockInfo
    {
        public int Index { get; set; }
        public ushort TypeIndex { get; set; }
        public string TypeName { get; set; } = "";
        public int Size { get; set; }
        public int DataOffset { get; set; }
    }

    /// <summary>
    ///     Converts an Xbox 360 NIF file to PC format.
    /// </summary>
    public ConversionResult Convert(byte[] data)
    {
        try
        {
            // Parse the source file
            var sourceInfo = ParseNif(data);
            if (sourceInfo == null)
            {
                return new ConversionResult
                {
                    Success = false,
                    ErrorMessage = "Failed to parse NIF header"
                };
            }

            // If already little-endian, just return the data as-is
            if (!sourceInfo.IsBigEndian)
            {
                return new ConversionResult
                {
                    Success = true,
                    OutputData = data,
                    SourceInfo = sourceInfo,
                    OutputInfo = sourceInfo,
                    ErrorMessage = "File is already little-endian (PC format)"
                };
            }

            // Check if there are any BSPackedAdditionalGeometryData blocks
            var packedBlocks = sourceInfo.Blocks
                .Where(b => b.TypeName == "BSPackedAdditionalGeometryData")
                .ToList();

            if (packedBlocks.Count == 0)
            {
                // No packed data - just do simple endian conversion
                var simpleResult = ConvertEndianOnly(data, sourceInfo);
                return simpleResult;
            }

            // Full conversion with geometry unpacking
            var fullResult = ConvertWithGeometryUnpacking(data, sourceInfo, packedBlocks);
            return fullResult;
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

    /// <summary>
    ///     Parse NIF file and extract header/block information.
    /// </summary>
    private NifInfo? ParseNif(byte[] data)
    {
        if (data.Length < 50)
        {
            return null;
        }

        var info = new NifInfo();

        // Find header string (ends with newline)
        var newlinePos = Array.IndexOf(data, (byte)0x0A, 0, Math.Min(60, data.Length));
        if (newlinePos < 0)
        {
            return null;
        }

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
        var hasBsHeader = IsBethesdaVersion(info.BinaryVersion, info.UserVersion);
        if (hasBsHeader)
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

        // NumBlockTypes (follows endian byte)
        var numBlockTypes = info.IsBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
        pos += 2;

        // Block type names (SizedStrings)
        for (var i = 0; i < numBlockTypes; i++)
        {
            var strLen = info.IsBigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
                : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            pos += 4;
            
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

        // Num strings
        var numStrings = info.IsBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        pos += 4;

        // Max string length
        var maxStrLen = info.IsBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        pos += 4;

        // Strings
        for (var i = 0; i < numStrings; i++)
        {
            var strLen = info.IsBigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
                : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            pos += 4 + (int)strLen;
        }

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

    /// <summary>
    ///     Simple endian conversion without geometry unpacking.
    /// </summary>
    private ConversionResult ConvertEndianOnly(byte[] data, NifInfo sourceInfo)
    {
        // For now, delegate to NifEndianConverter for simple cases
        var legacyConverter = new NifEndianConverter(_verbose);
        var result = legacyConverter.ConvertToLittleEndian(data);

        if (result == null)
        {
            return new ConversionResult
            {
                Success = false,
                ErrorMessage = "Endian conversion failed",
                SourceInfo = sourceInfo
            };
        }

        var outputInfo = ParseNif(result);
        return new ConversionResult
        {
            Success = true,
            OutputData = result,
            SourceInfo = sourceInfo,
            OutputInfo = outputInfo
        };
    }

    /// <summary>
    ///     Full conversion with BSPackedAdditionalGeometryData decompression.
    /// </summary>
    private ConversionResult ConvertWithGeometryUnpacking(byte[] data, NifInfo sourceInfo, List<BlockInfo> packedBlocks)
    {
        // Step 1: Extract geometry data from all BSPackedAdditionalGeometryData blocks
        var geometryDataByBlock = new Dictionary<int, PackedGeometryData>();
        
        foreach (var packedBlock in packedBlocks)
        {
            var packedData = ExtractPackedGeometryData(data, packedBlock);
            if (packedData != null)
            {
                geometryDataByBlock[packedBlock.Index] = packedData;
                if (_verbose)
                {
                    Console.WriteLine($"Extracted geometry from block {packedBlock.Index}: {packedData.NumVertices} vertices");
                }
            }
        }

        // Step 2: Find geometry blocks that reference the packed data and calculate size changes
        var geometryBlocksToExpand = new Dictionary<int, GeometryBlockExpansion>();
        
        foreach (var block in sourceInfo.Blocks)
        {
            if (block.TypeName is "NiTriStripsData" or "NiTriShapeData")
            {
                var expansion = AnalyzeGeometryBlock(data, block, sourceInfo, geometryDataByBlock);
                if (expansion != null)
                {
                    geometryBlocksToExpand[block.Index] = expansion;
                    if (_verbose)
                    {
                        Console.WriteLine($"Block {block.Index} ({block.TypeName}) will expand by {expansion.SizeIncrease} bytes");
                    }
                }
            }
        }

        // Step 3: Build the output file
        var output = BuildConvertedOutput(data, sourceInfo, packedBlocks, geometryBlocksToExpand, geometryDataByBlock);

        var outputInfo = ParseNif(output);
        return new ConversionResult
        {
            Success = true,
            OutputData = output,
            SourceInfo = sourceInfo,
            OutputInfo = outputInfo
        };
    }

    /// <summary>
    ///     Geometry data extracted from BSPackedAdditionalGeometryData.
    /// </summary>
    private sealed class PackedGeometryData
    {
        public ushort NumVertices { get; set; }
        public float[]? Positions { get; set; }     // numVerts * 3
        public float[]? Normals { get; set; }       // numVerts * 3
        public float[]? Tangents { get; set; }      // numVerts * 3
        public float[]? Bitangents { get; set; }    // numVerts * 3
        public float[]? UVs { get; set; }           // numVerts * 2
        public ushort BsDataFlags { get; set; }     // Flags indicating what data is present
    }

    /// <summary>
    ///     Information about how a geometry block needs to be expanded.
    /// </summary>
    private sealed class GeometryBlockExpansion
    {
        public int BlockIndex { get; set; }
        public int PackedBlockIndex { get; set; }
        public int SizeIncrease { get; set; }
        public int OriginalSize { get; set; }
        public int NewSize { get; set; }
    }

    /// <summary>
    ///     Extract geometry data from a BSPackedAdditionalGeometryData block.
    /// </summary>
    private PackedGeometryData? ExtractPackedGeometryData(byte[] data, BlockInfo block)
    {
        try
        {
            var pos = block.DataOffset;
            var end = block.DataOffset + block.Size;

            // numVertices (ushort BE)
            var numVertices = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
            pos += 2;

            // numBlockInfos (uint BE)
            var numBlockInfos = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            pos += 4;

            // Parse NiAGDDataStream entries to understand data layout
            var streams = new List<DataStreamInfo>();
            for (var i = 0; i < numBlockInfos; i++)
            {
                var stream = new DataStreamInfo
                {
                    Type = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos)),
                    UnitSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 4)),
                    TotalSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 8)),
                    Stride = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 12)),
                    BlockIndex = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 16)),
                    BlockOffset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 20)),
                    Flags = data[pos + 24]
                };
                streams.Add(stream);
                pos += 25;
            }

            // numBlocks (uint BE)
            var numDataBlocks = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            pos += 4;

            // Find the raw data location
            var rawDataOffset = -1;
            var rawDataSize = 0;

            for (var b = 0; b < numDataBlocks && pos < end; b++)
            {
                var hasData = data[pos];
                pos += 1;

                if (hasData == 0)
                {
                    continue;
                }

                // BSPacked format (arg=1)
                var blockSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
                var numInnerBlocks = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 4));
                pos += 8;

                // Skip block offsets
                pos += (int)numInnerBlocks * 4;

                // numData
                var numData = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
                pos += 4;

                // Skip data sizes
                pos += (int)numData * 4;

                // Raw data starts here
                rawDataOffset = pos;
                rawDataSize = (int)(numData * blockSize);
                pos += rawDataSize;

                // Skip shaderIndex and totalSize (BSPacked specific)
                pos += 8;
            }

            if (rawDataOffset < 0 || streams.Count == 0)
            {
                return null;
            }

            // Now extract the actual geometry data
            var result = new PackedGeometryData { NumVertices = numVertices };
            var stride = (int)streams[0].Stride;

            if (_verbose)
            {
                Console.WriteLine($"  Block has {streams.Count} data streams, stride={stride}:");
                for (var i = 0; i < streams.Count; i++)
                {
                    Console.WriteLine($"    Stream[{i}]: type={streams[i].Type}, unitSize={streams[i].UnitSize}, offset={streams[i].BlockOffset}");
                }
            }

            // Identify streams by their type and position
            // Type 16 (E_FLOAT) = half4 (tangents, bitangents, normals, positions)
            // Type 14 (E_TEXTCOORD) = half2 (UVs)
            // Type 28 (E_UBYTE4) = vertex colors or other byte data
            
            // Find all half4 streams (type 16) and half2 streams (type 14)
            var half4Streams = streams.Where(s => s.Type == 16 && s.UnitSize == 8).OrderBy(s => s.BlockOffset).ToList();
            var half2Streams = streams.Where(s => s.Type == 14 && s.UnitSize == 4).OrderBy(s => s.BlockOffset).ToList();
            
            // CORRECTED LAYOUT (verified against PC reference):
            // Offset 0:  Position (half4) - The FIRST half4 stream, NOT the last!
            // Offset 8:  Tangent (half4)
            // Offset 16: UV (half2)
            // Offset 20: Bitangent (half4)
            // Offset 28: Normal (half4) - The LAST half4 stream
            
            // Extract UVs (first half2 stream)
            if (half2Streams.Count > 0)
            {
                var uvIdx = streams.IndexOf(half2Streams[0]);
                result.UVs = ExtractHalf2Stream(data, rawDataOffset, numVertices, stride, streams, uvIdx);
            }
            
            // For half4 streams, assign based on actual offset values
            // Position is at offset 0 (first half4 stream)
            // Tangent is at offset 8 (second half4 stream)
            // Bitangent is at offset 20 (third half4 stream, after UVs)
            // Normal is at offset 28 (fourth half4 stream)
            if (half4Streams.Count >= 1)
            {
                // Offset 0 is Position
                result.Positions = ExtractHalf4Stream(data, rawDataOffset, numVertices, stride, streams, streams.IndexOf(half4Streams[0]));
            }
            if (half4Streams.Count >= 2)
            {
                // Offset 8 is Tangent
                result.Tangents = ExtractHalf4Stream(data, rawDataOffset, numVertices, stride, streams, streams.IndexOf(half4Streams[1]));
            }
            if (half4Streams.Count >= 3)
            {
                // Offset 20 is Bitangent
                result.Bitangents = ExtractHalf4Stream(data, rawDataOffset, numVertices, stride, streams, streams.IndexOf(half4Streams[2]));
            }
            if (half4Streams.Count >= 4)
            {
                // Offset 28 is Normal
                result.Normals = ExtractHalf4Stream(data, rawDataOffset, numVertices, stride, streams, streams.IndexOf(half4Streams[3]));
            }
            
            if (_verbose)
            {
                Console.WriteLine($"  Extracted: verts={result.Positions != null}, normals={result.Normals != null}, " +
                    $"tangents={result.Tangents != null}, bitangents={result.Bitangents != null}, uvs={result.UVs != null}");
            }

            // Build bsDataFlags based on what we have
            result.BsDataFlags = 0;
            if (result.UVs != null) result.BsDataFlags |= 1; // Has UVs
            if (result.Tangents != null) result.BsDataFlags |= 4096; // Has tangents

            return result;
        }
        catch (Exception ex)
        {
            if (_verbose)
            {
                Console.WriteLine($"Error extracting packed geometry: {ex.Message}");
            }
            return null;
        }
    }

    private sealed class DataStreamInfo
    {
        public uint Type { get; set; }
        public uint UnitSize { get; set; }
        public uint TotalSize { get; set; }
        public uint Stride { get; set; }
        public uint BlockIndex { get; set; }
        public uint BlockOffset { get; set; }
        public byte Flags { get; set; }
    }

    /// <summary>
    ///     Extract a half4 stream (4 half-floats = 8 bytes per vertex) as Vector3 floats.
    /// </summary>
    private float[]? ExtractHalf4Stream(byte[] data, int rawDataOffset, int numVertices, int stride, List<DataStreamInfo> streams, int streamIndex)
    {
        if (streamIndex >= streams.Count)
        {
            return null;
        }

        var stream = streams[streamIndex];
        if (stream.UnitSize != 8) // half4 = 8 bytes
        {
            return null;
        }

        var result = new float[numVertices * 3];
        var offset = (int)stream.BlockOffset;

        for (var v = 0; v < numVertices; v++)
        {
            var vertexOffset = rawDataOffset + v * stride + offset;
            
            // Read 3 half-floats (ignore the 4th)
            result[v * 3 + 0] = HalfToFloat(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(vertexOffset)));
            result[v * 3 + 1] = HalfToFloat(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(vertexOffset + 2)));
            result[v * 3 + 2] = HalfToFloat(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(vertexOffset + 4)));
        }

        return result;
    }

    /// <summary>
    ///     Extract a half2 stream (2 half-floats = 4 bytes per vertex) as Vector2 floats.
    /// </summary>
    private float[]? ExtractHalf2Stream(byte[] data, int rawDataOffset, int numVertices, int stride, List<DataStreamInfo> streams, int streamIndex)
    {
        if (streamIndex >= streams.Count)
        {
            return null;
        }

        var stream = streams[streamIndex];
        if (stream.UnitSize != 4) // half2 = 4 bytes
        {
            return null;
        }

        var result = new float[numVertices * 2];
        var offset = (int)stream.BlockOffset;

        for (var v = 0; v < numVertices; v++)
        {
            var vertexOffset = rawDataOffset + v * stride + offset;
            
            result[v * 2 + 0] = HalfToFloat(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(vertexOffset)));
            result[v * 2 + 1] = HalfToFloat(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(vertexOffset + 2)));
        }

        return result;
    }

    /// <summary>
    ///     Analyze a geometry block to determine if/how it needs to be expanded.
    /// </summary>
    private GeometryBlockExpansion? AnalyzeGeometryBlock(byte[] data, BlockInfo block, NifInfo sourceInfo, Dictionary<int, PackedGeometryData> geometryDataByBlock)
    {
        var pos = block.DataOffset;

        // Parse NiGeometryData fields for Bethesda 20.2.0.7
        // groupId (int BE)
        pos += 4;

        // numVertices (ushort BE)
        var numVertices = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
        pos += 2;

        // keepFlags, compressFlags (bytes)
        pos += 2;

        // hasVertices (byte)
        var hasVertices = data[pos];
        pos += 1;

        // If hasVertices, skip vertex array
        if (hasVertices != 0)
        {
            pos += numVertices * 12;
        }

        // bsDataFlags (ushort BE)
        var bsDataFlags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
        pos += 2;

        // hasNormals (byte)
        var hasNormals = data[pos];
        pos += 1;

        // Skip normals if present
        if (hasNormals != 0)
        {
            pos += numVertices * 12;
            
            // Tangents/bitangents if flag set
            if ((bsDataFlags & 4096) != 0)
            {
                pos += numVertices * 24;
            }
        }

        // center (Vector3), radius (float)
        pos += 16;

        // hasVertexColors (byte)
        var hasVertexColors = data[pos];
        pos += 1;

        // Skip vertex colors if present
        if (hasVertexColors != 0)
        {
            pos += numVertices * 16;
        }

        // UV sets
        var numUVSets = bsDataFlags & 1;
        if (numUVSets != 0)
        {
            pos += numVertices * 8;
        }

        // consistency (ushort BE)
        pos += 2;

        // additionalData ref (int BE)
        var additionalDataRef = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));

        if (additionalDataRef < 0 || !geometryDataByBlock.ContainsKey(additionalDataRef))
        {
            return null; // No packed data reference
        }

        var packedData = geometryDataByBlock[additionalDataRef];

        // Calculate size increase
        var sizeIncrease = 0;

        // If hasVertices=0, we need to add vertex positions
        if (hasVertices == 0 && packedData.Positions != null)
        {
            sizeIncrease += numVertices * 12; // Vector3 positions
        }

        // If hasNormals=0, we need to add normals (and possibly tangents/bitangents)
        if (hasNormals == 0 && packedData.Normals != null)
        {
            sizeIncrease += numVertices * 12; // Vector3 normals
            
            if (packedData.Tangents != null)
            {
                sizeIncrease += numVertices * 12; // Vector3 tangents
            }
            if (packedData.Bitangents != null)
            {
                sizeIncrease += numVertices * 12; // Vector3 bitangents
            }
        }

        // If no UVs, we need to add them
        if (numUVSets == 0 && packedData.UVs != null)
        {
            sizeIncrease += numVertices * 8; // TexCoord UVs
        }

        if (sizeIncrease == 0)
        {
            return null;
        }

        return new GeometryBlockExpansion
        {
            BlockIndex = block.Index,
            PackedBlockIndex = additionalDataRef,
            OriginalSize = block.Size,
            NewSize = block.Size + sizeIncrease,
            SizeIncrease = sizeIncrease
        };
    }

    /// <summary>
    ///     Build the final converted output.
    /// </summary>
    private byte[] BuildConvertedOutput(byte[] data, NifInfo sourceInfo, List<BlockInfo> packedBlocks, 
        Dictionary<int, GeometryBlockExpansion> geometryBlocksToExpand, Dictionary<int, PackedGeometryData> geometryDataByBlock)
    {
        // Calculate new file size
        var packedBlockIndices = new HashSet<int>(packedBlocks.Select(b => b.Index));
        
        // Calculate header size changes
        var removedBlockCount = packedBlocks.Count;
        var headerSizeDelta = -(removedBlockCount * 6); // 2 bytes type index + 4 bytes size per block
        
        // Calculate total block size change
        long totalBlockSizeDelta = 0;
        foreach (var block in sourceInfo.Blocks)
        {
            if (packedBlockIndices.Contains(block.Index))
            {
                totalBlockSizeDelta -= block.Size;
            }
            else if (geometryBlocksToExpand.TryGetValue(block.Index, out var expansion))
            {
                totalBlockSizeDelta += expansion.SizeIncrease;
            }
        }

        var newSize = data.Length + headerSizeDelta + totalBlockSizeDelta;
        var output = new byte[newSize];

        // Build block remapping
        var blockRemap = new int[sourceInfo.BlockCount];
        var newBlockIndex = 0;
        for (var i = 0; i < sourceInfo.BlockCount; i++)
        {
            if (packedBlockIndices.Contains(i))
            {
                blockRemap[i] = -1;
            }
            else
            {
                blockRemap[i] = newBlockIndex++;
            }
        }

        var newBlockCount = newBlockIndex;

        // Copy and convert header
        var outPos = WriteConvertedHeader(data, output, sourceInfo, blockRemap, packedBlockIndices, geometryBlocksToExpand);

        // Convert each block
        foreach (var block in sourceInfo.Blocks)
        {
            if (packedBlockIndices.Contains(block.Index))
            {
                continue; // Skip packed blocks
            }

            if (geometryBlocksToExpand.TryGetValue(block.Index, out var expansion))
            {
                // Expand geometry block with unpacked data
                var packedData = geometryDataByBlock[expansion.PackedBlockIndex];
                outPos = WriteExpandedGeometryBlock(data, output, outPos, block, sourceInfo, packedData, blockRemap);
            }
            else
            {
                // Regular block - just convert endianness
                outPos = WriteConvertedBlock(data, output, outPos, block, sourceInfo, blockRemap);
            }
        }

        // Write footer
        outPos = WriteConvertedFooter(data, output, outPos, sourceInfo, blockRemap);

        // Trim output if we allocated too much
        if (outPos < output.Length)
        {
            Array.Resize(ref output, outPos);
        }

        return output;
    }

    /// <summary>
    ///     Write the converted header to output.
    /// </summary>
    private int WriteConvertedHeader(byte[] data, byte[] output, NifInfo sourceInfo, int[] blockRemap,
        HashSet<int> packedBlockIndices, Dictionary<int, GeometryBlockExpansion> geometryBlocksToExpand)
    {
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
            // BS Version (already LE)
            Array.Copy(data, pos, output, outPos, 4);
            pos += 4;
            outPos += 4;

            // Copy short strings
            for (var i = 0; i < 3; i++)
            {
                var len = data[pos];
                Array.Copy(data, pos, output, outPos, 1 + len);
                pos += 1 + len;
                outPos += 1 + len;
            }
        }

        // NumBlockTypes (convert BE to LE)
        var numBlockTypes = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
        
        // Check if we need to remove BSPackedAdditionalGeometryData from type list
        var typeIndexToRemove = -1;
        for (var i = 0; i < sourceInfo.BlockTypeNames.Count; i++)
        {
            if (sourceInfo.BlockTypeNames[i] == "BSPackedAdditionalGeometryData")
            {
                typeIndexToRemove = i;
                break;
            }
        }

        // For simplicity, keep all type names but they won't be used
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numBlockTypes);
        pos += 2;
        outPos += 2;

        // Block type names (convert string lengths BE to LE)
        for (var i = 0; i < numBlockTypes; i++)
        {
            var strLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), strLen);
            pos += 4;
            outPos += 4;

            Array.Copy(data, pos, output, outPos, (int)strLen);
            pos += (int)strLen;
            outPos += (int)strLen;
        }

        // Block type indices (skip removed blocks, convert BE to LE)
        foreach (var block in sourceInfo.Blocks)
        {
            if (!packedBlockIndices.Contains(block.Index))
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), block.TypeIndex);
                outPos += 2;
            }
            pos += 2;
        }

        // Block sizes (skip removed blocks, update expanded blocks, convert BE to LE)
        foreach (var block in sourceInfo.Blocks)
        {
            if (!packedBlockIndices.Contains(block.Index))
            {
                var size = (uint)block.Size;
                if (geometryBlocksToExpand.TryGetValue(block.Index, out var expansion))
                {
                    size = (uint)expansion.NewSize;
                }
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), size);
                outPos += 4;
            }
            pos += 4;
        }

        // Num strings (convert BE to LE)
        var numStrings = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numStrings);
        pos += 4;
        outPos += 4;

        // Max string length (convert BE to LE)
        var maxStrLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), maxStrLen);
        pos += 4;
        outPos += 4;

        // Strings (convert lengths BE to LE)
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

        // Num groups (convert BE to LE)
        var numGroups = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numGroups);
        pos += 4;
        outPos += 4;

        // Group sizes (convert BE to LE)
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
    ///     Write a regular block with proper per-type endian conversion.
    /// </summary>
    private int WriteConvertedBlock(byte[] data, byte[] output, int outPos, BlockInfo block, NifInfo sourceInfo, int[] blockRemap)
    {
        // Copy block data first
        Array.Copy(data, block.DataOffset, output, outPos, block.Size);
        
        // Convert in-place using proper per-block-type handling
        ConvertBlockInPlace(output, outPos, block.Size, block.TypeName, blockRemap);
        
        return outPos + block.Size;
    }

    /// <summary>
    ///     Convert a block's fields from BE to LE in-place with proper type handling.
    /// </summary>
    private static void ConvertBlockInPlace(byte[] buf, int pos, int size, string blockType, int[] blockRemap)
    {
        switch (blockType)
        {
            case "BSShaderTextureSet":
                ConvertBSShaderTextureSetInPlace(buf, pos, size);
                break;

            case "NiStringExtraData":
                ConvertNiStringExtraDataInPlace(buf, pos, size);
                break;

            case "BSBehaviorGraphExtraData":
                ConvertBSBehaviorGraphExtraDataInPlace(buf, pos, size);
                break;

            case "NiTextKeyExtraData":
                ConvertNiTextKeyExtraDataInPlace(buf, pos, size);
                break;

            case "NiSourceTexture":
                ConvertNiSourceTextureInPlace(buf, pos, size);
                break;

            case "NiNode":
            case "BSFadeNode":
            case "BSLeafAnimNode":
            case "BSTreeNode":
            case "BSOrderedNode":
            case "BSMultiBoundNode":
            case "BSBlastNode":
            case "BSDamageStage":
            case "BSMasterParticleSystem":
            case "NiBillboardNode":
            case "NiSwitchNode":
            case "NiLODNode":
                ConvertNiNodeInPlace(buf, pos, size, blockRemap);
                break;

            case "NiTriStrips":
            case "NiTriShape":
            case "BSSegmentedTriShape":
            case "NiParticles":
            case "NiParticleSystem":
            case "NiMeshParticleSystem":
            case "BSStripParticleSystem":
                ConvertNiAVObjectWithPropsInPlace(buf, pos, size, blockRemap);
                break;

            case "NiTriStripsData":
                ConvertNiTriStripsDataInPlace(buf, pos, size, blockRemap);
                break;

            case "NiTriShapeData":
                ConvertNiTriShapeDataInPlace(buf, pos, size, blockRemap);
                break;

            case "BSShaderNoLightingProperty":
            case "SkyShaderProperty":
            case "TileShaderProperty":
                ConvertBSShaderNoLightingPropertyInPlace(buf, pos, size, blockRemap);
                break;

            case "BSShaderPPLightingProperty":
                ConvertBSShaderPPLightingPropertyInPlace(buf, pos, size, blockRemap);
                break;

            case "BSLightingShaderProperty":
            case "BSEffectShaderProperty":
            case "NiMaterialProperty":
            case "NiStencilProperty":
            case "NiAlphaProperty":
            case "NiZBufferProperty":
            case "NiVertexColorProperty":
            case "NiSpecularProperty":
            case "NiDitherProperty":
            case "NiWireframeProperty":
            case "NiShadeProperty":
            case "NiFogProperty":
                ConvertPropertyBlockInPlace(buf, pos, size, blockRemap);
                break;

            case "NiSkinInstance":
            case "BSDismemberSkinInstance":
                ConvertNiSkinInstanceInPlace(buf, pos, size, blockRemap);
                break;

            case "NiSkinData":
                ConvertNiSkinDataInPlace(buf, pos, size, blockRemap);
                break;

            case "NiSkinPartition":
                ConvertNiSkinPartitionInPlace(buf, pos, size);
                break;

            case "NiControllerSequence":
                ConvertNiControllerSequenceInPlace(buf, pos, size, blockRemap);
                break;

            case "NiTransformInterpolator":
            case "NiBlendTransformInterpolator":
                ConvertNiTransformInterpolatorInPlace(buf, pos, size);
                break;

            case "NiTransformData":
                ConvertNiTransformDataInPlace(buf, pos, size);
                break;

            case "NiFloatInterpolator":
            case "NiBlendFloatInterpolator":
                ConvertNiFloatInterpolatorInPlace(buf, pos, size);
                break;

            case "NiFloatData":
                ConvertNiFloatDataInPlace(buf, pos, size);
                break;

            case "NiBoolInterpolator":
                ConvertNiBoolInterpolatorInPlace(buf, pos, size);
                break;

            case "NiBoolData":
                ConvertNiBoolDataInPlace(buf, pos, size);
                break;

            case "NiPoint3Interpolator":
            case "NiBlendPoint3Interpolator":
                ConvertNiPoint3InterpolatorInPlace(buf, pos, size);
                break;

            default:
                // Generic 4-byte swap for unknown blocks
                ConvertBulkSwap4InPlace(buf, pos, size);
                break;
        }
    }

    #region Block-Specific Conversion Methods

    private static void SwapUInt32InPlace(byte[] buf, int pos)
    {
        (buf[pos], buf[pos + 1], buf[pos + 2], buf[pos + 3]) = (buf[pos + 3], buf[pos + 2], buf[pos + 1], buf[pos]);
    }

    private static void SwapUInt16InPlace(byte[] buf, int pos)
    {
        (buf[pos], buf[pos + 1]) = (buf[pos + 1], buf[pos]);
    }

    private static void ConvertBulkSwap4InPlace(byte[] buf, int pos, int size)
    {
        var end = pos + size;
        while (pos + 4 <= end)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }
    }

    private static void ConvertBSShaderTextureSetInPlace(byte[] buf, int pos, int size)
    {
        var end = pos + size;
        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos); // numTextures
        var numTextures = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        for (var i = 0; i < numTextures && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos); // strLen
            var strLen = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
            pos += 4;

            if (strLen > 0 && pos + strLen <= end)
            {
                pos += (int)strLen; // string bytes don't need swapping
            }
        }
    }

    private static void ConvertNiStringExtraDataInPlace(byte[] buf, int pos, int size)
    {
        var end = pos + size;
        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos); // nameIdx
        pos += 4;

        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos); // strLen
        var strLen = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // String content doesn't need swapping
    }

    private static void ConvertBSBehaviorGraphExtraDataInPlace(byte[] buf, int pos, int size)
    {
        var end = pos + size;
        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos); // nameIdx
        pos += 4;

        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos); // strLen
        var strLen = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        pos += (int)strLen; // string content

        if (pos + 1 <= end)
        {
            // byte controlsBaseSkeleton - no swap needed
        }
    }

    private static void ConvertNiTextKeyExtraDataInPlace(byte[] buf, int pos, int size)
    {
        var end = pos + size;
        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos); // nameIdx
        pos += 4;

        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos); // numTextKeys
        var numKeys = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        for (var i = 0; i < numKeys && pos + 8 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos); // time (float)
            pos += 4;

            SwapUInt32InPlace(buf, pos); // strLen
            var strLen = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
            pos += 4;

            pos += (int)strLen;
        }
    }

    private static void ConvertNiSourceTextureInPlace(byte[] buf, int pos, int size)
    {
        var end = pos + size;
        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos); // nameIdx
        pos += 4;

        if (pos + 1 > end) return;
        var useExternal = buf[pos++];

        if (useExternal != 0)
        {
            if (pos + 4 > end) return;
            SwapUInt32InPlace(buf, pos); // fileNameIdx (string index)
            pos += 4;
        }
        else
        {
            if (pos + 4 > end) return;
            SwapUInt32InPlace(buf, pos); // internalTextureRef
            pos += 4;
        }

        // formatPrefs
        if (pos + 8 > end) return;
        SwapUInt32InPlace(buf, pos); // pixelLayout
        pos += 4;
        SwapUInt32InPlace(buf, pos); // useMipmaps
        pos += 4;

        // alphaFormat
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // isStatic
        if (pos + 1 > end) return;
        pos++;

        // directRender
        if (pos + 1 > end) return;
        pos++;

        // persistRenderData
        if (pos + 1 > end) return;
    }

    private static void ConvertNiNodeInPlace(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;

        // NiAVObject portion
        pos = ConvertNiAVObjectInPlace(buf, pos, end, blockRemap);
        if (pos < 0 || pos + 4 > end) return;

        // numChildren
        SwapUInt32InPlace(buf, pos);
        var numChildren = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // children refs
        for (var i = 0; i < numChildren && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        // numEffects
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var numEffects = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // effects refs
        for (var i = 0; i < numEffects && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }
    }

    private static int ConvertNiAVObjectInPlace(byte[] buf, int pos, int end, int[] blockRemap)
    {
        // nameIdx
        if (pos + 4 > end) return -1;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // numExtraDataList
        if (pos + 4 > end) return -1;
        SwapUInt32InPlace(buf, pos);
        var numExtra = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // extraData refs
        for (var i = 0; i < numExtra && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        // controllerRef
        if (pos + 4 > end) return -1;
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        // flags
        if (pos + 4 > end) return -1;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // translation (3 floats)
        for (var i = 0; i < 3 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // rotation (9 floats - 3x3 matrix)
        for (var i = 0; i < 9 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // scale
        if (pos + 4 > end) return -1;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // numProperties
        if (pos + 4 > end) return -1;
        SwapUInt32InPlace(buf, pos);
        var numProps = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // property refs
        for (var i = 0; i < numProps && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        // collisionRef
        if (pos + 4 > end) return -1;
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        return pos;
    }

    private static void ConvertNiAVObjectWithPropsInPlace(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;

        pos = ConvertNiAVObjectInPlace(buf, pos, end, blockRemap);
        if (pos < 0) return;

        // dataRef
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        // skinInstanceRef
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        // numMaterials
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var numMats = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // materialNames (string indices)
        for (var i = 0; i < numMats && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // materialExtra (ints)
        for (var i = 0; i < numMats && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // activeMaterial
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // dirtyFlag
        if (pos + 1 > end) return;
        pos++;

        // bsProperties (2 refs for Bethesda)
        for (var i = 0; i < 2 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }
    }

    private static void ConvertPropertyBlockInPlace(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;

        // nameIdx
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // numExtraDataList
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var numExtra = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // extraData refs
        for (var i = 0; i < numExtra && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        // controllerRef
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        // Rest is property-specific - use bulk swap
        ConvertBulkSwap4InPlace(buf, pos, end - pos);
    }

    /// <summary>
    ///     Convert BSShaderNoLightingProperty (and similar) which has:
    ///     - NiObjectNET base (nameIdx, numExtra, extras[], controller)
    ///     - BSShaderProperty fields (shaderType, shaderFlags, shaderFlags2, envMapScale)
    ///     - BSShaderLightingProperty fields (textureClampMode)
    ///     - SizedString fileName
    ///     - Optional falloff params for BS version > 26
    /// </summary>
    private static void ConvertBSShaderNoLightingPropertyInPlace(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;

        // NiObjectNET portion
        // nameIdx
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // numExtraDataList
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var numExtra = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // extraData refs
        for (var i = 0; i < numExtra && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        // controllerRef
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        // BSShaderProperty portion
        // shaderType (uint)
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // shaderFlags (uint)
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // shaderFlags2 (uint)
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // envMapScale (float)
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // BSShaderLightingProperty portion
        // textureClampMode (uint)
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // BSShaderNoLightingProperty portion
        // fileName (SizedString) - length then string bytes
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var strLen = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;
        
        // String content doesn't need swapping, just skip it
        pos += (int)strLen;

        // Remaining bytes are float falloff params (BS ver > 26)
        // Just bulk swap 4 whatever's left
        ConvertBulkSwap4InPlace(buf, pos, end - pos);
    }

    /// <summary>
    ///     Convert BSShaderPPLightingProperty which has:
    ///     - NiObjectNET base
    ///     - BSShaderProperty fields
    ///     - BSShaderLightingProperty fields
    ///     - Ref textureSet
    ///     - Various float/int params
    /// </summary>
    private static void ConvertBSShaderPPLightingPropertyInPlace(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;

        // NiObjectNET portion
        // nameIdx
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // numExtraDataList
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var numExtra = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // extraData refs
        for (var i = 0; i < numExtra && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        // controllerRef
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        // BSShaderProperty portion
        // shaderType (uint)
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // shaderFlags (uint)
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // shaderFlags2 (uint)
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // envMapScale (float)
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // BSShaderLightingProperty portion
        // textureClampMode (uint)
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // BSShaderPPLightingProperty portion
        // textureSetRef
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        // Remaining fields are all 4-byte types (floats/ints)
        ConvertBulkSwap4InPlace(buf, pos, end - pos);
    }

    private static void ConvertNiSkinInstanceInPlace(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;

        // dataRef
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        // skinPartitionRef
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        // skeletonRootRef
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        // numBones
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var numBones = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // bone refs
        for (var i = 0; i < numBones && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        // Rest (partitions for dismember) - bulk swap
        ConvertBulkSwap4InPlace(buf, pos, end - pos);
    }

    private static void ConvertNiSkinDataInPlace(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;

        // skinTransform (rotation 9 floats + translation 3 floats + scale 1 float = 13 floats = 52 bytes)
        for (var i = 0; i < 13 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // numBones
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var numBones = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // hasVertexWeights (byte)
        if (pos + 1 > end) return;
        var hasWeights = buf[pos++];

        // For each bone: transform + boundingSphere + numVertices + vertexWeights
        for (var b = 0; b < numBones && pos < end; b++)
        {
            // boneTransform (13 floats)
            for (var i = 0; i < 13 && pos + 4 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos);
                pos += 4;
            }

            // boundingSphere center (3 floats) + radius (1 float)
            for (var i = 0; i < 4 && pos + 4 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos);
                pos += 4;
            }

            // numVertices
            if (pos + 2 > end) break;
            SwapUInt16InPlace(buf, pos);
            var numVerts = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
            pos += 2;

            // vertexWeights (if hasWeights)
            if (hasWeights != 0)
            {
                for (var v = 0; v < numVerts && pos + 6 <= end; v++)
                {
                    SwapUInt16InPlace(buf, pos); // vertexIndex
                    pos += 2;
                    SwapUInt32InPlace(buf, pos); // weight (float)
                    pos += 4;
                }
            }
        }
    }

    private static void ConvertNiSkinPartitionInPlace(byte[] buf, int pos, int size)
    {
        var end = pos + size;

        // numPartitions
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var numPartitions = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        for (var p = 0; p < numPartitions && pos < end; p++)
        {
            // numVertices, numTriangles, numBones, numStrips, numWeightsPerVertex
            if (pos + 10 > end) return;
            SwapUInt16InPlace(buf, pos); pos += 2;
            var numTris = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos));
            SwapUInt16InPlace(buf, pos); pos += 2;
            numTris = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos - 2));
            var numBones = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos));
            SwapUInt16InPlace(buf, pos); pos += 2;
            numBones = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos - 2));
            var numStrips = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos));
            SwapUInt16InPlace(buf, pos); pos += 2;
            numStrips = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos - 2));
            SwapUInt16InPlace(buf, pos); pos += 2; // numWeightsPerVertex

            // bones array
            for (var i = 0; i < numBones && pos + 2 <= end; i++)
            {
                SwapUInt16InPlace(buf, pos);
                pos += 2;
            }

            // hasVertexMap, hasVertexWeights, hasStrips, hasFaces
            if (pos + 4 > end) return;
            var hasVMap = buf[pos++];
            var hasVWeights = buf[pos++];
            var hasStripsFlag = buf[pos++];
            var hasFaces = buf[pos++];

            // Rest is complex - bulk swap remainder
            ConvertBulkSwap4InPlace(buf, pos, end - pos);
            return; // Complex structure - bail after first partition for safety
        }
    }

    private static void ConvertNiControllerSequenceInPlace(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;

        // nameIdx
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // numControlledBlocks
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var numBlocks = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // arrayGrowBy
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // controlledBlocks
        for (var i = 0; i < numBlocks && pos + 20 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos); // interpolatorRef
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;

            SwapUInt32InPlace(buf, pos); // controllerRef
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;

            // priority (byte)
            pos++;

            SwapUInt32InPlace(buf, pos); // nodeName
            pos += 4;

            SwapUInt32InPlace(buf, pos); // propType
            pos += 4;

            SwapUInt32InPlace(buf, pos); // ctrlType
            pos += 4;

            SwapUInt32InPlace(buf, pos); // ctrlID
            pos += 4;

            SwapUInt32InPlace(buf, pos); // interpID
            pos += 4;
        }

        // Remaining fields - bulk swap
        ConvertBulkSwap4InPlace(buf, pos, end - pos);
    }

    private static void ConvertNiTransformInterpolatorInPlace(byte[] buf, int pos, int size)
    {
        var end = pos + size;

        // translation (3 floats)
        for (var i = 0; i < 3 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // rotation (4 floats - quaternion)
        for (var i = 0; i < 4 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // scale
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // dataRef
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
    }

    private static void ConvertNiTransformDataInPlace(byte[] buf, int pos, int size)
    {
        // Complex keyframe data - bulk swap is safest
        ConvertBulkSwap4InPlace(buf, pos, size);
    }

    private static void ConvertNiFloatInterpolatorInPlace(byte[] buf, int pos, int size)
    {
        var end = pos + size;

        // value
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // dataRef
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
    }

    private static void ConvertNiFloatDataInPlace(byte[] buf, int pos, int size)
    {
        ConvertBulkSwap4InPlace(buf, pos, size);
    }

    private static void ConvertNiBoolInterpolatorInPlace(byte[] buf, int pos, int size)
    {
        var end = pos + size;

        // value (byte)
        if (pos + 1 > end) return;
        pos++;

        // dataRef
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
    }

    private static void ConvertNiBoolDataInPlace(byte[] buf, int pos, int size)
    {
        ConvertBulkSwap4InPlace(buf, pos, size);
    }

    private static void ConvertNiPoint3InterpolatorInPlace(byte[] buf, int pos, int size)
    {
        var end = pos + size;

        // value (3 floats)
        for (var i = 0; i < 3 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // dataRef
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
    }

    private static void ConvertNiTriStripsDataInPlace(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;
        
        // groupId
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;
        
        // numVertices
        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos);
        var numVerts = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
        pos += 2;
        
        // keepFlags, compressFlags
        if (pos + 2 > end) return;
        pos += 2;
        
        // hasVertices
        if (pos + 1 > end) return;
        var hasVerts = buf[pos++];
        
        // vertices (12 bytes each - 3 floats)
        if (hasVerts != 0)
        {
            for (var i = 0; i < numVerts && pos + 12 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
            }
        }
        
        // bsDataFlags
        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos);
        var dataFlags = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
        pos += 2;
        
        // materialCRC
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;
        
        // hasNormals
        if (pos + 1 > end) return;
        var hasNormals = buf[pos++];
        
        // normals (12 bytes each - 3 floats)
        if (hasNormals != 0)
        {
            for (var i = 0; i < numVerts && pos + 12 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
            }
            
            // tangents and bitangents if flag set (0x1000 = has tangents)
            if ((dataFlags & 0x1000) != 0)
            {
                // tangents
                for (var i = 0; i < numVerts && pos + 12 <= end; i++)
                {
                    SwapUInt32InPlace(buf, pos); pos += 4;
                    SwapUInt32InPlace(buf, pos); pos += 4;
                    SwapUInt32InPlace(buf, pos); pos += 4;
                }
                // bitangents
                for (var i = 0; i < numVerts && pos + 12 <= end; i++)
                {
                    SwapUInt32InPlace(buf, pos); pos += 4;
                    SwapUInt32InPlace(buf, pos); pos += 4;
                    SwapUInt32InPlace(buf, pos); pos += 4;
                }
            }
        }
        
        // center (3 floats) + radius (1 float)
        if (pos + 16 > end) return;
        SwapUInt32InPlace(buf, pos); pos += 4;
        SwapUInt32InPlace(buf, pos); pos += 4;
        SwapUInt32InPlace(buf, pos); pos += 4;
        SwapUInt32InPlace(buf, pos); pos += 4;
        
        // hasVertexColors
        if (pos + 1 > end) return;
        var hasColors = buf[pos++];
        
        // vertex colors (4 bytes each - RGBA, no swap needed for bytes)
        if (hasColors != 0)
        {
            pos += numVerts * 4; // Skip color bytes - no swap needed
        }
        
        // numUVSets (derived from dataFlags bits 0-5, but also stored)
        // UV sets count is in lower 6 bits of dataFlags
        var numUVSets = dataFlags & 0x3F;
        
        // UV sets (8 bytes each per vertex - 2 floats)
        for (var uvSet = 0; uvSet < numUVSets && pos < end; uvSet++)
        {
            for (var i = 0; i < numVerts && pos + 8 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos); pos += 4; // U
                SwapUInt32InPlace(buf, pos); pos += 4; // V
            }
        }
        
        // consistencyFlags
        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos);
        pos += 2;
        
        // additionalData ref
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;
        
        // numStrips
        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos);
        var numStrips = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
        pos += 2;
        
        // stripLengths (ushort array)
        var stripLengths = new ushort[numStrips];
        for (var i = 0; i < numStrips && pos + 2 <= end; i++)
        {
            SwapUInt16InPlace(buf, pos);
            stripLengths[i] = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
            pos += 2;
        }
        
        // hasPoints
        if (pos + 1 > end) return;
        var hasPoints = buf[pos++];
        
        // points (ushort arrays for each strip)
        if (hasPoints != 0)
        {
            for (var s = 0; s < numStrips && pos < end; s++)
            {
                for (var p = 0; p < stripLengths[s] && pos + 2 <= end; p++)
                {
                    SwapUInt16InPlace(buf, pos);
                    pos += 2;
                }
            }
        }
    }

    private static void ConvertNiTriShapeDataInPlace(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;
        
        // groupId
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;
        
        // numVertices
        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos);
        var numVerts = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
        pos += 2;
        
        // keepFlags, compressFlags
        if (pos + 2 > end) return;
        pos += 2;
        
        // hasVertices
        if (pos + 1 > end) return;
        var hasVerts = buf[pos++];
        
        // vertices (12 bytes each - 3 floats)
        if (hasVerts != 0)
        {
            for (var i = 0; i < numVerts && pos + 12 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
            }
        }
        
        // bsDataFlags
        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos);
        var dataFlags = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
        pos += 2;
        
        // materialCRC
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;
        
        // hasNormals
        if (pos + 1 > end) return;
        var hasNormals = buf[pos++];
        
        // normals (12 bytes each - 3 floats)
        if (hasNormals != 0)
        {
            for (var i = 0; i < numVerts && pos + 12 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
            }
            
            // tangents and bitangents if flag set
            if ((dataFlags & 0x1000) != 0)
            {
                for (var i = 0; i < numVerts && pos + 12 <= end; i++)
                {
                    SwapUInt32InPlace(buf, pos); pos += 4;
                    SwapUInt32InPlace(buf, pos); pos += 4;
                    SwapUInt32InPlace(buf, pos); pos += 4;
                }
                for (var i = 0; i < numVerts && pos + 12 <= end; i++)
                {
                    SwapUInt32InPlace(buf, pos); pos += 4;
                    SwapUInt32InPlace(buf, pos); pos += 4;
                    SwapUInt32InPlace(buf, pos); pos += 4;
                }
            }
        }
        
        // center + radius
        if (pos + 16 > end) return;
        SwapUInt32InPlace(buf, pos); pos += 4;
        SwapUInt32InPlace(buf, pos); pos += 4;
        SwapUInt32InPlace(buf, pos); pos += 4;
        SwapUInt32InPlace(buf, pos); pos += 4;
        
        // hasVertexColors
        if (pos + 1 > end) return;
        var hasColors = buf[pos++];
        
        // vertex colors (4 bytes each - RGBA bytes, no swap)
        if (hasColors != 0)
        {
            pos += numVerts * 4;
        }
        
        // UV sets
        var numUVSets = dataFlags & 0x3F;
        for (var uvSet = 0; uvSet < numUVSets && pos < end; uvSet++)
        {
            for (var i = 0; i < numVerts && pos + 8 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
            }
        }
        
        // consistencyFlags
        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos);
        pos += 2;
        
        // additionalData ref
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;
        
        // numTriangles
        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos);
        var numTris = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
        pos += 2;
        
        // numTrianglePoints (total indices = numTris * 3)
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;
        
        // hasTriangles
        if (pos + 1 > end) return;
        var hasTris = buf[pos++];
        
        // triangles (3 ushorts per triangle)
        if (hasTris != 0)
        {
            for (var i = 0; i < numTris && pos + 6 <= end; i++)
            {
                SwapUInt16InPlace(buf, pos); pos += 2;
                SwapUInt16InPlace(buf, pos); pos += 2;
                SwapUInt16InPlace(buf, pos); pos += 2;
            }
        }
        
        // matchGroups (optional, at end)
        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos);
        var numMatchGroups = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
        pos += 2;
        
        for (var g = 0; g < numMatchGroups && pos + 2 <= end; g++)
        {
            SwapUInt16InPlace(buf, pos);
            var numMatches = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
            pos += 2;
            
            for (var m = 0; m < numMatches && pos + 2 <= end; m++)
            {
                SwapUInt16InPlace(buf, pos);
                pos += 2;
            }
        }
    }

    private static void RemapBlockRefInPlace(byte[] buf, int pos, int[] blockRemap)
    {
        var refIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos));
        if (refIdx >= 0 && refIdx < blockRemap.Length)
        {
            var newIdx = blockRemap[refIdx];
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos), newIdx);
        }
    }

    #endregion

    /// <summary>
    ///     Write an expanded geometry block with unpacked data.
    /// </summary>
    private int WriteExpandedGeometryBlock(byte[] data, byte[] output, int outPos, BlockInfo block,
        NifInfo sourceInfo, PackedGeometryData packedData, int[] blockRemap)
    {
        var srcPos = block.DataOffset;
        var startOutPos = outPos;

        // groupId (int BE -> LE)
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outPos),
            BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(srcPos)));
        srcPos += 4;
        outPos += 4;

        // numVertices (ushort BE -> LE)
        var numVertices = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numVertices);
        srcPos += 2;
        outPos += 2;

        // keepFlags, compressFlags (bytes - no conversion)
        output[outPos++] = data[srcPos++];
        output[outPos++] = data[srcPos++];

        // hasVertices - set to 1 if we have positions
        var origHasVertices = data[srcPos++];
        var newHasVertices = (byte)(packedData.Positions != null ? 1 : origHasVertices);
        output[outPos++] = newHasVertices;

        // Write vertices if we have them
        if (newHasVertices != 0 && packedData.Positions != null && origHasVertices == 0)
        {
            // Write unpacked positions
            for (var v = 0; v < numVertices; v++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Positions[v * 3 + 0]);
                outPos += 4;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Positions[v * 3 + 1]);
                outPos += 4;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Positions[v * 3 + 2]);
                outPos += 4;
            }
        }
        else if (origHasVertices != 0)
        {
            // Copy and convert existing vertices
            for (var v = 0; v < numVertices; v++)
            {
                for (var c = 0; c < 3; c++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                        BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(srcPos)));
                    srcPos += 4;
                    outPos += 4;
                }
            }
        }

        // bsDataFlags - update to include tangent flag if we have tangents
        var origBsDataFlags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos));
        var newBsDataFlags = origBsDataFlags;
        if (packedData.Tangents != null)
        {
            newBsDataFlags |= 4096; // Tangent flag
        }
        if (packedData.UVs != null)
        {
            newBsDataFlags |= 1; // UV flag
        }
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), newBsDataFlags);
        srcPos += 2;
        outPos += 2;

        // hasNormals - set to 1 if we have normals
        var origHasNormals = data[srcPos++];
        var newHasNormals = (byte)(packedData.Normals != null ? 1 : origHasNormals);
        output[outPos++] = newHasNormals;

        // Write normals
        if (newHasNormals != 0 && packedData.Normals != null && origHasNormals == 0)
        {
            for (var v = 0; v < numVertices; v++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Normals[v * 3 + 0]);
                outPos += 4;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Normals[v * 3 + 1]);
                outPos += 4;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Normals[v * 3 + 2]);
                outPos += 4;
            }

            // Write tangents if we have them
            if ((newBsDataFlags & 4096) != 0 && packedData.Tangents != null)
            {
                for (var v = 0; v < numVertices; v++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Tangents[v * 3 + 0]);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Tangents[v * 3 + 1]);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Tangents[v * 3 + 2]);
                    outPos += 4;
                }
            }

            // Write bitangents if we have them
            if ((newBsDataFlags & 4096) != 0 && packedData.Bitangents != null)
            {
                for (var v = 0; v < numVertices; v++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Bitangents[v * 3 + 0]);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Bitangents[v * 3 + 1]);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Bitangents[v * 3 + 2]);
                    outPos += 4;
                }
            }
        }
        else if (origHasNormals != 0)
        {
            // Copy and convert existing normals
            for (var v = 0; v < numVertices; v++)
            {
                for (var c = 0; c < 3; c++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                        BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(srcPos)));
                    srcPos += 4;
                    outPos += 4;
                }
            }

            // Copy existing tangents/bitangents
            if ((origBsDataFlags & 4096) != 0)
            {
                for (var v = 0; v < numVertices * 6; v++) // 3 floats tangent + 3 floats bitangent
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                        BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(srcPos)));
                    srcPos += 4;
                    outPos += 4;
                }
            }
        }

        // center (Vector3 BE -> LE)
        for (var i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(srcPos)));
            srcPos += 4;
            outPos += 4;
        }

        // radius (float BE -> LE)
        BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
            BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(srcPos)));
        srcPos += 4;
        outPos += 4;

        // hasVertexColors (byte)
        var hasVertexColors = data[srcPos++];
        output[outPos++] = hasVertexColors;

        // Copy vertex colors if present
        if (hasVertexColors != 0)
        {
            for (var v = 0; v < numVertices * 4; v++) // Color4 = 4 floats
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                    BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(srcPos)));
                srcPos += 4;
                outPos += 4;
            }
        }

        // UV sets
        var origNumUVSets = origBsDataFlags & 1;
        var newNumUVSets = newBsDataFlags & 1;

        if (newNumUVSets != 0 && origNumUVSets == 0 && packedData.UVs != null)
        {
            // Write unpacked UVs
            for (var v = 0; v < numVertices; v++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.UVs[v * 2 + 0]);
                outPos += 4;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.UVs[v * 2 + 1]);
                outPos += 4;
            }
        }
        else if (origNumUVSets != 0)
        {
            // Copy and convert existing UVs
            for (var v = 0; v < numVertices * 2; v++) // TexCoord = 2 floats
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                    BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(srcPos)));
                srcPos += 4;
                outPos += 4;
            }
        }

        // consistency (ushort BE -> LE)
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos)));
        srcPos += 2;
        outPos += 2;

        // additionalData ref - set to -1 (no reference)
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outPos), -1);
        srcPos += 4;
        outPos += 4;

        // Copy remaining block data (NiTriStripsData/NiTriShapeData specific fields)
        var remainingBytes = block.Size - (srcPos - block.DataOffset);
        if (remainingBytes > 0)
        {
            // Copy and convert remaining data
            outPos = CopyAndConvertTriStripSpecificData(data, output, srcPos, outPos, remainingBytes, block.TypeName, blockRemap);
        }

        return outPos;
    }

    /// <summary>
    ///     Copy and convert NiTriStripsData/NiTriShapeData specific fields.
    /// </summary>
    private int CopyAndConvertTriStripSpecificData(byte[] data, byte[] output, int srcPos, int outPos, int remainingBytes, string blockType, int[] blockRemap)
    {
        if (blockType == "NiTriStripsData")
        {
            // numTriangles (ushort BE -> LE)
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos)));
            srcPos += 2;
            outPos += 2;

            // numStrips (ushort BE -> LE)
            var numStrips = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos));
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numStrips);
            srcPos += 2;
            outPos += 2;

            // stripLengths[numStrips] (ushort array)
            var stripLengths = new ushort[numStrips];
            for (var i = 0; i < numStrips; i++)
            {
                stripLengths[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos));
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), stripLengths[i]);
                srcPos += 2;
                outPos += 2;
            }

            // hasPoints (byte)
            var hasPoints = data[srcPos++];
            output[outPos++] = hasPoints;

            // points[numStrips][stripLengths[i]] (ushort arrays)
            if (hasPoints != 0)
            {
                for (var i = 0; i < numStrips; i++)
                {
                    for (var j = 0; j < stripLengths[i]; j++)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos)));
                        srcPos += 2;
                        outPos += 2;
                    }
                }
            }
        }
        else if (blockType == "NiTriShapeData")
        {
            // numTriangles (ushort BE -> LE)
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos)));
            var numTriangles = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos));
            srcPos += 2;
            outPos += 2;

            // numTrianglePoints (uint BE -> LE)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(srcPos)));
            srcPos += 4;
            outPos += 4;

            // hasTriangles (byte)
            var hasTriangles = data[srcPos++];
            output[outPos++] = hasTriangles;

            // triangles[numTriangles] (Triangle = 3 ushorts)
            if (hasTriangles != 0)
            {
                for (var i = 0; i < numTriangles * 3; i++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos)));
                    srcPos += 2;
                    outPos += 2;
                }
            }

            // matchGroups handling would go here if needed
        }

        return outPos;
    }

    /// <summary>
    ///     Write the footer with remapped block references.
    /// </summary>
    private int WriteConvertedFooter(byte[] data, byte[] output, int outPos, NifInfo sourceInfo, int[] blockRemap)
    {
        // Find footer position
        var footerPos = sourceInfo.Blocks.Count > 0
            ? sourceInfo.Blocks.Last().DataOffset + sourceInfo.Blocks.Last().Size
            : data.Length;

        if (footerPos >= data.Length)
        {
            return outPos;
        }

        // numRoots (uint BE -> LE)
        var numRoots = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(footerPos));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numRoots);
        footerPos += 4;
        outPos += 4;

        // root indices (int array BE -> LE with remapping)
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

    /// <summary>
    ///     Convert half-float (16-bit) to single-precision float (32-bit).
    /// </summary>
    private static float HalfToFloat(ushort half)
    {
        var sign = (half >> 15) & 1;
        var exp = (half >> 10) & 0x1F;
        var mant = half & 0x3FF;

        if (exp == 0)
        {
            if (mant == 0)
            {
                // Zero
                return sign == 1 ? -0.0f : 0.0f;
            }
            else
            {
                // Subnormal
                var value = (float)Math.Pow(2, -14) * (mant / 1024.0f);
                return sign == 1 ? -value : value;
            }
        }
        else if (exp == 31)
        {
            // Infinity or NaN
            return mant == 0
                ? (sign == 1 ? float.NegativeInfinity : float.PositiveInfinity)
                : float.NaN;
        }
        else
        {
            // Normalized
            var value = (float)Math.Pow(2, exp - 15) * (1 + mant / 1024.0f);
            return sign == 1 ? -value : value;
        }
    }

    private static bool IsBethesdaVersion(uint binaryVersion, uint userVersion)
    {
        return binaryVersion == 0x14020007 && (userVersion == 11 || userVersion == 12);
    }
}
