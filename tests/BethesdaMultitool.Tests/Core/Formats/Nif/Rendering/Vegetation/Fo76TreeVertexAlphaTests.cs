using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Tests.Helpers;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Vegetation;

/// <summary>
///     FO76 foliage stores wind weight in vertex alpha below BSLeafAnimNode/BSTreeNode. Mesh Viewer
///     must retain vertex RGB while sourcing cutout coverage exclusively from diffuse alpha.
/// </summary>
public sealed class Fo76TreeVertexAlphaTests
{
    private const string DiffusePath = @"textures\landscape\trees\testfoliage_d.dds";

    [Fact]
    public void TreeAnimationGraph_ClassifiesNestedClassicAndModernShapesOnly()
    {
        var nif = Nif(
            "NiNode",             // 0 ordinary root
            "BSLeafAnimNode",     // 1 animated tree root
            "NiNode",             // 2 nested transform
            "BSTriShape",         // 3 modern animated-tree shape
            "BSTreeNode",         // 4 second animated tree root
            "NiTriShape",         // 5 classic animated-tree shape
            "BSTriShape");        // 6 ordinary sibling
        var graph = new Dictionary<int, List<int>>
        {
            [0] = [1, 4, 6],
            [1] = [2],
            [2] = [3],
            [4] = [5]
        };

        var shapes = NifSceneGraphWalker.CollectTreeAnimationShapes(nif, graph);

        Assert.Equal([3, 5], shapes.Order());
        Assert.DoesNotContain(6, shapes);
    }

    [Fact]
    public void TreeAnimationPolicy_PreservesRgbAndNormalizesCoverageAlphaOnly()
    {
        var fo76Metadata = new NifShaderTextureMetadata
        {
            // FO76 BS version 155 does not expose legacy SLSF1 through the current parser.
            PropertyType = "BSLightingShaderProperty"
        };
        Assert.False(NifVertexColorPolicy.UsesAlphaForOpacity(
            fo76Metadata,
            isTreeAnimationShape: true));

        var readableLegacyMetadata = new NifShaderTextureMetadata
        {
            PropertyType = "BSLightingShaderProperty",
            ShaderFlags = 0x8u
        };
        Assert.True(NifVertexColorPolicy.UsesAlphaForOpacity(
            readableLegacyMetadata,
            isTreeAnimationShape: true));

        byte[] authoredColors = [32, 64, 96, 17];
        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            VertexColors = authoredColors,
            UseVertexColors = true,
            UseVertexAlphaForOpacity = false
        };

        var effective = NifVertexColorPolicy.Read(submesh, 0);
        var exported = NpcGlbTintColorEncoder.BuildVertexColor(submesh, 0);

