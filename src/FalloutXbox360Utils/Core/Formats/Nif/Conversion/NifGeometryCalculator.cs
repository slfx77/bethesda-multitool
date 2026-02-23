// NIF converter - Geometry expansion size calculations
// Parses geometry block fields and calculates how much additional space
// is needed when expanding packed geometry data into standard format.

using FalloutXbox360Utils.Core.Formats.Nif.Geometry;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Conversion;

/// <summary>
///     Calculates geometry block expansion sizes for NIF conversion.
///     Parses NiTriStripsData/NiTriShapeData field layouts and determines
///     how much extra space is needed for expanded packed geometry.
/// </summary>
internal static class NifGeometryCalculator
{
    /// <summary>
    ///     Calculate how much a geometry block needs to expand.
    /// </summary>
    internal static GeometryBlockExpansion? CalculateGeometryExpansion(
        byte[] data, BlockInfo block, PackedGeometryData packedData, bool isSkinned = false)
    {
        var fields = ParseGeometryBlockFields(data, block);
        if (fields == null)
        {
            return null;
        }

        var sizeIncrease = CalculateSizeIncrease(fields.Value, packedData, isSkinned);
        if (sizeIncrease == 0)
        {
            return null;
        }

        return new GeometryBlockExpansion
        {
            OriginalSize = block.Size,
            NewSize = block.Size + sizeIncrease,
            SizeIncrease = sizeIncrease,
            BlockTypeName = block.TypeName
        };
    }

    private static GeometryBlockFields? ParseGeometryBlockFields(byte[] data, BlockInfo block)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        pos += 4; // GroupId

        if (pos + 2 > end)
        {
            return null;
        }

        var numVertices = BinaryUtils.ReadUInt16BE(data, pos);
        pos += 2;

        pos += 2; // KeepFlags, CompressFlags

        if (pos + 1 > end)
        {
            return null;
        }

        var hasVertices = data[pos];
        pos += 1;

        if (hasVertices != 0)
        {
            pos += numVertices * 12;
        }

        if (pos + 2 > end)
        {
            return null;
        }

        var bsDataFlags = BinaryUtils.ReadUInt16BE(data, pos);
        pos += 2;

        if (pos + 1 > end)
        {
            return null;
        }

        var hasNormals = data[pos];
        pos += 1;

        if (hasNormals != 0)
        {
            pos += numVertices * 12;
            if ((bsDataFlags & 4096) != 0)
            {
                pos += numVertices * 24;
            }
        }

        pos += 16; // center + radius

        if (pos + 1 > end)
        {
            return null;
        }

        var hasVertexColors = data[pos];

        return new GeometryBlockFields(numVertices, bsDataFlags, hasVertices, hasNormals, hasVertexColors);
    }

    private static int CalculateSizeIncrease(GeometryBlockFields fields, PackedGeometryData packedData, bool isSkinned)
    {
        var sizeIncrease = 0;
        var numVertices = fields.NumVertices;

        if (fields.HasVertices == 0 && packedData.Positions != null)
        {
            sizeIncrease += numVertices * 12;
        }

        if (fields.HasNormals == 0 && packedData.Normals != null)
        {
            sizeIncrease += numVertices * 12;
            if (packedData.Tangents != null)
            {
                sizeIncrease += numVertices * 12;
            }

            if (packedData.Bitangents != null)
            {
                sizeIncrease += numVertices * 12;
            }
        }

        // Vertex colors: skip for skinned meshes (ubyte4 is bone indices)
        if (fields.HasVertexColors == 0 && packedData.VertexColors != null && !isSkinned)
        {
            sizeIncrease += numVertices * 16;
        }

        var numUVSets = fields.BsDataFlags & 1;
        if (numUVSets == 0 && packedData.UVs != null)
        {
            sizeIncrease += numVertices * 8;
        }

        return sizeIncrease;
    }

    internal readonly record struct GeometryBlockFields(
        ushort NumVertices,
        ushort BsDataFlags,
        byte HasVertices,
        byte HasNormals,
        byte HasVertexColors);
}
