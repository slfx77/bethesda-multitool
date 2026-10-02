using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Actors;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.RuntimeSession;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Actors;

public sealed class ActorInvocationStatisticsServiceTests
{
    [Theory]
    [InlineData("both", true, true)]
    [InlineData("critical-sequence", true, false)]
    [InlineData("damage-sequence", false, true)]
    [InlineData("critical-owner", true, false)]
    [InlineData("critical-prior-abandon", true, true)]
    public async Task DamageAndCriticalSelectorsKeepSeparateInvocationEvidence(string control, bool damageObserved, bool criticalObserved)
    {
        using var temp = CliExeRunner.CreateTempDirectory();
        var token = TestContext.Current.CancellationToken;
        var pluginHash = new string('a', 64);
        var damage = ActorMeleeDamageObservationReaderTests.Row();
        damage["connectionGeneration"] = 2;
        var critical = JsonNode.Parse(JsonSerializer.Serialize(ActorCriticalInvocationObservationReaderTests.Fixture()))!.AsObject();
        critical["protocol"] = 1; critical["sequence"] = 4; critical["requestId"] = 1; critical["frame"] = 1;
        if (control == "critical-owner") critical["sourceFormId"] = 21;
        if (control == "critical-prior-abandon") critical["abandonedInvocations"] = 2;
        var header = new JsonObject
        {
            ["kind"] = "capture-header", ["schema"] = "bmt/runtime-trace", ["version"] = 1,
            ["identity"] = new JsonObject
            {
                ["sequence"] = 1, ["executableFileSha256"] = ActorEngineProfile.PcRetail.ExecutableSha256,
                ["activePluginIdentityStatus"] = "complete", ["activePlugins"] = new JsonArray(new JsonObject
                { ["index"] = 0, ["name"] = "FalloutNV.esm", ["sha256"] = pluginHash, ["hashScope"] = "copied-backing-file", ["status"] = "verified" })
            }
        };
        string[] lines =
        [
            header.ToJsonString(),
            """{"kind":"capture-start","protocol":1,"sequence":2,"dropped":0,"dispatchWindow":{"captureGeneration":1,"connectionGeneration":2}}""",
            damage.ToJsonString(), critical.ToJsonString(),
            """{"kind":"capture-end","status":"completed","protocol":1,"sequence":5,"dropped":0}""",
            """{"kind":"capture-footer","status":"completed","events":4,"dropped":0,"snapshots":0,"errors":0}"""
        ];
        await File.WriteAllLinesAsync(Path.Combine(temp.Path, "trace.ndjson"), lines, token);
        var scenarioPath = Path.Combine(temp.Path, "scenario.json");
        await File.WriteAllTextAsync(scenarioPath, new JsonObject
        {
            ["runtimeTrace"] = "trace.ndjson", ["referenceFormId"] = 20,
            ["executableSha256"] = ActorEngineProfile.PcRetail.ExecutableSha256,
            ["damageObservationSequence"] = control == "damage-sequence" ? 4 : 3,
            ["criticalObservationSequence"] = control == "critical-sequence" ? 3 : 4
        }.ToJsonString(), token);
        var actor = new ActorInspection(7, "Actor", "NPC_", [], [], null) { Game = BethesdaGame.FalloutNewVegas };
        var values = await ActorStatisticsService.InspectAsync(actor, "pc-retail", scenarioPath, token,
            new RuntimeTraceSources([new(0, "FalloutNV.esm", pluginHash)], true));
        var hit = Assert.Single(values, value => value.Key == "RuntimeDamage");
        Assert.Equal(damageObserved ? "Observed" : "Unavailable", hit.Status);
        var decisions = values.Where(value => value.Key == "CritChance.accepted").ToArray();
        Assert.Equal(criticalObserved ? 1 : 0, decisions.Length);
        Assert.Equal("Unavailable", Assert.Single(values, value => value.Key == "RuntimeCriticalChance").Status);
        if (criticalObserved)
        {
            var decision = Assert.Single(decisions);
            Assert.Equal("Observed", decision.Status); Assert.Equal(1d, decision.Value);
            Assert.Equal(4UL, decision.Sequence); Assert.Equal(20u, decision.ReferenceFormId);
            Assert.Equal(4u, decision.HitFlags); Assert.EndsWith("trace.ndjson#L4", decision.Evidence, StringComparison.Ordinal);
            Assert.Contains(decision.Inputs, input => input.Key == "Weapon.address" && input.Value == 0);
            Assert.Contains(decision.Inputs, input => input.Key == "Weapon.selectedAddress" && input.Value == 0x6000);
            if (control == "critical-prior-abandon") Assert.Contains(decision.TraceDiagnostics, diagnostic => diagnostic.Contains("abandoned", StringComparison.Ordinal));
            if (damageObserved) { Assert.Equal(3UL, hit.Sequence); Assert.Equal(hit.TraceSha256, decision.TraceSha256); }
        }
    }

