using System.Buffers.Binary;
using static Xbox360MemoryCarver.Core.Formats.Nif.NifEndianUtils;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Endian conversion for NiGeometryData blocks (NiTriStripsData, NiTriShapeData).
/// </summary>
internal static class NifGeometryDataConverter
{
    public static void ConvertNiTriStripsData(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;
        pos = ConvertGeometryDataCommon(buf, pos, end, blockRemap);
        if (pos < 0 || pos + 2 > end) return;

        SwapUInt16InPlace(buf, pos);
        var numStrips = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
        pos += 2;

        var stripLengths = new ushort[numStrips];
        for (var i = 0; i < numStrips && pos + 2 <= end; i++)
        {
            SwapUInt16InPlace(buf, pos);
            stripLengths[i] = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
            pos += 2;
        }

        if (pos + 1 > end) return;

        var hasPoints = buf[pos++];

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

    public static void ConvertNiTriShapeData(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;
        pos = ConvertGeometryDataCommon(buf, pos, end, blockRemap);
        if (pos < 0 || pos + 2 > end) return;

        SwapUInt16InPlace(buf, pos);
        var numTris = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
        pos += 2;

        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos);
        pos += 4;

        if (pos + 1 > end) return;

        var hasTris = buf[pos++];

        if (hasTris != 0)
            for (var i = 0; i < numTris * 3 && pos + 2 <= end; i++)
            {
                SwapUInt16InPlace(buf, pos);
                pos += 2;
            }

        ConvertMatchGroups(buf, ref pos, end);
    }

    private static void ConvertMatchGroups(byte[] buf, ref int pos, int end)
    {
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

    /// <summary>
    ///     Convert common NiGeometryData fields. Returns position after additionalData ref.
    /// </summary>
    private static int ConvertGeometryDataCommon(byte[] buf, int pos, int end, int[] blockRemap)
    {
        // groupId
        if (pos + 4 > end) return -1;

        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // numVertices
        if (pos + 2 > end) return -1;

        SwapUInt16InPlace(buf, pos);
        var numVerts = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
        pos += 2;

        // keepFlags, compressFlags
        if (pos + 2 > end) return -1;

        pos += 2;

        // hasVertices
        if (pos + 1 > end) return -1;

        var hasVerts = buf[pos++];

        if (hasVerts != 0)
            for (var i = 0; i < numVerts * 3 && pos + 4 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos);
                pos += 4;
            }

        // bsDataFlags
        if (pos + 2 > end) return -1;

        SwapUInt16InPlace(buf, pos);
        var dataFlags = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
        pos += 2;

        // materialCRC
        if (pos + 4 > end) return -1;

        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // hasNormals
        if (pos + 1 > end) return -1;

        var hasNormals = buf[pos++];

        if (hasNormals != 0) pos = ConvertNormalsAndTangents(buf, pos, end, numVerts, dataFlags);

        // center + radius
        for (var i = 0; i < 4 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // hasVertexColors
        if (pos + 1 > end) return -1;

        var hasColors = buf[pos++];
        if (hasColors != 0) pos += numVerts * 4;

        // UV sets
        pos = ConvertUVSets(buf, pos, end, numVerts, dataFlags);

        // consistencyFlags
        if (pos + 2 > end) return -1;

        SwapUInt16InPlace(buf, pos);
        pos += 2;

        // additionalData ref
        if (pos + 4 > end) return -1;

        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        return pos;
    }

    private static int ConvertNormalsAndTangents(byte[] buf, int pos, int end, int numVerts, int dataFlags)
    {
        // normals
        for (var i = 0; i < numVerts * 3 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // tangents + bitangents
        if ((dataFlags & 0x1000) != 0)
            for (var i = 0; i < numVerts * 6 && pos + 4 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos);
                pos += 4;
            }

        return pos;
    }

    private static int ConvertUVSets(byte[] buf, int pos, int end, int numVerts, int dataFlags)
    {
        var numUVSets = dataFlags & 0x3F;
        for (var uvSet = 0; uvSet < numUVSets && pos < end; uvSet++)
        {
            for (var i = 0; i < numVerts * 2 && pos + 4 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos);
                pos += 4;
            }
        }

        return pos;
    }
}
