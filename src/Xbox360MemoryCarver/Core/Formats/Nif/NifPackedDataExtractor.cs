// BSPackedAdditionalGeometryData extraction for Xbox 360 NIF files
// Extracts half-float geometry data and converts to full floats

using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

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

            Log.Debug($"    Packed data: {numVertices} vertices, {numBlockInfos} streams");

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

                var s = streams[^1];
                Log.Debug(
                    $"      Stream {i}: type={s.Type}, unitSize={s.UnitSize}, stride={s.Stride}, offset={s.BlockOffset}");
            }

            if (streams.Count == 0) return null;

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
                Log.Debug("    Failed to find raw data offset");
                return null;
            }

            // Identify stream types based on Type and UnitSize
            // Type 16 + UnitSize 8 = half4 (positions, normals, tangents, bitangents)
            // Type 14 + UnitSize 4 = half2 (UVs)
            // Type 28 + UnitSize 4 = ubyte4 (bone indices at offset 16, or vertex colors)
            var half4Streams = streams.Where(s => s.Type == 16 && s.UnitSize == 8)
                .OrderBy(s => s.BlockOffset).ToList();
            var half2Streams = streams.Where(s => s.Type == 14 && s.UnitSize == 4)
                .OrderBy(s => s.BlockOffset).ToList();
            var ubyte4Streams = streams.Where(s => s.Type == 28 && s.UnitSize == 4)
                .OrderBy(s => s.BlockOffset).ToList();

            var result = new PackedGeometryData { NumVertices = numVertices };

            // Extract UVs (first half2 stream)
            if (half2Streams.Count > 0)
                result.UVs = ExtractHalf2Stream(data, rawDataOffset, numVertices, stride, half2Streams[0], isBigEndian);

            // Determine if this is a skinned mesh based on STRIDE, not ubyte4 presence.
            // Both skinned meshes and static meshes with vertex colors have ubyte4 at offset 16,
            // but they have different strides:
            //   - Stride 48: Skinned mesh (ubyte4 = bone indices, offset 8 = auxiliary data)
            //   - Stride < 48: Non-skinned mesh (ubyte4 = vertex colors if present, offset 8 = normals)
            var isSkinned = stride == 48;
            var hasBoneIndicesStream = ubyte4Streams.Any(s => s.BlockOffset == 16);

            if (isSkinned && hasBoneIndicesStream)
            {
                // Extract bone indices from ubyte4 stream at offset 16
                var boneIndicesStream = ubyte4Streams.First(s => s.BlockOffset == 16);
                result.BoneIndices = ExtractUbyte4Stream(data, rawDataOffset, numVertices, stride, boneIndicesStream);

                // Bone weights are at offset 8 for skinned meshes (stride 48)!
                // The half4 at offset 8 contains the bone weights, NOT auxiliary data.
                // Values are stored as 4 half-floats that sum to ~1.0 (e.g., 1.0, 0.0, 0.0, 0.0).
                // The half4 at offset 40 is bitangent data (unit-length), not bone weights.
                var hasBoneWeightsStream = half4Streams.Any(s => s.BlockOffset == 8);
                if (hasBoneWeightsStream)
                {
                    var boneWeightsStream = half4Streams.First(s => s.BlockOffset == 8);
                    result.BoneWeights = ExtractHalf4WeightsStream(data, rawDataOffset, numVertices, stride,
                        boneWeightsStream, isBigEndian);
                    Log.Debug("      Extracted bone weights from offset 8");
                }

                Log.Debug("      Skinned mesh (stride 48): extracted bone indices");
            }
            else
            {
                // Non-skinned mesh: ubyte4 (if present) is vertex colors
                if (ubyte4Streams.Count > 0)
                {
                    result.VertexColors =
                        ExtractUbyte4Stream(data, rawDataOffset, numVertices, stride, ubyte4Streams[0]);

                    Log.Debug("      Non-skinned mesh: extracted vertex colors");
                }
            }

            // Xbox 360 packed geometry has different layouts based on stride:
            //
            // NON-SKINNED (stride 36 bytes - no vertex colors):
            //   Offset 0:  half4 (8 bytes) - Position
            //   Offset 8:  half4 (8 bytes) - Normal (unit-length)
            //   Offset 16: half2 (4 bytes) - UV coordinates
            //   Offset 20: half4 (8 bytes) - Tangent (unit-length)
            //   Offset 28: half4 (8 bytes) - Bitangent (unit-length)
            //
            // NON-SKINNED (stride 40 bytes - with vertex colors):
            //   Offset 0:  half4 (8 bytes) - Position
            //   Offset 8:  half4 (8 bytes) - Normal (unit-length)
            //   Offset 16: ubyte4 (4 bytes) - Vertex colors
            //   Offset 20: half2 (4 bytes) - UV coordinates
            //   Offset 24: half4 (8 bytes) - Tangent (unit-length)
            //   Offset 32: half4 (8 bytes) - Bitangent (unit-length)
            //
            // SKINNED (stride 48 bytes):
            //   Offset 0:  half4 (8 bytes) - Position
            //   Offset 8:  half4 (8 bytes) - Bone weights (4 weights summing to 1.0)
            //   Offset 16: ubyte4 (4 bytes) - Bone indices
            //   Offset 20: half4 (8 bytes) - Normal (unit-length)
            //   Offset 28: half2 (4 bytes) - UV coordinates
            //   Offset 32: half4 (8 bytes) - Tangent (unit-length)
            //   Offset 40: half4 (8 bytes) - Bitangent (unit-length)

            // Position is always the first stream (offset 0)
            if (half4Streams.Count >= 1 && half4Streams[0].BlockOffset == 0)
                result.Positions = ExtractHalf4Stream(data, rawDataOffset, numVertices, stride, half4Streams[0],
                    isBigEndian);

            // Find ALL unit-length streams (including offset 8 for non-skinned meshes)
            var unitStreams = new List<(DataStreamInfo stream, float[] data, int offset)>();
            foreach (var stream in half4Streams.Where(s => s.BlockOffset >= 8)) // Include offset 8!
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
                        unitStreams.Add((stream, streamData, (int)stream.BlockOffset));
                        Log.Debug(
                            $"      Found unit-length stream at offset {stream.BlockOffset}, avgLen={avgLen:F3}");
                    }
                }
            }

            // Sort by offset to ensure consistent assignment
            unitStreams.Sort((a, b) => a.offset.CompareTo(b.offset));

            // Assign unit-length streams based on layout type
            // Non-skinned (36-byte): offsets 8, 20, 28 → Normals, Tangents, Bitangents
            // Skinned (48-byte): offsets 20, 32, 40 → Normals, Tangents, Bitangents (offset 8 = bone weights)
            if (isSkinned)
            {
                // For skinned meshes, skip offset 8 (it contains bone weights, extracted earlier)
                var skinnedUnitStreams = unitStreams.Where(s => s.offset >= 20).ToList();
                if (skinnedUnitStreams.Count >= 1) result.Normals = skinnedUnitStreams[0].data;
                if (skinnedUnitStreams.Count >= 2) result.Tangents = skinnedUnitStreams[1].data;
                if (skinnedUnitStreams.Count >= 3) result.Bitangents = skinnedUnitStreams[2].data;
            }
            else
            {
                // For non-skinned meshes, offset 8 is normals!
                if (unitStreams.Count >= 1) result.Normals = unitStreams[0].data;
                if (unitStreams.Count >= 2) result.Tangents = unitStreams[1].data;
                if (unitStreams.Count >= 3) result.Bitangents = unitStreams[2].data;
            }

            // If we have normals and tangents but no bitangents, compute them
            // Bitangent = cross(Normal, Tangent) - common for meshes with only 2 unit-length streams
            if (result.Normals != null && result.Tangents != null && result.Bitangents == null)
            {
                result.Bitangents = ComputeBitangents(result.Normals, result.Tangents, numVertices);
                Log.Debug("      Computed bitangents from normals and tangents");
            }

            // Calculate BS Data Flags based on what data is available
            // Bit 0: has UVs, Bit 12 (4096): has tangents/bitangents
            ushort bsDataFlags = 0;
            if (result.UVs != null) bsDataFlags |= 1;
            if (result.Tangents != null || result.Bitangents != null) bsDataFlags |= 4096;
            result.BsDataFlags = bsDataFlags;

            Log.Debug(
                $"    Extracted: verts={result.Positions != null}, normals={result.Normals != null}, " +
                $"tangents={result.Tangents != null}, bitangents={result.Bitangents != null}, uvs={result.UVs != null}, " +
                $"colors={result.VertexColors != null}, boneIndices={result.BoneIndices != null}, boneWeights={result.BoneWeights != null}");

            return result;
        }
        catch (Exception ex)
        {
            Log.Debug($"    Error extracting packed data: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Extract a half4 stream (4 half-floats = 8 bytes per vertex) as Vector3 floats.
    /// </summary>
    private static float[]? ExtractHalf4Stream(byte[] data, int rawDataOffset, int numVertices, int stride,
        DataStreamInfo stream, bool isBigEndian)
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
    private static float[]? ExtractHalf4WeightsStream(byte[] data, int rawDataOffset, int numVertices, int stride,
        DataStreamInfo stream, bool isBigEndian)
    {
        var result = new float[numVertices * 4];
        var offset = (int)stream.BlockOffset;

        for (var v = 0; v < numVertices; v++)
        {
            var vertexOffset = rawDataOffset + v * stride + offset;
            if (vertexOffset + 8 > data.Length) break;

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
    private static float[]? ExtractHalf2Stream(byte[] data, int rawDataOffset, int numVertices, int stride,
        DataStreamInfo stream, bool isBigEndian)
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
    private static byte[]? ExtractUbyte4Stream(byte[] data, int rawDataOffset, int numVertices, int stride,
        DataStreamInfo stream)
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
            if (mant != 0)
                return float.NaN;
            return sign == 0 ? float.PositiveInfinity : float.NegativeInfinity;
        }

        // Normalized
        var e = exp - 15 + 127;
        var m = mant << 13;
        var bits = (sign << 31) | (e << 23) | m;
        return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
    }
}