    [Theory]
    [InlineData("valid", true, true)]
    [InlineData("partial-operands", false, true)]
    [InlineData("selected-sequence", false, false)]
    [InlineData("source-hash", false, false)]
    [InlineData("header-hash", false, false)]
    [InlineData("record-kind", false, false)]
    [InlineData("game", false, false)]
    [InlineData("incomplete", false, false)]
    [InlineData("boundary-generation", false, false)]
    [InlineData("missing-boundary", false, false)]
    [InlineData("no-selector", false, false)]
    [InlineData("no-trace", false, false)]
    [InlineData("independent-health", true, true)]
    public async Task SelectsOneBoundDamageInvocationWithoutChangingHealthSelection(string control, bool calculated, bool hitObserved)
    {
        using var temp = CliExeRunner.CreateTempDirectory();
        var token = TestContext.Current.CancellationToken;
        var pluginHash = new string('a', 64);
        var executable = control == "header-hash" ? new string('b', 64) : ActorEngineProfile.PcRetail.ExecutableSha256;
        var row = ActorMeleeDamageObservationReaderTests.Row();
        if (control == "partial-operands") row["damageStage"]!["inputs"]!.AsObject().Remove("ammunitionDamage");
        var start = new JsonObject
        {
            ["kind"] = "capture-start", ["protocol"] = 1, ["sequence"] = 2, ["dropped"] = 0,
            ["dispatchWindow"] = new JsonObject
            { ["captureGeneration"] = control == "boundary-generation" ? 2 : 1, ["connectionGeneration"] = 1 }
        };
        if (control == "missing-boundary") start.Remove("dispatchWindow");
        var header = new JsonObject
        {
            ["kind"] = "capture-header", ["schema"] = "bmt/runtime-trace", ["version"] = 1,
            ["identity"] = new JsonObject
            {
                ["sequence"] = 1, ["executableFileSha256"] = executable, ["activePluginIdentityStatus"] = "complete",
                ["activePlugins"] = new JsonArray(new JsonObject
                { ["index"] = 0, ["name"] = "FalloutNV.esm", ["sha256"] = pluginHash, ["hashScope"] = "copied-backing-file", ["status"] = "verified" })
            }
        };
        var completion = control == "incomplete" ? "cancelled" : "completed";
        string[] lines =
        [
            header.ToJsonString(), start.ToJsonString(), row.ToJsonString(),
            new JsonObject { ["kind"] = "capture-end", ["status"] = completion, ["protocol"] = 1, ["sequence"] = 4, ["dropped"] = 0 }.ToJsonString(),
            new JsonObject { ["kind"] = "capture-footer", ["status"] = completion, ["events"] = 3, ["dropped"] = 0, ["snapshots"] = 0, ["errors"] = 0 }.ToJsonString()
        ];
        await File.WriteAllLinesAsync(Path.Combine(temp.Path, "trace.ndjson"), lines, token);
        var scenario = new JsonObject
        {
            ["runtimeTrace"] = "trace.ndjson", ["referenceFormId"] = 20, ["executableSha256"] = executable,
            ["damageObservationSequence"] = control == "selected-sequence" ? 9 : 3
        };
        if (control == "no-selector") scenario.Remove("damageObservationSequence");
        if (control == "no-trace") scenario.Remove("runtimeTrace");
        if (control == "independent-health") scenario["calculationObservationSequence"] = 9;
        var scenarioPath = Path.Combine(temp.Path, "scenario.json");
        await File.WriteAllTextAsync(scenarioPath, scenario.ToJsonString(), token);
        var actor = new ActorInspection(7, "Actor", control == "record-kind" ? "CREA" : "NPC_", [], [], null)
        { Game = control == "game" ? BethesdaGame.Fallout3 : BethesdaGame.FalloutNewVegas };
        var values = await ActorStatisticsService.InspectAsync(actor, "pc-retail", scenarioPath, token,
            new RuntimeTraceSources([new(0, "FalloutNV.esm", control == "source-hash" ? new string('c', 64) : pluginHash)], true));

        var predictions = values.Where(value => value.Key == "WeaponDamage.preTargetStage" && value.Status == "Calculated").ToArray();
        Assert.Equal(calculated ? 1 : 0, predictions.Length);
        if (calculated)
        {
            Assert.Equal(15d, predictions[0].Value);
            Assert.Equal(999d, Assert.Single(values, value => value.Key == "WeaponDamage.preTargetStage" && value.Status == "Observed").Value);
        }
        var damage = Assert.Single(values, value => value.Key == "RuntimeDamage");
        Assert.Equal(hitObserved ? "Observed" : "Unavailable", damage.Status);
        Assert.Contains(values, value => value.Key == "RuntimeHealth" && value.Status == "Unavailable");
        Assert.Contains(values, value => value.Key == "RuntimeCriticalChance" && value.Status == "Unavailable");
        if (hitObserved)
        {
            Assert.Equal(6d, damage.Value); Assert.Equal(0x80u, damage.HitFlags);
            Assert.Equal(3UL, damage.Sequence); Assert.Equal(20u, damage.ReferenceFormId);
            Assert.Equal("Complete", damage.TraceStatus); Assert.Equal(64, damage.TraceSha256!.Length);
            Assert.EndsWith("trace.ndjson#L3", damage.Evidence, StringComparison.Ordinal);
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
            {
                writer.WriteStartObject(); ActorStatisticsService.Write(writer, values, "pc-retail", scenarioPath); writer.WriteEndObject();
            }
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
            var exported = json.RootElement.GetProperty("runtimeStatistics").GetProperty("values").EnumerateArray()
                .Single(value => value.GetProperty("key").GetString() == "RuntimeDamage");
            Assert.Equal(0x80u, exported.GetProperty("hitFlags").GetUInt32());
            Assert.Equal(damage.TraceSha256, exported.GetProperty("traceSha256").GetString());
        }
    }
}
