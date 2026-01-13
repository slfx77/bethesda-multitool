// NIF converter - Geometry block expansion and writing

using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

internal sealed partial class NifConverter
{
    /// <summary>
    ///     Write a geometry block with expanded packed data.
    ///     If vertexMap is provided (for skinned meshes), vertices are remapped from partition order to mesh order.
    ///     If triangles is provided, triangles are written for NiTriShapeData (converted from NiSkinPartition strips).
    /// </summary>
    private static int WriteExpandedGeometryBlock(byte[] input, byte[] output, int outPos, BlockInfo block,
        PackedGeometryData packedData, ushort[]? vertexMap, ushort[]? triangles)
    {
        var srcPos = block.DataOffset;

        // groupId (int BE -> LE)
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outPos),
            BinaryPrimitives.ReadInt32BigEndian(input.AsSpan(srcPos)));
        srcPos += 4;
        outPos += 4;

        // numVertices (ushort BE -> LE)
        var numVertices = ReadUInt16BE(input, srcPos);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numVertices);
        srcPos += 2;
        outPos += 2;

        // keepFlags, compressFlags (bytes - no conversion)
        output[outPos++] = input[srcPos++];
        output[outPos++] = input[srcPos++];

        // hasVertices - set to 1 if we have positions from packed data
        var origHasVertices = input[srcPos++];
        var newHasVertices = (byte)(packedData.Positions != null ? 1 : origHasVertices);
        output[outPos++] = newHasVertices;

        // Write vertices
        outPos = WriteVertices(input, output, srcPos, outPos, numVertices, packedData, vertexMap,
            origHasVertices, newHasVertices, out srcPos);

        // bsDataFlags - update flags based on packed data
        var origBsDataFlags = ReadUInt16BE(input, srcPos);
        var newBsDataFlags = origBsDataFlags;
        if (packedData.Tangents != null)
        {
            newBsDataFlags |= 4096; // Has tangents flag
        }

        if (packedData.UVs != null)
        {
            newBsDataFlags |= 1; // Has UVs flag
        }

        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), newBsDataFlags);
        srcPos += 2;
        outPos += 2;

        // hasNormals - set to 1 if we have normals from packed data
        var origHasNormals = input[srcPos++];
        var newHasNormals = (byte)(packedData.Normals != null ? 1 : origHasNormals);
        output[outPos++] = newHasNormals;

        // Write normals and tangents
        outPos = WriteNormalsAndTangents(input, output, srcPos, outPos, numVertices, packedData, vertexMap,
            origHasNormals, newHasNormals, origBsDataFlags, newBsDataFlags, out srcPos);

        // center (Vector3) + radius (float) = 16 bytes
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadSingleBigEndian(input.AsSpan(srcPos)));
            srcPos += 4;
            outPos += 4;
        }

        // Write vertex colors
        var isSkinned = vertexMap != null;
        outPos = WriteVertexColors(input, output, srcPos, outPos, numVertices, packedData, vertexMap,
            isSkinned, out srcPos);

        // Write UV sets
        outPos = WriteUVSets(input, output, srcPos, outPos, numVertices, packedData, vertexMap,
            origBsDataFlags, newBsDataFlags, out srcPos);

        // consistency (ushort BE -> LE)
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
            ReadUInt16BE(input, srcPos));
        srcPos += 2;
        outPos += 2;

        // additionalData ref - set to -1 since we're removing the packed block
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outPos), -1);
        srcPos += 4;
        outPos += 4;

        // Copy and convert remaining block data (strips/triangles specific to NiTriStripsData/NiTriShapeData)
        var remainingOriginalBytes = block.Size - (srcPos - block.DataOffset);
        if (remainingOriginalBytes > 0)
        {
            outPos = WriteTriStripSpecificData(input, output, srcPos, outPos, block.TypeName, triangles);
        }

        return outPos;
    }

    /// <summary>
    ///     Write vertex positions from packed data or copy existing.
    /// </summary>
    private static int WriteVertices(byte[] input, byte[] output, int srcPos, int outPos, ushort numVertices,
        PackedGeometryData packedData, ushort[]? vertexMap, byte origHasVertices, byte newHasVertices,
        out int newSrcPos)
    {
        // Always prefer packed data when available - Xbox 360 files set hasVertices=1
        // even when actual vertex data is in BSPackedAdditionalGeometryData
        if (newHasVertices != 0 && packedData.Positions != null)
        {
            // Write unpacked positions (with optional vertex map remapping for skinned meshes)
            if (vertexMap != null && vertexMap.Length > 0)
            {
                // Skinned mesh: remap from partition order to mesh order
                var vertexDataSize = numVertices * 12; // 3 floats * 4 bytes
                var basePos = outPos;

                // Pre-zero the vertex area (in case mapping is sparse)
                output.AsSpan(basePos, vertexDataSize).Clear();

                // Iterate over ALL partition vertices from packed data
                var packedVertexCount = Math.Min(vertexMap.Length, packedData.Positions.Length / 3);
                for (var partitionIdx = 0; partitionIdx < packedVertexCount; partitionIdx++)
                {
                    var meshIdx = vertexMap[partitionIdx];
                    if (meshIdx >= numVertices)
                    {
                        continue; // Skip invalid indices
                    }

                    var writePos = basePos + meshIdx * 12;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos),
                        packedData.Positions[partitionIdx * 3 + 0]);
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 4),
                        packedData.Positions[partitionIdx * 3 + 1]);
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 8),
                        packedData.Positions[partitionIdx * 3 + 2]);
                }

                outPos += vertexDataSize;
            }
            else
            {
                // Non-skinned mesh: write in sequential order
                var packedVertexCount = Math.Min(numVertices, packedData.Positions.Length / 3);
                for (var v = 0; v < packedVertexCount; v++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Positions[v * 3 + 0]);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Positions[v * 3 + 1]);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Positions[v * 3 + 2]);
                    outPos += 4;
                }

                // Pad remaining vertices with zeros if packed data has fewer vertices
                var remaining = numVertices - packedVertexCount;
                if (remaining > 0)
                {
                    output.AsSpan(outPos, remaining * 12).Clear();
                    outPos += remaining * 12;
                }
            }
        }
        else if (origHasVertices != 0 && packedData.Positions == null)
        {
            // Copy and convert existing vertices (only if no packed data available)
            for (var v = 0; v < numVertices * 3; v++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                    BinaryPrimitives.ReadSingleBigEndian(input.AsSpan(srcPos)));
                srcPos += 4;
                outPos += 4;
            }
        }

        newSrcPos = srcPos;
        return outPos;
    }

    /// <summary>
    ///     Write normals, tangents, and bitangents from packed data or copy existing.
    /// </summary>
    private static int WriteNormalsAndTangents(byte[] input, byte[] output, int srcPos, int outPos, ushort numVertices,
        PackedGeometryData packedData, ushort[]? vertexMap, byte origHasNormals, byte newHasNormals,
        ushort origBsDataFlags, ushort newBsDataFlags, out int newSrcPos)
    {
        // Always prefer packed data when available - Xbox 360 files set hasNormals=1
        // even when actual normal data is in BSPackedAdditionalGeometryData
        if (newHasNormals != 0 && packedData.Normals != null)
        {
            // Write unpacked normals (with optional vertex map remapping for skinned meshes)
            outPos = WriteVec3Array(output, outPos, numVertices, packedData.Normals, vertexMap);

            // Write tangents if available (with optional remapping)
            if ((newBsDataFlags & 4096) != 0 && packedData.Tangents != null)
            {
                outPos = WriteVec3Array(output, outPos, numVertices, packedData.Tangents, vertexMap);

                // Write bitangents if available (with optional remapping)
                if (packedData.Bitangents != null)
                {
                    outPos = WriteVec3Array(output, outPos, numVertices, packedData.Bitangents, vertexMap);
                }
            }
        }
        else if (origHasNormals != 0 && packedData.Normals == null)
        {
            // Copy and convert existing normals (only if no packed data available)
            for (var v = 0; v < numVertices * 3; v++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                    BinaryPrimitives.ReadSingleBigEndian(input.AsSpan(srcPos)));
                srcPos += 4;
                outPos += 4;
            }

            // Copy existing tangents/bitangents if present
            if ((origBsDataFlags & 4096) != 0)
            {
                for (var v = 0; v < numVertices * 6; v++) // 3 floats tangent + 3 floats bitangent
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                        BinaryPrimitives.ReadSingleBigEndian(input.AsSpan(srcPos)));
                    srcPos += 4;
                    outPos += 4;
                }
            }
        }

        newSrcPos = srcPos;
        return outPos;
    }

    /// <summary>
    ///     Write a vec3 array with optional vertex map remapping.
    /// </summary>
    private static int WriteVec3Array(byte[] output, int outPos, ushort numVertices, float[] data, ushort[]? vertexMap)
    {
        if (vertexMap != null && vertexMap.Length > 0)
        {
            var dataSize = numVertices * 12;
            var basePos = outPos;
            output.AsSpan(basePos, dataSize).Clear();

            var packedCount = Math.Min(vertexMap.Length, data.Length / 3);
            for (var partitionIdx = 0; partitionIdx < packedCount; partitionIdx++)
            {
                var meshIdx = vertexMap[partitionIdx];
                if (meshIdx >= numVertices)
                {
                    continue;
                }

                var writePos = basePos + meshIdx * 12;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos), data[partitionIdx * 3 + 0]);
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 4), data[partitionIdx * 3 + 1]);
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 8), data[partitionIdx * 3 + 2]);
            }

            return outPos + dataSize;
        }
        else
        {
            for (var v = 0; v < numVertices; v++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), data[v * 3 + 0]);
                outPos += 4;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), data[v * 3 + 1]);
                outPos += 4;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), data[v * 3 + 2]);
                outPos += 4;
            }

            return outPos;
        }
    }

    /// <summary>
    ///     Write vertex colors from packed data or copy existing.
    /// </summary>
    private static int WriteVertexColors(byte[] input, byte[] output, int srcPos, int outPos, ushort numVertices,
        PackedGeometryData packedData, ushort[]? vertexMap, bool isSkinned, out int newSrcPos)
    {
        // hasVertexColors (byte) - set to 1 if we have colors from packed data
        // NOTE: For skinned meshes (vertexMap != null), ubyte4 stream is bone indices, NOT vertex colors!
        var origHasVertexColors = input[srcPos++];
        var newHasVertexColors = (byte)(packedData.VertexColors != null && !isSkinned ? 1 : origHasVertexColors);
        output[outPos++] = newHasVertexColors;

        // NIF stores vertex colors as Color4 (4 floats, 16 bytes per vertex) in RGBA order
        // Xbox 360 packed data stores them as ByteColor4 (4 bytes per vertex) in ARGB order
        if (newHasVertexColors != 0 && packedData.VertexColors != null && !isSkinned)
        {
            // Convert from ByteColor4 ARGB (0-255) to Color4 RGBA (0.0-1.0) (with optional remapping)
            if (vertexMap != null && vertexMap.Length > 0)
            {
                var colorDataSize = numVertices * 16; // 4 floats * 4 bytes
                var basePos = outPos;
                output.AsSpan(basePos, colorDataSize).Clear();

                var packedColorCount = Math.Min(vertexMap.Length, packedData.VertexColors.Length / 4);
                for (var partitionIdx = 0; partitionIdx < packedColorCount; partitionIdx++)
                {
                    var meshIdx = vertexMap[partitionIdx];
                    if (meshIdx >= numVertices)
                    {
                        continue;
                    }

                    // Xbox packed format: A, R, G, B -> NIF Color4 format: R, G, B, A
                    var a = packedData.VertexColors[partitionIdx * 4 + 0] / 255.0f;
                    var r = packedData.VertexColors[partitionIdx * 4 + 1] / 255.0f;
                    var g = packedData.VertexColors[partitionIdx * 4 + 2] / 255.0f;
                    var b = packedData.VertexColors[partitionIdx * 4 + 3] / 255.0f;

                    var writePos = basePos + meshIdx * 16;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos), r);
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 4), g);
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 8), b);
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 12), a);
                }

                outPos += colorDataSize;
            }
            else
            {
                for (var v = 0; v < numVertices; v++)
                {
                    // Xbox packed format: A, R, G, B -> NIF Color4 format: R, G, B, A
                    var a = packedData.VertexColors[v * 4 + 0] / 255.0f;
                    var r = packedData.VertexColors[v * 4 + 1] / 255.0f;
                    var g = packedData.VertexColors[v * 4 + 2] / 255.0f;
                    var b = packedData.VertexColors[v * 4 + 3] / 255.0f;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), r);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), g);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), b);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), a);
                    outPos += 4;
                }
            }
        }
        else if (origHasVertexColors != 0 && packedData.VertexColors == null)
        {
            // Copy and convert existing Color4 values (only if no packed data available)
            for (var v = 0; v < numVertices * 4; v++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                    BinaryPrimitives.ReadSingleBigEndian(input.AsSpan(srcPos)));
                srcPos += 4;
                outPos += 4;
            }
        }

        newSrcPos = srcPos;
        return outPos;
    }

    /// <summary>
    ///     Write UV coordinates from packed data or copy existing.
    /// </summary>
    private static int WriteUVSets(byte[] input, byte[] output, int srcPos, int outPos, ushort numVertices,
        PackedGeometryData packedData, ushort[]? vertexMap, ushort origBsDataFlags, ushort newBsDataFlags,
        out int newSrcPos)
    {
        var origNumUVSets = origBsDataFlags & 1;
        var newNumUVSets = newBsDataFlags & 1;

        if (newNumUVSets != 0 && packedData.UVs != null)
        {
            // Write unpacked UVs (with optional remapping)
            if (vertexMap != null && vertexMap.Length > 0)
            {
                var uvDataSize = numVertices * 8; // 2 floats * 4 bytes
                var basePos = outPos;
                output.AsSpan(basePos, uvDataSize).Clear();

                var packedUvCount = Math.Min(vertexMap.Length, packedData.UVs.Length / 2);
                for (var partitionIdx = 0; partitionIdx < packedUvCount; partitionIdx++)
                {
                    var meshIdx = vertexMap[partitionIdx];
                    if (meshIdx >= numVertices)
                    {
                        continue;
                    }

                    var writePos = basePos + meshIdx * 8;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos),
                        packedData.UVs[partitionIdx * 2 + 0]);
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(writePos + 4),
                        packedData.UVs[partitionIdx * 2 + 1]);
                }

                outPos += uvDataSize;
            }
            else
            {
                for (var v = 0; v < numVertices; v++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.UVs[v * 2 + 0]);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.UVs[v * 2 + 1]);
                    outPos += 4;
                }
            }
        }
        else if (origNumUVSets != 0 && packedData.UVs == null)
        {
            // Copy and convert existing UVs (only if no packed data available)
            for (var v = 0; v < numVertices * 2; v++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                    BinaryPrimitives.ReadSingleBigEndian(input.AsSpan(srcPos)));
                srcPos += 4;
                outPos += 4;
            }
        }

        newSrcPos = srcPos;
        return outPos;
    }

    /// <summary>
    ///     Write NiTriStripsData or NiTriShapeData specific fields.
    /// </summary>
    private static int WriteTriStripSpecificData(byte[] input, byte[] output, int srcPos, int outPos, string blockType,
        ushort[]? triangles)
    {
        if (blockType == "NiTriStripsData")
        {
            // numTriangles (ushort)
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                ReadUInt16BE(input, srcPos));
            srcPos += 2;
            outPos += 2;

            // numStrips (ushort)
            var numStrips = ReadUInt16BE(input, srcPos);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numStrips);
            srcPos += 2;
            outPos += 2;

            // stripLengths[numStrips]
            var stripLengths = new ushort[numStrips];
            for (var i = 0; i < numStrips; i++)
            {
                stripLengths[i] = ReadUInt16BE(input, srcPos);
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), stripLengths[i]);
                srcPos += 2;
                outPos += 2;
            }

            // hasPoints (byte)
            var hasPoints = input[srcPos++];
            output[outPos++] = hasPoints;

            // points[numStrips][stripLengths[i]]
            if (hasPoints != 0)
            {
                for (var i = 0; i < numStrips; i++)
                {
                    for (var j = 0; j < stripLengths[i]; j++)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                            ReadUInt16BE(input, srcPos));
                        srcPos += 2;
                        outPos += 2;
                    }
                }
            }
        }
        else if (blockType == "NiTriShapeData")
        {
            outPos = WriteNiTriShapeDataTriangles(input, output, srcPos, outPos, triangles, out srcPos);
        }

        return outPos;
    }

    /// <summary>
    ///     Write NiTriShapeData triangles section.
    /// </summary>
    private static int WriteNiTriShapeDataTriangles(byte[] input, byte[] output, int srcPos, int outPos,
        ushort[]? triangles, out int newSrcPos)
    {
        // Read source values
        var srcNumTriangles = ReadUInt16BE(input, srcPos);
        srcPos += 2;
        var srcNumTrianglePoints = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos));
        srcPos += 4;
        var srcHasTriangles = input[srcPos++];

        // If we have triangles extracted from NiSkinPartition, use them
        if (triangles != null && triangles.Length >= 3)
        {
            var numTriangles = (ushort)(triangles.Length / 3);
            var numTrianglePoints = (uint)(numTriangles * 3);

            // numTriangles (ushort)
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numTriangles);
            outPos += 2;

            // numTrianglePoints (uint)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numTrianglePoints);
            outPos += 4;

            // hasTriangles (byte) - set to 1 since we're writing triangles
            output[outPos++] = 1;

            // Write triangles
            for (var i = 0; i < triangles.Length; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), triangles[i]);
                outPos += 2;
            }

            // Skip source triangles if any (shouldn't be any since srcHasTriangles=0)
            if (srcHasTriangles != 0)
            {
                srcPos += srcNumTriangles * 6;
            }
        }
        else
        {
            // No extracted triangles - copy source values
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), srcNumTriangles);
            outPos += 2;
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), srcNumTrianglePoints);
            outPos += 4;
            output[outPos++] = srcHasTriangles;

            // Copy source triangles if present
            if (srcHasTriangles != 0)
            {
                for (var i = 0; i < srcNumTriangles * 3; i++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                        ReadUInt16BE(input, srcPos));
                    srcPos += 2;
                    outPos += 2;
                }
            }
        }

        // numMatchGroups (ushort)
        var numMatchGroups = ReadUInt16BE(input, srcPos);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numMatchGroups);
        srcPos += 2;
        outPos += 2;

        // matchGroups[numMatchGroups] - each is a variable-size MatchGroup struct
        for (var i = 0; i < numMatchGroups; i++)
        {
            // numVertices in this match group (ushort)
            var groupNumVertices = ReadUInt16BE(input, srcPos);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), groupNumVertices);
            srcPos += 2;
            outPos += 2;

            // vertexIndices[groupNumVertices] (ushort array)
            for (var j = 0; j < groupNumVertices; j++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                    ReadUInt16BE(input, srcPos));
                srcPos += 2;
                outPos += 2;
            }
        }

        newSrcPos = srcPos;
        return outPos;
    }

    /// <summary>
    ///     Write a Havok collision block with decompressed vertices (HalfVector3 -> Vector3).
    /// </summary>
    private static int WriteExpandedHavokBlock(byte[] input, byte[] output, int outPos, BlockInfo block)
    {
        var srcPos = block.DataOffset;

        // NumTriangles (uint BE -> LE)
        var numTriangles = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numTriangles);
        srcPos += 4;
        outPos += 4;

        // Triangles (TriangleData[NumTriangles]) - each is 8 bytes: 3 ushorts + 1 ushort weldinfo
        for (var i = 0; i < numTriangles; i++)
        {
            // v1 (ushort)
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(srcPos, 2)));
            srcPos += 2;
            outPos += 2;
            // v2 (ushort)
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(srcPos, 2)));
            srcPos += 2;
            outPos += 2;
            // v3 (ushort)
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(srcPos, 2)));
            srcPos += 2;
            outPos += 2;
            // WeldInfo (ushort)
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(srcPos, 2)));
            srcPos += 2;
            outPos += 2;
        }

        // NumVertices (uint BE -> LE)
        var numVertices = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numVertices);
        srcPos += 4;
        outPos += 4;

        // Compressed flag - read as 1, write as 0 (we're decompressing)
        srcPos += 1; // Skip the compressed=1 byte
        output[outPos++] = 0; // Write compressed=0

        // Convert HalfVector3 to Vector3 for each vertex
        for (var i = 0; i < numVertices; i++)
        {
            // Read 3 half-floats (6 bytes) as big-endian
            var hx = BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(srcPos, 2));
            var hy = BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(srcPos + 2, 2));
            var hz = BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(srcPos + 4, 2));
            srcPos += 6;

            // Convert to full floats
            var x = HalfToFloat(hx);
            var y = HalfToFloat(hy);
            var z = HalfToFloat(hz);

            // Write as little-endian floats (12 bytes)
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), x);
            outPos += 4;
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), y);
            outPos += 4;
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), z);
            outPos += 4;
        }

        // NumSubShapes (ushort BE -> LE)
        var numSubShapes = BinaryPrimitives.ReadUInt16BigEndian(input.AsSpan(srcPos, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numSubShapes);
        srcPos += 2;
        outPos += 2;

        // SubShapes (hkSubPartData[NumSubShapes])
        for (var i = 0; i < numSubShapes; i++)
        {
            // HavokFilter (uint)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos, 4)));
            srcPos += 4;
            outPos += 4;
            // NumVertices (uint)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos, 4)));
            srcPos += 4;
            outPos += 4;
            // HavokMaterial - typically 4 bytes (varies by version)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(srcPos, 4)));
            srcPos += 4;
            outPos += 4;
        }

        return outPos;
    }
}
