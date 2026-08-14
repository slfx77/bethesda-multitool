using System.Numerics;
using System.Text.Json;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using BethesdaMultitool.Core.Games;
using BethesdaRendererProfiler;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Profiling;

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
    public void TextureCacheSummary_EmitsTaggedResidentAliasSchema()
    {
        var aliases = new GpuTextureCache12.LegacyAliasTrace(
            @"SetDressing\NewsStand\NewStand01_n.dds");
        aliases.Observe(@"textures\SetDressing\NewsStand\NewStand01_n.dds");
        var aliasSummary = GpuTextureCache12.BuildAliasTraceSummary(
        [
            new GpuTextureCache12.AliasTraceEntry(
                @"textures\setdressing\newsstand\newstand01_n.dds",
                IsResident: true,
                ResidentPayloadBytes: 4_096,
                AliasTrace: aliases)
        ]);
        var fields = GpuTextureCache12.BuildCacheSummaryTraceFields(
            "reference",
            new ResourceStats
            {
                EstimatedBytes = 4_096,
                EntryCount = 1,
                Hits = 7,
                Misses = 2,
                Evictions = 1,
                QueueDepth = 3,
                InFlight = 4
            },
            pendingResolves: 5,
            pendingUploads: 6,
            pendingUploadDispatch: 3,
            aliases: aliasSummary);

        using var writer = new StringWriter();
        try
        {
            RendererProfilerTrace.SetWriterForTesting(writer);
            RendererProfilerTrace.Event("resource-event", fields);

            using var document = JsonDocument.Parse(writer.ToString());
            var root = document.RootElement;
            Assert.Equal("resource-event", root.GetProperty("event").GetString());
            Assert.Equal("texture", root.GetProperty("resource").GetString());
            Assert.Equal("cache-summary", root.GetProperty("phase").GetString());
            Assert.Equal("reference", root.GetProperty("cacheTag").GetString());
            Assert.Equal(1L, root.GetProperty("cacheEntries").GetInt64());
            Assert.Equal(1, root.GetProperty("residentEntries").GetInt32());
            Assert.Equal(0, root.GetProperty("nonResidentEntries").GetInt32());
            Assert.Equal(4_096L, root.GetProperty("residentPayloadBytes").GetInt64());
            Assert.Equal(5, root.GetProperty("pendingResolves").GetInt32());
            Assert.Equal(6, root.GetProperty("pendingUploads").GetInt32());
            Assert.Equal(3, root.GetProperty("pendingUploadDispatch").GetInt32());
            Assert.Equal(1, root.GetProperty("residentAliasGroups").GetInt32());
            Assert.Equal(1, root.GetProperty("residentLegacyExtraKeys").GetInt32());
            Assert.Equal(
                4_096L,
                root.GetProperty("estimatedResidentAliasPayloadBytesAvoided").GetInt64());

            var detail = Assert.Single(
                root.GetProperty("residentAliasDetails").EnumerateArray().ToArray());
            Assert.Equal(
                @"textures\setdressing\newsstand\newstand01_n.dds",
                detail.GetProperty("canonicalKey").GetString());
            Assert.Equal(2, detail.GetProperty("legacyKeys").GetArrayLength());
        }
        finally
        {
            RendererProfilerTrace.ResetForTesting();
        }
    }

    [Fact]
    public void StatsFields_EmitsLiveParticleWorkloadCounters()
    {
        var fields = RendererProfilerTrace.StatsFields("refs.", new WorldRenderStats
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
            ReferenceSoftParticleDepthSampleCount = 4
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
                Vector3.UnitZ,
                Vector3.UnitZ,
                1,
                Vector3.UnitY,
                1f,
                0,
                3,
                [],
                0,
                null,
                false,
                [],
                null,
                null,
                "unavailable",
                null,
                0x1234UL,
                true,
                "history-key",
                new RendererProfilerClimateTimingSnapshot(6f, 8f, 18f, 20f),
                new RendererProfilerAtmosphericColorBandSnapshot("Day", "HighNoon", 1f),
                [
                    new RendererProfilerWeatherImageSpaceContributionSnapshot(
                        "Day", 0x00164BA6, "NVJacobstownIS", 0.5f, 0f),
                    new RendererProfilerWeatherImageSpaceContributionSnapshot(
                        "HighNoon", 0x000CEE18, "NVWastelandIS", 0.5f, null)
                ],
                new RendererProfilerTonemapSnapshot(
                    0.6f, 0.7768509f, 0.6247225f, 0.2386268f, 0.33f),
                "FnvWater001Reconstructed-opaque-snapshot-main-scene-depth-approx-msaa4x",
                "selective-content-mask-approximated-by-main-depth",
                4,
                1,
                2,
                3,
                4,
                0,
                true,
                FnvActiveAdtBaseDraws: 5,
                FnvActiveAdtBaseInstances: 6,
                FnvActiveAdtBaseVertexColorDraws: 2,
                FnvActiveAdtBaseVertexColorInstances: 3,
                FnvActiveAdtBaseEnabled: true);
            var stepResult = new RendererProfilerScenarioStepResult(
                step,
                snapshot,
                new RendererProfilerCameraPose(step.CameraPosition, 0f, 0f, 16384f),
                new RendererProfilerScenarioAppliedPostProcessSettings(
                    true, true, true, true, true, true,
                    "EngineFo3Fnv", "NVDefaultExterior", "worldspace-inam", 1.21f, 1.21f),
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

                var startedStep = events[1].RootElement;
                // The cloud-motion scenario pins every post-process toggle explicitly (shadows
                // isolated OFF so the image delta belongs to the animation clock); the started
                // event must echo the requested toggles rather than nulls.
                Assert.True(startedStep.GetProperty("requestedHdrEnabled").GetBoolean());
                Assert.True(startedStep.GetProperty("requestedBloomEnabled").GetBoolean());
                Assert.True(startedStep.GetProperty("requestedImagespaceEnabled").GetBoolean());
                Assert.True(startedStep.GetProperty("requestedFogEnabled").GetBoolean());
                Assert.False(startedStep.GetProperty("requestedShadowsEnabled").GetBoolean());

                var completedStep = events[2].RootElement;
                Assert.Equal("complete", completedStep.GetProperty("phase").GetString());
                Assert.Equal(new string('A', 64), completedStep.GetProperty("pixelSha256").GetString());
                Assert.True(completedStep.GetProperty("appliedBloomEnabled").GetBoolean());
                Assert.Equal(1, completedStep.GetProperty("brightPixelCount").GetInt64());
                Assert.Equal(190, completedStep.GetProperty("luminanceP99").GetByte());
                Assert.Equal("0x0000000000001234",
                    completedStep.GetProperty("tonemapHistoryKey").GetString());
                Assert.True(completedStep.GetProperty("tonemapHistoryReset").GetBoolean());
                Assert.Equal("history-key",
                    completedStep.GetProperty("tonemapHistoryResetReason").GetString());
                Assert.Equal("FnvWater001Reconstructed-opaque-snapshot-main-scene-depth-approx-msaa4x",
                    completedStep.GetProperty("waterTechnique").GetString());
                Assert.Equal("selective-content-mask-approximated-by-main-depth",
                    completedStep.GetProperty("waterFallbackReason").GetString());
                Assert.Equal(4, completedStep.GetProperty("sceneSampleCount").GetInt32());
                Assert.Equal(1, completedStep.GetProperty("fnvSls1009Draws").GetInt32());
                Assert.Equal(2, completedStep.GetProperty("fnvSls1009Instances").GetInt32());
                Assert.Equal(3, completedStep.GetProperty("fnvSls1013Draws").GetInt32());
                Assert.Equal(4, completedStep.GetProperty("fnvSls1013Instances").GetInt32());
                Assert.Equal(0, completedStep.GetProperty("placedLightCount").GetInt32());
                Assert.True(completedStep.GetProperty("fnvClassicBasicLightingEnabled").GetBoolean());
                Assert.Equal(0, completedStep.GetProperty("fnvClassicBasicFallbackDraws").GetInt32());
                Assert.Equal(JsonValueKind.Null,
                    completedStep.GetProperty("fnvClassicBasicFallbackReason").ValueKind);
                Assert.Equal(5, completedStep.GetProperty("fnvActiveAdtBaseDraws").GetInt32());
                Assert.Equal(6, completedStep.GetProperty("fnvActiveAdtBaseInstances").GetInt32());
                Assert.Equal(2,
                    completedStep.GetProperty("fnvActiveAdtBaseVertexColorDraws").GetInt32());
                Assert.Equal(3,
                    completedStep.GetProperty("fnvActiveAdtBaseVertexColorInstances").GetInt32());
                Assert.True(completedStep.GetProperty("fnvActiveAdtBaseEnabled").GetBoolean());
                Assert.Equal(0,
                    completedStep.GetProperty("fnvActiveAdtBaseFallbackDraws").GetInt32());
                Assert.Equal(JsonValueKind.Null,
                    completedStep.GetProperty("fnvActiveAdtBaseFallbackReason").ValueKind);
                Assert.True(completedStep.GetProperty("shadowsEnabled").GetBoolean());
                Assert.Equal(6f, completedStep.GetProperty("sunriseBegin").GetSingle());
                Assert.Equal(8f, completedStep.GetProperty("sunriseEnd").GetSingle());
                Assert.Equal(18f, completedStep.GetProperty("sunsetBegin").GetSingle());
                Assert.Equal(20f, completedStep.GetProperty("sunsetEnd").GetSingle());
                var atmosphericColorBand = completedStep.GetProperty("atmosphericColorBand");
                Assert.Equal("Day", atmosphericColorBand.GetProperty("fromBand").GetString());
                Assert.Equal("HighNoon", atmosphericColorBand.GetProperty("toBand").GetString());
                Assert.Equal(1f, atmosphericColorBand.GetProperty("toWeight").GetSingle());
                Assert.Equal(0.6f, completedStep.GetProperty("tonemapTargetLum").GetSingle());
                Assert.Equal(
                    [0.7768509f, 0.6247225f, 0.2386268f, 0.33f],
                    completedStep.GetProperty("tonemapTint")
                        .EnumerateArray()
                        .Select(static value => value.GetSingle()));
                var contributions = completedStep
                    .GetProperty("weatherImageSpaceContributions")
                    .EnumerateArray()
                    .ToArray();
                Assert.Equal(2, contributions.Length);
                Assert.Equal("Day", contributions[0].GetProperty("band").GetString());
                Assert.Equal(0x00164BA6u,
                    contributions[0].GetProperty("modifierFormId").GetUInt32());
                Assert.Equal("0x00164BA6",
                    contributions[0].GetProperty("modifierFormIdHex").GetString());
                Assert.Equal("NVJacobstownIS",
                    contributions[0].GetProperty("modifierEditorId").GetString());
                Assert.Equal(0.5f, contributions[0].GetProperty("weight").GetSingle());
                Assert.Equal(0f, contributions[0].GetProperty("timelineTime").GetSingle());
                Assert.Equal("HighNoon", contributions[1].GetProperty("band").GetString());
                Assert.Equal(JsonValueKind.Null,
                    contributions[1].GetProperty("timelineTime").ValueKind);
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
