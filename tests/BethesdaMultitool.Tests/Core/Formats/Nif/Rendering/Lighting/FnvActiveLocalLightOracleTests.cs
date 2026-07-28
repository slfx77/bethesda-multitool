using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

public sealed class FnvActiveLocalLightOracleTests
{
    [Fact]
    public void Contract_IdentifiesRecoveredPassesButKeepsRuntimeDisabled()
    {
        Assert.False(FnvActiveLocalLightOracle.RuntimeSupported);
        Assert.Equal(220, FnvActiveLocalLightOracle.OneLocalPassId);
        Assert.Equal(143, FnvActiveLocalLightOracle.TwoOrThreeLocalPassId);
        Assert.Equal(128, FnvActiveLocalLightOracle.AttenuationTextureSize);
    }

    [Fact]
    public void Contract_HasNoProductionConsumerWhileRuntimeSupportIsDisabled()
    {
        var root = SourceContract.RepoRoot;
        var sourceRoot = Path.Combine(root, "src");
        var oraclePath = Path.GetFullPath(Path.Combine(
            root,
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Lighting",
            "FnvActiveLocalLightOracle.cs"));
        var productionSources = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".hlsl")
            .Where(path => !Path.GetFullPath(path).Equals(
                oraclePath, StringComparison.OrdinalIgnoreCase))
            .Select(path => (Path: path, Source: File.ReadAllText(path)))
            .ToArray();
        var forbiddenRuntimeTokens = new[]
        {
            nameof(FnvActiveLocalLightOracle),
            "BSSM_ADT2", "BSSM_ADT4",
            "SLS2008", "SLS2011", "SLS2022", "SLS2031"
        };
        var routeConsumers = productionSources
            .Where(entry => forbiddenRuntimeTokens.Any(token =>
                entry.Source.Contains(token, StringComparison.Ordinal)))
            .Select(entry => Path.GetRelativePath(root, entry.Path))
            .ToArray();
        var basePolicy = File.ReadAllText(Path.Combine(
            sourceRoot,
            "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Lighting",
            "FnvActiveAdtBasePolicy.cs"));

