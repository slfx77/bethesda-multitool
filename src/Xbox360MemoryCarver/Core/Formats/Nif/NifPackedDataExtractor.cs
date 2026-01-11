// BSPackedAdditionalGeometryData extraction for Xbox 360 NIF files
// Extracts half-float geometry data and converts to full floats

using System.Buffers.Binary;
using static Xbox360MemoryCarver.Core.Formats.Nif.NifEndianUtils;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Extracts geometry data from BSPackedAdditionalGeometryData blocks.
///     Xbox 360 NIFs store vertex data in packed format (half-floats) that must
///     be expanded for PC compatibility.
/// </summary>
internal static class NifPackedDataExtractor
{
    /// <summary>
    ///     Parse a BSPackedAdditionalGeometryData block and extract geometry.
    /// </summary>
    public static PackedGeometryData? Extract(byte[] data, int blockOffset, int blockSize, bool isBigEndian, bool verbose = false)
    {
        try
        {
            var pos = blockOffset;
            var end = blockOffset + blockSize;

            // NumVertices (ushort)
            if (pos + 2 > end) return null;
            var numVertices = ReadUInt16(data, pos, isBigEndian);
            pos += 2;

            // NumBlockInfos (uint) - number of data streams
            if (pos + 4 > end) return null;
            var numBlockInfos = ReadUInt32(data, pos, isBigEndian);
            pos += 4;

            if (verbose)
            {
                Console.WriteLine($"    Packed data: {numVertices} vertices, {numBlockInfos} streams");
            }

            // Parse stream infos (25 bytes each)
            var streams = new List<DataStreamInfo>();
            for (var i = 0; i < numBlockInfos && pos + 25 <= end; i++)
            {
                streams.Add(new DataStreamInfo
                {
                    Type = ReadUInt32(data, pos, isBigEndian),
                    UnitSize = ReadUInt32(data, pos + 4, isBigEndian),
                    TotalSize = ReadUInt32(data, pos + 8, isBigEndian),
                    Stride = ReadUInt32(data, pos + 12, isBigEndian),
                    BlockIndex = ReadUInt32(data, pos + 16, isBigEndian),
                    BlockOffset = ReadUInt32(data, pos + 20, isBigEndian),
                    Flags = data[pos + 24]
                });
                pos += 25;

                if (verbose)
                {
                    var s = streams[^1];
                    Console.WriteLine($"      Stream {i}: type={s.Type}, unitSize={s.UnitSize}, stride={s.Stride}, offset={s.BlockOffset}");
                }
            }

            if (streams.Count == 0)
            {
                return null;
            }

            var stride = (int)streams[0].Stride;

            // NumDataBlocks (uint)
            if (pos + 4 > end) return null;
            var numDataBlocks = ReadUInt32(data, pos, isBigEndian);
            pos += 4;

            // Find raw data offset by parsing data blocks
            var rawDataOffset = -1;
            for (var b = 0; b < numDataBlocks && pos < end; b++)
            {
                var hasData = data[pos++];
                if (hasData == 0) continue;

                if (pos + 8 > end) break;
                var blockDataSize = ReadUInt32(data, pos, isBigEndian);
                var numInnerBlocks = ReadUInt32(data, pos + 4, isBigEndian);
                pos += 8;

                // Block offsets
                pos += (int)numInnerBlocks * 4;

                if (pos + 4 > end) break;
                var numData = ReadUInt32(data, pos, isBigEndian);
                pos += 4;

                // Data sizes
                pos += (int)numData * 4;

                rawDataOffset = pos;
                pos += (int)blockDataSize;
                pos += 8; // shaderIndex + totalSize
            }

            if (rawDataOffset < 0)
            {
                if (verbose) Console.WriteLine("    Failed to find raw data offset");
                return null;
            }

            // Identify stream types based on Type and UnitSize
            // Type 16 + UnitSize 8 = half4 (positions, normals, tangents, bitangents)
            // Type 14 + UnitSize 4 = half2 (UVs)
            // Type 28 + UnitSize 4 = ubyte4 (vertex colors)
            var half4Streams = streams.Where(s => s.Type == 16 && s.UnitSize == 8)
                .OrderBy(s => s.BlockOffset).ToList();
            var half2Streams = streams.Where(s => s.Type == 14 && s.UnitSize == 4)
                .OrderBy(s => s.BlockOffset).ToList();
            var ubyte4Streams = streams.Where(s => s.Type == 28 && s.UnitSize == 4)
                .OrderBy(s => s.BlockOffset).ToList();

            var result = new PackedGeometryData { NumVertices = numVertices };

            // Extract UVs (first half2 stream)
            if (half2Streams.Count > 0)
            {
                result.UVs = ExtractHalf2Stream(data, rawDataOffset, numVertices, stride, half2Streams[0], isBigEndian);
            }

            // Extract vertex colors (first ubyte4 stream)
            if (ubyte4Streams.Count > 0)
            {
                result.VertexColors = ExtractUbyte4Stream(data, rawDataOffset, numVertices, stride, ubyte4Streams[0]);
            }

            // Extract half4 streams - identify by characteristics:
            // - Position: first stream at offset 0, large values (not unit length)
            // - Normal/Tangent/Bitangent: unit-length vectors (length ≈ 1.0)
            // 
            // Actual Xbox 360 layout (stride 48):
            //   half4[0] offset 0  = Position (large values)
            //   half4[1] offset 8  = Unknown auxiliary data (NOT unit length, Z often 0)
            //   half4[2] offset 20 = Normal (unit length!)
            //   half4[3] offset 32 = Tangent (unit length)
            //   half4[4] offset 40 = Bitangent (unit length)
            
            // Position is always the first stream (offset 0)
            if (half4Streams.Count >= 1 && half4Streams[0].BlockOffset == 0)
            {
                result.Positions = ExtractHalf4Stream(data, rawDataOffset, numVertices, stride, half4Streams[0], isBigEndian);
            }
            
            // Find unit-length streams for normals/tangents/bitangents
            // Skip offset 8 stream as it's auxiliary data, not true normals
            var unitStreams = new List<(DataStreamInfo stream, float[] data)>();
            foreach (var stream in half4Streams.Where(s => s.BlockOffset >= 16)) // Skip position (0) and auxiliary (8)
            {
                var streamData = ExtractHalf4Stream(data, rawDataOffset, numVertices, stride, stream, isBigEndian);
                if (streamData != null)
                {
                    // Check if this is a unit-length stream (sample first 10 vertices)
                    var sampleCount = Math.Min(10, (int)numVertices);
                    var avgLen = 0.0;
                    for (var v = 0; v < sampleCount; v++)
                    {
                        var x = streamData[v * 3 + 0];
                        var y = streamData[v * 3 + 1];
                        var z = streamData[v * 3 + 2];
                        avgLen += Math.Sqrt(x * x + y * y + z * z);
                    }
                    avgLen /= sampleCount;
                    
                    // Unit-length streams have avgLen ≈ 1.0 (allow some tolerance)
                    if (avgLen > 0.9 && avgLen < 1.1)
                    {
                        unitStreams.Add((stream, streamData));
                        if (verbose)
                            Console.WriteLine($"      Found unit-length stream at offset {stream.BlockOffset}, avgLen={avgLen:F3}");
                    }
                }
            }
            
            // Assign unit-length streams in order by offset: Normal, Tangent, Bitangent
            if (unitStreams.Count >= 1) result.Normals = unitStreams[0].data;
            if (unitStreams.Count >= 2) result.Tangents = unitStreams[1].data;
            if (unitStreams.Count >= 3) result.Bitangents = unitStreams[2].data;

            // Calculate BS Data Flags based on what data is available
            // Bit 0: has UVs, Bit 12 (4096): has tangents/bitangents
            ushort bsDataFlags = 0;
            if (result.UVs != null) bsDataFlags |= 1;
            if (result.Tangents != null || result.Bitangents != null) bsDataFlags |= 4096;
            result.BsDataFlags = bsDataFlags;

            if (verbose)
            {
                Console.WriteLine($"    Extracted: verts={result.Positions != null}, normals={result.Normals != null}, " +
                    $"tangents={result.Tangents != null}, bitangents={result.Bitangents != null}, uvs={result.UVs != null}, colors={result.VertexColors != null}");
            }

            return result;
        }
        catch (Exception ex)
        {
            if (verbose) Console.WriteLine($"    Error extracting packed data: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Extract a half4 stream (4 half-floats = 8 bytes per vertex) as Vector3 floats.
    /// </summary>
    private static float[]? ExtractHalf4Stream(byte[] data, int rawDataOffset, int numVertices, int stride, DataStreamInfo stream, bool isBigEndian)
    {
        var result = new float[numVertices * 3];
        var offset = (int)stream.BlockOffset;

        for (var v = 0; v < numVertices; v++)
        {
            var vertexOffset = rawDataOffset + v * stride + offset;
            if (vertexOffset + 6 > data.Length) break;

            // Read 3 half-floats (ignore the 4th W component)
            result[v * 3 + 0] = HalfToFloat(ReadUInt16(data, vertexOffset, isBigEndian));
            result[v * 3 + 1] = HalfToFloat(ReadUInt16(data, vertexOffset + 2, isBigEndian));
            result[v * 3 + 2] = HalfToFloat(ReadUInt16(data, vertexOffset + 4, isBigEndian));
        }

        return result;
    }

    /// <summary>
    ///     Extract a half2 stream (2 half-floats = 4 bytes per vertex) as Vector2 floats.
    /// </summary>
    private static float[]? ExtractHalf2Stream(byte[] data, int rawDataOffset, int numVertices, int stride, DataStreamInfo stream, bool isBigEndian)
    {
        var result = new float[numVertices * 2];
        var offset = (int)stream.BlockOffset;

        for (var v = 0; v < numVertices; v++)
        {
            var vertexOffset = rawDataOffset + v * stride + offset;
            if (vertexOffset + 4 > data.Length) break;

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
    private static byte[]? ExtractUbyte4Stream(byte[] data, int rawDataOffset, int numVertices, int stride, DataStreamInfo stream)
    {
        var result = new byte[numVertices * 4];
        var offset = (int)stream.BlockOffset;

        for (var v = 0; v < numVertices; v++)
        {
            var vertexOffset = rawDataOffset + v * stride + offset;
            if (vertexOffset + 4 > data.Length) break;

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
            if (mant == 0) return sign == 0 ? 0f : -0f;
            // Denormalized
            var val = mant / 1024.0f * (float)Math.Pow(2, -14);
            return sign == 0 ? val : -val;
        }

        if (exp == 31)
        {
            // Infinity or NaN
            return mant == 0
                ? (sign == 0 ? float.PositiveInfinity : float.NegativeInfinity)
                : float.NaN;
        }

        // Normalized
        var e = exp - 15 + 127;
        var m = mant << 13;
        var bits = (sign << 31) | (e << 23) | m;
        return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
    }
}
