using NifAnalyzer.Models;
using static FalloutXbox360Utils.Core.Formats.Nif.Conversion.NifEndianUtils;

namespace NifAnalyzer.Parsers;

/// <summary>
///     Parses NiSkinPartition blocks.
/// </summary>
internal static class SkinPartitionParser
{
    public static SkinPartitionInfo Parse(ReadOnlySpan<byte> data, bool bigEndian)
    {
        var info = new SkinPartitionInfo();
        var pos = 0;

        info.NumPartitions = ReadUInt32(data, pos, bigEndian);
        pos += 4;

        info.Partitions = new SkinPartitionData[info.NumPartitions];

        for (var p = 0; p < info.NumPartitions; p++)
        {
            var part = new SkinPartitionData();

            part.NumVertices = ReadUInt16(data, pos, bigEndian);
            pos += 2;

            part.NumTriangles = ReadUInt16(data, pos, bigEndian);
            pos += 2;

            part.NumBones = ReadUInt16(data, pos, bigEndian);
            pos += 2;

            part.NumStrips = ReadUInt16(data, pos, bigEndian);
            pos += 2;

            part.NumWeightsPerVertex = ReadUInt16(data, pos, bigEndian);
            pos += 2;

            // Bones array
            part.Bones = new ushort[part.NumBones];
            for (var i = 0; i < part.NumBones; i++)
            {
                part.Bones[i] = ReadUInt16(data, pos, bigEndian);
                pos += 2;
            }

            // HasVertexMap (since 10.1.0.0)
            part.HasVertexMap = data[pos++];

            // VertexMap (if HasVertexMap)
            if (part.HasVertexMap != 0)
            {
                part.VertexMap = new ushort[part.NumVertices];
                for (var i = 0; i < part.NumVertices; i++)
                {
                    part.VertexMap[i] = ReadUInt16(data, pos, bigEndian);
                    pos += 2;
                }
            }
            else
            {
                part.VertexMap = [];
            }

            // HasVertexWeights (since 10.1.0.0)
            part.HasVertexWeights = data[pos++];

            // VertexWeights (if HasVertexWeights) - NumVertices x NumWeightsPerVertex floats
            if (part.HasVertexWeights != 0)
            {
                part.VertexWeights = new float[part.NumVertices][];
                for (var v = 0; v < part.NumVertices; v++)
                {
                    part.VertexWeights[v] = new float[part.NumWeightsPerVertex];
                    for (var w = 0; w < part.NumWeightsPerVertex; w++)
                    {
                        part.VertexWeights[v][w] = ReadFloat(data, pos, bigEndian);
                        pos += 4;
                    }
                }
            }
            else
            {
                part.VertexWeights = [];
            }

            // StripLengths array (comes BEFORE HasFaces!)
            part.StripLengths = new ushort[part.NumStrips];
            for (var i = 0; i < part.NumStrips; i++)
            {
                part.StripLengths[i] = ReadUInt16(data, pos, bigEndian);
                pos += 2;
            }

            // HasFaces (since 10.1.0.0)
            part.HasFaces = data[pos++];

            // Strips (if HasFaces and NumStrips != 0)
            if (part.HasFaces != 0 && part.NumStrips != 0)
            {
                part.Strips = new ushort[part.NumStrips][];
                for (var s = 0; s < part.NumStrips; s++)
                {
                    part.Strips[s] = new ushort[part.StripLengths[s]];
                    for (var i = 0; i < part.StripLengths[s]; i++)
                    {
                        part.Strips[s][i] = ReadUInt16(data, pos, bigEndian);
                        pos += 2;
                    }
                }
            }
            else
            {
                part.Strips = [];
            }

            // Triangles (if HasFaces and NumStrips == 0)
            if (part.HasFaces != 0 && part.NumStrips == 0)
            {
                part.Triangles = new Triangle[part.NumTriangles];
                for (var i = 0; i < part.NumTriangles; i++)
                {
                    part.Triangles[i] = new Triangle(
                        ReadUInt16(data, pos, bigEndian),
                        ReadUInt16(data, pos + 2, bigEndian),
                        ReadUInt16(data, pos + 4, bigEndian)
                    );
                    pos += 6;
                }
            }
            else
            {
                part.Triangles = [];
            }

            // HasBoneIndices
            part.HasBoneIndices = data[pos++];

            // BoneIndices (if HasBoneIndices) - NumVertices x NumWeightsPerVertex bytes
            if (part.HasBoneIndices != 0)
            {
                part.BoneIndices = new byte[part.NumVertices][];
                for (var v = 0; v < part.NumVertices; v++)
                {
                    part.BoneIndices[v] = new byte[part.NumWeightsPerVertex];
                    for (var w = 0; w < part.NumWeightsPerVertex; w++) part.BoneIndices[v][w] = data[pos++];
                }
            }
            else
            {
                part.BoneIndices = [];
            }

            info.Partitions[p] = part;
        }

        return info;
    }
}