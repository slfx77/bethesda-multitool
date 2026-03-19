using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class TriNifGeometryInspectorTests
{
    [Fact]
    public void Inspect_HeadHumanSiblingNif_HasExactGeometryMatch()
    {
        var triPath = SampleFileFixture.FindSamplePath(
            @"Sample\Meshes\meshes_pc\meshes\characters\head\headhuman.tri");
        var nifPath = SampleFileFixture.FindSamplePath(
            @"Sample\Meshes\meshes_pc\meshes\characters\head\headhuman.nif");
        Assert.SkipWhen(triPath == null || nifPath == null, "Sample headhuman TRI/NIF not available.");

        var tri = Assert.IsType<TriParser>(TriParser.Parse(File.ReadAllBytes(triPath!)));
        var inspection = Assert.IsType<TriNifGeometryInspection>(
            TriNifGeometryInspector.Inspect(File.ReadAllBytes(nifPath!), tri));

        Assert.True(inspection.HasExactGeometryMatch);
        Assert.True(inspection.HasDeclaredTriangleCountMatch);
        Assert.True(inspection.HasVertexCountMatch);
        Assert.Equal(1, inspection.ExactMatchingGeometryBlockCount);
        Assert.Equal(1, inspection.DeclaredTriangleMatchingGeometryBlockCount);
        Assert.Equal(1, inspection.VertexMatchingGeometryBlockCount);
        Assert.Single(inspection.GeometryBlocks);
        Assert.Equal(tri.VertexCount, inspection.GeometryBlocks[0].VertexCount);
        Assert.Equal(tri.TriangleCount, inspection.GeometryBlocks[0].TriangleCount);
        Assert.Equal(tri.TriangleCount, inspection.GeometryBlocks[0].DeclaredTriangleCount);
    }

    [Fact]
    public void Inspect_EyeLeftHumanSiblingNif_HasVertexCountMatchButNotExactTriangleMatch()
    {
        var triPath = SampleFileFixture.FindSamplePath(
            @"Sample\Meshes\meshes_pc\meshes\characters\head\eyelefthuman.tri");
        var nifPath = SampleFileFixture.FindSamplePath(
            @"Sample\Meshes\meshes_pc\meshes\characters\head\eyelefthuman.nif");
        Assert.SkipWhen(triPath == null || nifPath == null, "Sample eyelefthuman TRI/NIF not available.");

        var tri = Assert.IsType<TriParser>(TriParser.Parse(File.ReadAllBytes(triPath!)));
        var inspection = Assert.IsType<TriNifGeometryInspection>(
            TriNifGeometryInspector.Inspect(File.ReadAllBytes(nifPath!), tri));

        Assert.False(inspection.HasExactGeometryMatch);
        Assert.True(inspection.HasDeclaredTriangleCountMatch);
        Assert.True(inspection.HasVertexCountMatch);
        Assert.Equal(0, inspection.ExactMatchingGeometryBlockCount);
        Assert.Equal(1, inspection.DeclaredTriangleMatchingGeometryBlockCount);
        Assert.Equal(1, inspection.VertexMatchingGeometryBlockCount);
        Assert.Single(inspection.GeometryBlocks);
        Assert.Equal(tri.VertexCount, inspection.GeometryBlocks[0].VertexCount);
        Assert.Equal(81, inspection.GeometryBlocks[0].TriangleCount);
        Assert.Equal(tri.TriangleCount, inspection.GeometryBlocks[0].DeclaredTriangleCount);
        Assert.Equal(116, inspection.GeometryBlocks[0].CandidateTriangleWindowCount);
        Assert.Equal(35, inspection.GeometryBlocks[0].DegenerateTriangleCount);
    }
}
