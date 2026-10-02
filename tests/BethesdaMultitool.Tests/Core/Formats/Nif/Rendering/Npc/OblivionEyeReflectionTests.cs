using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12.Shader;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class OblivionEyeReflectionTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData(" 1", false)]
    [InlineData("1 ", false)]
    [InlineData("1", true)]
    public void EyeComparisonRequiresTheExplicitRequest(string? request, bool expected)
    {
        Assert.Equal(expected, BethesdaViewerOblivionEyePolicy.IsEnabledFor(
            BethesdaViewerOblivionEyePolicy.IsRequested(request), BethesdaGame.Oblivion,
            BethesdaViewerScenePurpose.NpcAppearance, true, true, false));
    }

    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, BethesdaViewerScenePurpose.NpcAppearance, true, true, false)]
    [InlineData(BethesdaGame.Oblivion, BethesdaViewerScenePurpose.RawNif, true, true, false)]
    [InlineData(BethesdaGame.Oblivion, BethesdaViewerScenePurpose.CreatureAppearance, true, true, false)]
    [InlineData(BethesdaGame.Oblivion, BethesdaViewerScenePurpose.NpcAppearance, false, true, false)]
    [InlineData(BethesdaGame.Oblivion, BethesdaViewerScenePurpose.NpcAppearance, true, false, false)]
    [InlineData(BethesdaGame.Oblivion, BethesdaViewerScenePurpose.NpcAppearance, true, true, true)]
    internal void OtherSourcesAndDrawStatesCannotEnterTheEyeComparison(BethesdaGame game,
        BethesdaViewerScenePurpose purpose, bool source, bool opaque, bool billboard)
    {
        Assert.False(BethesdaViewerOblivionEyePolicy.IsEnabledFor(true, game, purpose, source, opaque, billboard));
    }

    [Theory]
    [InlineData(1.2f, 1f)]
    [InlineData(131.2f, 1f)]
    [InlineData(161.2f, 0.5f)]
    [InlineData(191.2f, 0f)]
    [InlineData(250f, 0f)]
    public void EyeLodUsesDistanceToTheSphereSurface(float distance, float expected)
    {
        Assert.Equal(expected,
            BethesdaViewerOblivionEyePolicy.DistanceFade(new Vector3(distance, 0, 0), Vector3.Zero, 1.2f),
            5);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1f)]
    [InlineData(0f)]
    public void InvalidEyeBoundsCannotContribute(float radius)
    {
        Assert.Equal(0f, BethesdaViewerOblivionEyePolicy.DistanceFade(Vector3.One, Vector3.Zero, radius));
    }

    [Fact]
    public void CubeRequestKeepsTheOrdinary2DTextureAndSixIndependentFaceCopies()
    {
        var bytes = Enumerable.Range(0, 2048).Select(index => (byte)(index % 251)).ToArray();
        var source = new GpuTexturePayload(GpuTexturePayloadFormat.BC1, 64, 64,
            [new GpuTextureMipPayload(64, 64, bytes)]);
        var requested = new List<string>();
        using var resolver = new NifGpuTextureResolver(path =>
        {
            requested.Add(path);
            return source;
        });
        var cube = Assert.IsType<GpuTexturePayload>(resolver.GetTexture(OblivionEyeCubePayload.RequestPath));
        Assert.True(cube.IsCubemap);
        Assert.Equal(6, cube.MipLevels.Count);
        Assert.Equal(1, cube.MipCount);
        Assert.Equal(12288, cube.ByteSize);
        Assert.All(cube.MipLevels, mip =>
        {
            Assert.Equal(bytes, mip.Bytes);
            Assert.NotSame(bytes, mip.Bytes);
        });
        cube.MipLevels[0].Bytes[0] = 255;
        Assert.Equal(0, bytes[0]);
        Assert.Equal(0, cube.MipLevels[1].Bytes[0]);
        Assert.Same(source, resolver.GetTexture(OblivionEyeCubePayload.SourcePath));
        Assert.False(source.IsCubemap);
        Assert.All(requested, path => Assert.Equal(OblivionEyeCubePayload.SourcePath, path));
    }

    [Fact]
    public void MissingOrIncompatibleImagesCannotBeRelabeledAsCubes()
    {
        Assert.Null(OblivionEyeCubePayload.Create(null));
        Assert.Null(OblivionEyeCubePayload.Create(new GpuTexturePayload(GpuTexturePayloadFormat.BC1, 64, 64, [])));
        Assert.Null(OblivionEyeCubePayload.Create(new GpuTexturePayload(GpuTexturePayloadFormat.BC1, 64, 64,
            [new GpuTextureMipPayload(64, 64, [0])])));
        Assert.Null(OblivionEyeCubePayload.Create(new GpuTexturePayload(GpuTexturePayloadFormat.Rgba8, 64, 64,
            [new GpuTextureMipPayload(64, 64, new byte[16384])])));
    }

    [Fact]
    public void CloneAndNativeDecodePreserveEyeProvenanceWithoutAliasingGeometry()
    {
        var source = new RenderableSubmesh
        {
            Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0], Normals = [0, 0, 1, 0, 0, 1, 0, 0, 1],
            UVs = [0, 0, 1, 0, 0, 1], Triangles = [0, 1, 2], HasReviewedOblivionEyeSource = true,
            OblivionEyeBounds = new NifLocalBounds(new Vector3(2, 3, 4), 5f)
        };
        var clone = RenderableSubmeshCloner.DeepClone(source);
        source.HasReviewedOblivionEyeSource = false;
        source.OblivionEyeBounds = null;
        Assert.NotSame(source.Positions, clone.Positions);
        var scene = new BethesdaViewerScene("eye", BethesdaViewerScenePurpose.NpcAppearance,
            game: BethesdaGame.Oblivion);
        var node = scene.AddNode("eye", BethesdaViewerScene.RootNodeIndex, Matrix4x4.Identity,
            Matrix4x4.Identity, BethesdaViewerNodeRole.Attachment);
        scene.MeshParts.Add(new BethesdaViewerMeshPart { Name = "eye", NodeIndex = node, Submesh = clone });
        var decoded = BethesdaViewerSceneDecoder12.Decode(scene);
        clone.HasReviewedOblivionEyeSource = false;
        clone.OblivionEyeBounds = null;
        var posed = BethesdaViewerScenePoseMaterializer12.Materialize(decoded);
        Assert.True(Assert.Single(posed.Source.MeshParts).NativeSemantics.HasReviewedOblivionEyeSource);
        Assert.Equal(new NifLocalBounds(new Vector3(2, 3, 4), 5f),
            BethesdaViewerScenePoseMaterializer12.ResolveReviewedEyeBounds(decoded, 0));
        Assert.Empty(posed.UnsupportedMeshParts);
    }

    [Fact]
    [Trait("Category", TestCategories.ShaderCompile)]
    public void CompiledEyeVaryingsLinkAcrossTheDedicatedShaderPair()
    {
        ShaderCompileTestGuard.SkipUnlessEnabled();
        using var vertex = Compiler.Reflect<ID3D12ShaderReflection>(GpuShaderCompiler12.Compile(
            "reference_oblivion_eye.vert.hlsl", "main", "vs_5_1").Span);
        using var pixel = Compiler.Reflect<ID3D12ShaderReflection>(GpuShaderCompiler12.Compile(
            "reference_oblivion_eye.frag.hlsl", "main", "ps_5_1").Span);
        var outputs = new List<ShaderParameterDescription>();
        for (var index = 0u; index < vertex.Description.OutputParameters; index++)
            outputs.Add(vertex.GetOutputParameterDescription(index));
        var consumed = 0;
        for (var index = 0u; index < pixel.Description.InputParameters; index++)
        {
            var input = pixel.GetInputParameterDescription(index);
            if (input.SystemValueType != SystemValueType.Undefined || (int)input.ReadWriteMask == 0) continue;
            var output = Assert.Single(outputs, item => item.SemanticIndex == input.SemanticIndex &&
                                                        string.Equals(item.SemanticName, input.SemanticName,
                                                            StringComparison.OrdinalIgnoreCase));
            Assert.Equal(output.Register, input.Register);
            Assert.Equal(output.ComponentType, input.ComponentType);
            Assert.Equal((int)input.ReadWriteMask, (int)input.ReadWriteMask & (int)output.UsageMask);
            consumed++;
        }

        Assert.Equal(2, consumed);
        Assert.Equal(1u, pixel.Description.OutputParameters);
        Assert.Equal(15, (int)pixel.GetOutputParameterDescription(0).UsageMask);
    }
}
