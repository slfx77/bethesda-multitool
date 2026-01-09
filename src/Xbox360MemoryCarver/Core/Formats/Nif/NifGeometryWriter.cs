using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Writes expanded geometry blocks with unpacked BSPackedAdditionalGeometryData.
/// </summary>
internal static class NifGeometryWriter
{
    /// <summary>
    ///     Write an expanded geometry block with unpacked data.
    /// </summary>
    public static int WriteExpandedBlock(byte[] data, byte[] output, int outPos, BlockInfo block,
        PackedGeometryData packedData)
    {
        var srcPos = block.DataOffset;

        // groupId
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outPos),
            BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(srcPos)));
        srcPos += 4;
        outPos += 4;

        // numVertices
        var numVertices = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numVertices);
        srcPos += 2;
        outPos += 2;

        // keepFlags, compressFlags
        output[outPos++] = data[srcPos++];
        output[outPos++] = data[srcPos++];

        // hasVertices
        var origHasVertices = data[srcPos++];
        var newHasVertices = (byte)(packedData.Positions != null ? 1 : origHasVertices);
        output[outPos++] = newHasVertices;

        outPos = WriteVertices(data, output, ref srcPos, outPos, numVertices, origHasVertices, newHasVertices,
            packedData);

        // bsDataFlags
        var origBsDataFlags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos));
        var newBsDataFlags = origBsDataFlags;
        if (packedData.Tangents != null) newBsDataFlags |= 4096;
        if (packedData.UVs != null) newBsDataFlags |= 1;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), newBsDataFlags);
        srcPos += 2;
        outPos += 2;

        // hasNormals
        var origHasNormals = data[srcPos++];
        var newHasNormals = (byte)(packedData.Normals != null ? 1 : origHasNormals);
        output[outPos++] = newHasNormals;

        outPos = WriteNormalsAndTangents(data, output, ref srcPos, outPos, numVertices,
            origHasNormals, newHasNormals, origBsDataFlags, newBsDataFlags, packedData);

        // center + radius
        outPos = CopyAndSwapFloats(data, output, ref srcPos, outPos, 4);

        // hasVertexColors
        var hasVertexColors = data[srcPos++];
        output[outPos++] = hasVertexColors;
        if (hasVertexColors != 0) outPos = CopyAndSwapFloats(data, output, ref srcPos, outPos, numVertices * 4);

        // UV sets
        outPos = WriteUVSets(data, output, ref srcPos, outPos, numVertices, origBsDataFlags, newBsDataFlags,
            packedData);

        // consistency
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos)));
        srcPos += 2;
        outPos += 2;

        // additionalData ref - set to -1
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outPos), -1);
        srcPos += 4;
        outPos += 4;

        // Copy remaining type-specific data
        if (block.Size - (srcPos - block.DataOffset) > 0)
            outPos = CopyTriStripSpecificData(data, output, srcPos, outPos, block.TypeName);

        return outPos;
    }

    private static int WriteVertices(byte[] data, byte[] output, ref int srcPos, int outPos,
        ushort numVertices, byte origHasVertices, byte newHasVertices, PackedGeometryData packedData)
    {
        if (newHasVertices != 0 && packedData.Positions != null && origHasVertices == 0)
            for (var v = 0; v < numVertices; v++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Positions[v * 3 + 0]);
                outPos += 4;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Positions[v * 3 + 1]);
                outPos += 4;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.Positions[v * 3 + 2]);
                outPos += 4;
            }
        else if (origHasVertices != 0) outPos = CopyAndSwapFloats(data, output, ref srcPos, outPos, numVertices * 3);

        return outPos;
    }

    private static int WriteNormalsAndTangents(byte[] data, byte[] output, ref int srcPos, int outPos,
        ushort numVertices, byte origHasNormals, byte newHasNormals,
        ushort origBsDataFlags, ushort newBsDataFlags, PackedGeometryData packedData)
    {
        if (newHasNormals != 0 && packedData.Normals != null && origHasNormals == 0)
        {
            outPos = WriteVector3Array(output, outPos, packedData.Normals, numVertices);

            if ((newBsDataFlags & 4096) != 0 && packedData.Tangents != null)
                outPos = WriteVector3Array(output, outPos, packedData.Tangents, numVertices);

            if ((newBsDataFlags & 4096) != 0 && packedData.Bitangents != null)
                outPos = WriteVector3Array(output, outPos, packedData.Bitangents, numVertices);
        }
        else if (origHasNormals != 0)
        {
            outPos = CopyAndSwapFloats(data, output, ref srcPos, outPos, numVertices * 3);

            if ((origBsDataFlags & 4096) != 0)
                outPos = CopyAndSwapFloats(data, output, ref srcPos, outPos, numVertices * 6);
        }

        return outPos;
    }

    private static int WriteUVSets(byte[] data, byte[] output, ref int srcPos, int outPos,
        ushort numVertices, ushort origBsDataFlags, ushort newBsDataFlags, PackedGeometryData packedData)
    {
        var origNumUVSets = origBsDataFlags & 1;
        var newNumUVSets = newBsDataFlags & 1;

        if (newNumUVSets != 0 && origNumUVSets == 0 && packedData.UVs != null)
            for (var v = 0; v < numVertices; v++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.UVs[v * 2 + 0]);
                outPos += 4;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.UVs[v * 2 + 1]);
                outPos += 4;
            }
        else if (origNumUVSets != 0) outPos = CopyAndSwapFloats(data, output, ref srcPos, outPos, numVertices * 2);

        return outPos;
    }

    private static int WriteVector3Array(byte[] output, int outPos, float[] data, int count)
    {
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), data[i * 3 + 0]);
            outPos += 4;
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), data[i * 3 + 1]);
            outPos += 4;
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), data[i * 3 + 2]);
            outPos += 4;
        }

        return outPos;
    }

    private static int CopyAndSwapFloats(byte[] data, byte[] output, ref int srcPos, int outPos, int count)
    {
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(srcPos)));
            srcPos += 4;
            outPos += 4;
        }

        return outPos;
    }

    private static int CopyTriStripSpecificData(byte[] data, byte[] output, int srcPos, int outPos, string blockType)
    {
        if (blockType == "NiTriStripsData")
        {
            // numTriangles
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos)));
            srcPos += 2;
            outPos += 2;

            // numStrips
            var numStrips = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos));
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numStrips);
            srcPos += 2;
            outPos += 2;

            // stripLengths
            var stripLengths = new ushort[numStrips];
            for (var i = 0; i < numStrips; i++)
            {
                stripLengths[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos));
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), stripLengths[i]);
                srcPos += 2;
                outPos += 2;
            }

            // hasPoints
            var hasPoints = data[srcPos++];
            output[outPos++] = hasPoints;

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
            // numTriangles
            var numTriangles = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos));
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numTriangles);
            srcPos += 2;
            outPos += 2;

            // numTrianglePoints
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos),
                BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(srcPos)));
            srcPos += 4;
            outPos += 4;

            // hasTriangles
            var hasTriangles = data[srcPos++];
            output[outPos++] = hasTriangles;

            if (hasTriangles != 0)
                for (var i = 0; i < numTriangles * 3; i++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos)));
                    srcPos += 2;
                    outPos += 2;
                }
        }

        return outPos;
    }
}
