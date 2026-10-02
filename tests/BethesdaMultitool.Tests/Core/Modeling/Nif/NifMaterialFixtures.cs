using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Fixtures for the material and texture tests: a root node over one textured quad shape, with properties laid out by
///     <see cref="NifTestBlockLayouts" /> from nif.xml (never from NifSchema), and small accessors for the material a
///     placement received.
/// </summary>
internal static class NifMaterialFixtures
{
    /// <summary>The first block index a fixture's extra blocks receive (0 root, 1 shape, 2 shape data).</summary>
    public const int FirstExtraBlock = 3;

    private static readonly float[] QuadVertices = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 1f, 1f, 0f];
    private static readonly float[] QuadNormals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f];
    private static readonly float[] QuadUvs = [0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f];
    private static readonly ushort[] QuadTriangles = [0, 1, 2, 1, 3, 2];

    /// <summary>The quad streams (positions, normals, UV set 0).</summary>
    public static NifTestGeometryStreams Quad()
    {
        return new NifTestGeometryStreams { Vertices = QuadVertices, Normals = QuadNormals, Uvs = QuadUvs };
    }

    /// <summary>The quad without texture coordinates.</summary>
    public static NifTestGeometryStreams QuadWithoutUvs()
    {
        return new NifTestGeometryStreams { Vertices = QuadVertices, Normals = QuadNormals };
    }

    /// <summary>
    ///     0 NiNode "Root" [1] (root properties), 1 NiTriShape "Shape" (data 2, shape properties), 2 NiTriShapeData, then
    ///     the blocks <paramref name="addBlocks" /> appends from index <see cref="FirstExtraBlock" />.
    /// </summary>
    public static byte[] Shape(uint bs, int[] shapeProperties, Action<NifTestFileBuilder> addBlocks,
        int[]? rootProperties = null, bool bigEndian = false, NifTestGeometryStreams? streams = null)
    {
        var builder = new NifTestFileBuilder(bigEndian, bs);
        AddNode(builder, builder.AddString("Root"), [1], properties: rootProperties);
        AddTriShape(builder, builder.AddString("Shape"), 2, properties: shapeProperties);
        AddTriShapeData(builder, streams ?? Quad(), QuadTriangles);
        addBlocks(builder);
        return builder.Build();
    }

    /// <summary>Adds an NiAlphaProperty and returns its index.</summary>
    public static int AddAlpha(NifTestFileBuilder builder, ushort flags, byte threshold)
    {
        return builder.AddBlock("NiAlphaProperty", w => NifTestBlockLayouts.AlphaProperty(w, flags, threshold));
    }

    /// <summary>Adds an NiMaterialProperty with distinctive values and returns its index.</summary>
    public static int AddMaterial(NifTestFileBuilder builder, float emissiveMultiplier = 2.5f, float alpha = 0.75f)
    {
        var bs = builder.BsVersion;
        return builder.AddBlock("NiMaterialProperty", w => NifTestBlockLayouts.MaterialProperty(w, bs,
            [0.1f, 0.2f, 0.3f], [0.4f, 0.5f, 0.6f], [0.7f, 0.8f, 0.9f], [0.2f, 0.4f, 0.6f], 12.5f, alpha,
            emissiveMultiplier));
    }

    /// <summary>The material the placement of the node named <paramref name="nodeName" /> received.</summary>
    public static SceneMaterial MaterialOf(ModelDocument document, string nodeName = "Shape")
    {
        var primitive = PrimitiveOf(document, nodeName);
        var index = Assert.IsType<int>(primitive.MaterialIndex);
        return document.Materials[index];
    }

    /// <summary>The image a material layer binds.</summary>
    public static SceneImage ImageOf(ModelDocument document, SceneTextureLayer layer)
    {
        return document.Images[layer.Binding.ImageIndex];
    }
}
