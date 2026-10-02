using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeMilestoneTests
{
    [Theory]
    [InlineData("read-reference-state", RuntimeRequestKind.ReferenceSnapshot, "player")]
    [InlineData("start-combat", RuntimeRequestKind.ReferenceAction, "start-combat\t@player\t000014\tAddon.esp\t000123")]
    [InlineData("move-reference", RuntimeRequestKind.ReferenceAction, "move-reference\t@player\t000014\tAddon.esp\t000123")]
    [InlineData("stop-combat", RuntimeRequestKind.ReferenceAction, "stop-combat\t@player\t000014")]
    [InlineData("disable-reference", RuntimeRequestKind.ReferenceAction, "disable-reference\t@player\t000014")]
    [InlineData("set-position", RuntimeRequestKind.ReferenceAction, "set-position\t@player\t000014\tX\t0.125")]
    [InlineData("read-inventory", RuntimeRequestKind.Inventory, "read-inventory\t@player\t000014\tAddon.esp\t000123")]
    [InlineData("add-item", RuntimeRequestKind.Inventory, "add-item\t@player\t000014\tAddon.esp\t000123\t2")]
    [InlineData("equip-item", RuntimeRequestKind.Inventory, "equip-item\t@player\t000014\tAddon.esp\t000123")]
    [InlineData("set-quest-stage", RuntimeRequestKind.QuestState, "set-quest-stage\tAddon.esp\t000123\t0")]
    [InlineData("set-quest-objective", RuntimeRequestKind.QuestState, "set-quest-objective\tAddon.esp\t000123\t0\tcompleted\t1")]
    public void Typed_operations_preserve_bound_identities_and_values(string kind, RuntimeRequestKind request, string payload)
    {
        var action = new RuntimeAction(kind, Target: "player", OtherPlugin: "Addon.esp", OtherFormId: "0x00000123",
            Count: 2, Name: "X", Value: 0.125, Index: 0);
        if (kind.StartsWith("set-quest-", StringComparison.Ordinal))
            action = action with { Target = null, Plugin = "Addon.esp", FormId = "123", Name = "completed", Value = 1 };
        Assert.Equal(new RuntimeFrame(request, 17, payload), action.ToFrame(17));
    }

    [Theory]
    [InlineData("start-combat", "../Addon.esp", "123", 1, 0, 0)]
    [InlineData("start-combat", "Addon.esp", "01000123", 1, 0, 0)]
    [InlineData("start-combat", "Addon.esp", "00000000", 1, 0, 0)]
    [InlineData("add-item", "Addon.esp", "123", 0, 0, 0)]
    [InlineData("remove-item", "Addon.esp", "123", 10001, 0, 0)]
    [InlineData("set-position", "Addon.esp", "123", 1, 0, double.PositiveInfinity)]
    [InlineData("set-quest-stage", "Addon.esp", "123", 1, -1, 0)]
    [InlineData("set-quest-objective", "Addon.esp", "123", 1, 1, 2)]
    public void Invalid_typed_operations_are_rejected(string kind, string plugin, string form, int count, int index, double value)
    {
        var action = new RuntimeAction(kind, Plugin: plugin, FormId: form, OtherPlugin: plugin, OtherFormId: form,
            Count: count, Index: index, Name: kind == "set-position" ? "X" : "completed", Value: value);
        Assert.Throws<ArgumentException>(() => action.ToFrame(1));
    }

    [Theory]
    [InlineData("0", "0", "equal", 0, true)]
    [InlineData("0.125", "0.12", "equal", 0.01, true)]
    [InlineData("0", "1", "less", 0, true)]
    [InlineData("true", "false", "not-equal", 0, true)]
    [InlineData("\"ready\"", "\"Ready\"", "equal", 0, false)]
    [InlineData("\"0\"", "0", "equal", 0, false)]
    [InlineData("1e999", "0", "greater", 0, false)]
    public void Expected_fields_compare_typed_values(string actual, string expected, string comparison, double tolerance, bool matches)
    {
        Assert.Equal(matches, RuntimeMilestoneMonitor.Matches(Json(actual), new("value", Json(expected), comparison, tolerance)));
    }

    [Theory]
    [InlineData("complete", 1, "01000123", true)]
    [InlineData("complete", 2, "02000123", true)]
    [InlineData("complete", 2, "01000123", false)]
    [InlineData("partial", 1, "01000123", false)]
    public void Form_expectations_use_verified_plugin_slots(string status, int slot, string observed, bool matched)
    {
        var milestone = Milestone();
        var identity = Json($$$"""{"activePluginIdentityStatus":"{{{status}}}","activePlugins":[{"name":"Addon.esp","index":{{{slot}}},"status":"verified"}]}""");
        var row = Json($$$"""{"kind":"actor-state","engineTargetFormId":"{{{observed}}}","state":{"values":[0,true]}}""");
        Assert.Equal(matched, RuntimeMilestoneMonitor.MatchesEvent(milestone, identity, row));
    }

    [Theory]
    [InlineData("mutating-probe")]
    [InlineData("missing-passive-owner")]
    [InlineData("duplicate-form-path")]
    [InlineData("missing-field")]
    [InlineData("control-event")]
    public void Invalid_expectations_fail_before_session_start(string fault)
    {
        var milestone = Milestone();
        milestone = fault switch
        {
            "mutating-probe" => milestone with { Probe = new("disable-reference", Target: "player") },
            "missing-passive-owner" => milestone with { Forms = null },
            "duplicate-form-path" => milestone with { Forms = [milestone.Forms![0], milestone.Forms[0]] },
            "control-event" => milestone with { EventKind = "action-begin" },
            _ => milestone with { Fields = [null!] }
        };
        Assert.Throws<ArgumentException>(() => new RuntimeMilestoneMonitor([milestone], 1));
    }

    [Theory]
    [InlineData("matched", true)]
    [InlineData("retry", true)]
    [InlineData("wrong-owner", false)]
    [InlineData("wrong-request", false)]
    [InlineData("silent", false)]
    [InlineData("event-loss", false)]
    [InlineData("native-error", false)]
    [InlineData("disconnect", false)]
    [InlineData("cancel", false)]
    public async Task Capture_waits_for_attributed_observations_and_stops_after_failure(string behavior, bool success)
    {
        using var cancellation = new CancellationTokenSource();
        using var engine = new TestEngine(behavior, cancellation);
        await using var connection = new RuntimeConnection(engine, Identity());
        using var trace = new MemoryStream();
        var actions = new[] { new RuntimeAction("set-global", "AuditFlag", Value: 1) };
        var milestone = Milestone() with { AfterActionIndex = -1, Probe = new("read-actor-state", Plugin: "Addon.esp", FormId: "123") };
        var result = await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromMilliseconds(900), actions,
            cancellation.Token, milestones: [milestone]);
        trace.Position = 0;
        var document = await RuntimeTraceImporter.ReadDocumentAsync(trace);
        var outcome = Assert.Single(document.ControllerResults);
        Assert.Equal(success, outcome.Status == "matched");
        Assert.Equal(success, engine.Requests.Any(frame => frame.Kind == RuntimeRequestKind.Execute));
        Assert.Equal(result.Errors, document.Summary.Errors);
        Assert.Equal(result.ControllerErrors, document.Summary.ControllerErrors);
        Assert.Equal(result.MissingSequences, document.Summary.MissingSequences);
        Assert.DoesNotContain("footer-count-mismatch", document.Summary.Diagnostics);
        Assert.DoesNotContain("missing-milestone-results", document.Summary.Diagnostics);
        if (behavior == "event-loss") Assert.Contains("sequence-gaps", document.Summary.Diagnostics);
        if (behavior == "retry") Assert.Equal(2, engine.Requests.Count(frame => frame.Kind == RuntimeRequestKind.ActorSnapshot));
        if (behavior == "silent") Assert.Equal("timeout", outcome.Status);
        if (behavior == "cancel") Assert.Contains(engine.Requests, frame => frame.Kind == RuntimeRequestKind.Cancel);
        if (success) { Assert.True(document.Summary.Complete); Assert.Equal(0, result.Errors); }
        trace.Position = 0;
        Assert.Equal(document.Summary.Sha256, (await RuntimeTraceImporter.ImportAsync(trace)).Sha256);
    }

    [Fact]
    public async Task Passive_milestone_is_armed_before_the_action_and_import_checks_its_source()
    {
        using var cancellation = new CancellationTokenSource();
        using var engine = new TestEngine("passive", cancellation);
        await using var connection = new RuntimeConnection(engine, Identity());
        using var trace = new MemoryStream();
        await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromMilliseconds(200),
            [new("set-global", "AuditFlag", Value: 1)], milestones: [Milestone()]);
        var lines = Encoding.UTF8.GetString(trace.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        trace.Position = 0;
        Assert.Equal("matched", Assert.Single((await RuntimeTraceImporter.ReadDocumentAsync(trace)).ControllerResults).Status);
        foreach (var tamper in new[] { "owner", "value", "sequence", "elapsed", "action", "probe", "boundary" })
        {
            var nodes = lines.Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
            var result = nodes.Single(row => row["kind"]!.GetValue<string>() == "scenario-result");
            var observation = nodes.Single(row => row["kind"]!.GetValue<string>() == "actor-state");
            switch (tamper)
            {
                case "owner": observation["engineTargetFormId"] = "02000123"; break;
                case "value": observation["state"]!["values"]![0] = 1; break;
                case "sequence": result["observedSequence"] = 999; break;
                case "elapsed": result["matchedElapsedMilliseconds"] = 99999; break;
                case "action": nodes.Single(row => row["kind"]!.GetValue<string>() == "action-request")["action"]!["value"] = 2; break;
                case "probe": nodes[0]["scenario"]!["milestones"]![0]!["probe"] = JsonSerializer.SerializeToNode(new RuntimeAction("read-actor-state", Target: "player"), RuntimeJsonContext.Default.RuntimeAction); break;
                case "boundary": nodes.Single(row => row["kind"]!.GetValue<string>() == "action-begin")["requestId"] = 999; break;
            }
            using var modified = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', nodes.Select(node => node.ToJsonString()))));
            await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeTraceImporter.ImportAsync(modified));
        }
    }

    [Fact]
    public async Task Unreached_milestones_remain_explicit_and_missing_results_fail_import_completeness()
    {
        using var cancellation = new CancellationTokenSource();
        using var engine = new TestEngine("silent", cancellation);
        await using var connection = new RuntimeConnection(engine, Identity());
        using var trace = new MemoryStream();
        await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromSeconds(1), [new("set-global", "AuditFlag", Value: 1)],
            milestones: [Milestone() with { Id = "initial", AfterActionIndex = -1, Probe = new("read-actor-state", Target: "player") }, Milestone()]);
        trace.Position = 0;
        var imported = await RuntimeTraceImporter.ReadDocumentAsync(trace);
        Assert.Equal(new[] { "timeout", "not-run" }, imported.ControllerResults.Select(item => item.Status));
        Assert.Equal(2, imported.Summary.ControllerErrors);
        var lines = Encoding.UTF8.GetString(trace.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.Contains("\"status\":\"not-run\"", StringComparison.Ordinal));
        using var modified = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        var missing = await RuntimeTraceImporter.ImportAsync(modified);
        Assert.False(missing.Complete);
        Assert.Contains("missing-milestone-results", missing.Diagnostics);
    }

    [Fact]
    public async Task Passive_events_are_read_between_probe_polls()
    {
        using var cancellation = new CancellationTokenSource();
        using var engine = new TestEngine("mixed", cancellation);
        await using var connection = new RuntimeConnection(engine, Identity());
        using var trace = new MemoryStream();
        await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromMilliseconds(800),
            [new("set-global", "AuditFlag", Value: 1)], milestones:
            [Milestone() with { TimeoutMilliseconds = 200 }, Milestone() with { Id = "probe", TimeoutMilliseconds = 700,
                PollMilliseconds = 300, Probe = new("read-actor-state", Plugin: "Addon.esp", FormId: "123") }]);
        trace.Position = 0;
        var document = await RuntimeTraceImporter.ReadDocumentAsync(trace);
        Assert.Equal(new[] { "matched", "matched" }, document.ControllerResults.Select(item => item.Status));
        Assert.Equal(0UL, document.ControllerResults[0].ObservedRequestId);
        Assert.True(document.Summary.Complete);
    }

    [Fact]
    public async Task Queued_pre_action_events_cannot_satisfy_a_passive_milestone()
    {
        using var cancellation = new CancellationTokenSource();
        using var engine = new TestEngine("stale", cancellation);
        await using var connection = new RuntimeConnection(engine, Identity());
        using var trace = new MemoryStream();
        await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromSeconds(1),
            [new("set-global", "AuditFlag", Value: 1), new("set-global", "AuditFlag", Value: 2)], milestones: [Milestone()]);
        trace.Position = 0;
        var document = await RuntimeTraceImporter.ReadDocumentAsync(trace);
        Assert.Equal("timeout", Assert.Single(document.ControllerResults).Status);
        Assert.Single(engine.Requests.Where(frame => frame.Kind == RuntimeRequestKind.Execute));
    }

    [Fact]
    public async Task Import_rejects_an_observation_that_precedes_its_probe_marker()
    {
        using var cancellation = new CancellationTokenSource();
        using var engine = new TestEngine("matched", cancellation);
        await using var connection = new RuntimeConnection(engine, Identity());
        using var trace = new MemoryStream();
        await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromMilliseconds(150),
            milestones: [Milestone() with { AfterActionIndex = -1, Probe = new("read-actor-state", Target: "player") }]);
        var lines = Encoding.UTF8.GetString(trace.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        var probe = lines.FindIndex(line => line.Contains("\"kind\":\"milestone-probe\"", StringComparison.Ordinal));
        var observation = lines.FindIndex(line => line.Contains("\"kind\":\"actor-state\"", StringComparison.Ordinal));
        Assert.True(probe < observation);
        (lines[probe], lines[observation]) = (lines[observation], lines[probe]);
        using var modified = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeTraceImporter.ImportAsync(modified));
    }

    private static RuntimeMilestone Milestone() => new("actor-ready", 0, "actor-state",
        [new("state.values.0", Json("0")), new("state.values.1", Json("true"))], TimeoutMilliseconds: 350,
        Forms: [new("engineTargetFormId", Plugin: "Addon.esp", FormId: "123")]);

    private static JsonElement Identity() => Json("""{"processId":123,"sequence":1,"capabilities":{"actionBoundaries":true},"activePluginIdentityStatus":"complete","activePlugins":[{"name":"Addon.esp","index":1,"status":"verified"}]}""");
    private static JsonElement Json(string text) { using var json = JsonDocument.Parse(text); return json.RootElement.Clone(); }

    /// <summary>Reacts to frames written by the controller; a silent engine responds only to stop/cancel.</summary>
    private sealed class TestEngine(string behavior, CancellationTokenSource cancellation) : Stream
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        private readonly MemoryStream _request = new();
        private MemoryStream? _current;
        private ulong _sequence = 1;
        private int _probes;
        private Task _scheduled = Task.CompletedTask;
        internal List<RuntimeFrame> Requests { get; } = [];
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            while (_current == null || _current.Position == _current.Length)
            {
                _current?.Dispose();
                if (!await _responses.Reader.WaitToReadAsync(token)) return 0;
                _current = new MemoryStream(await _responses.Reader.ReadAsync(token));
            }
            return await _current.ReadAsync(buffer, token);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => _request.WriteAsync(buffer, token);
        public override async Task FlushAsync(CancellationToken token)
        {
            _request.Position = 0;
            var frame = (await RuntimeProtocol.ReadAsync(_request, token))!;
            _request.SetLength(0);
            Requests.Add(frame);
            if (frame.Kind == RuntimeRequestKind.Start)
            {
                await Emit("capture-start", frame.RequestId);
                if (behavior == "stale") await Actor(0);
            }
            else if (frame.Kind is RuntimeRequestKind.Stop or RuntimeRequestKind.Cancel)
                await Emit("capture-end", frame.RequestId, new() { ["status"] = frame.Kind == RuntimeRequestKind.Cancel ? "cancelled" : "completed" });
            else if (frame.Kind == RuntimeRequestKind.ActorSnapshot)
            {
                ++_probes;
                if (behavior == "disconnect") { _responses.Writer.TryComplete(); return; }
                if (behavior == "silent") return;
                if (behavior == "cancel") { cancellation.Cancel(); return; }
                if (behavior == "native-error") { await Emit("error", frame.RequestId); return; }
                if (behavior == "event-loss") ++_sequence;
                await Actor(behavior == "wrong-request" ? frame.RequestId + 50 : frame.RequestId);
                if (behavior == "mixed" && _probes == 1) _scheduled = DelayedActor();
                if (behavior == "wrong-request") await Emit("actor-state", frame.RequestId);
            }
            else
            {
                await Emit("action-begin", frame.RequestId, new() { ["requestKind"] = (ushort)frame.Kind });
                if (behavior == "passive") await Actor(frame.RequestId);
                await Emit("action-result", frame.RequestId, new() { ["accepted"] = true });
            }
        }
        private async Task DelayedActor()
        {
            await Task.Delay(50, cancellation.Token);
            await Actor(0, true);
        }
        private Task Actor(ulong request, bool forceValid = false) => Emit("actor-state", request, new()
        {
            ["engineTargetFormId"] = behavior == "wrong-owner" ? "02000123" : "01000123",
            ["state"] = new JsonObject { ["values"] = new JsonArray(!forceValid && (behavior is "retry" or "mixed") && _probes == 1 ? 1 : 0, true) }
        });
        private async Task Emit(string kind, ulong request, JsonObject? row = null)
        {
            row ??= new(); row["kind"] = kind; row["requestId"] = request; row["protocol"] = 1;
            row["sequence"] = ++_sequence; row["frame"] = _sequence; row["qpc"] = _sequence; row["dropped"] = 0;
            using var bytes = new MemoryStream();
            await RuntimeProtocol.WriteAsync(bytes, new(RuntimeRequestKind.Event, request, row.ToJsonString()));
            await _responses.Writer.WriteAsync(bytes.ToArray());
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override async ValueTask DisposeAsync()
        {
            try { await _scheduled; } catch (OperationCanceledException) { }
            Dispose();
            GC.SuppressFinalize(this);
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { _request.Dispose(); _current?.Dispose(); _responses.Writer.TryComplete(); } base.Dispose(disposing); }
    }
}
