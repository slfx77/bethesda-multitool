using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Geometry;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NifPackedDataExtractorTests
{
    [Fact]
    public void NormalizeDecodedBoneWeights_CollapsesXboxSentinelFourthWeight()
    {
        var x = 0.522f;
        var y = 0.368f;
        var z = 0.110f;
        var w = 1.0f;

        NifPackedDataExtractor.NormalizeDecodedBoneWeights(ref x, ref y, ref z, ref w);

        Assert.InRange(x, 0.521f, 0.523f);
        Assert.InRange(y, 0.367f, 0.369f);
        Assert.InRange(z, 0.109f, 0.111f);
        Assert.Equal(0f, w);
        Assert.InRange(x + y + z + w, 0.999f, 1.001f);
    }

    [Fact]
    public void NormalizeDecodedBoneWeights_PreservesRealFourthInfluence()
    {
        var x = 0.125f;
        var y = 0.250f;
        var z = 0.375f;
        var w = 0.250f;

        NifPackedDataExtractor.NormalizeDecodedBoneWeights(ref x, ref y, ref z, ref w);

        Assert.InRange(x, 0.124f, 0.126f);
        Assert.InRange(y, 0.249f, 0.251f);
        Assert.InRange(z, 0.374f, 0.376f);
        Assert.InRange(w, 0.249f, 0.251f);
        Assert.InRange(x + y + z + w, 0.999f, 1.001f);
    }

    [Theory]
    [InlineData(@"Sample\Meshes\meshes_360_final\meshes\armor\cass\cass_companion.nif")]
    [InlineData(@"Sample\Meshes\meshes_360_final\meshes\armor\leatherarmor\f\outfitf.nif")]
    public void XboxPackedBoneWeights_KnownProblemMeshesStayNormalized(string relativePath)
    {
        var nifPath = SampleFileFixture.FindSamplePath(relativePath);
        Assert.SkipWhen(nifPath is null, $"Sample NIF not available: {relativePath}");

        var data = File.ReadAllBytes(nifPath!);
        var info = Assert.IsType<NifInfo>(NifParser.Parse(data));
        Assert.True(info.IsBigEndian);

        var weightSums = new List<float>();
        foreach (var block in info.Blocks.Where(block => block.TypeName == "BSPackedAdditionalGeometryData"))
        {
            var packed = NifPackedDataExtractor.Extract(data, block.DataOffset, block.Size, info.IsBigEndian);
            if (packed?.BoneWeights == null)
            {
                continue;
            }

            for (var vertexIndex = 0; vertexIndex < packed.NumVertices; vertexIndex++)
            {
                var baseIndex = vertexIndex * 4;
                var sum =
                    packed.BoneWeights[baseIndex + 0] +
                    packed.BoneWeights[baseIndex + 1] +
                    packed.BoneWeights[baseIndex + 2] +
                    packed.BoneWeights[baseIndex + 3];
                if (sum > 0.0001f)
                {
                    weightSums.Add(sum);
                }
            }
        }

        Assert.NotEmpty(weightSums);
        Assert.All(weightSums, sum => Assert.InRange(sum, 0.999f, 1.001f));
    }
}
