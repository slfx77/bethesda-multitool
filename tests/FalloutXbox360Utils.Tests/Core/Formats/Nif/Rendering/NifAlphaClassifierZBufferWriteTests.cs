using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Covers the BSShaderFlags2 ZBuffer_Write handling in <see cref="NifAlphaClassifier" />: a shape
///     authored to occlude (e.g. MobileHomeASNV's cabin shell — alpha-blend + alpha-test on an opaque
///     hull with window cutouts) must write depth, so it is routed to the depth-writing opaque/cutout
///     pass rather than the depth-write-off blend pass where its interior faces show through the roof.
/// </summary>
public sealed class NifAlphaClassifierZBufferWriteTests
{
    [Fact]
    public void Classify_BlendWithZBufferWriteAndAlphaTest_RoutesToCutoutAndWritesDepth()
    {
        var submesh = CreateSubmesh(hasAlphaBlend: true, hasAlphaTest: true, shaderFlags2: 0x1u);

        var state = NifAlphaClassifier.Classify(submesh, diffuseTexture: null);

        Assert.Equal(NifAlphaRenderMode.Cutout, state.RenderMode);
        Assert.True(state.WritesDepth);
        Assert.False(state.HasAlphaBlend);
        Assert.True(state.HasAlphaTest);
    }

    [Fact]
    public void Classify_BlendWithZBufferWriteNoAlphaTest_RoutesToOpaque()
    {
        var submesh = CreateSubmesh(hasAlphaBlend: true, hasAlphaTest: false, shaderFlags2: 0x1u);

        var state = NifAlphaClassifier.Classify(submesh, diffuseTexture: null);

        Assert.Equal(NifAlphaRenderMode.Opaque, state.RenderMode);
        Assert.True(state.WritesDepth);
        Assert.False(state.HasAlphaBlend);
    }

    [Fact]
    public void Classify_BlendWithoutZBufferWrite_StaysBlendAndSkipsDepth()
    {
        // Genuine transparency (glass, decals, smoke) leaves the ZBuffer_Write bit clear.
        var submesh = CreateSubmesh(hasAlphaBlend: true, hasAlphaTest: false, shaderFlags2: 0x0u);

        var state = NifAlphaClassifier.Classify(submesh, diffuseTexture: null);

        Assert.Equal(NifAlphaRenderMode.Blend, state.RenderMode);
        Assert.False(state.WritesDepth);
        Assert.True(state.HasAlphaBlend);
    }

    [Fact]
    public void Classify_BlendWithZBufferWriteButNoShaderMetadata_StaysBlend()
    {
        var submesh = CreateSubmesh(hasAlphaBlend: true, hasAlphaTest: false, shaderFlags2: null);

        var state = NifAlphaClassifier.Classify(submesh, diffuseTexture: null);

        Assert.Equal(NifAlphaRenderMode.Blend, state.RenderMode);
        Assert.False(state.WritesDepth);
    }

    private static RenderableSubmesh CreateSubmesh(bool hasAlphaBlend, bool hasAlphaTest, uint? shaderFlags2)
        => new()
        {
            Positions = [],
            Triangles = [],
            HasAlphaBlend = hasAlphaBlend,
            HasAlphaTest = hasAlphaTest,
            AlphaTestThreshold = 50,
            AlphaTestFunction = 4,
            MaterialAlpha = 1f,
            ShaderMetadata = shaderFlags2 is null
                ? null
                : new NifShaderTextureMetadata
                {
                    PropertyType = "BSShaderPPLightingProperty",
                    ShaderFlags2 = shaderFlags2
                }
        };
}
