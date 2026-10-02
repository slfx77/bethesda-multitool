using BethesdaMultitool.Tests.Helpers;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     A big-endian static console geometry fixture for the slice-10 tests, laid out by <see cref="NifTestBlockLayouts" />
///     and <see cref="NifTestPackedLayouts" /> (never from NifSchema or the production layout table): 0 NiNode "Root"
///     [1]; 1 NiTriShape or NiTriStrips "Shape" (data 2); 2 NiTriShapeData or NiTriStripsData with Has Vertices 0, the
///     declared Num Vertices and Additional Data 3, holding the triangles (the console keeps the index buffer inline);
///     3 BSPackedAdditionalGeometryData in the requested layout, or a custom block when <see cref="CustomPacked" />
///     is set.
/// </summary>
internal sealed class NifPackedFixture
{
    /// <summary>The shape block.</summary>
    public const int ShapeBlock = 1;

    /// <summary>The data block.</summary>
    public const int DataBlock = 2;

    /// <summary>The packed block.</summary>
    public const int PackedBlock = 3;

    /// <summary>A four-vertex quad whose values exercise the half rounding: a coordinate above 8,192, negatives, fractions.</summary>
    public static NifTestPackedVertexData Quad(byte[]? colors = null)
    {
        return new NifTestPackedVertexData
        {
            Positions =
            [
                0f, 0f, 0f,
                1.0009765625f, 0.1f, -0.25f,
                9000.3f, 1f, 0.3333f,
                1f, 1.0001f, 2.5e-5f
            ],
            Normals =
            [
                0f, 0f, 1f,
                0.7071f, 0f, 0.7071f,
                0f, 0.6f, 0.8f,
                -0.5773f, -0.5773f, 0.5773f
            ],
            Uvs = [0f, 0f, 1f, 0f, 0f, 1f, 0.333f, 0.7777f],
            Tangents =
            [
                1f, 0f, 0f,
                0.7071f, 0f, -0.7071f,
                1f, 0f, 0f,
                0.8165f, -0.4082f, 0.4082f
            ],
            Bitangents =
            [
                0f, 1f, 0f,
                0f, 1f, 0f,
                0f, -0.8f, 0.6f,
                0f, 0.7071f, 0.7071f
            ],
            Colors = colors
        };
    }

    /// <summary>The colors of <see cref="Quad" /> when the layout carries them: every vertex has R != B.</summary>
    public static byte[] QuadColors =>
    [
        255, 128, 0, 255,
        10, 200, 30, 128,
        0, 0, 255, 64,
        17, 34, 51, 0
    ];

    /// <summary>The layout id (L1, L2, L5 or L6 for a static shape).</summary>
    public string Layout { get; init; } = "L2";

    /// <summary>The vertex data.</summary>
    public NifTestPackedVertexData Data { get; init; } = Quad();

    /// <summary>True to write the colors in the PS3 byte order.</summary>
    public bool Ps3Order { get; init; }

    /// <summary>True for a NiTriStrips with one strip over the quad; false for a NiTriShape with a triangle list.</summary>
    public bool Strips { get; init; }

    /// <summary>The declared Num Vertices of the data block; defaults to the packed vertex count.</summary>
    public ushort? DeclaredVertices { get; init; }

    /// <summary>The Shader Index written; defaults to the layout's measured value.</summary>
    public int? ShaderIndex { get; init; }

    /// <summary>When set, writes this packed block body instead of the layout's.</summary>
    public Action<NifTestBlockWriter>? CustomPacked { get; init; }

    /// <summary>The skin instance link of the shape (-1 for none); set to reference a block the caller appends.</summary>
    public int SkinInstance { get; init; } = -1;

    /// <summary>Appends blocks after the packed block.</summary>
    public Action<NifTestFileBuilder>? ExtraBlocks { get; init; }

    /// <summary>Builds the file.</summary>
    public byte[] Build()
    {
        var builder = new NifTestFileBuilder(true, 34);
        AddNode(builder, builder.AddString("Root"), [ShapeBlock]);
        var name = builder.AddString("Shape");
        if (Strips)
        {
            builder.AddBlock("NiTriStrips", w => NifTestBlockLayouts.GeometryShape(w, 34, name, DataBlock, SkinInstance));
        }
        else
        {
            AddTriShape(builder, name, DataBlock, skinInstance: SkinInstance);
        }

        var streams = new NifTestGeometryStreams
        {
            Vertices = [],
            HasVertices = false,
            NumVertices = DeclaredVertices ?? (ushort)Data.VertexCount,
            AdditionalData = PackedBlock
        };
        if (Strips)
        {
            AddTriStripsData(builder, streams, [[0, 1, 2, 3]]);
        }
        else
        {
            AddTriShapeData(builder, streams, [0, 1, 2, 1, 3, 2]);
        }

        builder.AddBlock("BSPackedAdditionalGeometryData",
            CustomPacked ?? (w => NifTestPackedLayouts.Write(w, Layout, Data, Ps3Order, ShaderIndex)));
        ExtraBlocks?.Invoke(builder);
        return builder.Build();
    }
}
