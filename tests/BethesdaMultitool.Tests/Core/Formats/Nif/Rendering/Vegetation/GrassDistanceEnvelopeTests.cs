using BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Vegetation;

public sealed class GrassDistanceEnvelopeTests
{
    [Fact]
    public void FnvProfile_UsesConservativeRetailDefaultEnvelope()
    {
        var envelope = GrassScatterProfile.ForGame(BethesdaGame.FalloutNewVegas).DistanceEnvelope;

        Assert.True(envelope.Enabled);
        Assert.Equal(7000f, envelope.FadeStart);
        Assert.Equal(1000f, envelope.FadeRange);
        Assert.Equal(8000f, envelope.HardEnd);
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(6999f, true)]
    [InlineData(7000f, true)]
    [InlineData(7999f, true)]
    [InlineData(8000f, true)]
    [InlineData(8001f, false)]
    public void FnvGrass_HardEndIncludesEndpointOnly(float distance, bool expected)
    {
        var envelope = GrassScatterProfile.ForGame(BethesdaGame.FalloutNewVegas).DistanceEnvelope;

        var actual = GrassDistanceCullPolicy.Passes(
            true,
            in envelope,
            distance * distance,
            12000f);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NonGrass_IsNeverAffectedByGrassEnvelope()
    {
        var envelope = GrassScatterProfile.ForGame(BethesdaGame.FalloutNewVegas).DistanceEnvelope;

        Assert.True(GrassDistanceCullPolicy.Passes(
            false,
            in envelope,
            50000f * 50000f,
            12000f));
    }

    [Fact]
    public void SkyrimProfile_UsesRetailDefaultEnvelope()
    {
        var envelope = GrassScatterProfile.ForGame(BethesdaGame.Skyrim).DistanceEnvelope;

        Assert.True(envelope.Enabled);
        Assert.Equal(3500f, envelope.FadeStart);
        Assert.Equal(1000f, envelope.FadeRange);
        Assert.Equal(4500f, envelope.HardEnd);
    }

    [Theory]
    [InlineData(4499f, true)]
    [InlineData(4500f, true)]
    [InlineData(4501f, false)]
    public void SkyrimGrass_HardEndIncludesEndpointOnly(float distance, bool expected)
    {
        var envelope = GrassScatterProfile.ForGame(BethesdaGame.Skyrim).DistanceEnvelope;

        Assert.Equal(expected, GrassDistanceCullPolicy.Passes(
            true, in envelope, distance * distance, 12000f));
    }

    [Fact]
    public void BatchPolicyIdentity_ActivatesForRecoveredGrassProfiles()
    {
        var fnv = GrassScatterProfile.ForGame(BethesdaGame.FalloutNewVegas).DistanceEnvelope;
        var skyrim = GrassScatterProfile.ForGame(BethesdaGame.Skyrim).DistanceEnvelope;
        var fallout4 = GrassScatterProfile.ForGame(BethesdaGame.Fallout4).DistanceEnvelope;

        Assert.True(GrassDistanceCullPolicy.UsesEnvelope(true, in fnv));
        Assert.False(GrassDistanceCullPolicy.UsesEnvelope(false, in fnv));
        Assert.True(GrassDistanceCullPolicy.UsesEnvelope(true, in skyrim));
        Assert.False(GrassDistanceCullPolicy.UsesEnvelope(true, in fallout4));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void ExactInstanceRouting_IsIndependentOfGenericFrustumRefilter(
        bool genericRefilter,
        bool usesGrassDistanceEnvelope,
        bool expected)
    {
        Assert.Equal(
            expected,
            GrassDistanceCullPolicy.RequiresExactPerInstanceFiltering(
                genericRefilter,
                usesGrassDistanceEnvelope));
    }

    [Fact]
    public void ActiveRenderDistanceCapsHardEnd()
    {
        var envelope = GrassScatterProfile.ForGame(BethesdaGame.FalloutNewVegas).DistanceEnvelope;

        Assert.Equal(6000f, envelope.EffectiveHardEnd(6000f));
        Assert.Equal(8000f, envelope.EffectiveHardEnd(12000f));
        Assert.True(GrassDistanceCullPolicy.Passes(
            true, in envelope, 6000f * 6000f, 6000f));
        Assert.False(GrassDistanceCullPolicy.Passes(
            true, in envelope, 6001f * 6001f, 6000f));
    }

    [Fact]
    public void EstablishmentSlackKeepsDriftSupersetButExactCullRestoresHardEnd()
    {
        var envelope = GrassScatterProfile.ForGame(BethesdaGame.FalloutNewVegas).DistanceEnvelope;
        const float placementFromEstablishmentCamera = 8250f;

        Assert.True(GrassDistanceCullPolicy.Passes(
            true,
            in envelope,
            placementFromEstablishmentCamera * placementFromEstablishmentCamera,
            12000f,
            512f));
        Assert.False(GrassDistanceCullPolicy.Passes(
            true,
            in envelope,
            placementFromEstablishmentCamera * placementFromEstablishmentCamera,
            12000f));

        // After the camera moves 300 units toward the retained placement, the same frozen batch
        // becomes exactly eligible without rebuilding.
        const float placementFromDriftedCamera = placementFromEstablishmentCamera - 300f;
        Assert.True(GrassDistanceCullPolicy.Passes(
            true,
            in envelope,
            placementFromDriftedCamera * placementFromDriftedCamera,
            12000f));
    }

    [Fact]
    public void WindowsRenderer_WiresEnvelopePolicyThroughEveryDistanceCullRoute()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        var batches = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "OpaqueBatchRegistry12.cs");
        var compactRenderer = RemoveWhitespace(renderer);

        Assert.Contains(
            "GrassScatterProfile.ForGame(renderCache.Game).DistanceEnvelope",
            renderer,
            StringComparison.Ordinal);

        // Cold and resident establishment both use the authored placement origin, not OBND or
        // mesh-local cull-center offsets.
        Assert.Contains(
            "HorizontalDistanceSquared(r.WorldMatrix.Translation, cylinderX, cylinderY)",
            renderer,
            StringComparison.Ordinal);

        // In FNV, a submesh shared by envelope-covered GRAS and an ordinary REFR must split. The
        // qualified policy bit keeps every non-FNV placement on the established false key.
        Assert.Contains("Dictionary<OpaqueBatchKey, OpaqueBatchState>", batches, StringComparison.Ordinal);
        Assert.Contains(
            "submesh, usesGrassDistanceEnvelope, effectiveTallGrassWind, effectiveWaveMultiplier",
            batches,
            StringComparison.Ordinal);
        Assert.Contains(
            "public bool UsesGrassDistanceEnvelope { get; } = usesGrassDistanceEnvelope;",
            batches,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            SourceContract.CountOccurrences(
                compactRenderer,
                "_opaqueBatches.GetOrCreate(sub,pso," +
                "usesGrassDistanceEnvelope,usesTallGrassWind," +
                "r.GrassWaveMultiplier)"));

        // Both shared-ring and fallback-ring opaque copies route envelope batches through an exact
        // per-instance predicate even when generic/frustum refiltering is false. The generic exact
        // cull remains independently guarded, so a disabled frustum is never sampled by this route.
        Assert.Equal(
            2,
            SourceContract.CountOccurrences(renderer, "GrassDistanceCullPolicy.RequiresExactPerInstanceFiltering("));
        Assert.Contains(
            "_frameRefilterActive = _batchesWidened && hasFrustum;",
            renderer,
            StringComparison.Ordinal);
        Assert.Equal(2, SourceContract.CountOccurrences(renderer, "if (!exactMainPerInstance &&"));
        Assert.Equal(
            2,
            SourceContract.CountOccurrences(renderer, "if (filterMainGrassDistance && !PassesExactGrassDistance("));
        Assert.Equal(
            2,
            SourceContract.CountOccurrences(renderer, "if (refilter && !PassesExactCull(boundsSpan[i]))"));
        Assert.Contains(
            "r.WorldMatrix.Translation, mesh.LocalBoundsRadius * boundsScaleBasis.Length()",
            renderer,
            StringComparison.Ordinal);

        // Non-opaque grass keeps identity and is gated before reservation/draw without deleting a
        // retained entry, so moving back inside the envelope works on frozen batches.
        Assert.Contains("bool IsGrass,", renderer, StringComparison.Ordinal);
        Assert.Contains("float GrassWaveMultiplier,", renderer, StringComparison.Ordinal);
        Assert.Equal(
            2,
            SourceContract.CountOccurrences(renderer,
                "PassesExactGrassDistance(draw.SourceWorld.Translation, draw.IsGrass)"));
        Assert.Contains("draws[i].SourceWorld.Translation", renderer, StringComparison.Ordinal);
        Assert.Contains("draws[i].IsGrass", renderer, StringComparison.Ordinal);

        // Widened off-screen shadow casters are independently restored to the exact endpoint in
        // both shared-ring and fallback-ring copies.
        Assert.Equal(
            2,
            SourceContract.CountOccurrences(renderer,
                "var filterGrassDistance = batchState.UsesGrassDistanceEnvelope;"));
        Assert.Equal(
            2,
            SourceContract.CountOccurrences(renderer, "shadowSpan[i].Translation + _frameRenderOrigin"));
    }

    private static string RemoveWhitespace(string source)
    {
        return new string(source.Where(character => !char.IsWhiteSpace(character)).ToArray());
    }
}