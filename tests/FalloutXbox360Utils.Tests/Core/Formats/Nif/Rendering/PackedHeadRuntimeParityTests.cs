using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Nif.Geometry;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class PackedHeadRuntimeParityTests
{
    [Fact]
    public void FaceGenPackedHeadMorpher_FansOutMeshOrderDeltasToDuplicatePackedVertices()
    {
        var egm = ParseSyntheticEgm(
            2,
            [
                new EgmMorph
                {
                    Scale = 1f,
                    Deltas =
                    [
                        10, 0, 0,
                        20, 0, 0
                    ]
                }
            ],
            []);

        var packedPositions = new float[9];
        var topology = new PackedTopologyData
        {
            MeshVertexCount = 2,
            PackedVertexCount = 3,
            VertexMap = [0, 0, 1],
            PackedTriangles = [0, 1, 2]
        };

        FaceGenPackedHeadMorpher.Apply(
            packedPositions,
            topology,
            egm,
            [0.5f],
            null);

        Assert.Equal(5f, packedPositions[0], 3);
        Assert.Equal(5f, packedPositions[3], 3);
        Assert.Equal(10f, packedPositions[6], 3);
    }

    [Fact]
    public void FaceGenPackedHeadMorpher_SkipsVertexMapEntriesOutsideMeshRange()
    {
        var egm = ParseSyntheticEgm(
            1,
            [
                new EgmMorph
                {
                    Scale = 1f,
                    Deltas = [4, 5, 6]
                }
            ],
            []);

        var packedPositions = new float[6];
        var topology = new PackedTopologyData
        {
            MeshVertexCount = 1,
            PackedVertexCount = 2,
            VertexMap = [0, 5],
            PackedTriangles = [0, 1, 0]
        };

        FaceGenPackedHeadMorpher.Apply(
            packedPositions,
            topology,
            egm,
            [1f],
            null);

        Assert.Equal([4f, 5f, 6f, 0f, 0f, 0f], packedPositions);
    }

    [Fact]
    public void PackedTopologyData_Clone_DeepCopiesArrays()
    {
        var original = new PackedTopologyData
        {
            MeshVertexCount = 2,
            PackedVertexCount = 3,
            VertexMap = [0, 0, 1],
            PackedTriangles = [0, 1, 2]
        };

        var clone = original.Clone();
        clone.VertexMap[0] = 9;
        clone.PackedTriangles[0] = 8;

        Assert.Equal((ushort)0, original.VertexMap[0]);
        Assert.Equal((ushort)0, original.PackedTriangles[0]);
        Assert.NotSame(original.VertexMap, clone.VertexMap);
        Assert.NotSame(original.PackedTriangles, clone.PackedTriangles);
    }

    private static EgmParser ParseSyntheticEgm(int vertexCount, EgmMorph[] symmetricMorphs, EgmMorph[] asymmetricMorphs)
    {
        var size = 64 +
                   symmetricMorphs.Length * (4 + vertexCount * 6) +
                   asymmetricMorphs.Length * (4 + vertexCount * 6);
        var bytes = new byte[size];
        "FREGM002"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)vertexCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)symmetricMorphs.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)asymmetricMorphs.Length);

        var offset = 64;
        WriteMorphs(bytes, ref offset, vertexCount, symmetricMorphs);
        WriteMorphs(bytes, ref offset, vertexCount, asymmetricMorphs);

        return Assert.IsType<EgmParser>(EgmParser.Parse(bytes));
    }

    private static void WriteMorphs(byte[] bytes, ref int offset, int vertexCount, EgmMorph[] morphs)
    {
        foreach (var morph in morphs)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), morph.Scale);
            offset += 4;
            Assert.Equal(vertexCount * 3, morph.Deltas.Length);
            for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                var baseIndex = vertexIndex * 3;
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + 0), morph.Deltas[baseIndex + 0]);
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + 2), morph.Deltas[baseIndex + 1]);
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset + 4), morph.Deltas[baseIndex + 2]);
                offset += 6;
            }
        }
    }
}