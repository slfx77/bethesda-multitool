// NIF converter - Reference finding utilities
// Methods for finding references (Skin Instance, Data) in geometry blocks

using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

internal sealed partial class NifConverter
{
    /// <summary>
    ///     Parse hkPackedNiTriStripsData to check if it has compressed vertices.
    /// </summary>
    private static HavokBlockExpansion? ParseHavokBlock(byte[] data, BlockInfo block, bool isBigEndian)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (pos + 4 > end) return null;
        var numTriangles = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4;

        // Skip triangles (TriangleData = Triangle + WeldInfo = 6 + 2 = 8 bytes each)
        pos += (int)numTriangles * 8;

        if (pos + 4 > end) return null;
        var numVertices = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4;

        if (pos + 1 > end) return null;
        var compressed = data[pos] != 0;
        pos += 1;

        // Only need expansion if compressed
        if (!compressed) return null;

        // Calculate sizes
        var compressedVertexSize = (int)numVertices * 6;
        var vertexDataOffset = pos;
        pos += compressedVertexSize;

        if (pos + 2 > end) return null;
        var numSubShapes = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));

        // Size increase: expand from HalfVector3 (6 bytes) to Vector3 (12 bytes) per vertex
        var sizeIncrease = (int)numVertices * 6;

        return new HavokBlockExpansion
        {
            BlockIndex = block.Index,
            NumTriangles = (int)numTriangles,
            NumVertices = (int)numVertices,
            NumSubShapes = numSubShapes,
            OriginalSize = block.Size,
            NewSize = block.Size + sizeIncrease,
            VertexDataOffset = vertexDataOffset
        };
    }

    /// <summary>
    ///     Find the Skin Instance ref in a NiTriShape/NiTriStrips block.
    /// </summary>
    private static int FindSkinInstanceRef(byte[] data, BlockInfo block, NifInfo info)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        var isBE = info.IsBigEndian;

        // Skip name (StringIndex = 4 bytes)
        pos += 4;
        if (pos > end) return -1;

        // Skip extra data (uint count + int[] refs)
        if (pos + 4 > end) return -1;
        var extraDataCount = isBE
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));

        if (extraDataCount > 100) return -1;
        pos += 4 + (int)extraDataCount * 4;
        if (pos > end) return -1;

        // Skip controller ref
        pos += 4;
        if (pos > end) return -1;

        // Skip uint flags (4 bytes for version 20.2.0.7 with user version 11)
        pos += 4;

        // Skip Transform (Translation 12 + Rotation 36 + Scale 4 = 52 bytes)
        pos += 52;

        // Skip properties (uint count + int[] refs)
        if (pos + 4 > end) return -1;
        var numProperties = isBE
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));

        if (numProperties > 100) return -1;
        pos += 4 + (int)numProperties * 4;
        if (pos > end) return -1;

        // Skip collision object ref
        pos += 4;
        if (pos > end) return -1;

        // Skip data ref
        if (pos + 4 > end) return -1;
        pos += 4;

        // Skin Instance ref
        if (pos + 4 > end) return -1;
        var skinInstanceRef = isBE
            ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos, 4));

        return skinInstanceRef;
    }

    /// <summary>
    ///     Find the Data ref in a NiTriShape/NiTriStrips block.
    /// </summary>
    private static int FindDataRef(byte[] data, BlockInfo block, NifInfo info)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        var isBE = info.IsBigEndian;

        // Skip name
        pos += 4;
        if (pos > end) return -1;

        // Skip extra data
        if (pos + 4 > end) return -1;
        var extraDataCount = isBE
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));

        if (extraDataCount > 100) return -1;
        pos += 4 + (int)extraDataCount * 4;
        if (pos > end) return -1;

        // Skip controller ref
        pos += 4;
        if (pos > end) return -1;

        // Skip uint flags
        pos += 4;
        if (pos > end) return -1;

        // Skip transform (52 bytes)
        pos += 52;
        if (pos > end) return -1;

        // Skip properties
        if (pos + 4 > end) return -1;
        var numProperties = isBE
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));

        if (numProperties > 100) return -1;
        pos += 4 + (int)numProperties * 4;
        if (pos > end) return -1;

        // Skip collision object ref
        pos += 4;
        if (pos > end) return -1;

        // Data ref
        if (pos + 4 > end) return -1;
        var dataRef = isBE
            ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos, 4));

        return dataRef;
    }
}
