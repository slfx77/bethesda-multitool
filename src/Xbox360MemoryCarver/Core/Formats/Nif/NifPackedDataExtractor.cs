// BSPackedAdditionalGeometryData extraction for Xbox 360 NIF files
// Extracts half-float geometry data and converts to full floats

using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Context for extraction operations, grouping common parameters.
/// </summary>
/// <param name="Data">Source byte array containing packed geometry.</param>
/// <param name="RawDataOffset">Offset to raw vertex data within the block.</param>
/// <param name="NumVertices">Number of vertices to extract.</param>
/// <param name="Stride">Bytes per vertex (36, 40, or 48).</param>
/// <param name="IsBigEndian">Whether source data is big-endian.</param>
internal readonly record struct ExtractionContext(
    byte[] Data,
    int RawDataOffset,
    int NumVertices,
    int Stride,
    bool IsBigEndian)
{
    /// <summary>Whether this is a skinned mesh (stride 48).</summary>
    public bool IsSkinned => Stride == 48;
}

/// <summary>
///     Extracts geometry data from BSPackedAdditionalGeometryData blocks.
///     Xbox 360 NIFs store vertex data in packed format (half-floats) that must
///     be expanded for PC compatibility.
/// </summary>
internal static class NifPackedDataExtractor
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>
    ///     Parse a BSPackedAdditionalGeometryData block and extract geometry.
    /// </summary>
    public static PackedGeometryData? Extract(byte[] data, int blockOffset, int blockSize, bool isBigEndian)
    {
        try
        {
            if (!TryParseHeader(data, blockOffset, blockSize, isBigEndian, out var numVertices, out var streams,
                    out var pos))
            {
                return null;
            }

            if (streams.Count == 0)
            {
                return null;
            }

            var stride = (int)streams[0].Stride;
            var end = blockOffset + blockSize;

            var rawDataOffset = FindRawDataOffset(data, pos, end, isBigEndian);
            if (rawDataOffset < 0)
            {
                Log.Debug("    Failed to find raw data offset");
                return null;
            }

            var categorizedStreams = CategorizeStreams(streams);
            var result = new PackedGeometryData { NumVertices = numVertices };
            var ctx = new ExtractionContext(data, rawDataOffset, numVertices, stride, isBigEndian);

            ExtractUVs(ctx, categorizedStreams.Half2Streams, result);
            ExtractSkinnedOrVertexColorData(ctx, categorizedStreams, result);
            ExtractPositions(ctx, categorizedStreams.Half4Streams, result);
            ExtractNormalsTangentsBitangents(ctx, categorizedStreams.Half4Streams, result);
            ComputeMissingBitangents(numVertices, result);
            CalculateBsDataFlags(result);

            LogExtractionResult(result);
            return result;
        }
        catch (Exception ex)
        {
            Log.Debug($"    Error extracting packed data: {ex.Message}");
            return null;
        }
    }

    private static bool TryParseHeader(byte[] data, int blockOffset, int blockSize, bool isBigEndian,
        out ushort numVertices, out List<DataStreamInfo> streams, out int pos)
    {
        numVertices = 0;
        streams = [];
        pos = blockOffset;
        var end = blockOffset + blockSize;

        // NumVertices (ushort)
        if (pos + 2 > end)
        {
            return false;
        }

        numVertices = ReadUInt16(data, pos, isBigEndian);
        pos += 2;

        // NumBlockInfos (uint) - number of data streams
        if (pos + 4 > end)
        {
            return false;
        }

        var numBlockInfos = ReadUInt32(data, pos, isBigEndian);
        pos += 4;

        Log.Debug($"    Packed data: {numVertices} vertices, {numBlockInfos} streams");

        // Parse stream infos (25 bytes each)
        for (var i = 0; i < numBlockInfos && pos + 25 <= end; i++)
        {
            var streamInfo = ParseStreamInfo(data, pos, isBigEndian);
            streams.Add(streamInfo);
            pos += 25;

            Log.Debug(
                $"      Stream {i}: type={streamInfo.Type}, unitSize={streamInfo.UnitSize}, stride={streamInfo.Stride}, offset={streamInfo.BlockOffset}");
        }

        // Skip past NumDataBlocks field
        if (pos + 4 > end)
        {
            return false;
        }

        pos += 4;

        return true;
    }

    private static DataStreamInfo ParseStreamInfo(byte[] data, int pos, bool isBigEndian)
    {
        return new DataStreamInfo
        {
            Type = ReadUInt32(data, pos, isBigEndian),
            UnitSize = ReadUInt32(data, pos + 4, isBigEndian),
            TotalSize = ReadUInt32(data, pos + 8, isBigEndian),
            Stride = ReadUInt32(data, pos + 12, isBigEndian),
            BlockIndex = ReadUInt32(data, pos + 16, isBigEndian),
            BlockOffset = ReadUInt32(data, pos + 20, isBigEndian),
            Flags = data[pos + 24]
        };
    }

    private static int FindRawDataOffset(byte[] data, int pos, int end, bool isBigEndian)
    {
        // Rewind to read NumDataBlocks
        pos -= 4;
        if (pos + 4 > end)
        {
            return -1;
        }

        var numDataBlocks = ReadUInt32(data, pos, isBigEndian);
        pos += 4;

        var rawDataOffset = -1;
        for (var b = 0; b < numDataBlocks && pos < end; b++)
        {
            var hasData = data[pos++];
            if (hasData == 0)
            {
                continue;
            }

            if (pos + 8 > end)
            {
                break;
            }

            var blockDataSize = ReadUInt32(data, pos, isBigEndian);
            var numInnerBlocks = ReadUInt32(data, pos + 4, isBigEndian);
            pos += 8;

            // Block offsets
            pos += (int)numInnerBlocks * 4;

            if (pos + 4 > end)
            {
                break;
            }

            var numData = ReadUInt32(data, pos, isBigEndian);
            pos += 4;

            // Data sizes
            pos += (int)numData * 4;

            rawDataOffset = pos;
            pos += (int)blockDataSize;
            pos += 8; // shaderIndex + totalSize
        }

        return rawDataOffset;
    }

    private static CategorizedStreams CategorizeStreams(List<DataStreamInfo> streams)
    {
        // Type 16 + UnitSize 8 = half4 (positions, normals, tangents, bitangents)
        // Type 14 + UnitSize 4 = half2 (UVs)
        // Type 28 + UnitSize 4 = ubyte4 (bone indices at offset 16, or vertex colors)
        // Use single iteration to categorize, then sort in-place to avoid multiple allocations
        var half4List = new List<DataStreamInfo>();
        var half2List = new List<DataStreamInfo>();
        var ubyte4List = new List<DataStreamInfo>();

        foreach (var s in streams)
        {
            if (s is { Type: 16, UnitSize: 8 })
            {
                half4List.Add(s);
            }
            else if (s is { Type: 14, UnitSize: 4 })
            {
                half2List.Add(s);
            }
            else if (s is { Type: 28, UnitSize: 4 })
            {
                ubyte4List.Add(s);
            }
        }

        half4List.Sort((a, b) => a.BlockOffset.CompareTo(b.BlockOffset));
        half2List.Sort((a, b) => a.BlockOffset.CompareTo(b.BlockOffset));
        ubyte4List.Sort((a, b) => a.BlockOffset.CompareTo(b.BlockOffset));

        return new CategorizedStreams(half4List, half2List, ubyte4List);
    }

    private static void ExtractUVs(ExtractionContext ctx, List<DataStreamInfo> half2Streams, PackedGeometryData result)
    {
        if (half2Streams.Count > 0)
        {
            result.UVs = ExtractHalf2Stream(ctx.Data, ctx.RawDataOffset, ctx.NumVertices, ctx.Stride, half2Streams[0],
                ctx.IsBigEndian);
        }
    }

    private static void ExtractSkinnedOrVertexColorData(ExtractionContext ctx, CategorizedStreams streams,
        PackedGeometryData result)
    {
        var hasBoneIndicesStream = streams.Ubyte4Streams.Any(s => s.BlockOffset == 16);

        if (ctx.IsSkinned && hasBoneIndicesStream)
        {
            ExtractBoneData(ctx, streams, result);
        }
        else if (streams.Ubyte4Streams.Count > 0)
        {
            // Non-skinned mesh: ubyte4 (if present) is vertex colors
            result.VertexColors = ExtractUbyte4Stream(ctx.Data, ctx.RawDataOffset, ctx.NumVertices, ctx.Stride,
                streams.Ubyte4Streams[0]);
            Log.Debug("      Non-skinned mesh: extracted vertex colors");
        }
    }

    private static void ExtractBoneData(ExtractionContext ctx, CategorizedStreams streams, PackedGeometryData result)
    {
        // Extract bone indices from ubyte4 stream at offset 16
        var boneIndicesStream = streams.Ubyte4Streams.First(s => s.BlockOffset == 16);
        result.BoneIndices = ExtractUbyte4Stream(ctx.Data, ctx.RawDataOffset, ctx.NumVertices, ctx.Stride,
            boneIndicesStream);

        // Bone weights are at offset 8 for skinned meshes (stride 48)
        var hasBoneWeightsStream = streams.Half4Streams.Any(s => s.BlockOffset == 8);
        if (hasBoneWeightsStream)
        {
            var boneWeightsStream = streams.Half4Streams.First(s => s.BlockOffset == 8);
            result.BoneWeights = ExtractHalf4WeightsStream(ctx.Data, ctx.RawDataOffset, ctx.NumVertices, ctx.Stride,
                boneWeightsStream, ctx.IsBigEndian);
            Log.Debug("      Extracted bone weights from offset 8");
        }

        Log.Debug("      Skinned mesh (stride 48): extracted bone indices");
    }

    private static void ExtractPositions(ExtractionContext ctx, List<DataStreamInfo> half4Streams,
        PackedGeometryData result)
    {
        // Position is always the first stream (offset 0)
        if (half4Streams is [{ BlockOffset: 0 }, ..])
        {
            result.Positions = ExtractHalf4Stream(ctx.Data, ctx.RawDataOffset, ctx.NumVertices, ctx.Stride,
                half4Streams[0], ctx.IsBigEndian);
        }
    }

    private static void ExtractNormalsTangentsBitangents(ExtractionContext ctx, List<DataStreamInfo> half4Streams,
        PackedGeometryData result)
    {
        // Find ALL unit-length streams (including offset 8 for non-skinned meshes)
        var unitStreams = FindUnitLengthStreams(ctx, half4Streams);

        // Assign unit-length streams based on layout type
        AssignUnitLengthStreams(unitStreams, ctx.IsSkinned, result);
    }

    private static List<(DataStreamInfo stream, float[] data, int offset)> FindUnitLengthStreams(
        ExtractionContext ctx, List<DataStreamInfo> half4Streams)
    {
        var unitStreams = new List<(DataStreamInfo stream, float[] data, int offset)>();

        foreach (var stream in half4Streams.Where(s => s.BlockOffset >= 8))
        {
            var streamData = ExtractHalf4Stream(ctx.Data, ctx.RawDataOffset, ctx.NumVertices, ctx.Stride, stream,
                ctx.IsBigEndian);
            if (streamData == null)
            {
                continue;
            }

            var avgLen = CalculateAverageVectorLength(streamData, ctx.NumVertices);

            // Unit-length streams have avgLen ≈ 1.0 (allow some tolerance)
            if (avgLen is > 0.9 and < 1.1)
            {
                unitStreams.Add((stream, streamData, (int)stream.BlockOffset));
                Log.Debug($"      Found unit-length stream at offset {stream.BlockOffset}, avgLen={avgLen:F3}");
            }
        }

        // Sort by offset to ensure consistent assignment
        unitStreams.Sort((a, b) => a.offset.CompareTo(b.offset));
        return unitStreams;
    }

    private static double CalculateAverageVectorLength(float[] streamData, int numVertices)
    {
        var sampleCount = Math.Min(10, numVertices);
        var avgLen = 0.0;

        for (var v = 0; v < sampleCount; v++)
        {
            var x = streamData[v * 3 + 0];
            var y = streamData[v * 3 + 1];
            var z = streamData[v * 3 + 2];
            avgLen += Math.Sqrt(x * x + y * y + z * z);
        }

        return avgLen / sampleCount;
    }

    private static void AssignUnitLengthStreams(
        List<(DataStreamInfo stream, float[] data, int offset)> unitStreams, bool isSkinned, PackedGeometryData result)
    {
        // Non-skinned (36-byte): offsets 8, 20, 28 -> Normals, Tangents, Bitangents
        // Skinned (48-byte): offsets 20, 32, 40 -> Normals, Tangents, Bitangents (offset 8 = bone weights)
        if (isSkinned)
        {
            // For skinned meshes, skip offset 8 (it contains bone weights, extracted earlier)
            var skinnedUnitStreams = unitStreams.Where(s => s.offset >= 20).ToList();
            if (skinnedUnitStreams.Count >= 1)
            {
                result.Normals = skinnedUnitStreams[0].data;
            }

            if (skinnedUnitStreams.Count >= 2)
            {
                result.Tangents = skinnedUnitStreams[1].data;
            }

            if (skinnedUnitStreams.Count >= 3)
            {
                result.Bitangents = skinnedUnitStreams[2].data;
            }
        }
        else
        {
            // For non-skinned meshes, offset 8 is normals!
            if (unitStreams.Count >= 1)
            {
                result.Normals = unitStreams[0].data;
            }

            if (unitStreams.Count >= 2)
            {
                result.Tangents = unitStreams[1].data;
            }

            if (unitStreams.Count >= 3)
            {
                result.Bitangents = unitStreams[2].data;
            }
        }
    }

    private static void ComputeMissingBitangents(int numVertices, PackedGeometryData result)
    {
        // If we have normals and tangents but no bitangents, compute them
        // Bitangent = cross(Normal, Tangent) - common for meshes with only 2 unit-length streams
        if (result is { Normals: not null, Tangents: not null, Bitangents: null })
        {
            result.Bitangents = ComputeBitangents(result.Normals, result.Tangents, numVertices);
            Log.Debug("      Computed bitangents from normals and tangents");
        }
    }

    private static void CalculateBsDataFlags(PackedGeometryData result)
    {
        // Bit 0: has UVs, Bit 12 (4096): has tangents/bitangents
        ushort bsDataFlags = 0;
        if (result.UVs != null)
        {
            bsDataFlags |= 1;
        }

        if (result.Tangents != null || result.Bitangents != null)
        {
            bsDataFlags |= 4096;
        }

        result.BsDataFlags = bsDataFlags;
    }

    private static void LogExtractionResult(PackedGeometryData result)
    {
        Log.Debug(
            $"    Extracted: verts={result.Positions != null}, normals={result.Normals != null}, " +
            $"tangents={result.Tangents != null}, bitangents={result.Bitangents != null}, uvs={result.UVs != null}, " +
            $"colors={result.VertexColors != null}, boneIndices={result.BoneIndices != null}, boneWeights={result.BoneWeights != null}");
    }

    /// <summary>
    ///     Extract a half4 stream (4 half-floats = 8 bytes per vertex) as Vector3 floats.
    /// </summary>
    private static float[] ExtractHalf4Stream(byte[] data, int rawDataOffset, int numVertices, int stride,
        DataStreamInfo stream, bool isBigEndian)
    {
        var result = new float[numVertices * 3];
        var offset = (int)stream.BlockOffset;

        for (var v = 0; v < numVertices; v++)
        {
            var vertexOffset = rawDataOffset + v * stride + offset;
            if (vertexOffset + 6 > data.Length)
            {
                break;
            }

            // Read 3 half-floats (ignore the 4th W component)
            result[v * 3 + 0] = HalfToFloat(ReadUInt16(data, vertexOffset, isBigEndian));
            result[v * 3 + 1] = HalfToFloat(ReadUInt16(data, vertexOffset + 2, isBigEndian));
            result[v * 3 + 2] = HalfToFloat(ReadUInt16(data, vertexOffset + 4, isBigEndian));
        }

        return result;
    }

    /// <summary>
    ///     Compute bitangents from normals and tangents using cross product.
    ///     Bitangent = cross(Normal, Tangent)
    ///     This is needed when the packed geometry only has 2 unit-length streams.
    /// </summary>
    private static float[] ComputeBitangents(float[] normals, float[] tangents, int numVertices)
    {
        var bitangents = new float[numVertices * 3];

        for (var v = 0; v < numVertices; v++)
        {
            // Normal vector
            var nx = normals[v * 3 + 0];
            var ny = normals[v * 3 + 1];
            var nz = normals[v * 3 + 2];

            // Tangent vector
            var tx = tangents[v * 3 + 0];
            var ty = tangents[v * 3 + 1];
            var tz = tangents[v * 3 + 2];

            // Cross product: N × T
            var bx = ny * tz - nz * ty;
            var by = nz * tx - nx * tz;
            var bz = nx * ty - ny * tx;

            // Store bitangent (already unit-length if N and T are unit-length and perpendicular)
            bitangents[v * 3 + 0] = bx;
            bitangents[v * 3 + 1] = by;
            bitangents[v * 3 + 2] = bz;
        }

        return bitangents;
    }

    /// <summary>
    ///     Extract a half4 stream as bone weights (4 floats per vertex).
    ///     Unlike positions/normals, we need all 4 components for bone weights.
    /// </summary>
    private static float[] ExtractHalf4WeightsStream(byte[] data, int rawDataOffset, int numVertices, int stride,
        DataStreamInfo stream, bool isBigEndian)
    {
        var result = new float[numVertices * 4];
        var offset = (int)stream.BlockOffset;

        for (var v = 0; v < numVertices; v++)
        {
            var vertexOffset = rawDataOffset + v * stride + offset;
            if (vertexOffset + 8 > data.Length)
            {
                break;
            }

            // Read all 4 half-floats for bone weights
            result[v * 4 + 0] = HalfToFloat(ReadUInt16(data, vertexOffset, isBigEndian));
            result[v * 4 + 1] = HalfToFloat(ReadUInt16(data, vertexOffset + 2, isBigEndian));
            result[v * 4 + 2] = HalfToFloat(ReadUInt16(data, vertexOffset + 4, isBigEndian));
            result[v * 4 + 3] = HalfToFloat(ReadUInt16(data, vertexOffset + 6, isBigEndian));
        }

        return result;
    }

    /// <summary>
    ///     Extract a half2 stream (2 half-floats = 4 bytes per vertex) as Vector2 floats.
    /// </summary>
    private static float[] ExtractHalf2Stream(byte[] data, int rawDataOffset, int numVertices, int stride,
        DataStreamInfo stream, bool isBigEndian)
    {
        var result = new float[numVertices * 2];
        var offset = (int)stream.BlockOffset;

        for (var v = 0; v < numVertices; v++)
        {
            var vertexOffset = rawDataOffset + v * stride + offset;
            if (vertexOffset + 4 > data.Length)
            {
                break;
            }

            result[v * 2 + 0] = HalfToFloat(ReadUInt16(data, vertexOffset, isBigEndian));
            result[v * 2 + 1] = HalfToFloat(ReadUInt16(data, vertexOffset + 2, isBigEndian));
        }

        return result;
    }

    /// <summary>
    ///     Extract a ubyte4 stream (4 unsigned bytes per vertex) as raw RGBA bytes.
    ///     Vertex colors are stored as RGBA (4 bytes per vertex).
    ///     Note: No endian conversion needed for single-byte values.
    /// </summary>
    private static byte[] ExtractUbyte4Stream(byte[] data, int rawDataOffset, int numVertices, int stride,
        DataStreamInfo stream)
    {
        var result = new byte[numVertices * 4];
        var offset = (int)stream.BlockOffset;

        for (var v = 0; v < numVertices; v++)
        {
            var vertexOffset = rawDataOffset + v * stride + offset;
            if (vertexOffset + 4 > data.Length)
            {
                break;
            }

            result[v * 4 + 0] = data[vertexOffset + 0];
            result[v * 4 + 1] = data[vertexOffset + 1];
            result[v * 4 + 2] = data[vertexOffset + 2];
            result[v * 4 + 3] = data[vertexOffset + 3];
        }

        return result;
    }

    /// <summary>
    ///     Read uint16 with endian handling.
    /// </summary>
    private static ushort ReadUInt16(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    }

    /// <summary>
    ///     Read uint32 with endian handling.
    /// </summary>
    private static uint ReadUInt32(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }

    /// <summary>
    ///     Convert half-float (16-bit) to single-precision float (32-bit).
    /// </summary>
    public static float HalfToFloat(ushort half)
    {
        var sign = (half >> 15) & 1;
        var exp = (half >> 10) & 0x1F;
        var mant = half & 0x3FF;

        if (exp == 0)
        {
            // Zero or denormalized
            if (mant == 0)
            {
                return sign == 0 ? 0f : -0f;
            }

            // Denormalized
            var val = mant / 1024.0f * (float)Math.Pow(2, -14);
            return sign == 0 ? val : -val;
        }

        if (exp == 31)
        {
            // Infinity or NaN
            if (mant != 0)
            {
                return float.NaN;
            }

            return sign == 0 ? float.PositiveInfinity : float.NegativeInfinity;
        }

        // Normalized
        var e = exp - 15 + 127;
        var m = mant << 13;
        var bits = (sign << 31) | (e << 23) | m;
        return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
    }

    private sealed record CategorizedStreams(
        List<DataStreamInfo> Half4Streams,
        List<DataStreamInfo> Half2Streams,
        List<DataStreamInfo> Ubyte4Streams);
}
