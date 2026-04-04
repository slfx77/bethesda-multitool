using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class FaceGenSparseTriMorpherTests
{
    [Fact]
    public void Parse_MouthHumanSample_CanDecodeNamedDifferentialRecord()
    {
        var triPath = SampleFileFixture.FindSamplePath(
            @"Sample\Meshes\meshes_pc\meshes\characters\head\mouthhuman.tri");
        Assert.SkipWhen(triPath == null, "Sample mouthhuman.tri not available.");

        var tri = Assert.IsType<TriParser>(TriParser.Parse(File.ReadAllBytes(triPath!)));
        Assert.True(tri.TryGetDifferentialRecord("Aah", out var record));

        var deltas = tri.ReadDifferentialRecordDeltas(record);
        Assert.NotNull(deltas);
        Assert.Equal(tri.VertexCount * 3, deltas.Length);
    }

    [Fact]
    public void ApplyDifferentialWeights_SampleDifferentialRecord_ChangesGeometry()
    {
        var triPath = SampleFileFixture.FindSamplePath(
            @"Sample\Meshes\meshes_pc\meshes\characters\head\mouthhuman.tri");
        Assert.SkipWhen(triPath == null, "Sample mouthhuman.tri not available.");

        var tri = Assert.IsType<TriParser>(TriParser.Parse(File.ReadAllBytes(triPath!)));
        var record = (tri.DifferentialRegionCandidate?.Records.Where(candidate =>
            {
                if (MathF.Abs(candidate.Scale) < 1e-7f)
                {
                    return false;
                }

                var deltas = tri.ReadDifferentialRecordDeltas(candidate);
                return deltas != null && deltas.Any(static delta => delta != 0);
            }) ?? []).First();

        var model = new NifRenderableModel();
        var targetSubmesh = new RenderableSubmesh
        {
            Positions = new float[tri.VertexCount * 3],
            Triangles = [0, 1, 2],
            Normals = new float[tri.VertexCount * 3]
        };
        model.Submeshes.Add(targetSubmesh);

        var before = (float[])targetSubmesh.Positions.Clone();
        var changed = FaceGenSparseTriMorpher.ApplyDifferentialWeights(
            model,
            tri,
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                [record.Name] = 0.5f
            });

        Assert.True(changed);
        Assert.NotEqual(before, targetSubmesh.Positions);
    }

    [Fact]
    public void ResolveStaticNpcWeights_StaticNpcScope_ReturnsNeutralWeights()
    {
        var npc = new NpcAppearance
        {
            NpcFormId = 1,
            BaseHeadNifPath = @"meshes\characters\head\headhuman.nif"
        };

        var weights = FaceGenSparseTriMorpher.ResolveStaticNpcWeights(
            npc,
            @"meshes\characters\head\mouthhuman.nif");

        Assert.Empty(weights);
    }
}
