using BethesdaMultitool.Tests.Helpers;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     A configurable skinned-quad fixture for the slice-7 tests, laid out from nif.xml by
///     <see cref="NifTestBlockLayouts" /> (never from NifSchema):
///     0 NiNode "Scene Root" (<see cref="RootChildren" />); 1 NiNode "Bip01" [2, 3]; 2 NiNode "Bone A"; 3 NiNode
///     "Bone B"; 4 NiTriShape "Body" (data 5, skin 6); 5 NiTriShapeData (a four-vertex quad); 6 NiSkinInstance or
///     BSDismemberSkinInstance (data 7, partition 8 when <see cref="Partitions" /> is set, skeleton root
///     <see cref="SkeletonRoot" />, bones <see cref="Bones" />); 7 NiSkinData; 8 NiSkinPartition (optional); then the
///     blocks <see cref="ExtraBlocks" /> appends.
/// </summary>
/// <remarks>
///     The default bones are chosen so each rule has a control. Bone 0 is the identity with translation (0, 0, -1). Bone 1
///     is a quarter turn about Z with scale 2 and translation (1, 2, 3), so S R^T T and S R T differ in sign. Bone 2 is
///     diag(1, 1, 1 + ulp), orthonormal within 1e-5, so an orthonormalizing implementation would round its M33 to 1.
///     The weights give vertex 0 three entries summing to 0.875 (a normalizing implementation changes them), vertex 3
///     the entries (bone 1, 0.4) and (bone 2, 0.6) in bone order (sorting by weight would swap them), and vertex 2 one
///     entry, so the stride is 3 with padding.
/// </remarks>
internal sealed class NifSkinFixture
{
    /// <summary>The skin instance block.</summary>
    public const int InstanceBlock = 6;

    /// <summary>The skin data block.</summary>
    public const int DataBlock = 7;

    /// <summary>The skin partition block (when present).</summary>
    public const int PartitionBlock = 8;

    /// <summary>The shape block.</summary>
    public const int ShapeBlock = 4;

    /// <summary>A quarter turn about Z, row by row.</summary>
    public static readonly float[] QuarterTurnAboutZ = [0f, -1f, 0f, 1f, 0f, 0f, 0f, 0f, 1f];

    /// <summary>diag(1, 1, 1 + one ulp): orthonormal within 1e-5 but not exactly.</summary>
    public static readonly float[] NearlyIdentity = [1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, MathF.BitIncrement(1f)];

    /// <summary>The quad positions.</summary>
    public static readonly float[] QuadVertices = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 1f, 1f, 0f];

    /// <summary>The default bone data (see the type remarks).</summary>
    public static NifTestSkinBone[] DefaultBones =>
    [
        new NifTestSkinBone
        {
            Translation = (0f, 0f, -1f),
            Weights = [(0, 0.5f), (2, 1f)]
        },
        new NifTestSkinBone
        {
            Rotation = QuarterTurnAboutZ,
            Translation = (1f, 2f, 3f),
            Scale = 2f,
            Weights = [(0, 0.25f), (1, 1f), (3, 0.4f)]
        },
        new NifTestSkinBone
        {
            Rotation = NearlyIdentity,
            Translation = (0.5f, 0f, 0f),
            Weights = [(0, 0.125f), (3, 0.6f)]
        }
    ];

    /// <summary>True for a big-endian (Xbox-order, PC-layout) body.</summary>
    public bool BigEndian { get; init; }

    /// <summary>True for a BSDismemberSkinInstance with <see cref="BodyParts" />.</summary>
    public bool Dismember { get; init; }

    /// <summary>The dismember body-part list (Part Flag, Body Part).</summary>
    public (ushort PartFlag, ushort BodyPart)[] BodyParts { get; init; } = [];

    /// <summary>NiSkinData's Has Vertex Weights.</summary>
    public bool HasVertexWeights { get; init; } = true;

    /// <summary>The skin instance's Bones back pointers.</summary>
    public int[] Bones { get; init; } = [1, 2, 3];

    /// <summary>The skin instance's Skeleton Root back pointer.</summary>
    public int SkeletonRoot { get; init; }

    /// <summary>The root's children.</summary>
    public int[] RootChildren { get; init; } = [1, ShapeBlock];

    /// <summary>The NiSkinData bones.</summary>
    public NifTestSkinBone[] BoneData { get; init; } = DefaultBones;

    /// <summary>The overall Skin Transform's translation.</summary>
    public (float X, float Y, float Z) OverallTranslation { get; init; }

    /// <summary>The partitions, or null for no NiSkinPartition (Skin Partition link -1).</summary>
    public NifTestSkinPartition[]? Partitions { get; init; }

    /// <summary>The stored triangle list of the quad.</summary>
    public ushort[] Triangles { get; init; } = [0, 1, 2, 1, 3, 2];

    /// <summary>The NiTriShapeData's Additional Data link (-1 for inline streams only).</summary>
    public int AdditionalData { get; init; } = -1;

    /// <summary>When true in a big-endian file, NiSkinData's body is laid out little-endian (a wrong-order control).</summary>
    public bool SkinDataInTheWrongByteOrder { get; init; }

    /// <summary>Appends blocks after the skin blocks.</summary>
    public Action<NifTestFileBuilder>? ExtraBlocks { get; init; }

    /// <summary>The index the first extra block receives.</summary>
    public int FirstExtraBlock => Partitions is null ? PartitionBlock : PartitionBlock + 1;

    /// <summary>Builds the file.</summary>
    public byte[] Build()
    {
        var builder = new NifTestFileBuilder(BigEndian, 34);
        AddNode(builder, builder.AddString("Scene Root"), RootChildren);
        AddNode(builder, builder.AddString("Bip01"), [2, 3]);
        AddNode(builder, builder.AddString("Bone A"), []);
        AddNode(builder, builder.AddString("Bone B"), []);
        AddTriShape(builder, builder.AddString("Body"), 5, skinInstance: InstanceBlock);
        AddTriShapeData(builder, new NifTestGeometryStreams
        {
            Vertices = QuadVertices,
            AdditionalData = AdditionalData
        }, Triangles);
        var partitionLink = Partitions is null ? -1 : PartitionBlock;
        builder.AddBlock(Dismember ? "BSDismemberSkinInstance" : "NiSkinInstance", w =>
        {
            NifTestBlockLayouts.SkinInstance(w, DataBlock, partitionLink, SkeletonRoot, Bones);
            if (Dismember)
            {
                NifTestBlockLayouts.DismemberTail(w, BodyParts);
            }
        });
        if (SkinDataInTheWrongByteOrder)
        {
            var wrong = new NifTestBlockWriter(!BigEndian);
            WriteSkinData(wrong);
            builder.AddBlock("NiSkinData", w => w.Bytes(wrong.ToArray()));
        }
        else
        {
            builder.AddBlock("NiSkinData", WriteSkinData);
        }

        if (Partitions is { } partitions)
        {
            builder.AddBlock("NiSkinPartition", w => NifTestBlockLayouts.SkinPartition(w, partitions));
        }

        ExtraBlocks?.Invoke(builder);
        return builder.Build();
    }

    private void WriteSkinData(NifTestBlockWriter w)
    {
        NifTestBlockLayouts.SkinData(w, NifTestBlockLayouts.Identity, OverallTranslation, 1f, HasVertexWeights,
            BoneData);
    }
}
