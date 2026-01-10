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
    /// <param name="data">Source data</param>
    /// <param name="output">Output buffer</param>
    /// <param name="outPos">Current output position</param>
    /// <param name="block">Block info</param>
    /// <param name="packedData">Extracted packed geometry</param>
    /// <param name="vertexMap">VertexMap from NiSkinPartition (null for non-skinned meshes)</param>
    /// <param name="verbose">Enable verbose logging</param>
    public static int WriteExpandedBlock(byte[] data, byte[] output, int outPos, BlockInfo block,
        PackedGeometryData packedData, ushort[]? vertexMap = null, bool verbose = false)
    {
        var startOutPos = outPos;
        var srcPos = block.DataOffset;

        if (verbose) Console.WriteLine($"    Starting WriteExpandedBlock: block {block.Index}, outPos=0x{outPos:X4}, srcPos=0x{srcPos:X4}, blockSize={block.Size}");

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

        if (verbose) Console.WriteLine($"    numVertices={numVertices}");

        // keepFlags, compressFlags
        output[outPos++] = data[srcPos++];
        output[outPos++] = data[srcPos++];

        // hasVertices
        var origHasVertices = data[srcPos++];
        var newHasVertices = (byte)(packedData.Positions != null ? 1 : origHasVertices);
        output[outPos++] = newHasVertices;

        if (verbose)
        {
            Console.WriteLine($"    origHasVertices={origHasVertices}, newHasVertices={newHasVertices}");
            if (vertexMap != null)
                Console.WriteLine($"    Using VertexMap for remapping ({vertexMap.Length} entries)");
        }

        var beforeVerts = outPos;
        outPos = WriteVertices(data, output, ref srcPos, outPos, numVertices, origHasVertices, newHasVertices,
            packedData, vertexMap);
        if (verbose) Console.WriteLine($"    WriteVertices: wrote {outPos - beforeVerts} bytes");

        // bsDataFlags
        var origBsDataFlags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos));
        var newBsDataFlags = origBsDataFlags;
        if (packedData.Tangents != null) newBsDataFlags |= 4096;
        if (packedData.UVs != null) newBsDataFlags |= 1;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), newBsDataFlags);
        srcPos += 2;
        outPos += 2;

        if (verbose) Console.WriteLine($"    origBsDataFlags=0x{origBsDataFlags:X4}, newBsDataFlags=0x{newBsDataFlags:X4}");

        // hasNormals
        var origHasNormals = data[srcPos++];
        var newHasNormals = (byte)(packedData.Normals != null ? 1 : origHasNormals);
        output[outPos++] = newHasNormals;

        if (verbose) Console.WriteLine($"    origHasNormals={origHasNormals}, newHasNormals={newHasNormals}");

        var beforeNormals = outPos;
        outPos = WriteNormalsAndTangents(data, output, ref srcPos, outPos, numVertices,
            origHasNormals, newHasNormals, origBsDataFlags, newBsDataFlags, packedData, vertexMap);
        if (verbose) Console.WriteLine($"    WriteNormalsAndTangents: wrote {outPos - beforeNormals} bytes");

        // center + radius
        outPos = CopyAndSwapFloats(data, output, ref srcPos, outPos, 4);

        // hasVertexColors
        var hasVertexColors = data[srcPos++];
        output[outPos++] = hasVertexColors;
        if (hasVertexColors != 0) outPos = CopyAndSwapFloats(data, output, ref srcPos, outPos, numVertices * 4);

        if (verbose) Console.WriteLine($"    hasVertexColors={hasVertexColors}");

        // UV sets
        var beforeUVs = outPos;
        outPos = WriteUVSets(data, output, ref srcPos, outPos, numVertices, origBsDataFlags, newBsDataFlags,
            packedData, vertexMap);
        if (verbose) Console.WriteLine($"    WriteUVSets: wrote {outPos - beforeUVs} bytes");

        // consistency
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos)));
        srcPos += 2;
        outPos += 2;

        // additionalData ref - set to -1
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(outPos), -1);
        srcPos += 4;
        outPos += 4;

        if (verbose) Console.WriteLine($"    srcPos after base fields: 0x{srcPos:X4}, consumed {srcPos - block.DataOffset} of {block.Size}");

        // Copy remaining type-specific data
        if (block.Size - (srcPos - block.DataOffset) > 0)
        {
            var beforeTriStrip = outPos;
            outPos = CopyTriStripSpecificData(data, output, srcPos, outPos, block.TypeName, verbose);
            if (verbose) Console.WriteLine($"    CopyTriStripSpecificData: wrote {outPos - beforeTriStrip} bytes");
        }

        if (verbose)
            Console.WriteLine($"  WriteExpandedBlock: block {block.Index} ({block.TypeName}), wrote {outPos - startOutPos} bytes");

        return outPos;
    }

    private static int WriteVertices(byte[] data, byte[] output, ref int srcPos, int outPos,
        ushort numVertices, byte origHasVertices, byte newHasVertices, PackedGeometryData packedData,
        ushort[]? vertexMap)
    {
        if (newHasVertices != 0 && packedData.Positions != null && origHasVertices == 0)
        {
            if (vertexMap != null && vertexMap.Length == numVertices)
            {
                // Create remapped array: output[vertexMap[i]] = packed[i]
                var remapped = new float[numVertices * 3];
                for (var i = 0; i < numVertices; i++)
                {
                    var meshIdx = vertexMap[i];
                    if (meshIdx < numVertices)
                    {
                        remapped[meshIdx * 3 + 0] = packedData.Positions[i * 3 + 0];
                        remapped[meshIdx * 3 + 1] = packedData.Positions[i * 3 + 1];
                        remapped[meshIdx * 3 + 2] = packedData.Positions[i * 3 + 2];
                    }
                }

                // Write remapped data
                for (var v = 0; v < numVertices; v++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), remapped[v * 3 + 0]);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), remapped[v * 3 + 1]);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), remapped[v * 3 + 2]);
                    outPos += 4;
                }
            }
            else
            {
                // No remapping - write in order
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
        }
        else if (origHasVertices != 0)
        {
            outPos = CopyAndSwapFloats(data, output, ref srcPos, outPos, numVertices * 3);
        }

        return outPos;
    }

    private static int WriteNormalsAndTangents(byte[] data, byte[] output, ref int srcPos, int outPos,
        ushort numVertices, byte origHasNormals, byte newHasNormals,
        ushort origBsDataFlags, ushort newBsDataFlags, PackedGeometryData packedData, ushort[]? vertexMap)
    {
        if (newHasNormals != 0 && packedData.Normals != null && origHasNormals == 0)
        {
            outPos = WriteVector3ArrayRemapped(output, outPos, packedData.Normals, numVertices, vertexMap);

            if ((newBsDataFlags & 4096) != 0 && packedData.Tangents != null)
                outPos = WriteVector3ArrayRemapped(output, outPos, packedData.Tangents, numVertices, vertexMap);

            if ((newBsDataFlags & 4096) != 0 && packedData.Bitangents != null)
                outPos = WriteVector3ArrayRemapped(output, outPos, packedData.Bitangents, numVertices, vertexMap);
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
        ushort numVertices, ushort origBsDataFlags, ushort newBsDataFlags, PackedGeometryData packedData,
        ushort[]? vertexMap)
    {
        var origNumUVSets = origBsDataFlags & 1;
        var newNumUVSets = newBsDataFlags & 1;

        if (newNumUVSets != 0 && origNumUVSets == 0 && packedData.UVs != null)
        {
            if (vertexMap != null && vertexMap.Length == numVertices)
            {
                // Create remapped array
                var remapped = new float[numVertices * 2];
                for (var i = 0; i < numVertices; i++)
                {
                    var meshIdx = vertexMap[i];
                    if (meshIdx < numVertices)
                    {
                        remapped[meshIdx * 2 + 0] = packedData.UVs[i * 2 + 0];
                        remapped[meshIdx * 2 + 1] = packedData.UVs[i * 2 + 1];
                    }
                }

                // Write remapped data
                for (var v = 0; v < numVertices; v++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), remapped[v * 2 + 0]);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), remapped[v * 2 + 1]);
                    outPos += 4;
                }
            }
            else
            {
                // No remapping
                for (var v = 0; v < numVertices; v++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.UVs[v * 2 + 0]);
                    outPos += 4;
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), packedData.UVs[v * 2 + 1]);
                    outPos += 4;
                }
            }
        }
        else if (origNumUVSets != 0)
        {
            outPos = CopyAndSwapFloats(data, output, ref srcPos, outPos, numVertices * 2);
        }

        return outPos;
    }

    private static int WriteVector3ArrayRemapped(byte[] output, int outPos, float[] sourceData, int count,
        ushort[]? vertexMap)
    {
        if (vertexMap != null && vertexMap.Length == count)
        {
            // Create remapped array: remapped[vertexMap[i]] = sourceData[i]
            var remapped = new float[count * 3];
            for (var i = 0; i < count; i++)
            {
                var meshIdx = vertexMap[i];
                if (meshIdx < count)
                {
                    remapped[meshIdx * 3 + 0] = sourceData[i * 3 + 0];
                    remapped[meshIdx * 3 + 1] = sourceData[i * 3 + 1];
                    remapped[meshIdx * 3 + 2] = sourceData[i * 3 + 2];
                }
            }

            // Write remapped data
            for (var i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), remapped[i * 3 + 0]);
                outPos += 4;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), remapped[i * 3 + 1]);
                outPos += 4;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), remapped[i * 3 + 2]);
                outPos += 4;
            }
        }
        else
        {
            // No remapping - write in order
            for (var i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), sourceData[i * 3 + 0]);
                outPos += 4;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), sourceData[i * 3 + 1]);
                outPos += 4;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), sourceData[i * 3 + 2]);
                outPos += 4;
            }
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

    private static int CopyTriStripSpecificData(byte[] data, byte[] output, int srcPos, int outPos, string blockType, bool verbose)
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
            if (verbose) Console.WriteLine($"      numTriangles={numTriangles}");
            srcPos += 2;
            outPos += 2;

            // numTrianglePoints
            var numTrianglePoints = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(srcPos));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), numTrianglePoints);
            if (verbose) Console.WriteLine($"      numTrianglePoints={numTrianglePoints}");
            srcPos += 4;
            outPos += 4;

            // hasTriangles
            var hasTriangles = data[srcPos++];
            output[outPos++] = hasTriangles;
            if (verbose) Console.WriteLine($"      hasTriangles={hasTriangles}");

            if (hasTriangles != 0)
                for (var i = 0; i < numTriangles * 3; i++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos)));
                    srcPos += 2;
                    outPos += 2;
                }

            // NumMatchGroups and MatchGroups data
            var numMatchGroups = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos));
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numMatchGroups);
            if (verbose) Console.WriteLine($"      numMatchGroups={numMatchGroups}");
            srcPos += 2;
            outPos += 2;

            // Each MatchGroup has: numMatches (ushort), then numMatches vertex indices (ushort each)
            for (var g = 0; g < numMatchGroups; g++)
            {
                var numMatches = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos));
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), numMatches);
                srcPos += 2;
                outPos += 2;

                for (var m = 0; m < numMatches; m++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos),
                        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(srcPos)));
                    srcPos += 2;
                    outPos += 2;
                }
            }
        }

        return outPos;
    }
}
