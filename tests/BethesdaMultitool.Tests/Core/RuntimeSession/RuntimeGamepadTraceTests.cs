using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeGamepadTraceTests
{
    private const string Session = "fixture-session";
    private const ulong Started = 133000000000000000;
    private static readonly string Manifest = new('a', 64);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, false)]
    public void Only_the_actual_guest_pair_completes_a_live_request(int flags, bool wrap)
    {
        var rows = Trace(flags, wrap);
        var validator = new RuntimeGamepadTraceValidator();
        foreach (var row in rows.TakeWhile(r => Kind(r) != "scenario-result")) Feed(validator, row);
        var pulse = validator.Result(20);
        Assert.Equal("Observed", pulse.Status);
        Assert.Equal(Started, pulse.Identity!.ProcessStartedFileTime);
        Assert.Equal(0U, pulse.Release!.Buttons);
        Assert.Equal(unchecked(pulse.Press!.Packet!.Value + 1), pulse.Release.Packet);
        Assert.Contains("controller-result-missing", Assert.Single(validator.Complete("trace", false).Pulses).Diagnostics);
        Feed(validator, Row(rows, "scenario-result"));
        Assert.Equal("Observed", Assert.Single(validator.Complete("trace", false).Pulses).Status);
    }

    [Theory]
    [InlineData("no-header")]
    [InlineData("no-action")]
    [InlineData("no-binding")]
    [InlineData("no-queue")]
    [InlineData("no-press")]
    [InlineData("no-release")]
    [InlineData("host-release")]
    [InlineData("cleanup-release")]
    [InlineData("wrong-packet")]
    [InlineData("wrong-press-buttons")]
    [InlineData("nonneutral-release")]
    [InlineData("nonneutral-axis")]
    [InlineData("missing-axis")]
    [InlineData("guest-return-failed")]
    [InlineData("unsupported-flags")]
    [InlineData("short-hold")]
    [InlineData("deadline-equality")]
    [InlineData("admission-after-press")]
    [InlineData("wrong-request")]
    [InlineData("wrong-session")]
    [InlineData("wrong-generation")]
    [InlineData("wrong-capture")]
    [InlineData("wrong-binding")]
    [InlineData("wrong-window")]
    [InlineData("wrong-manifest")]
    [InlineData("wrong-pid")]
    [InlineData("wrong-process-start")]
    [InlineData("wrong-action")]
    [InlineData("wrong-index")]
    [InlineData("wrong-reference")]
    [InlineData("duplicate-press")]
    [InlineData("release-before-press")]
    [InlineData("result-before-release")]
    [InlineData("repeated-ordinal")]
    [InlineData("clock-reversed")]
    [InlineData("wrong-profile")]
    [InlineData("forged-controller-result")]
    [InlineData("native-error")]
    [InlineData("loss")]
    [InlineData("schema1")]
    [InlineData("wrong-schema")]
    [InlineData("bound-envelope")]
    public void Incomplete_or_conflicting_receipts_never_become_observed(string defect)
    {
        var rows = Trace();
        var press = Phase(rows, "pressed");
        var release = Phase(rows, "released");
        switch (defect)
        {
            case "no-header": rows.Remove(Row(rows, "capture-header")); break;
            case "no-action": rows.Remove(Row(rows, "action-request")); break;
            case "no-binding": rows.Remove(Row(rows, "guest-run-bound")); break;
            case "no-queue": rows.Remove(Row(rows, "gamepad-queued")); break;
            case "no-press": rows.Remove(press); break;
            case "no-release": rows.Remove(release); break;
            case "host-release": release["origin"] = "bridge-control"; break;
            case "cleanup-release": release["kind"] = "gamepad-output"; release["status"] = "neutralized"; break;
            case "wrong-packet": release["packet"] = 44; break;
            case "wrong-press-buttons": press["buttons"] = 4096; break;
            case "nonneutral-release": release["buttons"] = 16; break;
            case "nonneutral-axis": release["leftX"] = 1; break;
            case "missing-axis": press.Remove("rightTrigger"); break;
            case "guest-return-failed": release["result"] = 1; break;
            case "unsupported-flags": release["flags"] = 2; break;
            case "short-hold": release["observedMonotonicMilliseconds"] = 1100; break;
            case "deadline-equality": release["observedMonotonicMilliseconds"] = 2000; break;
            case "admission-after-press":
                foreach (var row in rows.Where(r => Kind(r).StartsWith("gamepad-", StringComparison.Ordinal))) row["admittedMonotonicMilliseconds"] = 1002;
                break;
            case "wrong-request": release["requestId"] = 21; break;
            case "wrong-session": release["session"] = "another-session"; break;
            case "wrong-generation": release["connectionGeneration"] = 2; break;
            case "wrong-capture": release["captureEpoch"] = 2; break;
            case "wrong-binding": release["bindingSerial"] = 2; break;
            case "wrong-window": release["windowIdentity"] = 2; break;
            case "wrong-manifest": release["runManifestSha256"] = new string('b', 64); break;
            case "wrong-pid": Row(rows, "guest-run-bound")["processId"] = 18; break;
            case "wrong-process-start": Row(rows, "guest-run-bound")["processStartedFileTime"] = Started + 1; break;
            case "wrong-action": Row(rows, "action-request")["action"]!["gamepad"]!["buttons"] = 4096; break;
            case "wrong-index": Row(rows, "action-request")["actionIndex"] = 1; break;
            case "wrong-reference": Row(rows, "gamepad-result")["releaseSequence"] = 5; break;
            case "duplicate-press": rows.Insert(rows.IndexOf(press) + 1, (JsonObject)press.DeepClone()); break;
            case "release-before-press": rows.Remove(release); rows.Insert(rows.IndexOf(press), release); break;
            case "result-before-release":
                var result = Row(rows, "gamepad-result"); rows.Remove(result); rows.Insert(rows.IndexOf(release), result); break;
            case "repeated-ordinal": release["observationOrdinal"] = 41; break;
            case "clock-reversed": release["observedMonotonicMilliseconds"] = 999; break;
            case "wrong-profile": Row(rows, "capture-header")["identity"]!["guestRunProfile"]!["sha256"] = new string('b', 64); break;
            case "forged-controller-result": Row(rows, "scenario-result")["observedSequence"] = 6; break;
            case "native-error": Row(rows, "gamepad-result")["kind"] = "error"; break;
            case "loss": release["dropped"] = 1; break;
            case "schema1": Row(rows, "capture-header")["version"] = 1; break;
            case "wrong-schema": Row(rows, "capture-header")["schema"] = "another-trace"; break;
            case "bound-envelope": Row(rows, "guest-run-bound").Remove("dropped"); break;
        }
        var validator = new RuntimeGamepadTraceValidator();
        foreach (var row in rows) Feed(validator, row);
        var report = validator.Complete("trace", false);
        Assert.NotEqual("Observed", report.Summary.Status);
        Assert.DoesNotContain(report.Pulses, p => p.Status == "Observed");
        Assert.Contains(report.Pulses, p => p.Diagnostics.Count > 0);
    }

    [Fact]
    public void Later_loss_and_missing_controller_results_preserve_partial_evidence()
    {
        var validator = new RuntimeGamepadTraceValidator();
        foreach (var row in Trace()) Feed(validator, row);
        Assert.Equal("Observed", validator.Result(20).Status);
        var report = validator.Complete("trace", true);
        var pulse = Assert.Single(report.Pulses);
        Assert.Equal("Partial", pulse.Status);
        Assert.NotNull(pulse.Press);
        Assert.NotNull(pulse.Release);
        Assert.Contains("capture-event-loss", pulse.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_duplicate_bound_tokens_cannot_authorize_a_second_action(bool retryAfterRejection)
    {
        var rows = Trace();
        rows.Insert(rows.IndexOf(Row(rows, "action-request")), (JsonObject)Row(rows, "guest-run-bound").DeepClone());
        if (retryAfterRejection)
            rows.Insert(rows.IndexOf(Row(rows, "action-request")), (JsonObject)rows.First(r => Kind(r) == "guest-run-bound").DeepClone());
        var validator = new RuntimeGamepadTraceValidator();
        foreach (var row in rows) Feed(validator, row);
        Assert.NotEqual("Observed", validator.Result(20).Status);
        Assert.Contains("binding-token-reused-or-capture-changed", validator.Complete("trace", false).Diagnostics);
    }

    [Fact]
    public async Task Import_retains_exact_source_locations_and_serializes_the_same_receipt()
    {
        var rows = Trace();
        var bytes = Encoding.UTF8.GetBytes(string.Join("\r\n", rows.Select(r => r.ToJsonString())) + "\r\n");
        using var stream = new MemoryStream(bytes);
        var document = await RuntimeTraceImporter.ReadDocumentAsync(stream, TestContext.Current.CancellationToken);
        Assert.True(document.Summary.Complete);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), document.Gamepad.TraceSha256);
        var pulse = Assert.Single(document.Gamepad.Pulses);
        Assert.Equal("Observed", pulse.Status);
        Assert.Equal(7, pulse.Release!.Source.Line);
        Assert.Equal(6UL, pulse.Release.Source.Sequence);
        Assert.Equal(Phase(rows, "released").ToJsonString(), document.ReadSourceLine(pulse.Release.Source));
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(document.Gamepad, RuntimeJsonContext.Default.RuntimeGamepadTraceReport));
        Assert.Equal("Observed", serialized.RootElement.GetProperty("pulses")[0].GetProperty("status").GetString());
        Assert.Equal(Started, serialized.RootElement.GetProperty("pulses")[0].GetProperty("identity").GetProperty("processStartedFileTime").GetUInt64());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Legacy_trace_versions_without_gamepad_keep_transport_acceptance(int version)
    {
        var header = Header();
        header["version"] = version;
        header["scenario"] = null;
        var rows = new List<JsonObject> { header, Native("capture-start", 2, 1) };
        if (version == 3)
        {
            header["scenario"] = JsonNode.Parse("""{"actions":[],"observeMilliseconds":1000,"milestones":[{"id":"ready","afterActionIndex":-1,"eventKind":"snapshot","fields":[{"path":"value","value":1}],"probe":{"kind":"read-actor-value","target":"player","name":"Health"},"timeoutMilliseconds":100,"pollMilliseconds":50}]}""");
            rows.Add(JsonNode.Parse("""{"kind":"scenario-result","origin":"controller","actionIndex":-1,"actionKind":"milestone:ready","milestoneId":"ready","status":"timeout","elapsedMilliseconds":100}""")!.AsObject());
        }
        rows.Add(Native("capture-end", 3, 2));
        rows[^1]["status"] = "completed";
        rows.Add(Footer(2, version == 3 ? 1 : 0, version == 3 ? 1 : 0));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', rows.Select(r => r.ToJsonString()))));
        var document = await RuntimeTraceImporter.ReadDocumentAsync(stream, TestContext.Current.CancellationToken);
        Assert.True(document.Summary.Complete);
        Assert.Equal("Unavailable", document.Gamepad.Summary.Status);
        Assert.Empty(document.Gamepad.Pulses);
    }

    /// <summary>Native rows reusable by the stream fixture; action/header rows still come from the real capture code.</summary>
    internal static (JsonObject Bound, IReadOnlyList<JsonObject> PulseRows) NativePulse(
        ulong requestId, ulong boundSequence, string session, string manifest, uint processId,
        ulong processStartedFileTime, ulong connectionGeneration = 1, ulong captureEpoch = 1,
        ulong bindingSerial = 1, ulong windowIdentity = 123, uint buttons = 16,
        int holdMilliseconds = 100, int deadlineMilliseconds = 1000, ulong admittedMilliseconds = 1000,
        ulong? firstPulseSequence = null, ulong firstObservationOrdinal = 41)
    {
        var rows = Trace();
        var bound = Row(rows, "guest-run-bound");
        var pulse = rows.Where(r => Kind(r) is "gamepad-queued" or "gamepad-state" or "gamepad-result").ToArray();
        var first = firstPulseSequence ?? boundSequence + 1;
        foreach (var row in pulse.Prepend(bound))
        {
            row["session"] = session; row["runManifestSha256"] = manifest;
            row["connectionGeneration"] = connectionGeneration; row["captureEpoch"] = captureEpoch;
            row["bindingSerial"] = bindingSerial; row["windowIdentity"] = windowIdentity;
        }
        bound["sequence"] = boundSequence; bound["processId"] = processId;
        bound["processStartedFileTime"] = processStartedFileTime;
        for (var i = 0; i < pulse.Length; ++i)
        {
            var row = pulse[i]; row["sequence"] = first + (ulong)i; row["requestId"] = requestId;
            row["requestedButtons"] = buttons; row["holdMilliseconds"] = holdMilliseconds;
            row["deadlineMilliseconds"] = deadlineMilliseconds; row["admittedMonotonicMilliseconds"] = admittedMilliseconds;
        }
        pulse[1]["buttons"] = buttons; pulse[1]["observedMonotonicMilliseconds"] = admittedMilliseconds + 1;
        pulse[1]["observationOrdinal"] = firstObservationOrdinal;
        pulse[2]["observedMonotonicMilliseconds"] = admittedMilliseconds + 1 + (ulong)holdMilliseconds;
        pulse[2]["observationOrdinal"] = firstObservationOrdinal + 1;
        pulse[3]["pressSequence"] = first + 1; pulse[3]["releaseSequence"] = first + 2;
        return (bound, pulse);
    }

    private static List<JsonObject> Trace(int flags = 0, bool wrap = false)
    {
        var bound = Native("guest-run-bound", 3, 10);
        bound["status"] = "bound"; bound["origin"] = "bridge-control";
        bound["processId"] = 17; bound["processStartedFileTime"] = Started;
        var queued = Common("gamepad-queued", 4); queued["status"] = "queued";
        var press = State("pressed", 5, 1001, 41, wrap ? uint.MaxValue : 5, 16, flags);
        var release = State("released", 6, 1101, 42, wrap ? 0U : 6U, 0, flags);
        var terminal = Common("gamepad-result", 7);
        terminal["status"] = "observed"; terminal["pressSequence"] = 5; terminal["releaseSequence"] = 6;
        terminal["releaseObserved"] = true; terminal["hostButtonsCleared"] = true;
        var end = Native("capture-end", 8, 30); end["status"] = "completed";
        return [Header(), Native("capture-start", 2, 1), bound,
            new() { ["kind"] = "action-request", ["requestId"] = 20, ["actionIndex"] = 0, ["action"] = Action() },
            queued, press, release, terminal,
            new() { ["kind"] = "scenario-result", ["origin"] = "controller", ["actionKind"] = "gamepad-pulse", ["actionIndex"] = 0,
                ["status"] = "matched", ["observedRequestId"] = 20, ["observedSequence"] = 7, ["elapsedMilliseconds"] = 101 },
            end, Footer(7, 1, 0)];
    }

    private static JsonObject Header() => new()
    {
        ["kind"] = "capture-header", ["schema"] = "bmt/runtime-trace", ["version"] = 2, ["session"] = Session,
        ["identity"] = new JsonObject { ["sequence"] = 1, ["processId"] = 17,
            ["processStartedUtc"] = DateTime.FromFileTimeUtc((long)Started).ToString("O"),
            ["guestRunProfile"] = new JsonObject { ["sha256"] = Manifest, ["status"] = "validated-file-pins" } },
        ["scenario"] = new JsonObject { ["actions"] = new JsonArray(Action()), ["observeMilliseconds"] = 1000 }
    };
    private static JsonObject Action() => new()
    {
        ["kind"] = "gamepad-pulse", ["gamepad"] = new JsonObject { ["slot"] = 0, ["buttons"] = 16,
            ["holdMilliseconds"] = 100, ["deadlineMilliseconds"] = 1000 }
    };
    private static JsonObject Native(string kind, ulong sequence, ulong request) => new()
    {
        ["kind"] = kind, ["protocol"] = 1, ["sequence"] = sequence, ["requestId"] = request, ["frame"] = 0, ["dropped"] = 0,
        ["session"] = Session, ["connectionGeneration"] = 1, ["captureEpoch"] = 1, ["bindingSerial"] = 1,
        ["windowIdentity"] = 123, ["runManifestSha256"] = Manifest
    };
    private static JsonObject Common(string kind, ulong sequence)
    {
        var row = Native(kind, sequence, 20);
        row["origin"] = "bridge-control"; row["slot"] = 0; row["requestedButtons"] = 16;
        row["holdMilliseconds"] = 100; row["deadlineMilliseconds"] = 1000;
        row["admittedMonotonicMilliseconds"] = 1000;
        return row;
    }
    private static JsonObject State(string phase, ulong sequence, ulong milliseconds, ulong ordinal, uint packet, uint buttons, int flags)
    {
        var row = Common("gamepad-state", sequence);
        row["origin"] = "guest-xam-input"; row["phase"] = phase;
        row["observationOrdinal"] = ordinal; row["observedMonotonicMilliseconds"] = milliseconds;
        row["result"] = 0; row["flags"] = flags; row["packet"] = packet; row["buttons"] = buttons;
        foreach (var field in new[] { "leftTrigger", "rightTrigger", "leftX", "leftY", "rightX", "rightY" }) row[field] = 0;
        return row;
    }
    private static JsonObject Footer(int events, int controllers, int errors) => new()
    {
        ["kind"] = "capture-footer", ["status"] = "completed", ["events"] = events, ["dropped"] = 0,
        ["snapshots"] = 0, ["errors"] = errors, ["controllerResults"] = controllers, ["controllerErrors"] = errors
    };
    private static JsonObject Row(List<JsonObject> rows, string kind) => rows.Single(r => Kind(r) == kind);
    private static JsonObject Phase(List<JsonObject> rows, string phase) => rows.Single(r => Kind(r) == "gamepad-state" && r["phase"]!.GetValue<string>() == phase);
    private static string Kind(JsonObject row) => row["kind"]!.GetValue<string>();
    private static void Feed(RuntimeGamepadTraceValidator validator, JsonObject row)
    {
        using var json = JsonDocument.Parse(row.ToJsonString());
        validator.Observe(json.RootElement);
    }
}
