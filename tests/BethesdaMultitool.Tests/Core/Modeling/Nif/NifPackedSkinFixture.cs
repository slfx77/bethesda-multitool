using BethesdaMultitool.Tests.Helpers;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     A big-endian skinned console geometry fixture for the slice-10 tests, laid out by <see cref="NifTestBlockLayouts" />
///     and <see cref="NifTestPackedLayouts" /> (never from NifSchema or the production layout table): 0 NiNode "Root"
///     [1, 4]; 1 NiNode "Bip01" [2, 3]; 2 NiNode "Bone A"; 3 NiNode "Bone B"; 4 NiTriShape "Body" (data 5, skin 6);
///     5 NiTriShapeData with Has Vertices 0, Has Triangles 0, the declared Num Vertices and Additional Data 9;
///     6 NiSkinInstance or BSDismemberSkinInstance (data 7, partition 8, skeleton root 0, bones [1, 2, 3]);
///     7 NiSkinData with Has Vertex Weights 0 (the console form); 8 NiSkinPartition with the given partitions (vertex
///     maps and faces, no weights, no bone indices, as the console stores them); 9 BSPackedAdditionalGeometryData in
///     the requested layout.
/// </summary>
internal sealed class NifPackedSkinFixture
{
    /// <summary>The shape block.</summary>
    public const int ShapeBlock = 4;

    /// <summary>The data block.</summary>
    public const int DataBlock = 5;

    /// <summary>The skin instance block.</summary>
    public const int InstanceBlock = 6;

    /// <summary>The skin data block.</summary>
    public const int SkinDataBlock = 7;

    /// <summary>The partition block.</summary>
    public const int PartitionBlock = 8;

    /// <summary>The packed block.</summary>
    public const int PackedBlock = 9;

    /// <summary>
    ///     Two partitions over a four-vertex shape: partition 0 maps shape vertices [0, 1, 2] with bones [0, 1] and one
    ///     triangle, partition 1 maps [2, 3, 1] with bones [2, 1] and one triangle, so the packed order holds six
    ///     vertices and shape vertices 1 and 2 appear twice.
    /// </summary>
    public static NifTestSkinPartition[] TwoPartitions =>
    [
        new NifTestSkinPartition
        {
            VertexCount = 3, Bones = [0, 1], WeightsPerVertex = 4, VertexMap = [0, 1, 2], Triangles = [0, 1, 2]
        },
        new NifTestSkinPartition
        {
            VertexCount = 3, Bones = [2, 1], WeightsPerVertex = 4, VertexMap = [2, 3, 1], Triangles = [0, 1, 2]
        }
    ];

    /// <summary>
    ///     The packed vertices of <see cref="TwoPartitions" />, in partition order: the shape's four positions repeated
    ///     where the maps repeat a vertex (a point keeps one position), plain weights and in-range bone indices.
    /// </summary>
    public static NifTestPackedVertexData SixVertices(byte[]? colors = null)
    {
        // 0.3 is not a half: its decode differs between nearest-even rounding and truncation, which the exact
        // round-trip test's control needs (every other coordinate is an exact half).
        float[] shape = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 1f, 1f, 0.3f];
        int[] points = [0, 1, 2, 2, 3, 1];
        var positions = new float[18];
        var normals = new float[18];
        var tangents = new float[18];
        var bitangents = new float[18];
        var uvs = new float[12];
        for (var i = 0; i < 6; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                positions[i * 3 + c] = shape[points[i] * 3 + c];
            }

            normals[i * 3 + 2] = 1f;
            tangents[i * 3] = 1f;
            bitangents[i * 3 + 1] = i % 2 == 0 ? 1f : -1f;
            uvs[i * 2] = points[i] * 0.25f;
            uvs[i * 2 + 1] = 0.5f;
        }

        return new NifTestPackedVertexData
        {
            Positions = positions,
            Normals = normals,
            Uvs = uvs,
            Tangents = tangents,
            Bitangents = bitangents,
            Colors = colors,
            Weights =
            [
                1f, 0f, 0f, 0f,
                0.5f, 0.5f, 0f, 0f,
                0.25f, 0.75f, 0f, 0f,
                0.75f, 0.25f, 0f, 0f,
                0.5f, 0.5f, 0f, 0f,
                1f, 0f, 0f, 0f
            ],
            BoneIndices =
            [
                0, 0, 0, 0,
                0, 1, 0, 0,
                1, 0, 0, 0,
                0, 1, 0, 0,
                1, 0, 0, 0,
                0, 0, 0, 0
            ]
        };
    }

    /// <summary>The layout id (L3 or L4; a static id makes the skinned shape's layout weightless).</summary>
    public string Layout { get; init; } = "L3";

    /// <summary>The packed vertex data, in partition order.</summary>
    public NifTestPackedVertexData Data { get; init; } = SixVertices();

    /// <summary>The partitions.</summary>
    public NifTestSkinPartition[] Partitions { get; init; } = TwoPartitions;

    /// <summary>The shape's declared Num Vertices (the point domain).</summary>
    public ushort DeclaredVertices { get; init; } = 4;

    /// <summary>True to write the colors in the PS3 byte order.</summary>
    public bool Ps3Order { get; init; }

    /// <summary>True for a BSDismemberSkinInstance with <see cref="BodyParts" />.</summary>
    public bool Dismember { get; init; }

    /// <summary>The dismember body-part list (Part Flag, Body Part), one entry per partition.</summary>
    public (ushort PartFlag, ushort BodyPart)[] BodyParts { get; init; } = new (ushort, ushort)[] { (1, 32), (1, 33) };

    /// <summary>Builds the file.</summary>
    public byte[] Build()
    {
        var builder = new NifTestFileBuilder(true, 34);
        AddNode(builder, builder.AddString("Root"), [1, ShapeBlock]);
        AddNode(builder, builder.AddString("Bip01"), [2, 3]);
        AddNode(builder, builder.AddString("Bone A"), []);
        AddNode(builder, builder.AddString("Bone B"), []);
        AddTriShape(builder, builder.AddString("Body"), DataBlock, skinInstance: InstanceBlock);
        AddTriShapeData(builder, new NifTestGeometryStreams
        {
            Vertices = [],
            HasVertices = false,
            NumVertices = DeclaredVertices,
            AdditionalData = PackedBlock
        }, [], hasTriangles: false);
        builder.AddBlock(Dismember ? "BSDismemberSkinInstance" : "NiSkinInstance", w =>
        {
            NifTestBlockLayouts.SkinInstance(w, SkinDataBlock, PartitionBlock, 0, [1, 2, 3]);
            if (Dismember)
            {
                NifTestBlockLayouts.DismemberTail(w, BodyParts);
            }
        });
        builder.AddBlock("NiSkinData", w => NifTestBlockLayouts.SkinData(w, NifTestBlockLayouts.Identity,
            (0f, 0f, 0f), 1f, false,
        [
            new NifTestSkinBone { Translation = (0f, 0f, -1f) },
            new NifTestSkinBone { Translation = (1f, 0f, 0f) },
            new NifTestSkinBone { Translation = (0f, 1f, 0f) }
        ]));
        builder.AddBlock("NiSkinPartition", w => NifTestBlockLayouts.SkinPartition(w, Partitions));
        builder.AddBlock("BSPackedAdditionalGeometryData",
            w => NifTestPackedLayouts.Write(w, Layout, Data, Ps3Order));
        return builder.Build();
    }
}