        Assert.Equal((byte)17, authoredColors[3]);
        Assert.Equal(((byte)32, (byte)64, (byte)96, byte.MaxValue), effective);
        Assert.Equal(32f / 255f, exported.X, 6);
        Assert.Equal(64f / 255f, exported.Y, 6);
        Assert.Equal(96f / 255f, exported.Z, 6);
        Assert.Equal(1f, exported.W);
    }

    [Fact]
    public void GlbExport_UsesOpaqueVertexAlphaButRetainsDiffuseMaskCutoffAndDoubleSidedState()
    {
        var submesh = TriangleSubmesh();
        var scene = new GlbScene();
        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = "FO76 foliage",
            Submesh = submesh
        });
        using var resolver = new NifTextureResolver(path =>
            string.Equals(path, DiffusePath, StringComparison.OrdinalIgnoreCase)
                ? DecodedTexture.FromBaseLevel(
                [
                    20, 80, 30, 0,
                    20, 80, 30, 255
                ], 2, 1, false)
                : null);

        var model = Read(GlbWriter.WriteToBytes(scene, resolver));

        var material = Assert.Single(model.LogicalMaterials);
        Assert.Equal(AlphaMode.MASK, material.Alpha);
        Assert.Equal(93f / 255f, material.AlphaCutoff, 6);
        Assert.True(material.DoubleSided);
        Assert.NotNull(Assert.IsType<MaterialChannel>(material.FindChannel("BaseColor")).Texture);

        var colors = Assert.Single(model.LogicalMeshes)
            .Primitives.Single()
            .GetVertexAccessor("COLOR_0")
            .AsVector4Array()
            .ToArray();
        Assert.Equal(3, colors.Length);
        Assert.All(colors, color => Assert.Equal(1f, color.W));
        Assert.Equal(new Vector3(32f, 64f, 96f) / 255f, new Vector3(colors[0].X, colors[0].Y, colors[0].Z));
        Assert.Equal(new Vector3(48f, 80f, 112f) / 255f, new Vector3(colors[1].X, colors[1].Y, colors[1].Z));
        Assert.Equal(new Vector3(64f, 96f, 128f) / 255f, new Vector3(colors[2].X, colors[2].Y, colors[2].Z));
    }

    [Fact]
    public void RendererAndHierarchyExport_BothApplyTreeAnimationAncestryPolicy()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
            "NifGeometryExtractor.cs");
        var exporter = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Export",
            "NifExportExtractor.cs");

        Assert.Contains(
            "NifSceneGraphWalker.CollectTreeAnimationShapes(nif, nodeChildren)",
            renderer,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            renderer,
            "var isTreeAnimationShape = treeAnimationShapes.Contains(shapeIndex)",
            "var useVertexAlpha = !isTreeAnimationShape",
            "NifVertexColorPolicy.UsesAlphaForOpacity(",
            "isTreeAnimationShape);",
            "submesh.UseVertexAlphaForOpacity");
        Assert.Contains(
            "NifSceneGraphWalker.CollectTreeAnimationShapes(nif, nodeChildren)",
            exporter,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            exporter,
            "treeAnimationShapes.Contains(shapeIndex)",
            "bool isTreeAnimationShape)",
            "ShapeProperties.TreeAnimationDefault",
            "NifVertexColorPolicy.UsesAlphaForOpacity(",
            "isTreeAnimationShape)");
        SourceContract.AssertOrder(
            exporter,
            "TreeAnimationDefault = new()",
            "UseVertexAlphaForOpacity = false");
        Assert.DoesNotContain(
            "TreeAnimationDefault = new()\n        {\n            UseVertexColors = true",
            exporter.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    private static RenderableSubmesh TriangleSubmesh()
    {
        return new RenderableSubmesh
        {
            ShapeName = "Tree foliage",
            Positions =
            [
                0f, 0f, 0f,
                1f, 0f, 0f,
                0f, 1f, 0f
            ],
            Triangles = [0, 1, 2],
            Normals =
            [
                0f, 0f, 1f,
                0f, 0f, 1f,
                0f, 0f, 1f
            ],
            UVs = [0f, 0f, 1f, 0f, 0f, 1f],
            VertexColors =
            [
                32, 64, 96, 0,
                48, 80, 112, 64,
                64, 96, 128, 192
            ],
            UseVertexColors = true,
            UseVertexAlphaForOpacity = false,
            DiffuseTexturePath = DiffusePath,
            HasAlphaTest = true,
            AlphaTestThreshold = 92,
            AlphaTestFunction = 4,
            IsDoubleSided = true
        };
    }

    private static NifInfo Nif(params string[] types)
    {
        var nif = new NifInfo
        {
            BinaryVersion = 0x14020007,
            BsVersion = 155,
            BlockCount = types.Length
        };
        for (var index = 0; index < types.Length; index++)
        {
            nif.Blocks.Add(new BlockInfo
            {
                Index = index,
                TypeName = types[index]
            });
        }

        return nif;
    }

    private static ModelRoot Read(byte[] glb)
    {
        using var stream = new MemoryStream(glb, writable: false);
        return ModelRoot.ReadGLB(stream);
    }
}
