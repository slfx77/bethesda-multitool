using BethesdaMultitool.Core.Formats.Nif.Materials;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Materials;

public sealed class StarfieldMaterialEffectPolicyTests
{
    private const string MaterialPath = @"materials\test\orm.mat";
    private const string OpacityPath = @"Data\Textures\Test\visor_opacity.dds";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveEffectPolicy_DecodesAuthoredStaticGlassAlphaBlend(bool useDiffChunks)
    {
        var db = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(StarfieldMaterialOrmPolicyTests.BuildDatabase(
                useDiffChunks,
                shaderRoute: "Effect",
                shaderModel: "1LayerEffectGlass",
                effectSettings: Glass())));

        var policy = db.ResolveEffectPolicy(MaterialPath);

        Assert.Equal(db.ComponentTableCount, db.ComponentChunkCount);
        Assert.True(policy.IsResolved);
        Assert.True(policy.HasEffectSettings);
        Assert.False(policy.HasMalformedSettings);
        Assert.Equal(StarfieldMaterialShaderRoute.Effect, policy.ShaderRoute);
        Assert.True(policy.IsGlass);
        Assert.False(policy.HasFrosting);
        Assert.False(policy.UsesVertexColor);
        Assert.Equal(0.35f, policy.MaterialOverallAlpha);
        Assert.Equal(StarfieldMaterialEffectBlendMode.AlphaBlend, policy.BlendingMode);
        Assert.Equal(OpacityPath, policy.OpacitySlot.TexturePath);
        Assert.True(policy.TryResolveStaticGlassAlphaBlend(out var state));
        Assert.Equal(0.35f, state.MaterialOverallAlpha);
        Assert.Equal(policy.OpacitySlot, state.OpacitySlot);
    }

    [Theory]
    [InlineData(false, 0, false, false, true)]
    [InlineData(true, 0, false, false, true)]
    [InlineData(false, 1, false, false, false)]
    [InlineData(true, 1, false, false, false)]
    [InlineData(false, 0, true, false, false)]
    [InlineData(true, 0, true, false, false)]
    [InlineData(false, 0, false, true, false)]
    [InlineData(true, 0, false, true, false)]
    public void ResolveEffectPolicy_DecodesOpacityComponentAndAdmitsOnlyStaticLayerZero(
        bool useDiffChunks,
        int sourceLayer,
        bool secondLayerActive,
        bool thirdLayerActive,
        bool expectedSupported)
    {
        var db = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(StarfieldMaterialOrmPolicyTests.BuildDatabase(
                useDiffChunks,
                shaderRoute: "Effect",
                shaderModel: "1LayerEffectGlass",
                effectSettings: Glass(),
                effectOpacity: new StarfieldEffectOpacityFixture(
                    sourceLayer,
                    secondLayerActive,
                    thirdLayerActive))));

        var policy = db.ResolveEffectPolicy(MaterialPath);

        Assert.True(policy.IsResolved);
        Assert.False(policy.HasMalformedSettings);
        Assert.Equal(sourceLayer, policy.OpacitySourceLayer);
        Assert.Equal(secondLayerActive || thirdLayerActive, policy.HasSecondaryOpacityLayers);
        Assert.Equal(expectedSupported, policy.TryResolveStaticGlassAlphaBlend(out _));
    }

    [Theory]
    [InlineData("deferred")]
    [InlineData("not-glass")]
    [InlineData("frosting")]
    [InlineData("vertex-color")]
    [InlineData("additive")]
    [InlineData("malformed-blend")]
    [InlineData("extra-layer")]
    [InlineData("flipbook")]
    public void ResolveEffectPolicy_RejectsEffectCompositionCoreGltfCannotRepresent(string unsupported)
    {
        var effect = unsupported switch
        {
            "not-glass" => Glass() with { IsGlass = false },
            "frosting" => Glass() with { HasFrosting = true },
            "vertex-color" => Glass() with { UsesVertexColor = true },
            "additive" => Glass() with { BlendingMode = "Additive" },
            "malformed-blend" => Glass() with { BlendingMode = "InventedBlend" },
            _ => Glass()
        };
        var db = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(StarfieldMaterialOrmPolicyTests.BuildDatabase(
                useDiffChunks: true,
                unsupported: unsupported is "extra-layer" or "flipbook" ? unsupported : null,
                shaderRoute: unsupported == "deferred" ? "Deferred" : "Effect",
                shaderModel: "1LayerEffectGlass",
                effectSettings: effect)));

        var policy = db.ResolveEffectPolicy(MaterialPath);

        Assert.True(policy.IsResolved);
        Assert.False(policy.TryResolveStaticGlassAlphaBlend(out _));
        Assert.Equal(unsupported == "malformed-blend", policy.HasMalformedSettings);
    }

    [Fact]
    public void ResolveEffectPolicy_NoEffectSettingsDoesNotInferGlassFromShaderModelName()
    {
        var db = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(StarfieldMaterialOrmPolicyTests.BuildDatabase(
                useDiffChunks: true,
                shaderRoute: "Effect",
                shaderModel: "1LayerEffectGlass")));

        var policy = db.ResolveEffectPolicy(MaterialPath);

        Assert.True(policy.IsResolved);
        Assert.False(policy.HasEffectSettings);
        Assert.False(policy.IsGlass);
        Assert.False(policy.TryResolveStaticGlassAlphaBlend(out _));
    }

    private static StarfieldEffectSettingsFixture Glass() =>
        new(
            IsGlass: true,
            HasFrosting: false,
            UsesVertexColor: false,
            MaterialOverallAlpha: 0.35f,
            BlendingMode: "AlphaBlend",
            OpacityTexturePath: OpacityPath);
}
