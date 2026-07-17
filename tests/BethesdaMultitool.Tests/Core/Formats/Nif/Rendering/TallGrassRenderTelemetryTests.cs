using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class TallGrassRenderTelemetryTests
{
    [Fact]
    public void WorldRenderStats_SnapshotAndResetPreserveTallGrassTelemetryContract()
    {
        var stats = new global::BethesdaMultitool.WorldRenderStats
        {
            ReferenceTallGrassWindSupported = true,
            ReferenceTallGrassAnimationsEnabled = true,
            ReferenceTallGrassNormalizedStrength = 0.75f,
            ReferenceTallGrassMagnitudeWorldUnits = 95f,
            ReferenceTallGrassDirectionX = 0f,
            ReferenceTallGrassDirectionY = 1f,
            ReferenceTallGrassAnimationSeconds = 60f,
            ReferenceTallGrassInstancedDraws = 4,
            ReferenceTallGrassInstancedInstances = 2_077,
            ReferenceTallGrassDirectDraws = 2,
            ReferenceTallGrassDirectInstances = 2,
            ReferenceTallGrassShadowDraws = 12,
            ReferenceTallGrassShadowInstances = 6_231,
            ReferenceTallGrassWaveMultiplierMinimum = 10f,
            ReferenceTallGrassWaveMultiplierMaximum = 15f,
            ReferenceTallGrassWaveMultiplierDistinctCount = 3,
            ReferenceTallGrassTemporalPhaseRadiansMinimum = MathF.PI / 3f,
            ReferenceTallGrassTemporalPhaseRadiansMaximum = MathF.PI / 2f,
        };

        var snapshot = stats.Snapshot();

        Assert.True(snapshot.ReferenceTallGrassWindSupported);
        Assert.True(snapshot.ReferenceTallGrassAnimationsEnabled);
        Assert.Equal(0.75f, snapshot.ReferenceTallGrassNormalizedStrength);
        Assert.Equal(95f, snapshot.ReferenceTallGrassMagnitudeWorldUnits);
        Assert.Equal(0f, snapshot.ReferenceTallGrassDirectionX);
        Assert.Equal(1f, snapshot.ReferenceTallGrassDirectionY);
        Assert.Equal(60f, snapshot.ReferenceTallGrassAnimationSeconds);
        Assert.Equal(4, snapshot.ReferenceTallGrassInstancedDraws);
        Assert.Equal(2_077, snapshot.ReferenceTallGrassInstancedInstances);
        Assert.Equal(2, snapshot.ReferenceTallGrassDirectDraws);
        Assert.Equal(2, snapshot.ReferenceTallGrassDirectInstances);
        Assert.Equal(12, snapshot.ReferenceTallGrassShadowDraws);
        Assert.Equal(6_231, snapshot.ReferenceTallGrassShadowInstances);
        Assert.Equal(10f, snapshot.ReferenceTallGrassWaveMultiplierMinimum);
        Assert.Equal(15f, snapshot.ReferenceTallGrassWaveMultiplierMaximum);
        Assert.Equal(3, snapshot.ReferenceTallGrassWaveMultiplierDistinctCount);
        Assert.Equal(MathF.PI / 3f, snapshot.ReferenceTallGrassTemporalPhaseRadiansMinimum);
        Assert.Equal(MathF.PI / 2f, snapshot.ReferenceTallGrassTemporalPhaseRadiansMaximum);

        stats.Reset();

        Assert.False(stats.ReferenceTallGrassWindSupported);
        Assert.False(stats.ReferenceTallGrassAnimationsEnabled);
        Assert.Equal(0f, stats.ReferenceTallGrassMagnitudeWorldUnits);
        Assert.Equal(0, stats.ReferenceTallGrassInstancedDraws);
        Assert.Equal(0, stats.ReferenceTallGrassDirectDraws);
        Assert.Equal(0, stats.ReferenceTallGrassShadowDraws);
        Assert.Equal(0, stats.ReferenceTallGrassWaveMultiplierDistinctCount);
        Assert.Equal(0f, stats.ReferenceTallGrassTemporalPhaseRadiansMaximum);
    }

    [Fact]
    public void AnimationToggle_ReportsExactlyOneInvalidationPerActualTransition()
    {
        var enabled = true;
        var invalidations = 0;

        void Set(bool requested)
        {
            if (ReferenceAnimationToggle.TryApply(ref enabled, requested))
            {
                invalidations++;
            }
        }

        Set(true);
        Assert.True(enabled);
        Assert.Equal(0, invalidations);

        Set(false);
        Set(false);
        Assert.False(enabled);
        Assert.Equal(1, invalidations);

        Set(true);
        Set(true);
        Assert.True(enabled);
        Assert.Equal(2, invalidations);
    }

    [Fact]
    public void RendererAndCaptureSources_ExposeAllTallGrassDrawRoutesWithAppendOnlyConstantAbi()
    {
        var renderer = ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
            "ReferenceRenderer12.cs");
        var capture = ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3DControl.SceneCapture.cs");
        var compactRenderer = string.Concat(renderer.Where(c => !char.IsWhiteSpace(c)));
        var compactCapture = string.Concat(capture.Where(c => !char.IsWhiteSpace(c)));

        Assert.Contains("private const uint InstanceDrawByteSize = 256;", renderer, StringComparison.Ordinal);
        Assert.Contains("bool UsesTallGrassWind);", renderer, StringComparison.Ordinal);
        Assert.Contains("ReferenceTallGrassInstancedDraws++", renderer, StringComparison.Ordinal);
        Assert.Contains("ReferenceTallGrassDirectDraws++", renderer, StringComparison.Ordinal);
        Assert.Contains("ReferenceTallGrassShadowDraws++", renderer, StringComparison.Ordinal);
        Assert.Contains("ObserveTallGrassWaveMultiplier", renderer, StringComparison.Ordinal);
        Assert.Contains(
            "if(!ReferenceAnimationToggle.TryApply(ref_animationsEnabled,value)){return;}" +
            "unchecked{BatchContentVersion++;}",
            compactRenderer,
            StringComparison.Ordinal);
        var supportAssignment = compactRenderer.IndexOf(
            "LastStats.ReferenceTallGrassWindSupported=_tallGrassWindSupported;",
            StringComparison.Ordinal);
        Assert.True(supportAssignment >= 0);
        var supportBranch = compactRenderer.IndexOf(
            "if(_tallGrassWindSupported)",
            supportAssignment,
            StringComparison.Ordinal);
        Assert.True(supportBranch > supportAssignment);
        Assert.Contains(
            "fields[\"tallGrassWind\"]=referenceStatsisnot" +
            "{ReferenceTallGrassWindSupported:true}?null:",
            compactCapture,
            StringComparison.Ordinal);

        foreach (var key in new[]
                 {
                     "tallGrassWindSupported", "tallGrassWind", "animationsEnabled",
                     "normalizedStrength", "magnitudeWorldUnits",
                     "instancedDraws", "instancedInstances", "directDraws", "directInstances",
                     "shadowDraws", "shadowInstances", "waveMultiplierMin", "waveMultiplierMax",
                     "waveMultiplierDistinctCount", "temporalPhaseRadiansMin", "temporalPhaseRadiansMax",
                 })
        {
            Assert.Contains($"[\"{key}\"]", capture, StringComparison.Ordinal);
        }
    }

    private static string ReadSource(params string[] relativePath) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), Path.Combine(relativePath)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
