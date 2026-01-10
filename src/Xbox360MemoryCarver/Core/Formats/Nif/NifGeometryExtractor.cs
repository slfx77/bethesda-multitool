using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Extracts geometry data from BSPackedAdditionalGeometryData blocks.
/// </summary>
internal sealed class NifGeometryExtractor
{
    private readonly bool _verbose;

    public NifGeometryExtractor(bool verbose = false)
    {
        _verbose = verbose;
    }

    /// <summary>
    ///     Extract geometry data from a BSPackedAdditionalGeometryData block.
    /// </summary>
    public PackedGeometryData? Extract(byte[] data, BlockInfo block)
    {
        try
        {
            var pos = block.DataOffset;
            var end = block.DataOffset + block.Size;

            var numVertices = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
            pos += 2;

            var numBlockInfos = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            pos += 4;

            var streams = ParseDataStreams(data, ref pos, (int)numBlockInfos);

            var numDataBlocks = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            pos += 4;

            var (rawDataOffset, stride) = FindRawData(data, ref pos, (int)numDataBlocks, end, streams);
            if (rawDataOffset < 0 || streams.Count == 0) return null;

            return ExtractGeometryData(data, rawDataOffset, numVertices, stride, streams);
        }
        catch (Exception ex)
        {
            if (_verbose) Console.WriteLine($"Error extracting packed geometry: {ex.Message}");
            return null;
        }
    }

    private static List<DataStreamInfo> ParseDataStreams(byte[] data, ref int pos, int count)
    {
        var streams = new List<DataStreamInfo>();
        for (var i = 0; i < count; i++)
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

        return streams;
    }

    private static (int rawDataOffset, int stride) FindRawData(
        byte[] data, ref int pos, int numDataBlocks, int end, List<DataStreamInfo> streams)
    {
        var rawDataOffset = -1;
        var stride = streams.Count > 0 ? (int)streams[0].Stride : 0;

        for (var b = 0; b < numDataBlocks && pos < end; b++)
        {
            var hasData = data[pos++];
            if (hasData == 0) continue;

            var blockSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            var numInnerBlocks = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 4));
            pos += 8;

            pos += (int)numInnerBlocks * 4; // Skip block offsets

            var numData = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            pos += 4;

            pos += (int)numData * 4; // Skip data sizes

            rawDataOffset = pos;
            var rawDataSize = (int)(numData * blockSize);
            pos += rawDataSize;

