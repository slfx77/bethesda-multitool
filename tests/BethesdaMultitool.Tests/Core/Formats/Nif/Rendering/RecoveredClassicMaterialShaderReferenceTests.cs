using System.Numerics;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Independent CPU vectors for recovered FO3/FNV SLS material math. Production uses HLSL; these
///     fixtures are intentionally written from the disassembled shader equations instead of calling it.
/// </summary>
public sealed class RecoveredClassicMaterialShaderReferenceTests
{
    [Fact]
    public void ImagespaceEmissiveMultScalesLighting30MaterialEmissionButNotNoLighting()
    {
        // FalloutNV MemDebug Lighting30Shader::SetupGeometryConstants_Emittance reads hdrData[3]
        // only under bHDR and combines it with NiMaterialProperty::m_fEmitMult. The complete
        // pImageSpaceManager xref census finds no corresponding NoLighting shader read.
        var materialEmission = new Vector3(0.25f, 0.5f, 0.75f);
        const float materialEmitMult = 2f;
        const float exteriorImagespaceEmissiveMult = 1.2f;
        var lighting30Hdr = materialEmission * materialEmitMult * exteriorImagespaceEmissiveMult;

        var noLightingTexture = new Vector3(0.2f, 0.4f, 0.8f);
        var noLightingExterior = noLightingTexture;
        var noLightingInterior = noLightingTexture;

        Assert.Equal(0.6f, lighting30Hdr.X, 6);
        Assert.Equal(1.2f, lighting30Hdr.Y, 6);
        Assert.Equal(1.8f, lighting30Hdr.Z, 6);
        Assert.Equal(noLightingInterior, noLightingExterior);
    }

    [Fact]
    public void Lighting30NoGlow_HdrFoldsScaledEmissionIntoAmbientBeforeAlbedo()
    {
        var output = RecoveredNoGlow(
            new Vector3(0.25f, 0.5f, 0.75f),
            new Vector3(0.1f, 0.2f, 0.3f),
            new Vector3(0.4f, 0.5f, 0.6f),
            new Vector3(0.2f, 0.4f, 0.6f),
            true,
            1.5f,
            2f);

        VectorAssert.Equal(new Vector3(0.275f, 0.95f, 2.025f), output);
    }

    [Fact]
    public void Lighting30NoGlow_NonHdrIgnoresBothMultipliersAndClampsAmbientSum()
    {
        var output = RecoveredNoGlow(
            new Vector3(0.25f, 0.5f, 0.75f),
            new Vector3(0.4f, 0.2f, 0.3f),
            new Vector3(0.4f, 0.5f, 0.6f),
            new Vector3(0.8f, 0.4f, 0.6f),
            false,
            9f,
            7f);

        VectorAssert.Equal(new Vector3(0.35f, 0.55f, 1.125f), output);
    }

    [Fact]
    public void Sls2004Glow_HdrMultipliesScaledEmissionByGlowBeforeAlbedo()
    {
        var output = RecoveredGlow(
            new Vector3(0.25f, 0.5f, 0.75f),
            new Vector3(0.1f, 0.2f, 0.3f),
            new Vector3(0.4f, 0.5f, 0.6f),
            new Vector3(0.2f, 0.4f, 0.6f),
            new Vector3(0.5f, 0.25f, 1f),
            true,
            1.5f,
            2f);

        VectorAssert.Equal(new Vector3(0.2f, 0.5f, 2.025f), output);
    }

    [Fact]
    public void Sls2004Glow_NonHdrClampsRawEmissionAndIgnoresBothMultipliers()
    {
        var output = RecoveredGlow(
            new Vector3(0.25f, 0.5f, 0.75f),
            new Vector3(0.1f, 0.2f, 0.3f),
            new Vector3(0.4f, 0.5f, 0.6f),
            new Vector3(1.4f, 0.4f, 1.2f),
            new Vector3(0.5f, 0.25f, 1f),
            false,
            9f,
            7f);

        VectorAssert.Equal(new Vector3(0.25f, 0.4f, 1.425f), output);
    }

    [Fact]
    public void Lighting30EmissionNeverBypassesAlbedo_BlackAlbedoRemainsBlack()
    {
        var noGlow = RecoveredNoGlow(
            Vector3.Zero, new Vector3(0.1f), new Vector3(0.2f), new Vector3(4f),
            true, 3f, 2f);
        var glow = RecoveredGlow(
            Vector3.Zero, new Vector3(0.1f), new Vector3(0.2f), new Vector3(4f),
            Vector3.One,
            true, 3f, 2f);

        Assert.Equal(Vector3.Zero, noGlow);
        Assert.Equal(Vector3.Zero, glow);
    }

    [Fact]
    public void Sls1009_UnpacksNormalRgbWithoutNegatingGreen()
    {
        // SLS1009.pso: texld NormalMap; add -0.5; add value to itself; dp3 with the tangent-space
        // light vector. Use an asymmetric sample/light so a green inversion cannot pass unnoticed.
        var packedNormal = new Vector3(0.65f, 0.80f, 0.90f);
        var packedLight = new Vector3(0.40f, 0.75f, 0.71875f);
        var normal = packedNormal * 2f - Vector3.One;
        var light = packedLight * 2f - Vector3.One;
        var recoveredNdotL = Vector3.Dot(normal, light);
        var incorrectlyFlipped = Vector3.Dot(new Vector3(normal.X, -normal.Y, normal.Z), light);

        Assert.Equal(0.59f, recoveredNdotL, 6);
        Assert.Equal(-0.01f, incorrectlyFlipped, 6);
        Assert.True(recoveredNdotL > 0f);
        Assert.True(incorrectlyFlipped < recoveredNdotL);
    }

    private static Vector3 RecoveredNoGlow(
        Vector3 albedo,
        Vector3 ambient,
        Vector3 direct,
        Vector3 rawEmission,
        bool hdr,
        float imagespaceEmissiveMult,
        float materialEmitMult)
    {
        var emission = hdr
            ? rawEmission * imagespaceEmissiveMult * materialEmitMult
            : rawEmission;
        var effectiveAmbient = ambient + emission;
        if (!hdr)
        {
            effectiveAmbient = Vector3.Min(effectiveAmbient, Vector3.One);
        }

        return Vector3.Multiply(albedo, effectiveAmbient + direct);
    }

    private static Vector3 RecoveredGlow(
        Vector3 albedo,
        Vector3 ambient,
        Vector3 direct,
        Vector3 rawEmission,
        Vector3 glowMap,
        bool hdr,
        float imagespaceEmissiveMult,
        float materialEmitMult)
    {
        var emission = hdr
            ? rawEmission * imagespaceEmissiveMult * materialEmitMult
            : Vector3.Min(rawEmission, Vector3.One);
        var shade = ambient + direct + Vector3.Multiply(emission, glowMap);
        return Vector3.Multiply(albedo, shade);
    }
}