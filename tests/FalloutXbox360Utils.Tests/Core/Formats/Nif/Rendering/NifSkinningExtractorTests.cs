using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Skinning;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NifSkinningExtractorTests
{
    [Fact]
    public void BuildPerVertexInfluences_ClampsToTopFourAndNormalizesWeights()
    {
        var skinData = new NifSkinData
        {
            Bones =
            [
                new NifBoneSkinInfo { VertexWeights = [(0, 0.50f)] },
                new NifBoneSkinInfo { VertexWeights = [(0, 0.25f)] },
                new NifBoneSkinInfo { VertexWeights = [(0, 0.15f)] },
                new NifBoneSkinInfo { VertexWeights = [(0, 0.05f)] },
                new NifBoneSkinInfo { VertexWeights = [(0, 0.05f)] }
            ]
        };

        var influences = NifSkinningExtractor.BuildPerVertexInfluences(skinData, 1);

        var vertexInfluences = Assert.Single(influences);
        Assert.Equal(4, vertexInfluences.Length);
        Assert.Equal([0, 1, 2, 3], vertexInfluences.Select(i => i.BoneIdx));
        Assert.Equal(1f, vertexInfluences.Sum(i => i.Weight), 3);
    }

    [Fact]
    public void ApplySkinningPositionsDqs_MatchesLbsForIdenticalTransforms()
    {
        var positions = new[] { 0f, 0f, 0f, 1f, 0f, 0f };
        var influences = new (int BoneIdx, float Weight)[][]
        {
            [(0, 0.4f), (1, 0.6f)],
            [(0, 0.4f), (1, 0.6f)]
        };
        var transform = Matrix4x4.CreateTranslation(3f, 4f, 5f);
        var boneSkinMatrices = new[] { transform, transform };

        var lbs = NifSkinningExtractor.ApplySkinningPositions(
            positions,
            influences,
            boneSkinMatrices);
        var dqs = NifSkinningExtractor.ApplySkinningPositionsDQS(
            positions,
            influences,
            boneSkinMatrices);

        Assert.Equal(lbs.Length, dqs.Length);
        for (var i = 0; i < lbs.Length; i++)
        {
            Assert.Equal(lbs[i], dqs[i], 4);
        }
    }
}