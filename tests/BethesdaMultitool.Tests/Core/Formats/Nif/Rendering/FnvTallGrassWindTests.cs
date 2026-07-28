using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class FnvTallGrassWindTests
{
    [Fact]
    public void Capability_IsExplicitlyLimitedToFalloutNewVegas()
    {
        foreach (var game in Enum.GetValues<BethesdaGame>())
        {
            Assert.Equal(
                game == BethesdaGame.FalloutNewVegas,
                FnvTallGrassWind.IsSupported(game));
        }
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 2f)]
    [InlineData(1f, 8f)]
    public void WorldOffset_SquaresAuthoredVertexAlphaAlongFixedWorldPositiveY(
        float alpha,
        float expectedY)
    {
        // Spatial phase π/2 => sin=1, isolating the recovered alpha² term.
        var placement = new Vector2(MathF.PI * 64f, 0f);

        var offset = FnvTallGrassWind.EvaluateWorldOffset(
            placement,
            8f,
            0.0,
            15f,
            alpha);

        Assert.Equal(0f, offset.X);
        Assert.Equal(expectedY, offset.Y, 4);
    }

    [Fact]
    public void WorldOffset_ZeroWindIsExactRestIdentity()
    {
        var offset = FnvTallGrassWind.EvaluateWorldOffset(
            new Vector2(12345f, -6789f),
            0f,
            173.25,
            15f,
            1f);

        Assert.Equal(Vector2.Zero, offset);
    }

    [Theory]
    [InlineData(-1f, 5f)]
    [InlineData(0f, 5f)]
    [InlineData(0.5f, 65f)]
    [InlineData(1f, 125f)]
    [InlineData(2f, 125f)]
    public void WindMagnitude_LerpsRecoveredSettingDefaults(float fraction, float expected)
    {
        Assert.Equal(expected, FnvTallGrassWind.ComputeWindMagnitude(fraction));
    }

    [Fact]
    public void WindMagnitude_NonFiniteInputFallsBackToRecoveredMinimum()
    {
        Assert.Equal(5f, FnvTallGrassWind.ComputeWindMagnitude(float.NaN));
        Assert.Equal(5f, FnvTallGrassWind.ComputeWindMagnitude(float.PositiveInfinity));
    }

    [Fact]
    public void TimePhase_UsesRawAuthoredMultiplierWithoutReciprocalConversion()
    {
        // timer=60 and raw multiplier=15 produce one quarter-turn:
        // 60 / 3600 * 15 * 2π = π/2.
        var phase = FnvTallGrassWind.ComputeTimePhaseRadians(
            60.0,
            15f);

        Assert.Equal(MathF.PI / 2f, phase, 5);
        Assert.Equal(0f, FnvTallGrassWind.ComputeTimePhaseRadians(60.0, 0f));
        Assert.Equal(0f, FnvTallGrassWind.ComputeTimePhaseRadians(60.0, float.NaN));
    }

    [Fact]
    public void WorldOffset_IsInvariantUnderRenderOriginRebasing()
    {
        var absolutePlacement = new Vector2(52_123f, -31_777f);
        var originA = Vector2.Zero;
        var originB = new Vector2(49_152f, -32_768f);
        var relativeA = absolutePlacement - originA;
        var relativeB = absolutePlacement - originB;

        var offsetA = FnvTallGrassWind.EvaluateWorldOffset(
            relativeA + originA, 0.75f, 42.0, 15f, 0.6f);
        var offsetB = FnvTallGrassWind.EvaluateWorldOffset(
            relativeB + originB, 0.75f, 42.0, 15f, 0.6f);

        Assert.Equal(offsetA, offsetB);
    }

    [Theory]
    [InlineData(7000f, 1f)]
    [InlineData(7500f, 0.5f)]
    [InlineData(7999f, 0.001f)]
    [InlineData(8000f, 0f)]
    [InlineData(9000f, 0f)]
    public void ConfiguredEnvelopeSignal_IsCullingDataNotGradualOpacity(float distance, float expected)
    {
        var envelope = GrassScatterProfile.ForGame(BethesdaGame.FalloutNewVegas).DistanceEnvelope;

        Assert.Equal(
            expected,
            FnvTallGrassWind.ComputeConfiguredEnvelopeSignal(distance, in envelope),
            3);
        // The production hard-end policy remains separately pinned by GrassDistanceEnvelopeTests.
        // Retail derives an analogous signal from a transformed instance origin, squares it, and
        // turns only zero/nonzero into an output-alpha gate. This horizontal culling oracle is not
        // authority for multiplying sampled alpha, RGB, or material opacity.
    }

    [Fact]
    public void ShaderSources_UseWorldSpaceAbsolutePhaseAndRestoreCoverageAlpha()
    {
        var normal = ReadEmbeddedShader("reference.vert.hlsl");
        var instanced = ReadEmbeddedShader("reference_instanced.vert.hlsl");
        var pixel = ReadEmbeddedShader("reference.frag.hlsl");
        var shadow = ReadEmbeddedShader("shadow.frag.hlsl");

        foreach (var vertexShader in new[] { normal, instanced })
        {
            Assert.Contains("tallGrassWindWeight * tallGrassWindWeight", vertexShader,
                StringComparison.Ordinal);
            Assert.Contains("+ uCameraOrigin.xy", vertexShader, StringComparison.Ordinal);
            Assert.Contains("worldPos.xy +=", vertexShader, StringComparison.Ordinal);
            Assert.Contains("o.vVertexColor.a = 1.0;", vertexShader, StringComparison.Ordinal);
            Assert.DoesNotContain("modelPosition.xy +=", vertexShader, StringComparison.Ordinal);
        }

        Assert.Contains("float4 uTallGrassWind;", instanced, StringComparison.Ordinal);
        Assert.Contains("uSoftParticle is GRASS2000 WindData", normal, StringComparison.Ordinal);
        Assert.DoesNotContain("TallGrassFade", normal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TallGrassFade", instanced, StringComparison.OrdinalIgnoreCase);

        // Both coverage consumers retain their existing diffuse/card-alpha tests; the VS has
        // already reset the authored wind weight before these inputs arrive. The bounded active
        // ADT route deliberately bypasses vertex alpha, but TallGrass never sets that runtime flag
        // and therefore still consumes the false branch below.
        Assert.Contains(
            "sample.a * (fnvActiveAdtBase ? 1.0 : input.vVertexColor.a)",
            pixel,
            StringComparison.Ordinal);
        Assert.Contains("alpha * input.vVertexColor.a", shadow, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererSource_GatesWindToFnvGrassAndPreservesMatrixOnlyInstances()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
            "ReferenceRenderer12.cs");
        var batches = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
            "OpaqueBatchRegistry12.cs");

        Assert.Contains(
            "_tallGrassWindSupported = FnvTallGrassWind.IsSupported(renderCache.Game);",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains("_tallGrassWindSupported && r.IsGrass && sub.IsTallGrass", renderer,
            StringComparison.Ordinal);
        Assert.Contains("_tallGrassWindSupported &&", renderer, StringComparison.Ordinal);
        Assert.Contains("draw.IsGrass && draw.Submesh.IsTallGrass", renderer, StringComparison.Ordinal);
        Assert.Contains("batchState.UsesTallGrassWind", renderer, StringComparison.Ordinal);
        Assert.Contains("ComputeTimePhaseRadians(", renderer, StringComparison.Ordinal);
        Assert.Contains("StructuredBuffer<float4x4> uInstanceWorlds", ReadEmbeddedShader(
            "reference_instanced.vert.hlsl"), StringComparison.Ordinal);
        Assert.Contains("float GrassWaveMultiplier", batches, StringComparison.Ordinal);
        Assert.Contains("effectiveTallGrassWind = usesTallGrassWind && submesh.IsTallGrass", batches,
            StringComparison.Ordinal);
        Assert.Contains("public bool UsesTallGrassWind { get; } = usesTallGrassWind;", batches,
            StringComparison.Ordinal);
        Assert.DoesNotContain("UsesTallGrassWind => UsesGrassDistanceEnvelope", batches,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MeshCacheSource_DisablesSoftParticlesForTallGrassMaterialRoute()
    {
        var meshCache = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
            "ReferenceMeshCache12.cs");
        var compact = string.Concat(meshCache.Where(c => !char.IsWhiteSpace(c)));

        Assert.Contains(
            "varsoftParticle=sub.IsTallGrass?NifSoftParticleSettings.Disabled:" +
            "NifSoftParticlePolicy.Resolve(",
            compact,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RendererSource_UsesRecoveredFixedWorldPositiveYDirection()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
            "ReferenceRenderer12.cs");
        var compact = string.Concat(renderer.Where(c => !char.IsWhiteSpace(c)));

        Assert.Contains(
            "returnnewVector4(Vector2.UnitY," +
            "AnimationsEnabled?FnvTallGrassWind.ComputeWindMagnitude(_windStrength):0f,",
            compact,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_windDirection", renderer, StringComparison.Ordinal);
    }

    private static string ReadEmbeddedShader(string name)
    {
        var assembly = typeof(SptGeometryOptions).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(candidate => candidate.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}