using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Xngine.Mesh;

/// <summary>GLB export round-trips: primitives per texture, materials, embedded images and the Y flip.</summary>
public class XnGineMeshGlbExporterTests
{
    [Fact]
    public void Write_ProducesOnePrimitivePerTexture_WithNamedMaterials()
    {
        var bytes = XnGineMeshFixture.Build("v2.7",
            [(0, 0, 0), (256, 0, 0), (256, 0, 256), (0, 0, 256)],
            [
                new XnGineMeshFixture.Plane(XnGineMeshFixture.Texture(24, 0), [(0, 0, 0), (1, 0, 0), (2, 0, 0)],
                    (0, -256, 0)),
                new XnGineMeshFixture.Plane(XnGineMeshFixture.Texture(321, 4), [(0, 0, 0), (2, 0, 0), (3, 0, 0)],
                    (0, -256, 0))
            ]);
        var mesh = XnGineMeshDecomposer.Decompose(XnGineMesh.Parse(bytes, 44005));

        var glb = XnGineMeshGlbExporter.WriteToBytes(mesh);
        var model = ModelRoot.ParseGLB(glb);

        var logicalMesh = Assert.Single(model.LogicalMeshes);
        Assert.Equal("mesh_44005", logicalMesh.Name);
        Assert.Equal(2, logicalMesh.Primitives.Count);
        Assert.Equal(["TEXTURE.024#0", "TEXTURE.321#4"], logicalMesh.Primitives.Select(p => p.Material.Name));
        Assert.All(logicalMesh.Primitives, p => Assert.Single(p.GetTriangleIndices()));
        Assert.All(logicalMesh.Primitives, p => Assert.True(p.Material.DoubleSided));
        Assert.Empty(model.LogicalImages);
    }

    [Fact]
    public void Write_FlipsY_AndNormalisesUvsByTheTextureSize()
    {
        var mesh = XnGineMeshDecomposer.Decompose(XnGineMesh.Parse(XnGineMeshFixture.Quad(), 5000));
        var png = PngWriter.EncodeRgba(new byte[32 * 32 * 4], 32, 32);

        var glb = XnGineMeshGlbExporter.WriteToBytes(mesh, (_, _) => new XnGineTexturePng(png, 32, 32));
        var model = ModelRoot.ParseGLB(glb);

        var primitive = Assert.Single(Assert.Single(model.LogicalMeshes).Primitives);
        Assert.Single(model.LogicalImages);
        Assert.NotNull(primitive.Material.FindChannel("BaseColor")?.Texture);

        var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
        var uvs = primitive.GetVertexAccessor("TEXCOORD_0").AsVector2Array();
        Assert.Equal(4, positions.Count);

        // The native quad lies at Y = 0 with its normal pointing "up" in Y-down space (0, -1, 0);
        // after the flip the normal is +Y and the corner (256, 0, 256) native is (1, 0, 1).
        Assert.Contains(positions, p => p == new Vector3(1, 0, 1));
        var normals = primitive.GetVertexAccessor("NORMAL").AsVector3Array();
        Assert.All(normals, n => Assert.Equal(new Vector3(0, 1, 0), n));

        // 64 texels over a 32-pixel texture is UV 2.0.
        Assert.Contains(uvs, uv => uv == new Vector2(2, 2));
        Assert.Contains(uvs, uv => uv == new Vector2(0, 0));
    }

    [Fact]
    public void Write_CreatesTheOutputDirectory()
    {
        var mesh = XnGineMeshDecomposer.Decompose(XnGineMesh.Parse(XnGineMeshFixture.Quad(), 7));
        var directory = Path.Combine(Path.GetTempPath(), "bmt-glb-" + Guid.NewGuid().ToString("N"), "nested");
        var path = Path.Combine(directory, "7.glb");
        try
        {
            XnGineMeshGlbExporter.Write(mesh, path);
            Assert.True(File.Exists(path));
            Assert.Equal("mesh_7", Assert.Single(ModelRoot.Load(path).LogicalMeshes).Name);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(directory)!, true);
        }
    }
}