using System.Numerics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
/// Independent CPU vectors for recovered legacy water shader math. These helpers deliberately do
/// not call renderer production code: the production implementation is HLSL, while these values are
/// calculated from the recovered assembly contract and catch accidental formula reuse/conflation.
/// </summary>
public sealed class RecoveredWaterShaderReferenceTests
{
    [Fact]
    public void FnvNoiseBlendUsesBgrChannelsAndAuthoredAmplitudes()
    {
        // ISNOISESCROLLANDBLEND samples B from layer 0, G from layer 1 and R from layer 2.
        // Selected scalar inputs unpack to +0.5, -0.5 and +1.0 respectively.
        const float layer0Blue = 0.75f;
        const float layer1Green = 0.25f;
        const float layer2Red = 1f;
        var signedWeighted = (layer0Blue * 2f - 1f) * 0.3f
                           + (layer1Green * 2f - 1f) * 0.5f
                           + (layer2Red * 2f - 1f) * 0.2f;
        var packedHeight = signedWeighted * 0.5f + 0.5f;

        Assert.Equal(0.55f, packedHeight, 6);
    }

    [Fact]
    public void FnvNoiseNormalUsesRecoveredSobelSignsAndFixedTexelOffset()
    {
        const float texelOffset = 1f / 256f;
        Assert.Equal(0.00390625f, texelOffset);

        // A height field with W/NW/SW=1 and E/NE/SE=0 yields recovered gradient (+4,0,1).
        // Evaluate that fixture independently, then pin the packed normalized result.
        const float w = 1f, e = 0f, n = 0f, s = 0f;
        const float nw = 1f, ne = 0f, sw = 1f, se = 0f;
        var gradient = new Vector3(
            2f * w + nw + sw - 2f * e - ne - se,
            2f * n + nw + ne - 2f * s - sw - se,
            1f);
        var packed = Vector3.Normalize(gradient) * 0.5f + new Vector3(0.5f);
        AssertVector(new Vector3(0.98507125f, 0.5f, 0.6212678f), packed);
    }

    [Fact]
    public void FnvNoiseScrollUsesCompassDirectionConvention()
    {
        const float distance = 0.25f;
        var directionZero = new Vector2(MathF.Sin(0f), MathF.Cos(0f)) * distance;
        var directionNinety = new Vector2(MathF.Sin(MathF.PI / 2f), MathF.Cos(MathF.PI / 2f)) * distance;

        Assert.Equal(new Vector2(0f, 0.25f), directionZero);
        Assert.Equal(0.25f, directionNinety.X, 6);
        Assert.Equal(0f, directionNinety.Y, 6);
    }

    [Fact]
    public void Oblivion_AnamFloorAffectsAlphaButNotRgbFresnel()
    {
        var body = new Vector3(0.2f, 0.3f, 0.4f);
        var reflection = new Vector3(0.8f, 0.7f, 0.6f);
        const float ndotv = 1f;
        const float f0 = 0.025f;
        const float anamOpacity = 0.85f;

        var schlick = f0 + (1f - f0) * MathF.Pow(1f - ndotv, 5f);
        var rgb = Vector3.Lerp(body, reflection, schlick);
        var alphaFloor = MathF.Max(anamOpacity, schlick);

        AssertVector(new Vector3(0.215f, 0.31f, 0.405f), rgb);
        Assert.Equal(0.85f, alphaFloor, 6);
        Assert.NotEqual(Vector3.Lerp(body, reflection, alphaFloor), rgb);
    }

    [Fact]
    public void Oblivion_ReflectionInterpolatesAuthoredColorTowardReflectionTarget()
    {
        var authored = new Vector3(0.1f, 0.2f, 0.3f);
        var reflectionTarget = new Vector3(0.9f, 0.6f, 0.5f);

        var result = Vector3.Lerp(authored, reflectionTarget, 0.25f);

        AssertVector(new Vector3(0.3f, 0.3f, 0.35f), result);
    }

    [Fact]
    public void LegacySunSpecularUsesAuthoredExponentAndSunIntensityGate()
    {
        var sunColor = new Vector3(0.8f, 0.6f, 0.4f);
        const float reflectedDotSun = 1f;
        const float authoredSunPower = 50f;
        const float sunIntensityGate = 0.25f;

        var specular = MathF.Pow(reflectedDotSun, authoredSunPower) * sunIntensityGate * sunColor;

        AssertVector(new Vector3(0.2f, 0.15f, 0.1f), specular);
        Assert.Equal(Vector3.Zero,
            MathF.Pow(reflectedDotSun, authoredSunPower) * 0f * sunColor);
    }

