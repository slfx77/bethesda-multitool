using System.Numerics;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class FnvGeometryArtifactRetailTests
{
    private const string MeshesBsaRelative =
        @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa";
    private const string TexturesBsaRelative =
        @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Textures.bsa";
    private const string Textures2BsaRelative =
        @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Textures2.bsa";

    [Fact]
    public void CampGolfCourse_RetailDecalStackRemainsClassifiedAndTopologicallyValid()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        using var meshes = ArchiveReader.Open(FindRetailArchive(MeshesBsaRelative, "meshes"));
        using var textures = OpenRetailTextures();
        var model = Extract(meshes, textures,
            @"meshes\dungeons\nv_campgolfcourse\nvcampgolfcourse.nif");

        Assert.Equal(5, model.Submeshes.Count);
        Assert.All(model.Submeshes, AssertValidTopology);

        var decal = model.Submeshes[4];
        Assert.Equal(26, decal.SourceBlockIndex);
        Assert.Equal("NVCampGolfCourse:7", decal.ShapeName);
        Assert.Equal("BSShaderNoLightingProperty", decal.ShaderMetadata?.PropertyType);
        Assert.Equal(0x8E000000u, decal.ShaderMetadata?.ShaderFlags);
        Assert.True(decal.IsDecal);
        Assert.True(decal.HasAlphaBlend);
        Assert.False(decal.HasAlphaTest);
        Assert.Equal(304, decal.VertexCount);
        Assert.Equal(228, decal.TriangleCount);

        var components = Components(decal);
        Assert.Equal(38, components.Count);
        Assert.All(components, component =>
        {
            Assert.Equal(8, component.VertexCount);
            Assert.Equal(6, component.TriangleCount);
        });

        // The front-facing authored strips are genuinely near-coplanar with the backing geometry.
        // This pins the asset boundary that requires the renderer's decal route without pretending
        // that static extraction alone can establish whether the current live depth bias is adequate.
        var backing = model.Submeshes[2];
        var frontDecalY = FrontPlaneCoordinates(decal);
        var frontBackingY = FrontPlaneCoordinates(backing);
        var nearestSeparation = frontDecalY
            .SelectMany(left => frontBackingY.Select(right => MathF.Abs(left - right)))
            .Where(distance => distance > 1e-4f)
            .Min();
        Assert.InRange(nearestSeparation, 0.03f, 0.1f);
    }

    [Fact]
    public void RetailGrass_TopologyAndAuthoredWindWeightsStayFiniteAndBounded()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        using var meshes = ArchiveReader.Open(FindRetailArchive(MeshesBsaRelative, "meshes"));
        using var textures = OpenRetailTextures();
        var modelPaths = meshes.ListFiles()
            .Select(entry => entry.FullPath)
            .Where(path => path.StartsWith(@"meshes\landscape\grass\", StringComparison.OrdinalIgnoreCase) &&
                           path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(17, modelPaths.Length);
        foreach (var modelPath in modelPaths)
        {
            var submesh = Assert.Single(Extract(meshes, textures, modelPath).Submeshes);
            Assert.Equal("TallGrassShaderProperty", submesh.ShaderMetadata?.PropertyType);
            Assert.False(submesh.UseVertexAlphaForOpacity);
            Assert.NotNull(submesh.VertexColors);
            Assert.Equal(submesh.VertexCount * 4, submesh.VertexColors!.Length);
            AssertValidTopology(submesh);

            var vertices = GpuMeshUploader.BuildVertices(
                submesh,
                preserveAuthoredVertexAlpha: true);
            Assert.Equal(submesh.VertexCount, vertices.Length);

            var deformed = new Vector3[3];
            for (var triangle = 0; triangle < submesh.Triangles.Length; triangle += 3)
            {
                for (var corner = 0; corner < 3; corner++)
                {
                    var index = submesh.Triangles[triangle + corner];
                    var vertex = vertices[index];
                    Assert.InRange(vertex.VertexColor.W, 0f, 1f);
                    var offset = FnvTallGrassWind.EvaluateWorldOffset(
                        new Vector2(MathF.PI * 64f, 0f),
                        FnvTallGrassWind.GrassWindMagnitudeMaxDefault,
                        timerValue: 0.0,
                        grassWaveMultiplier: 15f,
                        authoredVertexAlpha: vertex.VertexColor.W);
                    deformed[corner] = vertex.Position + new Vector3(offset, 0f);
                    Assert.True(IsFinite(deformed[corner]));
                    Assert.Equal(vertex.Position.Z, deformed[corner].Z);
                }

                // Every vertex shares one placement phase; differing authored weights can bend a
                // blade by at most the recovered 125-unit amplitude, never launch a rogue vertex.
                var staticMaximumEdge = MaximumEdge(
                    vertices[submesh.Triangles[triangle]].Position,
                    vertices[submesh.Triangles[triangle + 1]].Position,
                    vertices[submesh.Triangles[triangle + 2]].Position);
                Assert.True(MaximumEdge(deformed[0], deformed[1], deformed[2]) <=
                            staticMaximumEdge + FnvTallGrassWind.GrassWindMagnitudeMaxDefault + 1e-3f);
            }
        }
    }

    private static NifRenderableModel Extract(
        ArchiveReader meshes,
        NifTextureResolver textures,
        string modelPath)
    {
        var data = Assert.IsType<byte[]>(meshes.ReadFile(modelPath));
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        return Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            data, nif, textures, skipSkinning: true, treatRootsAsIdentity: true,
            collectBillboards: true, dropBoneAttachedShapes: true));
    }

    private static void AssertValidTopology(RenderableSubmesh submesh)
    {
        Assert.NotEmpty(submesh.Triangles);
        Assert.Equal(0, submesh.Triangles.Length % 3);
        Assert.All(submesh.Triangles, index => Assert.True(index < submesh.VertexCount));
        for (var triangle = 0; triangle < submesh.Triangles.Length; triangle += 3)
        {
            var a = Position(submesh, submesh.Triangles[triangle]);
            var b = Position(submesh, submesh.Triangles[triangle + 1]);
            var c = Position(submesh, submesh.Triangles[triangle + 2]);
            Assert.True(IsFinite(a) && IsFinite(b) && IsFinite(c));
            Assert.True(Vector3.Cross(b - a, c - a).LengthSquared() > 1e-10f);
        }
    }

    private static Vector3 Position(RenderableSubmesh submesh, int index)
    {
        var offset = index * 3;
        return new Vector3(
            submesh.Positions[offset],
            submesh.Positions[offset + 1],
            submesh.Positions[offset + 2]);
    }

    private static float MaximumEdge(Vector3 a, Vector3 b, Vector3 c) =>
        MathF.Max(Vector3.Distance(a, b), MathF.Max(Vector3.Distance(b, c), Vector3.Distance(c, a)));

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static float[] FrontPlaneCoordinates(RenderableSubmesh submesh) =>
        Enumerable.Range(0, submesh.VertexCount)
            .Select(index => Position(submesh, index).Y)
            .Where(y => y < -1800f)
            .Distinct()
            .ToArray();

    private static IReadOnlyList<ComponentSummary> Components(RenderableSubmesh submesh)
    {
        var parent = Enumerable.Range(0, submesh.VertexCount).ToArray();
        int Find(int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }

            return value;
        }

        void Union(int left, int right)
        {
            left = Find(left);
            right = Find(right);
            if (left != right) parent[right] = left;
        }

        for (var i = 0; i < submesh.Triangles.Length; i += 3)
        {
            Union(submesh.Triangles[i], submesh.Triangles[i + 1]);
            Union(submesh.Triangles[i], submesh.Triangles[i + 2]);
        }

        var verticesByRoot = Enumerable.Range(0, submesh.VertexCount)
            .GroupBy(Find)
            .ToDictionary(group => group.Key, group => group.Count());
        var trianglesByRoot = Enumerable.Range(0, submesh.TriangleCount)
            .GroupBy(triangle => Find(submesh.Triangles[triangle * 3]))
            .ToDictionary(group => group.Key, group => group.Count());
        return verticesByRoot
            .Select(pair => new ComponentSummary(pair.Value, trianglesByRoot[pair.Key]))
            .ToArray();
    }

    private NifTextureResolver OpenRetailTextures() => new(
    [
        FindRetailArchive(TexturesBsaRelative, "textures"),
        FindRetailArchive(Textures2BsaRelative, "textures2"),
    ]);

    private static string FindRetailArchive(string relativePath, string label)
    {
        var path = SampleFileFixture.FindSamplePath(relativePath);
        Assert.SkipWhen(path is null, $"FNV PC-final {label} BSA not available");
        return path!;
    }

    private sealed record ComponentSummary(int VertexCount, int TriangleCount);
}
