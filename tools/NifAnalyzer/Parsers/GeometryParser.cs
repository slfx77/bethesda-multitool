using NifAnalyzer.Models;
using static NifAnalyzer.Utils.BinaryHelpers;

namespace NifAnalyzer.Parsers;

/// <summary>
///     Parses NiTriShapeData and NiTriStripsData blocks.
/// </summary>
internal static class GeometryParser
{
    public static GeometryInfo Parse(ReadOnlySpan<byte> data, bool bigEndian, int bsVersion, string blockType)
    {
        var info = new GeometryInfo();
        var pos = 0;

        // NiGeometryData base fields
        info.GroupId = ReadInt32(data, pos, bigEndian);
        pos += 4;
        info.NumVertices = ReadUInt16(data, pos, bigEndian);
        pos += 2;
        info.FieldOffsets["GroupId"] = 0;
        info.FieldOffsets["NumVertices"] = 4;

        // Bethesda format (BSVersion >= 34): keepFlags, compressFlags come next
        if (bsVersion >= 34)
        {
            info.KeepFlags = data[pos++];
            info.CompressFlags = data[pos++];
            info.FieldOffsets["KeepFlags"] = 6;
            info.FieldOffsets["CompressFlags"] = 7;
        }

        // Has Vertices
        info.FieldOffsets["HasVertices"] = pos;
        info.HasVertices = data[pos++];

        // Vertices (if present) - skip
        if (info.HasVertices != 0)
        {
            info.FieldOffsets["Vertices"] = pos;
            pos += info.NumVertices * 12; // 3 floats per vertex
        }

        // BS Vector Flags (BSVersion >= 34) - comes AFTER vertices
        if (bsVersion >= 34)
        {
            info.FieldOffsets["BsVectorFlags"] = pos;
            info.BsVectorFlags = ReadUInt16(data, pos, bigEndian);
            pos += 2;
        }

        // Has Normals
        info.FieldOffsets["HasNormals"] = pos;
        info.HasNormals = data[pos++];

        // Normals (if present) - skip
        if (info.HasNormals != 0)
        {
            info.FieldOffsets["Normals"] = pos;
            pos += info.NumVertices * 12; // 3 floats per vertex
        }

        // Center and radius (always present after hasNormals in BSVersion >= 34)
        if (bsVersion >= 34)
        {
            info.FieldOffsets["Center"] = pos;
            info.TangentCenterX = ReadFloat(data, pos, bigEndian);
            pos += 4;
            info.TangentCenterY = ReadFloat(data, pos, bigEndian);
            pos += 4;
            info.TangentCenterZ = ReadFloat(data, pos, bigEndian);
            pos += 4;
            info.TangentRadius = ReadFloat(data, pos, bigEndian);
            pos += 4;
        }

        // Tangents and Bitangents (if BS Vector Flags has tangent space bit)
        if (bsVersion >= 34 && (info.BsVectorFlags & 0x1000) != 0)
        {
            info.FieldOffsets["Tangents"] = pos;
            pos += info.NumVertices * 12; // Tangents
            info.FieldOffsets["Bitangents"] = pos;
            pos += info.NumVertices * 12; // Bitangents
        }

        // Has Vertex Colors
        info.FieldOffsets["HasVertexColors"] = pos;
        info.HasVertexColors = data[pos++];
        if (info.HasVertexColors != 0)
        {
            info.FieldOffsets["VertexColors"] = pos;
            pos += info.NumVertices * 16; // 4 floats per color
        }

        // UV Sets handling - BS Version >= 34 uses flag in bsVectorFlags
        ushort numUvSets;
        if (bsVersion >= 34)
        {
            numUvSets = (ushort)((info.BsVectorFlags & 0x0001) != 0 ? 1 : 0);
        }
        else
        {
            numUvSets = ReadUInt16(data, pos, bigEndian);
            pos += 2;
            info.TSpaceFlag = ReadUInt16(data, pos, bigEndian);
            pos += 2;
        }

        info.NumUvSets = numUvSets;

        // UV coordinates
        var actualUvSets = numUvSets & 0x3F;
        if (actualUvSets > 0)
        {
            info.FieldOffsets["UVSets"] = pos;
            pos += info.NumVertices * actualUvSets * 8; // 2 floats per UV per set
        }

        // Consistency Flags
        info.FieldOffsets["ConsistencyFlags"] = pos;
        info.ConsistencyFlags = ReadUInt16(data, pos, bigEndian);
        pos += 2;

        // Additional Data (ref to BSPackedAdditionalGeometryData or -1)
        info.FieldOffsets["AdditionalData"] = pos;
        info.AdditionalData = ReadInt32(data, pos, bigEndian);
        pos += 4;

        // Now we're at NiTriBasedGeomData fields
        info.FieldOffsets["NumTriangles"] = pos;
        info.NumTriangles = ReadUInt16(data, pos, bigEndian);
        pos += 2;

        if (blockType.Contains("NiTriShapeData"))
        {
            // NiTriShapeData specific
            info.FieldOffsets["NumTrianglePoints"] = pos;
            info.NumTrianglePoints = ReadUInt32(data, pos, bigEndian);
            pos += 4;

            info.FieldOffsets["HasTriangles"] = pos;
            info.HasTriangles = data[pos++];
            info.TrianglesFieldOffset = pos;

            // Triangles would be here if HasTriangles != 0
            if (info.HasTriangles != 0)
            {
                info.FieldOffsets["Triangles"] = pos;
                pos += info.NumTriangles * 6; // 3 ushorts per triangle
            }

            // Num Match Groups
            info.FieldOffsets["NumMatchGroups"] = pos;
            if (pos + 2 <= data.Length) info.NumMatchGroups = ReadUInt16(data, pos, bigEndian);
        }
        else if (blockType.Contains("NiTriStripsData"))
        {
            // NiTriStripsData specific
            info.FieldOffsets["NumStrips"] = pos;
            info.NumStrips = ReadUInt16(data, pos, bigEndian);
            pos += 2;

            // Strip lengths
            info.StripLengths = new ushort[info.NumStrips];
            info.FieldOffsets["StripLengths"] = pos;
            for (var i = 0; i < info.NumStrips; i++)
            {
                info.StripLengths[i] = ReadUInt16(data, pos, bigEndian);
                pos += 2;
            }

            info.FieldOffsets["HasPoints"] = pos;
            info.HasPoints = data[pos++];
            info.TrianglesFieldOffset = pos;

            // Points would be here if HasPoints != 0
        }

        info.ParsedSize = pos;
        return info;
    }
}