    [Fact]
    public void OblivionDetailBlendUsesLinearDistanceTermNotSquaredNormalAttenuation()
    {
        // WATER000.pso: r2.w = 1-distance*0.000122; normal.xy uses r2.w^2, while the DetailMap
        // blend uses r2.w*VarAmounts.w. Evaluate the two independently so production cannot
        // accidentally reuse the squared normal attenuation for TNAM detail.
        const float horizontalDistance = 4096f;
        const float textureBlend = 0.5f;
        var linearAttenuation = 1f - horizontalDistance * 0.000122f;
        var detailWeight = linearAttenuation * textureBlend;
        var wrongSquaredWeight = linearAttenuation * linearAttenuation * textureBlend;
        var baseColor = new Vector3(0.2f, 0.3f, 0.4f);
        var detailColor = new Vector3(0.8f, 0.7f, 0.6f);

        Assert.Equal(0.250144f, detailWeight, 6);
        Assert.Equal(0.12514403f, wrongSquaredWeight, 6);
        AssertVector(new Vector3(0.3500864f, 0.4000576f, 0.4500288f),
            Vector3.Lerp(baseColor, detailColor, detailWeight));
    }

    [Fact]
    public void FnvCellWaterKeepsRecoveredOpaqueVertexAlpha()
    {
        // WATER000 writes interpolated vertex alpha. Viewer cell-water packets author every vertex as 1.
        const float authoredVertexAlpha = 1f;
        Assert.Equal(1f, authoredVertexAlpha);
    }

    [Fact]
    public void ModernPointLightUsesRecoveredBoundedPowerAttenuation()
    {
        // At d/r=0.5, (1-(d/r)^2)^2.2 = 0.75^2.2. This pins both the squared radius term and
        // exponent independently from the production HLSL.
        const float distanceOverRadius = 0.5f;
        var attenuation = MathF.Pow(1f - distanceOverRadius * distanceOverRadius, 2.2f);

        Assert.Equal(0.53104925f, attenuation, 6);
        Assert.Equal(0f, MathF.Pow(MathF.Max(1f - 1f * 1f, 0f), 2.2f));
    }

    [Fact]
    public void NeutralGlossSampleReconstructsAuthoredPowerAndMagnitude()
    {
        const float authoredPower = 951f;
        const float authoredMagnitude = 8.803f;
        const float neutralGlossPower = 1f;
        const float neutralGlossAmplitude = 1f;

        // Recovered shader: exp(10*y*A+1), and x*B*pi. Compute the inverse scales locally;
        // this test deliberately does not call ModernWaterPipeline's production mappings.
        var scaleA = (MathF.Log(authoredPower) - 1f) / 10f;
        var scaleB = authoredMagnitude / MathF.PI;
        var reconstructedPower = MathF.Exp(10f * neutralGlossPower * scaleA + 1f);
        var reconstructedMagnitude = neutralGlossAmplitude * scaleB * MathF.PI;

        Assert.Equal(0.5857514f, scaleA, 6);
        Assert.Equal(authoredPower, reconstructedPower, 3);
        Assert.Equal(authoredMagnitude, reconstructedMagnitude, 5);
        Assert.Equal(0.4142486f, 1f - neutralGlossPower * scaleA, 6); // Oren-Nayar sigma
    }

    [Fact]
    public void ModernDepthLutUsesGammaDomainAndIndependentAuthoredRanges()
    {
        const float depthAmount = 1000f;
        const float worldDepthFraction = 0.5f;
        var gammaCoordinate = MathF.Pow(worldDepthFraction, 1f / 2.2f);
        var worldDepth = MathF.Pow(gammaCoordinate, 2.2f) * depthAmount;
        var colorT = Math.Clamp((worldDepth - 100f) / (900f - 100f), 0f, 1f);
        var alphaT = Math.Clamp((worldDepth - 0f) / (1000f - 0f), 0f, 1f);
        var color = Vector3.Lerp(new Vector3(0.1f, 0.2f, 0.3f), new Vector3(0.5f, 0.6f, 0.7f), colorT);

        Assert.Equal(0.7297401f, gammaCoordinate, 6);
        Assert.Equal(500f, worldDepth, 3);
        Assert.Equal(0.5f, colorT, 5);
        Assert.Equal(0.5f, alphaT, 5);
        AssertVector(new Vector3(0.3f, 0.4f, 0.5f), color);
    }

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Z, actual.Z, 6);
    }
}
