using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Materials;

public sealed class OblivionOrdinarySpecularPolicyTests
{
    private const string Diffuse = @"textures\clothes\lowerclass\shoes.dds";
    private const string Normal = @"textures\clothes\lowerclass\shoes_n.dds";
    private static readonly Vector4 Candidate = new(0.17f, 0.31f, 0.59f, 12f);
    private static readonly Vector4 Disabled = new(0.17f, 0.31f, 0.59f, 0f);

    [Theory]
    [InlineData((int)GpuTexturePayloadFormat.BC1, 0f)]
    [InlineData((int)GpuTexturePayloadFormat.BC2, 12f)]
    [InlineData((int)GpuTexturePayloadFormat.BC3, 12f)]
    [InlineData((int)GpuTexturePayloadFormat.Rgba8, 12f)]
    [InlineData((int)GpuTexturePayloadFormat.BC4, 12f)]
    [InlineData((int)GpuTexturePayloadFormat.BC5, 12f)]
    [InlineData((int)GpuTexturePayloadFormat.BC7, 12f)]
    [InlineData((int)GpuTexturePayloadFormat.BC4S, 12f)]
    [InlineData((int)GpuTexturePayloadFormat.BC5S, 12f)]
    [InlineData(999, 12f)]
    public void Resolve_UsesTheResidentFormatAndPreservesAuthoredRgb(
        int format, float expectedExponent)
    {
        var actual = OblivionOrdinarySpecularPolicy.Resolve(Candidate, true, true, true, true, (GpuTexturePayloadFormat)format);

        Assert.Equal(new Vector4(0.17f, 0.31f, 0.59f, expectedExponent), actual);
        Assert.Equal(new Vector4(0.17f, 0.31f, 0.59f, 12f), Candidate);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void Resolve_RequiresUsablePathBackedResidentNormalForTheSupportedSubset(
        bool hasBump, bool isPathBacked, bool isResident)
    {
        var actual = OblivionOrdinarySpecularPolicy.Resolve(
            Candidate, true, hasBump, isPathBacked, isResident, GpuTexturePayloadFormat.Rgba8);

        // A resident pinned flat normal is RGBA, but its absent path is not positive normal evidence.
        Assert.Equal(Disabled, actual);
    }

    [Theory]
    [InlineData(true, true, true, (int)GpuTexturePayloadFormat.BC1)]
    [InlineData(false, true, true, (int)GpuTexturePayloadFormat.Rgba8)]
    [InlineData(true, false, true, (int)GpuTexturePayloadFormat.Rgba8)]
    [InlineData(true, true, false, (int)GpuTexturePayloadFormat.Rgba8)]
    [InlineData(false, false, false, (int)GpuTexturePayloadFormat.BC1)]
    public void Resolve_UnprovenMaterialsKeepTheirExistingCandidate(
        bool hasBump, bool isPathBacked, bool isResident, int format)
    {
        var actual = OblivionOrdinarySpecularPolicy.Resolve(
            Candidate, false, hasBump, isPathBacked, isResident, (GpuTexturePayloadFormat)format);

        Assert.Equal(Candidate, actual);
    }

    [Theory]
    [InlineData(0f, (int)GpuTexturePayloadFormat.BC1)]
    [InlineData(0f, (int)GpuTexturePayloadFormat.BC3)]
    [InlineData(-4f, (int)GpuTexturePayloadFormat.BC1)]
    [InlineData(-4f, (int)GpuTexturePayloadFormat.BC3)]
    public void Resolve_DoesNotEnableOrRewriteAnAlreadyDisabledCandidate(
        float exponent, int format)
    {
        var candidate = new Vector4(0.17f, 0.31f, 0.59f, exponent);

        var actual = OblivionOrdinarySpecularPolicy.Resolve(candidate, true, true, true, true, (GpuTexturePayloadFormat)format);

        Assert.Equal(candidate, actual);
    }

    [Fact]
    public void Resolve_ReevaluatesPromotionFailureAndReplacementFactsWithoutChangingTheCandidate()
    {
        var pending = OblivionOrdinarySpecularPolicy.Resolve(
            Candidate, true, true, true, false, GpuTexturePayloadFormat.Rgba8);
        var loadedBc3 = OblivionOrdinarySpecularPolicy.Resolve(
            Candidate, true, true, true, true, GpuTexturePayloadFormat.BC3);
        var terminalUnavailable = OblivionOrdinarySpecularPolicy.Resolve(
            Candidate, true, true, true, false, GpuTexturePayloadFormat.Rgba8);
        var reloadedBc1 = OblivionOrdinarySpecularPolicy.Resolve(
            Candidate, true, true, true, true, GpuTexturePayloadFormat.BC1);
        var generatedRgbaReplacement = OblivionOrdinarySpecularPolicy.Resolve(
            Candidate, true, true, true, true, GpuTexturePayloadFormat.Rgba8);

        // These are the GPU-neutral facts at each observation, not a simulated GPU cache.
        Assert.Equal(Disabled, pending);
        Assert.Equal(Candidate, loadedBc3);
        Assert.Equal(Disabled, terminalUnavailable);
        Assert.Equal(Disabled, reloadedBc1);
        Assert.Equal(Candidate, generatedRgbaReplacement);
    }

    [Theory]
    [InlineData(Diffuse, Normal)]
    [InlineData("Data/Textures/Clothes/LowerClass/Shoes.DDS", "Data/Textures/Clothes/LowerClass/Shoes_N.DDS")]
    [InlineData("clothes/lowerclass/shoes.dds", "clothes/lowerclass/shoes_n.dds")]
    [InlineData(Diffuse, null)]
    [InlineData(Diffuse, "")]
    [InlineData(Diffuse, " ")]
    public void IsEligible_RequiresTheOriginalDiffuseAndItsImplicitNormalOrNoNormal(
        string diffuse, string? normal)
    {
        var source = CreateSubmesh();

        var actual = OblivionOrdinarySpecularPolicy.IsEligible(source, diffuse, normal);

        Assert.True(actual);
        Assert.Equal(Diffuse, source.DiffuseTexturePath);
        Assert.Equal(Normal, source.NormalMapTexturePath);
    }

    [Theory]
    [InlineData(@"textures\body_egt\test.dds", Normal)]
    [InlineData(@"textures\override\shoes.dds", Normal)]
    [InlineData(Diffuse, @"textures\override\shoes_n.dds")]
    [InlineData(Diffuse, @"textures\clothes\lowerclass\shoes_other_n.dds")]
    [InlineData(null, Normal)]
    [InlineData("", Normal)]
    [InlineData(" ", Normal)]
    public void IsEligible_RejectsUnprovedOverridesAndMissingEffectiveDiffuse(
        string? diffuse, string? normal)
    {
        Assert.False(OblivionOrdinarySpecularPolicy.IsEligible(CreateSubmesh(), diffuse, normal));
    }

    [Theory]
    [InlineData(false, Diffuse)]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, " ")]
    public void IsEligible_DoesNotInferSourceProofFromOrdinaryLookingFields(bool proven, string? authoredDiffuse)
    {
        var source = CreateSubmesh(proven: proven, authoredDiffuse: authoredDiffuse);

        Assert.False(OblivionOrdinarySpecularPolicy.IsEligible(source, Diffuse, Normal));
    }

    [Theory]
    [InlineData("facegen")]
    [InlineData("hair")]
    [InlineData("eye")]
    [InlineData("emissive")]
    [InlineData("shader")]
    [InlineData("blend")]
    [InlineData("cutout")]
    [InlineData("material-alpha")]
    [InlineData("alpha-controller")]
    [InlineData("animated-emission")]
    [InlineData("specular")]
    [InlineData("gradient")]
    [InlineData("environment")]
    [InlineData("classic-environment")]
    [InlineData("classic-mask")]
    [InlineData("height")]
    [InlineData("lighting30-glow")]
    [InlineData("bgsm-glow")]
    public void IsEligible_RechecksCurrentMaterialAndTextureLanesAfterComposition(string variant)
    {
        var source = CreateSubmesh(variant);

        Assert.False(OblivionOrdinarySpecularPolicy.IsEligible(source, Diffuse, Normal));
    }

    [Theory]
    [InlineData("skin")]
    [InlineData("SKIN")]
    [InlineData("right eye")]
    [InlineData("LEFT EYE")]
    [InlineData("EnvMapTest")]
    [InlineData("RefractTest")]
    [InlineData("DynAlphaTest")]
    [InlineData("HideSecretTest")]
    [InlineData("HAIRTest")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void IsEligible_RejectsCurrentSpecialMaterialNamesEvenBeforeShaderConversion(string? materialName)
    {
        var source = CreateSubmesh();
        source.LegacyMaterialName = materialName;

        Assert.False(source.IsFaceGen);
        Assert.False(source.UsesClassicHairMaterial);
        Assert.False(source.IsEyeEnvmap);
        Assert.False(OblivionOrdinarySpecularPolicy.IsEligible(source, Diffuse, Normal));
    }

    [Theory]
    [InlineData("foot")]
    [InlineData("FOOT")]
    [InlineData("ordinary")]
    public void IsEligible_OrdinaryNamesDoNotReplaceTheIndependentSourceProof(string materialName)
    {
        var proven = CreateSubmesh();
        proven.LegacyMaterialName = materialName;
        var unproven = CreateSubmesh(proven: false);
        unproven.LegacyMaterialName = materialName;

        Assert.True(OblivionOrdinarySpecularPolicy.IsEligible(proven, Diffuse, Normal));
        Assert.False(OblivionOrdinarySpecularPolicy.IsEligible(unproven, Diffuse, Normal));
    }

    private static RenderableSubmesh CreateSubmesh(
        string? variant = null, bool proven = true, string? authoredDiffuse = Diffuse)
    {
        return new RenderableSubmesh
        {
            Positions = [0, 0, 0],
            Triangles = [],
            LegacyMaterialName = "foot",
            HasAuthoredOblivionOrdinaryInputs = proven,
            AuthoredOblivionOrdinaryDiffusePath = authoredDiffuse,
            DiffuseTexturePath = Diffuse,
            NormalMapTexturePath = Normal,
            IsFaceGen = variant == "facegen",
            UsesClassicHairMaterial = variant == "hair",
            IsEyeEnvmap = variant == "eye",
            IsEmissive = variant == "emissive",
            ShaderMetadata = variant == "shader" ? new NifShaderTextureMetadata() : null,
            HasAlphaBlend = variant == "blend",
            HasAlphaTest = variant == "cutout",
            MaterialAlpha = variant == "material-alpha" ? 0.5f : 1f,
            MaterialAlphaController = variant == "alpha-controller"
                ? new NifMaterialAlphaController(1, "Alpha", NifKeyInterpolation.Linear, [], 1f, default, default)
                : null,
            AnimatedEmissiveColor = variant == "animated-emission" ? (0.1f, 0.2f, 0.3f) : null,
            SpecularMapTexturePath = variant == "specular" ? "extra.dds" : null,
            GradientMapTexturePath = variant == "gradient" ? "extra.dds" : null,
            EnvironmentMapTexturePath = variant == "environment" ? "extra.dds" : null,
            ClassicEnvironmentMapTexturePath = variant == "classic-environment" ? "extra.dds" : null,
            ClassicEnvironmentMaskTexturePath = variant == "classic-mask" ? "extra.dds" : null,
            ClassicParallaxHeightMapTexturePath = variant == "height" ? "extra.dds" : null,
            Lighting30GlowMapTexturePath = variant == "lighting30-glow" ? "extra.dds" : null,
            BgsmGlowMapTexturePath = variant == "bgsm-glow" ? "extra.dds" : null
        };
    }
}