            pos += 8; // Skip shaderIndex and totalSize
        }

        return (rawDataOffset, stride);
    }

    private PackedGeometryData ExtractGeometryData(
        byte[] data, int rawDataOffset, ushort numVertices, int stride, List<DataStreamInfo> streams)
    {
        var result = new PackedGeometryData { NumVertices = numVertices };

        if (_verbose)
        {
            Console.WriteLine($"  Block has {streams.Count} data streams, stride={stride}:");
            for (var i = 0; i < streams.Count; i++)
                Console.WriteLine($"    Stream[{i}]: type={streams[i].Type}, " +
                                  $"unitSize={streams[i].UnitSize}, offset={streams[i].BlockOffset}");
        }

        // Type 16 = half4, Type 14 = half2, Type 28 = ubyte4
        var half4Streams = streams.Where(s => s.Type == 16 && s.UnitSize == 8)
            .OrderBy(s => s.BlockOffset).ToList();
        var half2Streams = streams.Where(s => s.Type == 14 && s.UnitSize == 4)
            .OrderBy(s => s.BlockOffset).ToList();

        // Stream layout depends on whether mesh is skinned (stride 48) or not (stride 32/36)
        // Skinned (stride 48): Position(0), BoneWeights(8), BoneIdx(16), Normal(20), UV(28), Tangent(32), Bitangent(40)
        // Non-skinned (stride 32): Position(0), Normal(8), UV(16), Tangent(20), Bitangent(28)

        if (half2Streams.Count > 0)
            result.UVs = ExtractHalf2Stream(data, rawDataOffset, numVertices, stride,
                streams[streams.IndexOf(half2Streams[0])]);

        // Position is always the first half4 stream at offset 0
        if (half4Streams.Count >= 1)
            result.Positions = ExtractHalf4Stream(data, rawDataOffset, numVertices, stride, half4Streams[0]);

        // For skinned meshes (stride >= 48), stream at offset 8 is bone weights, not normals
        // Normal is at offset 20 (third half4), Tangent at 32 (fourth), Bitangent at 40 (fifth)
        // For non-skinned meshes, Normal is at offset 8 (second half4)
        bool isSkinned = stride >= 48 && half4Streams.Count >= 5;

        if (isSkinned)
        {
            // Skinned layout: skip stream[1] (bone weights), use streams 2,3,4 for normal/tangent/bitangent
            if (half4Streams.Count >= 3)
                result.Normals = ExtractHalf4Stream(data, rawDataOffset, numVertices, stride, half4Streams[2]);
            if (half4Streams.Count >= 4)
                result.Tangents = ExtractHalf4Stream(data, rawDataOffset, numVertices, stride, half4Streams[3]);
            if (half4Streams.Count >= 5)
                result.Bitangents = ExtractHalf4Stream(data, rawDataOffset, numVertices, stride, half4Streams[4]);

            if (_verbose)
                Console.WriteLine($"  Skinned mesh layout detected (stride {stride}): Normal@{half4Streams[2].BlockOffset}, Tangent@{half4Streams[3].BlockOffset}, Bitangent@{half4Streams[4].BlockOffset}");
        }
        else
        {
            // Non-skinned layout: streams 1,2,3 for normal/tangent/bitangent
            if (half4Streams.Count >= 2)
                result.Normals = ExtractHalf4Stream(data, rawDataOffset, numVertices, stride, half4Streams[1]);
            if (half4Streams.Count >= 3)
                result.Tangents = ExtractHalf4Stream(data, rawDataOffset, numVertices, stride, half4Streams[2]);
            if (half4Streams.Count >= 4)
                result.Bitangents = ExtractHalf4Stream(data, rawDataOffset, numVertices, stride, half4Streams[3]);

            if (_verbose)
                Console.WriteLine($"  Non-skinned mesh layout (stride {stride}): Normal@{half4Streams[1].BlockOffset}");
        }

        result.BsDataFlags = 0;
        if (result.UVs != null) result.BsDataFlags |= 1;
        if (result.Tangents != null) result.BsDataFlags |= 4096;

        if (_verbose)
            Console.WriteLine($"  Extracted: verts={result.Positions != null}, normals={result.Normals != null}, " +
                              $"tangents={result.Tangents != null}, bitangents={result.Bitangents != null}, uvs={result.UVs != null}");

        return result;
    }

    private static float[]? ExtractHalf4Stream(byte[] data, int rawDataOffset, int numVertices, int stride,
        DataStreamInfo stream)
    {
        if (stream.UnitSize != 8) return null;

        var result = new float[numVertices * 3];
        var offset = (int)stream.BlockOffset;

        for (var v = 0; v < numVertices; v++)
        {
            var vertexOffset = rawDataOffset + v * stride + offset;
            result[v * 3 + 0] = HalfToFloat(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(vertexOffset)));
            result[v * 3 + 1] = HalfToFloat(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(vertexOffset + 2)));
            result[v * 3 + 2] = HalfToFloat(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(vertexOffset + 4)));
        }

        return result;
    }

    private static float[]? ExtractHalf2Stream(byte[] data, int rawDataOffset, int numVertices, int stride,
        DataStreamInfo stream)
    {
        if (stream.UnitSize != 4) return null;

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

    private static float HalfToFloat(ushort half)
    {
        var sign = (half >> 15) & 1;
        var exp = (half >> 10) & 0x1F;
        var mant = half & 0x3FF;

        if (exp == 0)
        {
            if (mant == 0) return sign == 1 ? -0.0f : 0.0f;
            var value = (float)Math.Pow(2, -14) * (mant / 1024.0f);
            return sign == 1 ? -value : value;
        }

        if (exp == 31)
        {
            if (mant == 0) return sign == 1 ? float.NegativeInfinity : float.PositiveInfinity;
            return float.NaN;
        }

        var normalizedValue = (float)Math.Pow(2, exp - 15) * (1 + mant / 1024.0f);
        return sign == 1 ? -normalizedValue : normalizedValue;
    }
}
