using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;
using Xunit;
using Xunit.Sdk;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NifTextureAnimationEvaluatorTests
{
    [Theory]
    [InlineData(@"Sample\Meshes\meshes_pc\meshes\weapons\hand2hand\powerfistrigid.nif")]
    [InlineData(@"Sample\Meshes\meshes_360_final\meshes\weapons\hand2hand\powerfistrigid.nif")]
    public void PowerFistRigid_AnimatedBaseTextureController_ResolvesMidCycleTranslateU(string relativePath)
    {
        var (data, nif) = LoadNif(relativePath);

        var animatedShape = FindFirstAnimatedBaseUvShape(data, nif);

        Assert.True(
            animatedShape.ShapeName.Contains("spraymesh", StringComparison.OrdinalIgnoreCase),
            $"Expected a spray mesh, found '{animatedShape.ShapeName}'.");
        Assert.Equal(0.5f, animatedShape.Transform.TranslationU, 3);
        Assert.Equal(0f, animatedShape.Transform.TranslationV, 3);
        Assert.Equal(1f, animatedShape.Transform.ScaleU, 3);
        Assert.Equal(1f, animatedShape.Transform.ScaleV, 3);
        Assert.Equal(0f, animatedShape.Transform.RotationRadians, 3);
        Assert.True(animatedShape.Transform.HasNonIdentity);
    }

    [Theory]
    [InlineData(@"Sample\Meshes\meshes_pc\meshes\weapons\hand2hand\powerfistrigid.nif")]
    [InlineData(@"Sample\Meshes\meshes_360_final\meshes\weapons\hand2hand\powerfistrigid.nif")]
    public void PowerFistRigid_Extraction_AppliesAnimatedBaseTextureTransform(string relativePath)
    {
        var (data, nif) = LoadNif(relativePath);
        var animatedShape = FindFirstAnimatedBaseUvShape(data, nif);

        var expectedUvs = (float[])animatedShape.RawSubmesh.UVs!.Clone();
        NifTextureAnimationEvaluator.ApplyInPlace(expectedUvs, animatedShape.Transform);

        Assert.NotEqual(animatedShape.RawSubmesh.UVs[0], expectedUvs[0]);

        var extracted = NifGeometryExtractor.Extract(data, nif);
        Assert.NotNull(extracted);

        var extractedSubmesh = extracted!.Submeshes.FirstOrDefault(submesh =>
            string.Equals(submesh.ShapeName, animatedShape.ShapeName, StringComparison.OrdinalIgnoreCase) &&
            submesh.VertexCount == animatedShape.RawSubmesh.VertexCount &&
            submesh.UVs != null);

        Assert.NotNull(extractedSubmesh);
        Assert.NotNull(extractedSubmesh!.UVs);
        Assert.Equal(expectedUvs.Length, extractedSubmesh.UVs!.Length);

        for (var i = 0; i < expectedUvs.Length; i++)
        {
            Assert.Equal(expectedUvs[i], extractedSubmesh.UVs[i], 4);
        }
    }

    [Fact]
    public void PowerFistRigid_ConvertedXboxSprayVertexColors_MatchPc()
    {
        var (pcData, pcNif) = LoadNif(@"Sample\Meshes\meshes_pc\meshes\weapons\hand2hand\powerfistrigid.nif");
        var (xboxData, xboxNif) =
            LoadNif(@"Sample\Meshes\meshes_360_final\meshes\weapons\hand2hand\powerfistrigid.nif");

        var pcModel = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(pcData, pcNif));
        var xboxModel = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(xboxData, xboxNif));

        var pcSprays = pcModel.Submeshes
            .Where(IsSprayMesh)
            .OrderBy(submesh => submesh.ShapeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var xboxSprays = xboxModel.Submeshes
            .Where(IsSprayMesh)
            .OrderBy(submesh => submesh.ShapeName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(pcSprays);
        Assert.Equal(pcSprays.Count, xboxSprays.Count);

        for (var i = 0; i < pcSprays.Count; i++)
        {
            Assert.Equal(pcSprays[i].ShapeName, xboxSprays[i].ShapeName, true);
            Assert.Equal(pcSprays[i].VertexCount, xboxSprays[i].VertexCount);
            Assert.NotNull(pcSprays[i].VertexColors);
            Assert.NotNull(xboxSprays[i].VertexColors);
            var expectedColors = pcSprays[i].VertexColors!;
            var actualColors = xboxSprays[i].VertexColors!;
            Assert.Equal(expectedColors.Length, actualColors.Length);

            for (var channelIndex = 0; channelIndex < expectedColors.Length; channelIndex++)
            {
                var expected = expectedColors[channelIndex];
                var actual = actualColors[channelIndex];
                if ((channelIndex & 3) == 3)
                {
                    Assert.InRange(actual, Math.Max(0, expected - 1), Math.Min(255, expected + 1));
                }
                else
                {
                    Assert.Equal(expected, actual);
                }
            }
        }
    }

    private static AnimatedShapeSample FindFirstAnimatedBaseUvShape(byte[] data, NifInfo nif)
    {
        var nodeChildren = new Dictionary<int, List<int>>();
        var nodeTransforms = new Dictionary<int, Matrix4x4>();
        var shapeDataMap = new Dictionary<int, int>();
        var shapePropertyMap = new Dictionary<int, List<int>>();
        var shapeSkinInstanceMap = new Dictionary<int, int>();

        NifSceneGraphWalker.ClassifyBlocks(
            data,
            nif,
            nodeChildren,
            shapeDataMap,
            shapePropertyMap,
            shapeSkinInstanceMap);
        NifSceneGraphWalker.ComputeWorldTransforms(data, nif, nodeChildren, nodeTransforms);

        foreach (var (shapeIndex, dataIndex) in shapeDataMap.OrderBy(entry => entry.Key))
        {
            if (!shapePropertyMap.TryGetValue(shapeIndex, out var propRefs) ||
                !NifTextureAnimationEvaluator.TryResolveBaseUvTransform(data, nif, propRefs, out var transform))
            {
                continue;
            }

            var shapeName = NifBlockParsers.ReadBlockName(data, nif.Blocks[shapeIndex], nif);
            var rawSubmesh = NifBlockParsers.ExtractSubmesh(
                data,
                nif,
                shapeIndex,
                dataIndex,
                nodeTransforms,
                shapeName);

            if (rawSubmesh?.UVs == null || rawSubmesh.UVs.Length == 0)
            {
                continue;
            }

            return new AnimatedShapeSample(shapeName ?? string.Empty, transform, rawSubmesh);
        }

        throw new XunitException("No animated base-texture shape with UVs was found in powerfistrigid.nif.");
    }

    private static (byte[] Data, NifInfo Info) LoadNif(string relativePath)
    {
        var fullPath = ResolveSamplePath(relativePath);
        var sourceData = File.ReadAllBytes(fullPath);
        var sourceNif = NifParser.Parse(sourceData);
        Assert.NotNull(sourceNif);

        if (sourceNif!.IsBigEndian)
        {
            var conversion = NifConverter.Convert(sourceData);
            Assert.True(conversion.Success, conversion.ErrorMessage);
            Assert.NotNull(conversion.OutputData);

            var convertedNif = NifParser.Parse(conversion.OutputData!);
            Assert.NotNull(convertedNif);
            return (conversion.OutputData!, convertedNif!);
        }

        return (sourceData, sourceNif);
    }

    private static string ResolveSamplePath(string relativePath)
    {
        if (File.Exists(relativePath))
        {
            return Path.GetFullPath(relativePath);
        }

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        throw new FileNotFoundException($"Could not locate sample asset: {relativePath}");
    }

    private static bool IsSprayMesh(RenderableSubmesh submesh)
    {
        return submesh.ShapeName?.Contains("SprayMeshConnect", StringComparison.OrdinalIgnoreCase) == true &&
               submesh.VertexColors is { Length: > 0 };
    }

    private sealed record AnimatedShapeSample(
        string ShapeName,
        NifTextureAnimationEvaluator.NifTextureTransformSnapshot Transform,
        RenderableSubmesh RawSubmesh);
}