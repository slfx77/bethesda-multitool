using System.Text.Json;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class RendererProfilerTraceTests
{
    [Fact]
    public void Event_WritesOneJsonObjectPerLineWithoutLoggerPrefix()
    {
        using var writer = new StringWriter();
        try
        {
            RendererProfilerTrace.SetWriterForTesting(writer);

            RendererProfilerTrace.Event("frame-stall", new Dictionary<string, object?>
            {
                ["frame"] = 42,
                ["cpuMs"] = 51.25,
                ["cameraMotion"] = "sweep"
            });

            var text = writer.ToString();
            var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(lines);
            Assert.StartsWith("{", lines[0], StringComparison.Ordinal);
            Assert.EndsWith("}", lines[0], StringComparison.Ordinal);

            using var document = JsonDocument.Parse(lines[0]);
            var root = document.RootElement;
            Assert.Equal("frame-stall", root.GetProperty("event").GetString());
            Assert.True(root.TryGetProperty("timestamp", out _));
            Assert.Equal(42, root.GetProperty("frame").GetInt32());
            Assert.Equal(51.25, root.GetProperty("cpuMs").GetDouble(), 3);
            Assert.Equal("sweep", root.GetProperty("cameraMotion").GetString());
        }
        finally
        {
            RendererProfilerTrace.ResetForTesting();
        }
    }

    [Fact]
    public void Event_NoOpsWhenTraceIsNotConfigured()
    {
        RendererProfilerTrace.ResetForTesting();

        RendererProfilerTrace.Event("startup");

        Assert.False(RendererProfilerTrace.IsEnabled);
    }

    [Fact]
    public void StatsFields_EmitsLiveParticleWorkloadCounters()
    {
        var fields = RendererProfilerTrace.StatsFields("refs.", new global::BethesdaMultitool.WorldRenderStats
        {
            ReferenceLiveParticleOwners = 2,
            ReferenceLiveParticleParticles = 19,
            ReferenceLiveParticleDraws = 3,
            ReferenceLiveParticleFallbacks = 1,
            ReferenceLiveParticleUploadBytes = 4096,
            ReferenceLiveParticleUvFrame = 7,
            ReferenceLiveParticleAtlasFrameCount = 16,
            ReferenceLiveParticleAuthoredCapacity = 19,
        });

        Assert.Equal(2, Assert.IsType<int>(fields["refs.refLiveParticleOwners"]));
        Assert.Equal(19, Assert.IsType<int>(fields["refs.refLiveParticleParticles"]));
        Assert.Equal(3, Assert.IsType<int>(fields["refs.refLiveParticleDraws"]));
        Assert.Equal(1, Assert.IsType<int>(fields["refs.refLiveParticleFallbacks"]));
        Assert.Equal((uint)4096, Assert.IsType<uint>(fields["refs.refLiveParticleUploadBytes"]));
        Assert.Equal(7, Assert.IsType<int>(fields["refs.refLiveParticleUvFrame"]));
        Assert.Equal(16, Assert.IsType<int>(fields["refs.refLiveParticleAtlasFrames"]));
        Assert.Equal(19, Assert.IsType<int>(fields["refs.refLiveParticleAuthoredCapacity"]));
    }
}
