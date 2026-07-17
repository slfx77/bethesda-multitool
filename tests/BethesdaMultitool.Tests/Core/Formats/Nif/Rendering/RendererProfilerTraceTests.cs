using System.Text.Json;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Games;
using BethesdaRendererProfiler;
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
            ReferenceSoftParticleDraws = 4,
            ReferenceSoftParticleFallbackDraws = 3,
            ReferenceSoftParticleDepthSampleCount = 4,
        });

        Assert.Equal(2, Assert.IsType<int>(fields["refs.refLiveParticleOwners"]));
        Assert.Equal(19, Assert.IsType<int>(fields["refs.refLiveParticleParticles"]));
        Assert.Equal(3, Assert.IsType<int>(fields["refs.refLiveParticleDraws"]));
        Assert.Equal(1, Assert.IsType<int>(fields["refs.refLiveParticleFallbacks"]));
        Assert.Equal((uint)4096, Assert.IsType<uint>(fields["refs.refLiveParticleUploadBytes"]));
        Assert.Equal(7, Assert.IsType<int>(fields["refs.refLiveParticleUvFrame"]));
        Assert.Equal(16, Assert.IsType<int>(fields["refs.refLiveParticleAtlasFrames"]));
        Assert.Equal(19, Assert.IsType<int>(fields["refs.refLiveParticleAuthoredCapacity"]));
        Assert.Equal(4, Assert.IsType<int>(fields["refs.refSoftParticleDraws"]));
        Assert.Equal(3, Assert.IsType<int>(fields["refs.refSoftParticleFallbackDraws"]));
        Assert.Equal(4, Assert.IsType<int>(fields["refs.refSoftParticleDepthSamples"]));
    }

    [Fact]
    public void ScenarioTrace_EmitsCorrelatedMonotonicJsonlEvents()
    {
        using var writer = new StringWriter();
        try
        {
            RendererProfilerTrace.SetWriterForTesting(writer);
            Assert.True(RendererProfilerScenarioCatalog.TryCreate(
                RendererProfilerScenarioCatalog.FnvCloudMotion, out var plan));
            var sink = new RendererProfilerScenarioTraceSink();
            var step = plan!.Steps[0];
            var snapshot = new RendererProfilerScenarioSnapshot(
                BethesdaGame.FalloutNewVegas,
                "WastelandNV",
                step.WeatherEditorId,
                step.GameHour,
                step.GameDay,
                step.AnimationTimeSeconds,
                System.Numerics.Vector3.UnitZ,
                System.Numerics.Vector3.UnitZ,
                1,
                System.Numerics.Vector3.UnitY,
                1f,
                0,
                3,
                [],
                0,
                null,
                false,
                []);
            var stepResult = new RendererProfilerScenarioStepResult(
                step,
                snapshot,
                new RendererProfilerCameraPose(step.CameraPosition, 0f, 0f, 16384f),
                new RendererProfilerScenarioAppliedPostProcessSettings(
                    true, true, true, true, true, true,
                    "EngineFo3Fnv", "NVDefaultExterior", "worldspace-inam"),
                @"C:\captures\00-t-000.png",
                new string('A', 64),
                new string('B', 64),
                new RendererProfilerScenarioImageStatistics(
                    2, 2, 16, 4, 1, 0.95, 1, 200, 180, 190, 0.5),
                null,
                25);
            var assertion = new RendererProfilerScenarioAssertion(
                "synthetic.failed", false, 0, step.Id, "expected", "actual", "details");
            var runResult = new RendererProfilerScenarioRunResult(
                false, 1, 1, 1, 1, "scenario-assertion-failed", [stepResult], [assertion]);

            sink.ScenarioStarted(plan, @"C:\captures");
            sink.StepStarted(step, 0, 10);
            sink.StepCompleted(stepResult, 0, 35);
            sink.AssertionCompleted(assertion, 40);
            sink.ScenarioCompleted(runResult, 45);

            var events = writer.ToString()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line))
                .ToArray();
            try
            {
                Assert.Equal(
                    ["scenario-start", "scenario-step", "scenario-step", "scenario-assertion", "scenario-complete"],
                    events.Select(document => document.RootElement.GetProperty("event").GetString()));
                Assert.All(events, document =>
                    Assert.Equal(sink.RunId, document.RootElement.GetProperty("runId").GetString()));
                Assert.Equal([1L, 2L, 3L, 4L, 5L],
                    events.Select(document => document.RootElement.GetProperty("sequence").GetInt64()));

                var completedStep = events[2].RootElement;
                Assert.Equal("complete", completedStep.GetProperty("phase").GetString());
                Assert.Equal(new string('A', 64), completedStep.GetProperty("pixelSha256").GetString());
                Assert.True(completedStep.GetProperty("appliedBloomEnabled").GetBoolean());
                Assert.Equal(1, completedStep.GetProperty("brightPixelCount").GetInt64());
                Assert.Equal(190, completedStep.GetProperty("luminanceP99").GetByte());
                var failedAssertion = events[3].RootElement;
                Assert.False(failedAssertion.GetProperty("passed").GetBoolean());
                Assert.Equal("expected", failedAssertion.GetProperty("expected").GetString());
                Assert.Equal("actual", failedAssertion.GetProperty("actual").GetString());
                var complete = events[4].RootElement;
                Assert.Equal(1, complete.GetProperty("failedAssertionCount").GetInt32());
                Assert.Equal(1, complete.GetProperty("exitCode").GetInt32());
            }
            finally
            {
                foreach (var document in events) document.Dispose();
            }
        }
        finally
        {
            RendererProfilerTrace.ResetForTesting();
        }
    }
}
