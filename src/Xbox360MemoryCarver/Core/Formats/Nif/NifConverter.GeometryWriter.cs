// NIF converter - Geometry block expansion and writing

using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Groups geometry flags for conversion methods.
/// </summary>
internal readonly record struct GeometryFlags(
    byte OrigHasNormals,
    byte NewHasNormals,
    ushort OrigBsDataFlags,
    ushort NewBsDataFlags);

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

        // Create context for helper methods
        var ctx = new GeometryWriteContext(input, output, numVertices, packedData, vertexMap, vertexMap != null);

        // Write vertices
        outPos = WriteVertices(ctx, srcPos, outPos, origHasVertices, newHasVertices, out srcPos);

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
        var flags = new GeometryFlags(origHasNormals, newHasNormals, origBsDataFlags, newBsDataFlags);
        outPos = WriteNormalsAndTangents(ctx, srcPos, outPos, flags, out srcPos);

        // center (Vector3) + radius (float) = 16 bytes
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadSingleBigEndian(input.AsSpan(srcPos)));
            srcPos += 4;
            outPos += 4;
        }

        // Write vertex colors
        outPos = WriteVertexColors(ctx, srcPos, outPos, out srcPos);

        // Write UV sets
        outPos = WriteUVSets(ctx, srcPos, outPos, origBsDataFlags, newBsDataFlags, out srcPos);

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
    private static int WriteVertices(GeometryWriteContext ctx, int srcPos, int outPos,
        byte origHasVertices, byte newHasVertices, out int newSrcPos)
    {
        // Always prefer packed data when available - Xbox 360 files set hasVertices=1
        // even when actual vertex data is in BSPackedAdditionalGeometryData
        if (newHasVertices != 0 && ctx.PackedData.Positions != null)
        {
            outPos = WriteVerticesFromPackedData(ctx, outPos);
        }
        else if (origHasVertices != 0 && ctx.PackedData.Positions == null)
        {
            outPos = CopyAndConvertVertices(ctx.Input, ctx.Output, srcPos, outPos, ctx.NumVertices, out srcPos);
            newSrcPos = srcPos;
            return outPos;
        }

        newSrcPos = srcPos;
        return outPos;
    }

    private static int WriteVerticesFromPackedData(GeometryWriteContext ctx, int outPos)
    {
        if (ctx.VertexMap is { Length: > 0 })
        {
            return WriteRemappedVertices(ctx, outPos);
        }

        return WriteSequentialVertices(ctx.Output, outPos, ctx.NumVertices, ctx.PackedData.Positions!);
    }

    private static int WriteRemappedVertices(GeometryWriteContext ctx, int outPos)
    {
        var vertexDataSize = ctx.NumVertices * 12;
        var basePos = outPos;
        ctx.Output.AsSpan(basePos, vertexDataSize).Clear();

        var packedVertexCount = Math.Min(ctx.VertexMap!.Length, ctx.PackedData.Positions!.Length / 3);
        for (var partitionIdx = 0; partitionIdx < packedVertexCount; partitionIdx++)
        {
            var meshIdx = ctx.VertexMap[partitionIdx];
            if (meshIdx >= ctx.NumVertices)
            {
                continue;
            }

            var writePos = basePos + meshIdx * 12;
            BinaryPrimitives.WriteSingleLittleEndian(ctx.Output.AsSpan(writePos),
                ctx.PackedData.Positions[partitionIdx * 3 + 0]);
            BinaryPrimitives.WriteSingleLittleEndian(ctx.Output.AsSpan(writePos + 4),
                ctx.PackedData.Positions[partitionIdx * 3 + 1]);
            BinaryPrimitives.WriteSingleLittleEndian(ctx.Output.AsSpan(writePos + 8),
                ctx.PackedData.Positions[partitionIdx * 3 + 2]);
        }

        return outPos + vertexDataSize;
    }

    private static int WriteSequentialVertices(byte[] output, int outPos, ushort numVertices, float[] positions)
    {
        var packedVertexCount = Math.Min(numVertices, positions.Length / 3);
        for (var v = 0; v < packedVertexCount; v++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), positions[v * 3 + 0]);
            outPos += 4;
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), positions[v * 3 + 1]);
            outPos += 4;
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), positions[v * 3 + 2]);
            outPos += 4;
        }

        // Pad remaining vertices with zeros if packed data has fewer vertices
        var remaining = numVertices - packedVertexCount;
        if (remaining > 0)
        {
            output.AsSpan(outPos, remaining * 12).Clear();
            outPos += remaining * 12;
        }

        return outPos;
    }

    private static int CopyAndConvertVertices(byte[] input, byte[] output, int srcPos, int outPos,
        ushort numVertices, out int newSrcPos)
    {
        for (var v = 0; v < numVertices * 3; v++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadSingleBigEndian(input.AsSpan(srcPos)));
            srcPos += 4;
            outPos += 4;
        }

        newSrcPos = srcPos;
        return outPos;
    }

    /// <summary>
    ///     Write normals, tangents, and bitangents from packed data or copy existing.
    /// </summary>
    private static int WriteNormalsAndTangents(GeometryWriteContext ctx, int srcPos, int outPos,
        GeometryFlags flags, out int newSrcPos)
    {
        // Always prefer packed data when available - Xbox 360 files set hasNormals=1
        // even when actual normal data is in BSPackedAdditionalGeometryData
        if (flags.NewHasNormals != 0 && ctx.PackedData.Normals != null)
        {
            outPos = WriteNormalsFromPackedData(ctx, outPos, flags.NewBsDataFlags);
        }
        else if (flags.OrigHasNormals != 0 && ctx.PackedData.Normals == null)
        {
            outPos = CopyAndConvertNormals(ctx.Input, ctx.Output, srcPos, outPos, ctx.NumVertices,
                flags.OrigBsDataFlags, out srcPos);
            newSrcPos = srcPos;
            return outPos;
        }

        newSrcPos = srcPos;
        return outPos;
    }

    private static int WriteNormalsFromPackedData(GeometryWriteContext ctx, int outPos, ushort newBsDataFlags)
    {
        // Write unpacked normals (with optional vertex map remapping for skinned meshes)
        outPos = WriteVec3Array(ctx.Output, outPos, ctx.NumVertices, ctx.PackedData.Normals!, ctx.VertexMap);

        // Write tangents if available (with optional remapping)
        if ((newBsDataFlags & 4096) != 0 && ctx.PackedData.Tangents != null)
        {
            outPos = WriteVec3Array(ctx.Output, outPos, ctx.NumVertices, ctx.PackedData.Tangents, ctx.VertexMap);

            // Write bitangents if available (with optional remapping)
            if (ctx.PackedData.Bitangents != null)
            {
                outPos = WriteVec3Array(ctx.Output, outPos, ctx.NumVertices, ctx.PackedData.Bitangents, ctx.VertexMap);
            }
        }

        return outPos;
    }

    private static int CopyAndConvertNormals(byte[] input, byte[] output, int srcPos, int outPos,
        ushort numVertices, ushort origBsDataFlags, out int newSrcPos)
    {
        // Copy and convert existing normals
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

        newSrcPos = srcPos;
        return outPos;
    }

    /// <summary>
    ///     Write a vec3 array with optional vertex map remapping.
    /// </summary>
    private static int WriteVec3Array(byte[] output, int outPos, ushort numVertices, float[] data, ushort[]? vertexMap)
    {
        if (vertexMap is { Length: > 0 })
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

    /// <summary>
    ///     Write vertex colors from packed data or copy existing.
    /// </summary>
    private static int WriteVertexColors(GeometryWriteContext ctx, int srcPos, int outPos, out int newSrcPos)
    {
        // hasVertexColors (byte) - set to 1 if we have colors from packed data
        // NOTE: For skinned meshes, ubyte4 stream is bone indices, NOT vertex colors!
        var origHasVertexColors = ctx.Input[srcPos++];
        var newHasVertexColors =
            (byte)(ctx.PackedData.VertexColors != null && !ctx.IsSkinned ? 1 : origHasVertexColors);
        ctx.Output[outPos++] = newHasVertexColors;

        // NIF stores vertex colors as Color4 (4 floats, 16 bytes per vertex) in RGBA order
        // Xbox 360 packed data stores them as ByteColor4 (4 bytes per vertex) in ARGB order
        if (newHasVertexColors != 0 && ctx.PackedData.VertexColors != null && !ctx.IsSkinned)
        {
            outPos = WriteVertexColorsFromPackedData(ctx, outPos);
        }
        else if (origHasVertexColors != 0 && ctx.PackedData.VertexColors == null)
        {
            outPos = CopyAndConvertVertexColors(ctx.Input, ctx.Output, srcPos, outPos, ctx.NumVertices, out srcPos);
            newSrcPos = srcPos;
            return outPos;
        }

        newSrcPos = srcPos;
        return outPos;
    }

    private static int WriteVertexColorsFromPackedData(GeometryWriteContext ctx, int outPos)
    {
        if (ctx.VertexMap is { Length: > 0 })
        {
            return WriteRemappedVertexColors(ctx, outPos);
        }

        return WriteSequentialVertexColors(ctx.Output, outPos, ctx.NumVertices, ctx.PackedData.VertexColors!);
    }

    private static int WriteRemappedVertexColors(GeometryWriteContext ctx, int outPos)
    {
        var colorDataSize = ctx.NumVertices * 16;
        var basePos = outPos;
        ctx.Output.AsSpan(basePos, colorDataSize).Clear();

        var packedColorCount = Math.Min(ctx.VertexMap!.Length, ctx.PackedData.VertexColors!.Length / 4);
        for (var partitionIdx = 0; partitionIdx < packedColorCount; partitionIdx++)
        {
            var meshIdx = ctx.VertexMap[partitionIdx];
            if (meshIdx >= ctx.NumVertices)
            {
                continue;
            }

            // Xbox packed format: A, R, G, B -> NIF Color4 format: R, G, B, A
            var (r, g, b, a) = ExtractArgbAsRgba(ctx.PackedData.VertexColors, partitionIdx);

            var writePos = basePos + meshIdx * 16;
            BinaryPrimitives.WriteSingleLittleEndian(ctx.Output.AsSpan(writePos), r);
            BinaryPrimitives.WriteSingleLittleEndian(ctx.Output.AsSpan(writePos + 4), g);
            BinaryPrimitives.WriteSingleLittleEndian(ctx.Output.AsSpan(writePos + 8), b);
            BinaryPrimitives.WriteSingleLittleEndian(ctx.Output.AsSpan(writePos + 12), a);
        }

        return outPos + colorDataSize;
    }

    private static int WriteSequentialVertexColors(byte[] output, int outPos, ushort numVertices, byte[] colors)
    {
        for (var v = 0; v < numVertices; v++)
        {
            // Xbox packed format: A, R, G, B -> NIF Color4 format: R, G, B, A
            var (r, g, b, a) = ExtractArgbAsRgba(colors, v);
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), r);
            outPos += 4;
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), g);
            outPos += 4;
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), b);
            outPos += 4;
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), a);
            outPos += 4;
        }

        return outPos;
    }

    private static (float r, float g, float b, float a) ExtractArgbAsRgba(byte[] colors, int index)
    {
        var a = colors[index * 4 + 0] / 255.0f;
        var r = colors[index * 4 + 1] / 255.0f;
        var g = colors[index * 4 + 2] / 255.0f;
        var b = colors[index * 4 + 3] / 255.0f;
        return (r, g, b, a);
    }

    private static int CopyAndConvertVertexColors(byte[] input, byte[] output, int srcPos, int outPos,
        ushort numVertices, out int newSrcPos)
    {
        for (var v = 0; v < numVertices * 4; v++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadSingleBigEndian(input.AsSpan(srcPos)));
            srcPos += 4;
            outPos += 4;
        }

        newSrcPos = srcPos;
        return outPos;
    }

    /// <summary>
    ///     Write UV coordinates from packed data or copy existing.
    /// </summary>
    private static int WriteUVSets(GeometryWriteContext ctx, int srcPos, int outPos,
        ushort origBsDataFlags, ushort newBsDataFlags, out int newSrcPos)
    {
        var origNumUVSets = origBsDataFlags & 1;
        var newNumUVSets = newBsDataFlags & 1;

        if (newNumUVSets != 0 && ctx.PackedData.UVs != null)
        {
            outPos = WriteUVsFromPackedData(ctx, outPos);
        }
        else if (origNumUVSets != 0 && ctx.PackedData.UVs == null)
        {
            outPos = CopyAndConvertUVs(ctx.Input, ctx.Output, srcPos, outPos, ctx.NumVertices, out srcPos);
            newSrcPos = srcPos;
            return outPos;
        }

        newSrcPos = srcPos;
        return outPos;
    }

    private static int WriteUVsFromPackedData(GeometryWriteContext ctx, int outPos)
    {
        if (ctx.VertexMap is { Length: > 0 })
        {
            return WriteRemappedUVs(ctx, outPos);
        }

        return WriteSequentialUVs(ctx.Output, outPos, ctx.NumVertices, ctx.PackedData.UVs!);
    }

    private static int WriteRemappedUVs(GeometryWriteContext ctx, int outPos)
    {
        var uvDataSize = ctx.NumVertices * 8;
        var basePos = outPos;
        ctx.Output.AsSpan(basePos, uvDataSize).Clear();

        var packedUvCount = Math.Min(ctx.VertexMap!.Length, ctx.PackedData.UVs!.Length / 2);
        for (var partitionIdx = 0; partitionIdx < packedUvCount; partitionIdx++)
        {
            var meshIdx = ctx.VertexMap[partitionIdx];
            if (meshIdx >= ctx.NumVertices)
            {
                continue;
            }

            var writePos = basePos + meshIdx * 8;
            BinaryPrimitives.WriteSingleLittleEndian(ctx.Output.AsSpan(writePos),
                ctx.PackedData.UVs[partitionIdx * 2 + 0]);
            BinaryPrimitives.WriteSingleLittleEndian(ctx.Output.AsSpan(writePos + 4),
                ctx.PackedData.UVs[partitionIdx * 2 + 1]);
        }

        return outPos + uvDataSize;
    }

    private static int WriteSequentialUVs(byte[] output, int outPos, ushort numVertices, float[] uvs)
    {
        for (var v = 0; v < numVertices; v++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), uvs[v * 2 + 0]);
            outPos += 4;
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), uvs[v * 2 + 1]);
            outPos += 4;
        }

        return outPos;
    }

    private static int CopyAndConvertUVs(byte[] input, byte[] output, int srcPos, int outPos,
        ushort numVertices, out int newSrcPos)
    {
        for (var v = 0; v < numVertices * 2; v++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadSingleBigEndian(input.AsSpan(srcPos)));
            srcPos += 4;
            outPos += 4;
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
                for (var j = 0; j < stripLengths[i]; j++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                        ReadUInt16BE(input, srcPos));
                    srcPos += 2;
                    outPos += 2;
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
        if (triangles is { Length: >= 3 })
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
            foreach (var triangle in triangles)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), triangle);
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

    /// <summary>
    ///     Context for geometry writing operations, reducing parameter count.
    /// </summary>
    private readonly record struct GeometryWriteContext(
        byte[] Input,
        byte[] Output,
        ushort NumVertices,
        PackedGeometryData PackedData,
        ushort[]? VertexMap,
        bool IsSkinned);
}
