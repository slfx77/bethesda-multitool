using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeCombatCleanupTests
{
    private static RuntimeAction Action(int? milliseconds = 5000) => new("start-combat-leased",
        Plugin: "Control.esp", FormId: "800", OtherTarget: "player", TimeoutMilliseconds: milliseconds);

    [Theory]
    [InlineData(1, true)]
    [InlineData(5000, true)]
    [InlineData(0, false)]
    [InlineData(5001, false)]
    [InlineData(null, false)]
    public void Deadline_is_bounded_before_a_request_can_be_sent(int? milliseconds, bool valid)
    {
        if (!valid) { Assert.Throws<ArgumentException>(() => Action(milliseconds).ToFrame(5)); return; }
        Assert.Equal(new RuntimeFrame(RuntimeRequestKind.ReferenceAction, 5,
            $"start-combat-leased\tControl.esp\t000800\t@player\t000014\t{milliseconds}"), Action(milliseconds).ToFrame(5));
        Assert.Throws<ArgumentException>(() => (Action(milliseconds) with { Target = "player", Plugin = null, FormId = null }).ToFrame(5));
        Assert.Throws<ArgumentException>(() => (Action(milliseconds) with
            { OtherPlugin = "control.ESP", OtherFormId = "000800", OtherTarget = null }).ToFrame(5));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Unsupported_or_stopping_connections_send_no_leased_action(bool capability, bool stopped)
    {
        using var identity = JsonDocument.Parse(new JsonObject
        {
            ["capabilities"] = new JsonObject { ["combatCleanupLease"] = capability }
        }.ToJsonString());
        using var output = new MemoryStream();
        await using var connection = new RuntimeConnection(output, identity.RootElement.Clone());
        using var capture = stopped || !capability ? connection.EnterCapture() : null;
        if (stopped) await connection.SendAsync(RuntimeRequestKind.Stop, "", TestContext.Current.CancellationToken);
        var before = output.Length;
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendActionAsync(Action(), TestContext.Current.CancellationToken));
        Assert.Equal(before, output.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_errors_without_a_lease_identity_release_the_cleanup_obligation(bool hasLeaseId)
    {
        var rows = Rows("missing");
        var error = Native("error", 3, 2);
        error["error"] = "combat-lease-runtime-unavailable";
        if (hasLeaseId) error["leaseId"] = 3;
        rows[3] = error;
        var validator = new RuntimeCombatCleanupValidator();
        foreach (var row in rows)
        {
            using var json = JsonDocument.Parse(row.ToJsonString());
            validator.Observe(json.RootElement);
        }
        if (!hasLeaseId) Assert.Empty(validator.GetDiagnostics());
        else
        {
            Assert.Contains("combat-cleanup-armed-error-without-admission", validator.GetDiagnostics());
            Assert.Contains("combat-cleanup-missing:2", validator.GetDiagnostics());
        }
    }

    [Theory]
    [InlineData("observed", true)]
    [InlineData("missing", false)]
    [InlineData("wrong-session", false)]
    [InlineData("wrong-actor", false)]
    [InlineData("wrong-lease", false)]
    [InlineData("missing-readback", false)]
    [InlineData("still-in-combat", false)]
    [InlineData("not-disabled", false)]
    [InlineData("duplicate", false)]
    [InlineData("after-end", false)]
    [InlineData("unrequested", false)]
    [InlineData("loss", false)]
    [InlineData("wrong-address", false)]
    [InlineData("invalid-address", false)]
    [InlineData("early-deadline", false)]
    [InlineData("unknown-trigger", false)]
    [InlineData("wrong-evidence", false)]
    [InlineData("missing-acceptance", false)]
    [InlineData("rejected-start-cleaned", true)]
    [InlineData("rejected-start-missing", false)]
    [InlineData("duplicate-action-index", false)]
    public void Only_attributed_observed_cleanup_completes_a_lease(string mode, bool valid)
    {
        var validator = new RuntimeCombatCleanupValidator();
        foreach (var row in Rows(mode))
        {
            using var json = JsonDocument.Parse(row.ToJsonString());
            validator.Observe(json.RootElement);
        }
        Assert.Equal(valid, validator.GetDiagnostics().Count == 0);
        if (mode == "missing") Assert.Contains(validator.GetDiagnostics(), value => value.StartsWith("combat-cleanup-missing:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("observed", true)]
    [InlineData("missing", false)]
    [InlineData("still-in-combat", false)]
    public async Task Import_independently_requires_cleanup_evidence(string mode, bool complete)
    {
        var rows = Rows(mode);
        rows.Add(new JsonObject { ["kind"] = "capture-footer", ["status"] = "completed", ["events"] = 4,
            ["dropped"] = 0, ["snapshots"] = 0, ["errors"] = 0, ["controllerResults"] = 0, ["controllerErrors"] = 0,
            ["combatCleanupDiagnostics"] = new JsonArray() });
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', rows.Select(row => row.ToJsonString()))));
        var result = await RuntimeTraceImporter.ImportAsync(input, TestContext.Current.CancellationToken);
        Assert.Equal(complete, result.Complete);
        if (!complete) Assert.Contains(result.Diagnostics, value => value.StartsWith("combat-cleanup-", StringComparison.Ordinal));
    }

    private static List<JsonObject> Rows(string mode)
    {
        const string session = "a001";
        var action = JsonSerializer.SerializeToNode(Action(), RuntimeJsonContext.Default.RuntimeAction)!;
        var identity = new JsonObject { ["sequence"] = 1, ["activePluginIdentityStatus"] = "complete",
            ["activePlugins"] = new JsonArray(new JsonObject { ["name"] = "FalloutNV.esm", ["index"] = 0, ["status"] = "verified" },
                new JsonObject { ["name"] = "Control.esp", ["index"] = 1, ["status"] = "verified" }) };
        var header = new JsonObject { ["kind"] = "capture-header", ["schema"] = "bmt/runtime-trace", ["version"] = 2,
            ["session"] = session, ["identity"] = identity.DeepClone(),
            ["scenario"] = new JsonObject { ["actions"] = new JsonArray(action.DeepClone()), ["observeMilliseconds"] = 6000 } };
        var start = Native("capture-start", 2, 1);
        start["identity"] = identity; start["session"] = session;
        var request = new JsonObject { ["kind"] = "action-request", ["actionIndex"] = 0, ["requestId"] = 2,
            ["action"] = action };
        var admission = Native("action-result", 3, 2);
        admission["operation"] = "start-combat-leased"; admission["accepted"] = true;
        var cleanup = Native("combat-cleanup", 4, 2);
        foreach (var row in new[] { admission, cleanup })
        {
            row["leaseId"] = 3; row["originSession"] = session; row["captureGeneration"] = 5;
            row["connectionGeneration"] = 4; row["loadEpoch"] = 1;
            row["engineTargetFormId"] = 0x01000800; row["engineTargetBaseFormId"] = 0x01000801;
            row["engineOtherFormId"] = 0x14; row["engineOtherBaseFormId"] = 7;
            row["attackerAddress"] = 0x100000; row["targetAddress"] = 0x200000;
            row["attackerBaseAddress"] = 0x300000; row["targetBaseAddress"] = 0x400000;
            row["durationMilliseconds"] = 5000; row["armedMonotonicMilliseconds"] = 100;
            row["deadlineMonotonicMilliseconds"] = 5100;
        }
        cleanup["status"] = "observed"; cleanup["trigger"] = "deadline";
        cleanup["evidence"] = "game-thread-fixed-cleanup-and-independent-readback";
        cleanup["cleanupStartedMonotonicMilliseconds"] = 5100; cleanup["completedMonotonicMilliseconds"] = 5101;
        cleanup["steps"] = new JsonArray(Step("stop-attacker", false, false), Step("stop-target", true, false), Step("disable-attacker", false, true));
        var end = Native("capture-end", 5, 3); end["status"] = "completed";
        var rows = new List<JsonObject> { header, start, request, admission };
        switch (mode)
        {
            case "wrong-session": cleanup["originSession"] = "older"; break;
            case "wrong-actor": cleanup["engineTargetFormId"] = 0x14; break;
            case "wrong-lease": cleanup["leaseId"] = 77; break;
            case "missing-readback": cleanup["steps"]![0]!["readback"] = null; break;
            case "still-in-combat": cleanup["steps"]![0]!["readback"]!["value"] = 1; break;
            case "not-disabled": cleanup["steps"]![2]!["readback"]!["value"] = 0; break;
            case "unrequested": rows.Remove(request); break;
            case "loss": cleanup["dropped"] = 1; break;
            case "wrong-address": cleanup["attackerAddress"] = 0x500000; break;
            case "invalid-address": admission["attackerAddress"] = 0; cleanup["attackerAddress"] = 0; break;
            case "early-deadline": cleanup["cleanupStartedMonotonicMilliseconds"] = 5099; break;
            case "unknown-trigger": cleanup["trigger"] = "assumed"; break;
            case "wrong-evidence": cleanup["evidence"] = "command-accepted"; break;
            case "missing-acceptance": admission.Remove("accepted"); break;
            case "rejected-start-cleaned":
            case "rejected-start-missing": admission["accepted"] = false; cleanup["trigger"] = "start-rejected"; break;
            case "duplicate-action-index":
                var duplicate = (JsonObject)request.DeepClone(); duplicate["requestId"] = 77;
                rows.Insert(3, duplicate); break;
        }
        if (mode == "after-end") rows.Add(end);
        if (mode is not ("missing" or "rejected-start-missing")) rows.Add(cleanup);
        if (mode == "duplicate") rows.Add((JsonObject)cleanup.DeepClone());
        if (mode != "after-end") rows.Add(end);
        return rows;
    }

    private static JsonObject Native(string kind, int sequence, int request) => new()
    {
        ["protocol"] = 1, ["kind"] = kind, ["sequence"] = sequence, ["requestId"] = request,
        ["frame"] = sequence, ["qpc"] = sequence, ["dropped"] = 0
    };

    private static JsonObject Step(string name, bool target, bool disabled) => new()
    {
        ["step"] = name, ["status"] = "observed", ["accepted"] = true, ["identityResolved"] = true,
        ["engineTargetFormId"] = target ? 0x14 : 0x01000800, ["engineTargetBaseFormId"] = target ? 7 : 0x01000801,
        ["readback"] = new JsonObject { ["statistic"] = disabled ? "Disabled" : "IsInCombat", ["status"] = "observed",
            ["value"] = disabled ? 1 : 0, ["reason"] = null }, ["error"] = null
    };
}