        Assert.Empty(routeConsumers);
        Assert.Contains("eligibility.PlacedLightCount == 0", basePolicy, StringComparison.Ordinal);
    }

    [Fact]
    public void AttenuationGenerator_MatchesRecoveredPcFinalTexelsAndSymmetry()
    {
        var red = FnvActiveLocalLightOracle.GeneratePcFinalAttenuationRed();

        Assert.Equal(128 * 128, red.Length);
        Assert.Equal(0, At(red, 63, 63));
        Assert.Equal(0, At(red, 64, 63));
        Assert.Equal(0, At(red, 63, 64));
        Assert.Equal(0, At(red, 64, 64));
        Assert.Equal(62, At(red, 32, 63));
        Assert.Equal(84, At(red, 31, 47));
        Assert.Equal(255, At(red, 0, 63));
        Assert.Equal(255, At(red, 0, 0));

        for (var y = 0; y < 128; y++)
        {
            for (var x = 0; x < 128; x++)
            {
                Assert.Equal(At(red, x, y), At(red, 127 - x, y));
                Assert.Equal(At(red, x, y), At(red, x, 127 - y));
                var transposedX = y; // transpose symmetry: sample the mirrored coordinate
                var transposedY = x;
                Assert.Equal(At(red, x, y), At(red, transposedX, transposedY));
            }
        }

        Assert.Equal(84f / 255f,
            FnvActiveLocalLightOracle.NormalizeAttenuationTexel(At(red, 31, 47)), 7);
    }

    [Fact]
    public void PreparedPointColor_UsesPackedDataColorAndKeepsShadowLodOutOfRgb()
    {
        var prepared = FnvActiveLocalLightOracle.PreparePointLightColor(
            0x00102040,
            false,
            0.5f,
            false,
            1f,
            0.25f,
            0.75f);

        VectorAssert.Equal(new Vector3(8f, 4f, 2f) / 255f, prepared.Rgb);
        Assert.Equal(0.75f, prepared.ShadowLodDimmer, 6);
    }

    [Fact]
    public void PreparedPointColor_ClampsFadeOnlyAboveOneWhenHdrIsOff()
    {
        var nonHdr = FnvActiveLocalLightOracle.PreparePointLightColor(
            0x000000ff, false, 2f, false, 1f, 1f, 0f);
        var hdr = FnvActiveLocalLightOracle.PreparePointLightColor(
            0x000000ff, false, 2f, true, 1f, 1f, 0f);
        var negativeFade = FnvActiveLocalLightOracle.PreparePointLightColor(
            0x000000ff, false, -0.5f, false, 1f, 1f, 0f);
        var negativeLightAndFade = FnvActiveLocalLightOracle.PreparePointLightColor(
            0x000000ff, true, -0.5f, false, 1f, 1f, 0f);

        VectorAssert.Equal(Vector3.UnitX, nonHdr.Rgb);
        VectorAssert.Equal(new Vector3(2f, 0f, 0f), hdr.Rgb);
        VectorAssert.Equal(new Vector3(-0.5f, 0f, 0f), negativeFade.Rgb);
        VectorAssert.Equal(new Vector3(0.5f, 0f, 0f), negativeLightAndFade.Rgb);
    }

    [Fact]
    public void PreparedPointColor_BlacksAnyForcedDarknessBelowOneWithoutClampingAboveOne()
    {
        var darkened = FnvActiveLocalLightOracle.PreparePointLightColor(
            0x00ffffff, false, 1f, false, 0.999f, 4f, 0f);
        var amplified = FnvActiveLocalLightOracle.PreparePointLightColor(
            0x00ffffff, false, 1f, false, 1.5f, 2f, 0f);

        VectorAssert.Equal(Vector3.Zero, darkened.Rgb);
        VectorAssert.Equal(new Vector3(3f), amplified.Rgb);
    }

    [Fact]
    public void PreparedPointColor_RejectsNonFiniteRuntimeInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FnvActiveLocalLightOracle.PreparePointLightColor(
                0, false, float.NaN, false, 1f, 1f, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FnvActiveLocalLightOracle.PreparePointLightColor(
                0, false, 1f, false, float.PositiveInfinity, 1f, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FnvActiveLocalLightOracle.PreparePointLightColor(
                0, false, 1f, false, 1f, float.NegativeInfinity, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FnvActiveLocalLightOracle.PreparePointLightColor(
                0, false, 1f, false, 1f, 1f, float.NaN));
    }

    [Fact]
    public void Id220VertexOracle_PreservesStageBoundaryAndBuildsAttenuationCoordinates()
    {
        var interpolants = FnvActiveLocalLightOracle.BuildId220VertexInterpolants(
            Vector3.Zero,
            new Vector3(2f, 0f, 0f),
            4f,
            new Vector3(2f, 0f, 0f),
            new Vector3(0f, 3f, 0f),
            new Vector3(0f, 0f, 4f),
            new Vector3(1f, 2f, 0f));

        var inverseSqrt10 = 1f / MathF.Sqrt(10f);
        VectorAssert.Equal(
            new Vector3(inverseSqrt10, 3f * inverseSqrt10, 0f),
            interpolants.SunTangentSpace);
        VectorAssert.Equal(new Vector3(2f, 0f, 0f), interpolants.LocalTangentSpace);
        VectorAssert.Equal(new Vector4(0.75f, 0.5f, 0.5f, 0.5f),
            interpolants.AttenuationCoordinates);
    }

    [Fact]
    public void Id220PixelOracle_KeepsSignedDotsAndNegativeAttenuationUntilAggregateClamp()
    {
        var result = FnvActiveLocalLightOracle.EvaluateId220Pixel(
            FnvClassicBasicShaderMode.Sls1009,
            new Vector3(1f, 0.5f, 0.5f),
            -Vector3.UnitX,
            Vector3.UnitX,
            0.75f,
            0.5f,
            new Vector3(1f, 0.1f, 0.5f),
            new Vector3(0.2f, 0.3f, 0.4f),
            new Vector3(0.4f, 0.2f, 0.8f),
            new Vector3(0.5f, 0.25f, 0.75f),
            new Vector3(0.01f));

        Assert.Equal(-1f, result.RawSignedSunDot, 6);
        Assert.Equal(1f, result.RawSignedLocalDot, 6);
        Assert.Equal(-0.25f, result.RawAttenuation, 6);
        VectorAssert.Equal(new Vector3(0.7f, -0.25f, -0.1f), result.TotalBeforeClamp);
        VectorAssert.Equal(new Vector3(0.7f, 0f, 0f), result.Shade);
        VectorAssert.Equal(new Vector3(0.35f, 0f, 0f), result.Rgb);
    }

    [Fact]
    public void Id220PixelOracle_DoesNotRenormalizeTheInterpolatedSunDirection()
    {
        var result = FnvActiveLocalLightOracle.EvaluateId220Pixel(
            FnvClassicBasicShaderMode.Sls1009,
            new Vector3(1f, 0.5f, 0.5f),
            new Vector3(0.25f, 0f, 0f),
            Vector3.UnitX,
            0f,
            0f,
            Vector3.Zero,
            Vector3.One,
            Vector3.Zero,
            Vector3.One,
            Vector3.One);

        Assert.Equal(0.25f, result.RawSignedSunDot, 6);
        VectorAssert.Equal(new Vector3(0.25f), result.Shade);
    }

    [Fact]
    public void Id143VertexOracle_NormalizesSunButLeavesLocalDirectionForPixelNormalization()
    {
        var sun = FnvActiveLocalLightOracle.BuildId143SunVertexInterpolant(
            new Vector3(2f, 0f, 0f),
            new Vector3(0f, 3f, 0f),
            new Vector3(0f, 0f, 4f),
            new Vector3(1f, 2f, 0f));
        var interpolants = FnvActiveLocalLightOracle.BuildId143LocalVertexInterpolants(
            new Vector3(1f, 0f, 0f),
            new Vector3(5f, 0f, 0f),
            new Vector3(2f, 0f, 0f),
            Vector3.UnitY,
            Vector3.UnitZ);

        var inverseSqrt10 = 1f / MathF.Sqrt(10f);
        VectorAssert.Equal(new Vector3(inverseSqrt10, 3f * inverseSqrt10, 0f), sun);
        VectorAssert.Equal(new Vector3(2f, 0f, 0f), interpolants.TangentSpaceDirection);
    }

    [Fact]
    public void Id143PixelOracle_UsesCountGateAndClampsOnlyAfterAllSignedTerms()
    {
        var local0 = new FnvActiveId143LocalInterpolants(Vector3.UnitX);
        var local0Constants = new FnvActiveId143LocalConstants(
            new Vector3(0.5f, 0f, 0f), 1f, new Vector3(1f, 0f, 0f));
        var local1 = new FnvActiveId143LocalInterpolants(-Vector3.UnitX);
        var local1Constants = new FnvActiveId143LocalConstants(
            Vector3.Zero, 1f, new Vector3(0f, 1f, 0f));
        var local2 = new FnvActiveId143LocalInterpolants(Vector3.UnitX);
        var local2Constants = new FnvActiveId143LocalConstants(
            new Vector3(2f, 0f, 0f), 1f, new Vector3(0f, 0f, 1f));

        var twoLocals = EvaluateId143(
            local0, local0Constants,
            local1, local1Constants,
            default, default,
            3f);
        Assert.True(twoLocals.Local0.Enabled);
        Assert.True(twoLocals.Local1.Enabled);
        Assert.False(twoLocals.Local2.Enabled);
        Assert.Equal(0.75f, twoLocals.Local0.RawAttenuation, 6);
        Assert.Equal(-1f, twoLocals.Local1.RawSignedDot, 6);
        Assert.Equal(0f, twoLocals.Local2.RawAttenuation, 6);
        VectorAssert.Equal(Vector3.Zero, twoLocals.Local2.PointDeltaOverRadius);
        VectorAssert.Equal(Vector3.Zero, twoLocals.Local2.Contribution);
        VectorAssert.Equal(new Vector3(0.85f, -0.8f, 3.2f), twoLocals.TotalBeforeClamp);
        VectorAssert.Equal(new Vector3(0.85f, 0f, 3.2f), twoLocals.Shade);

        var threeLocals = EvaluateId143(
            local0, local0Constants,
            local1, local1Constants,
            local2, local2Constants,
            4f);
        Assert.True(threeLocals.Local2.Enabled);
        VectorAssert.Equal(new Vector3(2f, 0f, 0f), threeLocals.Local2.PointDeltaOverRadius);
        Assert.Equal(-3f, threeLocals.Local2.RawAttenuation, 6);
        VectorAssert.Equal(new Vector3(0f, 0f, -3f), threeLocals.Local2.Contribution);
        VectorAssert.Equal(new Vector3(0.85f, -0.8f, 0.2f), threeLocals.TotalBeforeClamp);
        VectorAssert.Equal(new Vector3(0.85f, 0f, 0.2f), threeLocals.Shade);
    }

    [Theory]
    [InlineData(3f, false)]
    [InlineData(4f, true)]
    public void Id143PixelOracle_AcceptsOnlyShippedCountGatesAndUsesStrictSlotThresholds(
        float totalLightCountGate,
        bool local2Enabled)
    {
        var local = new FnvActiveId143LocalInterpolants(Vector3.UnitX);
        var constants = new FnvActiveId143LocalConstants(Vector3.Zero, 1f, Vector3.Zero);

        var result = EvaluateId143(
            local, constants,
            local, constants,
            local, constants,
            totalLightCountGate);

        Assert.True(result.Local0.Enabled);
        Assert.True(result.Local1.Enabled);
        Assert.Equal(local2Enabled, result.Local2.Enabled);
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(2.5f)]
    [InlineData(3.5f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Id143PixelOracle_RejectsNonShippedCountGates(float totalLightCountGate)
    {
        var local = new FnvActiveId143LocalInterpolants(Vector3.UnitX);
        var constants = new FnvActiveId143LocalConstants(Vector3.Zero, 1f, Vector3.Zero);

        Assert.Throws<ArgumentOutOfRangeException>(() => EvaluateId143(
            local, constants,
            local, constants,
            local, constants,
            totalLightCountGate));
    }

    [Fact]
    public void Id143PixelOracle_DoesNotRenormalizeTheInterpolatedSunDirection()
    {
        var local = new FnvActiveId143LocalInterpolants(Vector3.UnitX);
        var constants = new FnvActiveId143LocalConstants(Vector3.Zero, 1f, Vector3.Zero);
        var result = FnvActiveLocalLightOracle.EvaluateId143Pixel(
            FnvClassicBasicShaderMode.Sls1009,
            new Vector3(1f, 0.5f, 0.5f),
            new Vector3(0.25f, 0f, 0f),
            Vector3.Zero,
            local, constants,
            local, constants,
            default, default,
            3f,
            Vector3.Zero,
            Vector3.One,
            Vector3.One,
            Vector3.One);

        Assert.Equal(0.25f, result.RawSignedSunDot, 6);
        VectorAssert.Equal(new Vector3(0.25f), result.Shade);
    }

    [Fact]
    public void Id143PixelOracle_SubtractsInterpolatedObjectPositionBeforeDividingByRadius()
    {
        var local = new FnvActiveId143LocalInterpolants(Vector3.UnitX);
        var result = FnvActiveLocalLightOracle.EvaluateId143Pixel(
            FnvClassicBasicShaderMode.Sls1009,
            new Vector3(1f, 0.5f, 0.5f),
            Vector3.Zero,
            new Vector3(1f, 0f, 0f),
            local,
            new FnvActiveId143LocalConstants(
                new Vector3(5f, 0f, 0f), 2f, Vector3.One),
            local,
            new FnvActiveId143LocalConstants(
                new Vector3(1f, 0f, 0f), 1f, Vector3.Zero),
            default,
            default,
            3f,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.One,
            Vector3.One);

        VectorAssert.Equal(new Vector3(2f, 0f, 0f), result.Local0.PointDeltaOverRadius);
        Assert.Equal(-3f, result.Local0.RawAttenuation, 6);
    }

    [Theory]
    [InlineData(-0.001f)]
    [InlineData(1.001f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Id220PixelOracle_RejectsInvalidNormalizedAttenuationSamples(float sample)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FnvActiveLocalLightOracle.EvaluateId220Pixel(
                FnvClassicBasicShaderMode.Sls1009,
                new Vector3(1f, 0.5f, 0.5f),
                Vector3.UnitX,
                Vector3.UnitX,
                sample,
                0f,
                Vector3.Zero,
                Vector3.Zero,
                Vector3.Zero,
                Vector3.One,
                Vector3.One));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FnvActiveLocalLightOracle.EvaluateId220Pixel(
                FnvClassicBasicShaderMode.Sls1009,
                new Vector3(1f, 0.5f, 0.5f),
                Vector3.UnitX,
                Vector3.UnitX,
                0f,
                sample,
                Vector3.Zero,
                Vector3.Zero,
                Vector3.Zero,
                Vector3.One,
                Vector3.One));
    }

    [Fact]
    public void LocalLightOracles_MultiplyVertexRgbOnlyOnTheClassifiedVariant()
    {
        var ordinary = EvaluateId220(FnvClassicBasicShaderMode.Sls1009);
        var vertexColor = EvaluateId220(FnvClassicBasicShaderMode.Sls1013VertexColor);

        VectorAssert.Equal(new Vector3(0.5f, 0.25f, 0.75f), ordinary.Rgb);
        VectorAssert.Equal(new Vector3(0.4f, 0.1f, 0.15f), vertexColor.Rgb);
        VectorAssert.Equal(ordinary.Shade, vertexColor.Shade);
    }

    [Fact]
    public void LocalLightOracles_RejectUnclassifiedOrUndefinedVertexInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EvaluateId220(FnvClassicBasicShaderMode.None));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FnvActiveLocalLightOracle.BuildId220VertexInterpolants(
                Vector3.Zero,
                Vector3.UnitX,
                0f,
                Vector3.UnitX,
                Vector3.UnitY,
                Vector3.UnitZ,
                Vector3.UnitZ));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FnvActiveLocalLightOracle.EvaluateId220Pixel(
                FnvClassicBasicShaderMode.Sls1009,
                new Vector3(0.5f),
                Vector3.UnitZ,
                Vector3.UnitZ,
                0f,
                0f,
                Vector3.Zero,
                Vector3.One,
                Vector3.One,
                Vector3.One,
                Vector3.One));
    }

    private static FnvActiveId220Evaluation EvaluateId220(FnvClassicBasicShaderMode mode)
    {
        return FnvActiveLocalLightOracle.EvaluateId220Pixel(
            mode,
            new Vector3(0.5f, 0.5f, 1f),
            Vector3.UnitZ,
            Vector3.UnitZ,
            0f,
            0f,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.One,
            new Vector3(0.5f, 0.25f, 0.75f),
            new Vector3(0.8f, 0.4f, 0.2f));
    }

    private static FnvActiveId143Evaluation EvaluateId143(
        FnvActiveId143LocalInterpolants local0,
        FnvActiveId143LocalConstants local0Constants,
        FnvActiveId143LocalInterpolants local1,
        FnvActiveId143LocalConstants local1Constants,
        FnvActiveId143LocalInterpolants local2,
        FnvActiveId143LocalConstants local2Constants,
        float totalLightCountGate)
    {
        return FnvActiveLocalLightOracle.EvaluateId143Pixel(
            FnvClassicBasicShaderMode.Sls1009,
            new Vector3(1f, 0.5f, 0.5f),
            Vector3.UnitX,
            Vector3.Zero,
            local0, local0Constants,
            local1, local1Constants,
            local2, local2Constants,
            totalLightCountGate,
            new Vector3(0.1f, 0.2f, 3.2f),
            Vector3.Zero,
            Vector3.One,
            Vector3.One);
    }

    private static byte At(byte[] red, int x, int y)
    {
        return red[(y * 128) + x];
    }
}