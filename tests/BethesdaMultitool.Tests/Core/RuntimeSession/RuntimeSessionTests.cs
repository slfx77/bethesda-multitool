using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeSessionTests
{
    [Theory]
    [InlineData("read-dialogue-state")]
    [InlineData("read-cell-terrain")]
    public void Membership_queries_require_one_explicit_plugin_local_identity(string kind)
    {
        var frame = new RuntimeAction(kind, Plugin: "Control.esm", FormId: "0x00000A00").ToFrame(19);
        Assert.Equal(RuntimeRequestKind.RecordMembership, frame.Kind);
        Assert.Equal(kind + "\tControl.esm\t000A00", frame.Payload);
        Assert.Throws<ArgumentException>(() => new RuntimeAction(kind, Target: "player").ToFrame(19));
        Assert.Throws<ArgumentException>(() => new RuntimeAction(kind, Plugin: "Control.esm", FormId: "01000A00").ToFrame(19));
        Assert.Throws<ArgumentException>(() => new RuntimeAction(kind, Plugin: "../Control.esm", FormId: "A00").ToFrame(19));
    }

    [Theory]
    [InlineData(RuntimeRequestKind.Hello, "")]
    [InlineData(RuntimeRequestKind.Execute, "set TestQuest.Flag to 1")]
    [InlineData(RuntimeRequestKind.Evaluate, "player.GetAV Health")]
    [InlineData(RuntimeRequestKind.MessageProbe, "show-ok")]
    [InlineData(RuntimeRequestKind.ConditionProbe, "player-get-dead")]
    [InlineData(RuntimeRequestKind.QuestRead, "FalloutNV.esm\t0E61A4\tDishHutAttack")]
    [InlineData(RuntimeRequestKind.ActorSnapshot, "player")]
    [InlineData(RuntimeRequestKind.RecordMembership, "read-dialogue-state\tAddon.esm\t000A00")]
    [InlineData(RuntimeRequestKind.MessageMenu, "choose\tprobe\t0\tBMT runtime observation: click OK.\tOk")]
    [InlineData(RuntimeRequestKind.ActorSet, "FalloutNV.esm\t104C0F\tEndurance\t4")]
    [InlineData(RuntimeRequestKind.QuestSet, "FalloutNV.esm\t0E61A4\tDishHutAttack\t0")]
    public async Task Wire_contract_has_versioned_header_and_roundtrips_partial_reads(RuntimeRequestKind kind, string payload)
    {
        using var output = new MemoryStream();
        await RuntimeProtocol.WriteAsync(output, new(kind, 0x123456789abcdef0, payload));
        var bytes = output.ToArray();
        Assert.Equal("BMT1", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        Assert.Equal((ushort)kind, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        Assert.Equal(0x123456789abcdef0UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)));
        using var input = new OneByteReadStream(bytes);
        Assert.Equal(new RuntimeFrame(kind, 0x123456789abcdef0, payload), await RuntimeProtocol.ReadAsync(input));
        Assert.Null(await RuntimeProtocol.ReadAsync(input));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("oversize")]
    [InlineData("reserved")]
    [InlineData("truncated")]
    public async Task Invalid_or_incomplete_frames_are_rejected(string defect)
    {
        using var stream = new MemoryStream();
        await RuntimeProtocol.WriteAsync(stream, new(RuntimeRequestKind.Hello, 1, ""));
        var bytes = stream.ToArray();
        switch (defect)
        {
            case "version": bytes[4] = 2; break;
            case "oversize": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 65537); break;
            case "reserved": bytes[20] = 1; break;
            case "truncated": bytes = bytes[..^1]; break;
        }
        using var input = new MemoryStream(bytes);
        if (defect == "truncated") await Assert.ThrowsAsync<EndOfStreamException>(() => RuntimeProtocol.ReadAsync(input));
        else await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeProtocol.ReadAsync(input));
    }

    [Theory]
    [InlineData("read-actor-value", "player.GetAV Health")]
    [InlineData("read-base-actor-value", "player.GetBaseAV Health")]
    [InlineData("read-permanent-actor-value", "player.GetPermAV Health")]
    public void Actor_components_have_distinct_engine_queries(string kind, string expected)
    {
        var frame = new RuntimeAction(kind, "player", "Health").ToFrame(17);
        Assert.Equal(RuntimeRequestKind.Evaluate, frame.Kind);
        Assert.Equal(expected, frame.Payload);
        Assert.Throws<ArgumentException>(() => new RuntimeAction(kind, "player\nqqq", "Health").ToFrame(18));
    }

    [Theory]
    [InlineData("completed", false, true)]
    [InlineData("cancelled", false, false)]
    [InlineData("completed", true, false)]
    public async Task Capture_and_import_preserve_numeric_zero_and_terminal_integrity(string status, bool dropped, bool complete)
    {
        using var incoming = new MemoryStream();
        await Event("capture-start", 2, 1, "");
        await Event("snapshot", dropped ? 4UL : 3UL, 2,
            ",\"expression\":\"player.GetAV Health\",\"statistic\":\"Health\",\"targetKind\":\"actor\",\"component\":\"current\",\"value\":0,\"engineTargetFormId\":20,\"engineTargetBaseFormId\":7,\"evidence\":\"engine-expression-return\"");
        await Event("capture-end", dropped ? 5UL : 4UL, 3, ",\"status\":\""+status+"\"");
        using var identity = JsonDocument.Parse("""{"processId":123,"sequence":1,"executableFileSha256":"abc","capabilities":{"numericState":true}}""");
        using var duplex = new ScriptedDuplexStream(incoming.ToArray());
        await using var connection = new RuntimeConnection(duplex, identity.RootElement.Clone());
        using var trace = new MemoryStream();

        var result = await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromSeconds(1),
            [new RuntimeAction("read-actor-value", "player", "Health")]);
        trace.Position = 0;
        var imported = await RuntimeTraceImporter.ImportAsync(trace);
        trace.Position = 0;
        var repeated = await RuntimeTraceImporter.ImportAsync(trace);

        Assert.Equal(status, result.Status);
        Assert.Equal(1, result.Snapshots);
        Assert.Equal(complete, imported.Complete);
        Assert.Equal(imported.Sha256, repeated.Sha256);
        Assert.Equal(imported.Events, repeated.Events);
        Assert.Contains("\"value\":0", Encoding.UTF8.GetString(trace.ToArray()), StringComparison.Ordinal);
        Assert.Equal(dropped ? 1UL : 0UL, imported.Dropped);
        Assert.Equal(dropped ? 1L : 0L, imported.MissingSequences);
        duplex.Written.Position = 0;
        Assert.Equal(RuntimeRequestKind.Start, (await RuntimeProtocol.ReadAsync(duplex.Written))!.Kind);
        Assert.Equal("player.GetAV Health", (await RuntimeProtocol.ReadAsync(duplex.Written))!.Payload);

        async Task Event(string kind, ulong sequence, ulong request, string extra)
        {
            var json = "{\"protocol\":1,\"kind\":\""+kind+"\",\"sequence\":"+sequence+
                ",\"requestId\":"+request+",\"frame\":10,\"qpc\":100,\"dropped\":"+(dropped?"1":"0")+extra+"}";
            await RuntimeProtocol.WriteAsync(incoming, new(RuntimeRequestKind.Event,request,json));
        }
    }

    [Fact]
    public async Task Cancellation_sends_cancel_after_start_and_waits_for_engine_acknowledgement()
    {
        using var incoming = new MemoryStream();
        await RuntimeProtocol.WriteAsync(incoming, new(RuntimeRequestKind.Event, 1,
            """{"protocol":1,"kind":"capture-start","sequence":2,"requestId":1,"frame":1,"qpc":1,"dropped":0}"""));
        using var identity = JsonDocument.Parse("""{"processId":123,"sequence":1}""");
        using var cancellation = new CancellationTokenSource();
        using var duplex = new ScriptedDuplexStream(incoming.ToArray(), true, cancellation.Cancel);
        await using var connection = new RuntimeConnection(duplex, identity.RootElement.Clone());
        using var trace = new MemoryStream();

        var result = await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromSeconds(3),
            cancellationToken: cancellation.Token);

        Assert.Equal("cancelled", result.Status);
        duplex.Written.Position = 0;
        Assert.Equal(RuntimeRequestKind.Start, (await RuntimeProtocol.ReadAsync(duplex.Written))!.Kind);
        Assert.Equal(RuntimeRequestKind.Cancel, (await RuntimeProtocol.ReadAsync(duplex.Written))!.Kind);
        trace.Position = 0;
        var imported = await RuntimeTraceImporter.ImportAsync(trace);
        Assert.DoesNotContain("missing-engine-capture-end", imported.Diagnostics);
    }

    [Theory]
    [InlineData("requested", null, "@bmt-script-scope=requested")]
    [InlineData("off", null, "@bmt-script-scope=off")]
    [InlineData("all", null, "@bmt-script-scope=all")]
    [InlineData("selected", "0x000E61A4", "@bmt-script-scope=selected\n@bmt-quest=FalloutNV.esm\t0E61A4")]
    public async Task Script_scope_is_explicit_and_rejects_unsupported_backends(string mode, string? questId, string expected)
    {
        var options = new RuntimeScriptTraceOptions(mode, Quests: questId is null ? null : [new("FalloutNV.esm", questId)]);
        Assert.Equal(expected, options.StartMetadata());
        using var supported = JsonDocument.Parse("""{"capabilities":{"scriptTraceFilters":true}}""");
        await using var connection = new RuntimeConnection(new MemoryStream(), supported.RootElement.Clone());
        Assert.Equal("session\n" + expected, connection.StartPayload("session", options));
        using var unsupported = JsonDocument.Parse("""{"capabilities":{}}""");
        await using var oldConnection = new RuntimeConnection(new MemoryStream(), unsupported.RootElement.Clone());
        Assert.Throws<InvalidOperationException>(() => oldConnection.StartPayload("session", options));
    }

    [Theory]
    [InlineData("selected", null)]
    [InlineData("all", "000E61A4")]
    [InlineData("invalid", null)]
    [InlineData("selected", "010E61A4")]
    public void Invalid_script_scopes_are_rejected_before_capture(string mode, string? id) =>
        Assert.Throws<ArgumentException>(() => new RuntimeScriptTraceOptions(mode,
            Quests: id is null ? null : [new("FalloutNV.esm", id)]).StartMetadata());

    [Theory]
    [InlineData("read-quest-variable", RuntimeRequestKind.QuestRead, "")]
    [InlineData("set-quest-variable", RuntimeRequestKind.QuestSet, "\t0")]
    public void Explicit_quest_identity_preserves_plugin_local_id_and_zero(string kind, RuntimeRequestKind expected, string suffix)
    {
        var action = new RuntimeAction(kind, Name: "DishHutAttack", Value: 0, Plugin: "FalloutNV.esm", FormId: "0x000E61A4");
        var frame = action.ToFrame(27);
        Assert.Equal(expected, frame.Kind);
        Assert.Equal("FalloutNV.esm\t0E61A4\tDishHutAttack" + suffix, frame.Payload);
        Assert.Equal(27UL, frame.RequestId);
        Assert.Equal(RuntimeRequestKind.Evaluate, new RuntimeAction("read-quest-variable", "LegacyQuest", "Flag").ToFrame(28).Kind);
    }

    [Theory]
    [InlineData("player", null, null, "player")]
    [InlineData(null, "FalloutNV.esm", "0x00000014", "FalloutNV.esm\t000014")]
    [InlineData(null, "Addon.esp", "ABCDEF", "Addon.esp\tABCDEF")]
    public void Actor_state_uses_an_explicit_reference_identity(string? target, string? plugin, string? formId, string expected)
    {
        var frame = new RuntimeAction("read-actor-state", Target: target, Plugin: plugin, FormId: formId).ToFrame(7);
        Assert.Equal(RuntimeRequestKind.ActorSnapshot, frame.Kind);
        Assert.Equal(expected, frame.Payload);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("OtherEditorId", null, null)]
    [InlineData("player", "FalloutNV.esm", "14")]
    [InlineData(null, "FalloutNV.esm", "01000014")]
    [InlineData(null, "../FalloutNV.esm", "14")]
    public void Actor_state_rejects_ambiguous_or_unbound_targets(string? target, string? plugin, string? formId) =>
        Assert.Throws<ArgumentException>(() => new RuntimeAction("read-actor-state", Target: target,
            Plugin: plugin, FormId: formId).ToFrame(7));

    [Theory]
    [InlineData("../FalloutNV.esm", "000E61A4", "Flag", 0)]
    [InlineData("FalloutNV.esm\"", "000E61A4", "Flag", 0)]
    [InlineData("FalloutNV.esm", "010E61A4", "Flag", 0)]
    [InlineData("FalloutNV.esm", "0", "Flag", 0)]
    [InlineData(null, "000E61A4", "Flag", 0)]
    [InlineData("FalloutNV.esm", null, "Flag", 0)]
    [InlineData("FalloutNV.esm", "000E61A4", "Flag\t1", 0)]
    [InlineData("FalloutNV.esm", "000E61A4", "Flag", double.MaxValue)]
    public void Explicit_quest_actions_reject_ambiguous_identity_and_unrepresentable_values(string? plugin, string? id, string name, double value)
    {
        Assert.Throws<ArgumentException>(() => new RuntimeAction("set-quest-variable", Name: name, Value: value,
            Plugin: plugin, FormId: id).ToFrame(3));
    }

    [Theory]
    [InlineData("message-probe", RuntimeRequestKind.MessageProbe, "show-ok", false)]
    [InlineData("read-condition-probe", RuntimeRequestKind.ConditionProbe, "player-get-dead", true)]
    public void Controlled_probes_use_typed_requests_and_keep_message_mutation_distinct(
        string action, RuntimeRequestKind kind, string payload, bool readOnly)
    {
        var frame = new RuntimeAction(action).ToFrame(17);
        Assert.Equal(kind, frame.Kind);
        Assert.Equal(payload, frame.Payload);
        Assert.Equal(17UL, frame.RequestId);
        Assert.Equal(readOnly, action.StartsWith("read-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("action-result", ",\"accepted\":false")]
    [InlineData("error", ",\"error\":\"expression-compile-failed\"")]
    [InlineData("script-error", ",\"text\":\"engine script failure\"")]
    public async Task Failed_actions_remain_distinct_from_trace_integrity(string kind, string detail)
    {
        using var incoming = new MemoryStream();
        await Event("capture-start", 2, 1, "");
        await Event(kind, 3, 2, detail);
        await Event("capture-end", 4, 3, ",\"status\":\"completed\"");
        using var identity = JsonDocument.Parse("""{"processId":123,"sequence":1}""");
        using var duplex = new ScriptedDuplexStream(incoming.ToArray());
        await using var connection = new RuntimeConnection(duplex, identity.RootElement.Clone());
        using var trace = new MemoryStream();

        var result = await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromSeconds(1),
            [new RuntimeAction("read-global", "TestGlobal")]);
        trace.Position = 0;
        var imported = await RuntimeTraceImporter.ImportAsync(trace);

        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.Errors);
        Assert.Equal(result.Errors, imported.Errors);
        Assert.True(imported.Complete); // All observations survived; the attempted action failed.

        async Task Event(string eventKind, ulong sequence, ulong request, string extra) =>
            await RuntimeProtocol.WriteAsync(incoming, new(RuntimeRequestKind.Event, request,
                "{\"protocol\":1,\"kind\":\"" + eventKind + "\",\"sequence\":" + sequence +
                ",\"requestId\":" + request + ",\"frame\":10,\"qpc\":100,\"dropped\":0" + extra + "}"));
    }

    [Fact]
    public async Task Intermediate_hook_events_do_not_complete_an_action_before_its_result()
    {
        using var incoming = new MemoryStream();
        await Add("capture-start", 1, 2);
        await Add("condition-function", 2, 3);
        await Add("snapshot", 2, 4);
        var snapshotEnd = incoming.Length;
        await Add("action-result", 3, 5);
        await RuntimeProtocol.WriteAsync(incoming, new(RuntimeRequestKind.Event, 4,
            """{"protocol":1,"kind":"capture-end","sequence":6,"requestId":4,"frame":1,"qpc":1,"dropped":0,"status":"completed"}"""));
        using var identity = JsonDocument.Parse("""{"processId":123,"sequence":1}""");
        long secondActionReadPosition = -1;
        using var duplex = new ScriptedDuplexStream(incoming.ToArray(), onFrame: (kind, position) =>
        {
            if (kind == RuntimeRequestKind.Execute) secondActionReadPosition = position;
        });
        await using var connection = new RuntimeConnection(duplex, identity.RootElement.Clone());
        using var trace = new MemoryStream();
        var result = await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromSeconds(1),
            [new RuntimeAction("read-global", "Flag"), new RuntimeAction("set-global", "Flag", Value: 0)]);
        Assert.Equal("completed", result.Status);
        Assert.True(secondActionReadPosition >= snapshotEnd);

        async Task Add(string kind, ulong request, ulong sequence) => await RuntimeProtocol.WriteAsync(incoming,
            new(RuntimeRequestKind.Event, request, "{\"protocol\":1,\"kind\":\"" + kind + "\",\"sequence\":" + sequence +
                ",\"requestId\":" + request + ",\"frame\":1,\"qpc\":1,\"dropped\":0}"));
    }

    [Theory]
    [InlineData("read-actor-state", "actor-state")]
    [InlineData("read-message-state", "message-state")]
    [InlineData("read-dialogue-state", "record-membership")]
    [InlineData("read-cell-terrain", "record-membership")]
    public async Task Typed_state_waits_for_its_terminal_record_after_numeric_snapshots(string action, string terminal)
    {
        using var incoming = new MemoryStream();
        await Add("capture-start", 1, 2);
        await Add("snapshot", 2, 3);
        await Add("snapshot", 2, 4);
        await Add(terminal, 2, 5);
        var actorEnd = incoming.Length;
        await Add("snapshot", 3, 6);
        await RuntimeProtocol.WriteAsync(incoming, new(RuntimeRequestKind.Event, 4,
            """{"protocol":1,"kind":"capture-end","sequence":7,"requestId":4,"frame":1,"qpc":1,"dropped":0,"status":"completed"}"""));
        using var identity = JsonDocument.Parse("""{"processId":123,"sequence":1}""");
        long nextActionPosition = -1;
        using var duplex = new ScriptedDuplexStream(incoming.ToArray(), onFrame: (kind, position) =>
        {
            if (kind == RuntimeRequestKind.Evaluate) nextActionPosition = position;
        });
        await using var connection = new RuntimeConnection(duplex, identity.RootElement.Clone());
        using var trace = new MemoryStream();
        var result = await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromSeconds(1),
            [new RuntimeAction(action, Target: action == "read-actor-state" ? "player" : null,
                Plugin: terminal == "record-membership" ? "Addon.esm" : null,
                FormId: terminal == "record-membership" ? "000A00" : null), new RuntimeAction("read-global", "Flag")]);
        Assert.Equal("completed", result.Status);
        Assert.Equal(3, result.Snapshots);
        Assert.True(nextActionPosition >= actorEnd);

        async Task Add(string kind, ulong request, ulong sequence) => await RuntimeProtocol.WriteAsync(incoming,
            new(RuntimeRequestKind.Event, request, "{\"protocol\":1,\"kind\":\"" + kind + "\",\"sequence\":" + sequence +
                ",\"requestId\":" + request + ",\"frame\":1,\"qpc\":1,\"dropped\":0}"));
    }

    [Theory]
    [InlineData("probe", "BMT runtime observation: click OK.", "Ok")]
    [InlineData("visible", "Caravan Pack items added to inventory.", "OK")]
    public void Menu_choice_preserves_exact_observed_text_and_zero_button(string owner, string message, string label)
    {
        var action = new RuntimeAction("choose-message", Target: owner, Message: message, Button: label, ButtonIndex: 0);
        Assert.Equal(new RuntimeFrame(RuntimeRequestKind.MessageMenu, 8, $"choose\t{owner}\t0\t{message}\t{label}"), action.ToFrame(8));
    }

    [Theory]
    [InlineData("visible", "", "OK", 0)]
    [InlineData("visible", "Text\ncommand", "OK", 0)]
    [InlineData("visible", "Text", "OK\t1", 0)]
    [InlineData("visible", "Text", "OK", 1)]
    [InlineData("unknown", "Text", "OK", 0)]
    public void Menu_choice_rejects_ambiguous_or_unsupported_targets(string owner, string message, string label, int index)
    {
        Assert.Throws<ArgumentException>(() => new RuntimeAction("choose-message", Target: owner,
            Message: message, Button: label, ButtonIndex: index).ToFrame(8));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(10)]
    public void Actor_mutation_keeps_explicit_reference_identity_and_bounded_value(int value)
    {
        var action = new RuntimeAction("set-actor-value", Name: "Endurance", Value: value, Plugin: "FalloutNV.esm", FormId: "00104C0F");
        Assert.Equal(new RuntimeFrame(RuntimeRequestKind.ActorSet, 8, $"FalloutNV.esm\t104C0F\tEndurance\t{value}"), action.ToFrame(8));
    }

    [Theory]
    [InlineData("Endurance", 0)]
    [InlineData("Endurance", 11)]
    [InlineData("Endurance", 4.5)]
    [InlineData("Endurance", double.NaN)]
    [InlineData("Health", 5)]
    public void Actor_mutation_rejects_values_outside_the_calibration_contract(string name, double value)
    {
        Assert.Throws<ArgumentException>(() => new RuntimeAction("set-actor-value", Target: "player", Name: name, Value: value).ToFrame(8));
    }

    [Theory]
    [InlineData(0, "matched", "matched")]
    [InlineData(1, "matched", "timeout")]
    [InlineData(2, "matched", "timeout")]
    [InlineData(0, "capture-stopping", "capture-stopping")]
    [InlineData(1, "capture-stopping", "capture-stopping")]
    [InlineData(2, "capture-stopping", "timeout")]
    [InlineData(0, "cancelled", "cancelled")]
    [InlineData(2, "cancelled", "cancelled")]
    [InlineData(0, "disconnected", "disconnected")]
    [InlineData(2, "disconnected", "disconnected")]
    [InlineData(2, "native-error", "native-error")]
    [InlineData(2, "mismatch", "mismatch")]
    [InlineData(2, "capture-ended", "capture-ended")]
    [InlineData(0, "timeout", "timeout")]
    [InlineData(2, "timeout", "timeout")]
    public void Menu_wait_terminal_outcome_preserves_the_admitted_deadline(
        int priorResolution, string observedOutcome, string expectedOutcome)
    {
        // Force both sides of the race without relying on timer scheduling: 0 is pending,
        // 1 is a terminal result, and 2 means the menu deadline already requested capture stop.
        var resolution = priorResolution;
        Assert.Equal(expectedOutcome,
            RuntimeCaptureService.CompleteMenuWaitOutcome(observedOutcome, ref resolution));
        // Once completed, a later deadline callback cannot claim the pending wait.
        Assert.NotEqual(0, Interlocked.CompareExchange(ref resolution, 2, 0));
    }

    [Theory]
    [InlineData("matched", 0, true)]
    [InlineData("menu-not-ready", 0, true)]
    [InlineData("mismatch", 1, false)]
    [InlineData("stale-owner", 1, false)]
    [InlineData("malformed-status", 1, false)]
    [InlineData("malformed-reason", 1, false)]
    [InlineData("unknown-reason", 1, false)]
    [InlineData("malformed-button", 1, false)]
    [InlineData("native-error", 1, false)]
    [InlineData("timeout", 1, false)]
    [InlineData("silent-timeout", 1, false)]
    [InlineData("capture-ended", 1, false)]
    [InlineData("cancelled", 0, false)]
    [InlineData("disconnected", 0, false)]
    public async Task Menu_wait_preserves_observations_and_never_retries_a_choice(string behavior, int errors, bool chooses)
    {
        using var cancelled = new CancellationTokenSource();
        using var identity = JsonDocument.Parse("""{"processId":123,"sequence":1,"capabilities":{"messageStateAvailability":true}}""");
        using var duplex = new MenuResponseStream(behavior, cancelled);
        await using var connection = new RuntimeConnection(duplex, identity.RootElement.Clone());
        using var trace = new MemoryStream();
        var result = await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromMilliseconds(behavior == "capture-ended" ? 80 : 400),
            [new RuntimeAction("wait-message-state", Target: "probe", Message: "Expected", Button: "Ok", ButtonIndex: 0,
                TimeoutMilliseconds: behavior is "timeout" or "silent-timeout" ? 80 : 300, PollMilliseconds: 50),
             new RuntimeAction("choose-message", Target: "probe", Message: "Expected", Button: "Ok", ButtonIndex: 0)], cancelled.Token);
        Assert.Equal(errors, result.Errors);
        Assert.Equal(chooses ? 1 : 0, duplex.Choices);
        Assert.All(duplex.MenuPayloads.Where(payload => !payload.StartsWith("choose", StringComparison.Ordinal)), payload => Assert.Equal("inspect", payload));
        if (behavior is "mismatch" or "stale-owner" or "native-error" or "malformed-status" or
            "malformed-reason" or "unknown-reason" or "malformed-button") Assert.Single(duplex.MenuPayloads);
        trace.Position = 0;
        var imported = await RuntimeTraceImporter.ImportAsync(trace);
        Assert.Equal(errors, imported.Errors);
        Assert.Equal(1, imported.ControllerResults);
        Assert.Equal(behavior == "native-error" ? 0 : errors, imported.ControllerErrors);
        Assert.Equal(0, imported.MissingSequences);
        Assert.DoesNotContain("footer-count-mismatch", imported.Diagnostics);
        var lines = Encoding.UTF8.GetString(trace.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal(2, header.RootElement.GetProperty("version").GetInt32());
        using var outcome = JsonDocument.Parse(Assert.Single(lines.Where(line => line.Contains("\"scenario-result\"", StringComparison.Ordinal))));
        Assert.Equal(behavior switch
        {
            "menu-not-ready" => "matched",
            "stale-owner" or "malformed-status" or "malformed-reason" or "unknown-reason" or "malformed-button" => "mismatch",
            "silent-timeout" => "timeout", _ => behavior
        }, outcome.RootElement.GetProperty("status").GetString());
        Assert.False(outcome.RootElement.TryGetProperty("sequence", out _));
        Assert.False(outcome.RootElement.TryGetProperty("protocol", out _));
        if (behavior is "silent-timeout" or "capture-ended")
            Assert.False(outcome.RootElement.TryGetProperty("observedRequestId", out _));
    }

    [Fact]
    public async Task Denied_action_admission_never_writes_or_consumes_a_request_identity()
    {
        using var identity = JsonDocument.Parse("""{"sequence":1}""");
        using var duplex = new ScriptedDuplexStream([]);
        await using var connection = new RuntimeConnection(duplex, identity.RootElement.Clone());
        var choice = new RuntimeAction("choose-message", Target: "probe", Message: "Expected", Button: "Ok", ButtonIndex: 0);
        Assert.Null(await connection.TrySendActionAsync(choice, static () => false));
        Assert.Equal(0, duplex.Written.Length);
        Assert.Equal(1UL, await connection.SendActionAsync(new RuntimeAction("read-message-state")));
        duplex.Written.Position = 0;
        Assert.Equal("inspect", (await RuntimeProtocol.ReadAsync(duplex.Written))!.Payload);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(30001, 50)]
    [InlineData(100, 49)]
    [InlineData(100, 1001)]
    public void Menu_wait_rejects_unbounded_or_busy_polling(int timeout, int interval) =>
        Assert.Throws<ArgumentException>(() => new RuntimeAction("wait-message-state", Message: "Expected", Button: "Ok", ButtonIndex: 0,
            TimeoutMilliseconds: timeout, PollMilliseconds: interval).ToFrame(1));

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Import_rejects_unknown_trace_versions(int version)
    {
        using var trace = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"kind\":\"capture-header\",\"schema\":\"bmt/runtime-trace\",\"version\":{version}}}\n"));
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeTraceImporter.ImportAsync(trace));
    }

    [Fact]
    public async Task Menu_wait_rejects_a_backend_without_availability_contract_before_start()
    {
        using var identity = JsonDocument.Parse("""{"processId":123,"sequence":1}""");
        using var duplex = new ScriptedDuplexStream([]);
        await using var connection = new RuntimeConnection(duplex, identity.RootElement.Clone());
        using var trace = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromSeconds(1),
            [new RuntimeAction("wait-message-state", Message: "Expected", Button: "Ok", ButtonIndex: 0, TimeoutMilliseconds: 100, PollMilliseconds: 50)]));
        Assert.Equal(0, duplex.Written.Length);
    }

    private sealed class MenuResponseStream(string behavior, CancellationTokenSource cancelled) : Stream
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        private readonly MemoryStream _written = new();
        private MemoryStream? _current;
        private ulong _sequence = 1;
        private long _parsed;
        public int Choices { get; private set; }
        public List<string> MenuPayloads { get; } = [];
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            while (_current is null || _current.Position == _current.Length)
            {
                _current?.Dispose();
                if (!await _responses.Reader.WaitToReadAsync(token)) return 0;
                _current = new MemoryStream(await _responses.Reader.ReadAsync(token));
            }
            return await _current.ReadAsync(buffer, token);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            _written.Position = _written.Length;
            await _written.WriteAsync(buffer, token);
            var bytes = _written.ToArray();
            while (bytes.Length - _parsed >= RuntimeProtocol.HeaderLength)
            {
                var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)_parsed + 16));
                if (bytes.Length - _parsed < RuntimeProtocol.HeaderLength + length) return;
                using var frameInput = new MemoryStream(bytes, (int)_parsed, RuntimeProtocol.HeaderLength + (int)length);
                var frame = (await RuntimeProtocol.ReadAsync(frameInput, token))!;
                _parsed += RuntimeProtocol.HeaderLength + length;
                if (frame.Kind == RuntimeRequestKind.Start) await Respond("capture-start", frame.RequestId, "");
                else if (frame.Kind is RuntimeRequestKind.Stop or RuntimeRequestKind.Cancel)
                    await Respond("capture-end", frame.RequestId, ",\"status\":\"" + (frame.Kind == RuntimeRequestKind.Cancel ? "cancelled" : "completed") + "\"");
                else if (frame.Kind == RuntimeRequestKind.MessageMenu)
                {
                    MenuPayloads.Add(frame.Payload);
                    if (frame.Payload.StartsWith("choose", StringComparison.Ordinal)) { ++Choices; await Respond("action-result", frame.RequestId, ",\"accepted\":true"); }
                    else if (behavior == "disconnected") _responses.Writer.Complete();
                    else if (behavior == "native-error") await Respond("error", frame.RequestId, ",\"error\":\"owner-unreadable\"");
                    else if (behavior == "malformed-status") await Respond("message-state", frame.RequestId, ",\"status\":0");
                    else if (behavior == "malformed-reason") await Respond("message-state", frame.RequestId, ",\"status\":\"unavailable\",\"reason\":0");
                    else if (behavior == "unknown-reason") await Respond("message-state", frame.RequestId, ",\"status\":\"unavailable\",\"reason\":\"owner-unreadable\"");
                    else if (behavior == "malformed-button") await Respond("message-state", frame.RequestId,
                        ",\"status\":\"visible\",\"text\":\"Expected\",\"buttonLabel\":\"Ok\",\"owner\":\"dedicated-probe\",\"buttonIndex\":\"0\"");
                    else if (behavior is "silent-timeout" or "capture-ended") { }
                    else if (behavior == "menu-not-ready" && MenuPayloads.Count == 1)
                        await Respond("message-state", frame.RequestId, ",\"status\":\"unavailable\",\"reason\":\"menu-not-ready\",\"evidence\":\"engine-menu-availability\"");
                    else if (behavior is "timeout" or "cancelled" || behavior == "matched" && MenuPayloads.Count == 1)
                    {
                        await Respond("message-state", frame.RequestId, ",\"status\":\"unavailable\",\"reason\":\"menu-not-visible\"");
                        if (behavior == "cancelled") cancelled.Cancel();
                    }
                    else await Respond("message-state", frame.RequestId, ",\"status\":\"visible\",\"text\":\"" + (behavior == "mismatch" ? "Different" : "Expected") +
                        "\",\"buttonLabel\":\"Ok\",\"buttonIndex\":0,\"owner\":\"" + (behavior == "stale-owner" ? "stale-probe" : "dedicated-probe") + "\"");
                }
            }
        }
        private async Task Respond(string kind, ulong request, string fields)
        {
            using var response = new MemoryStream();
            await RuntimeProtocol.WriteAsync(response, new(RuntimeRequestKind.Event, request,
                $"{{\"protocol\":1,\"kind\":\"{kind}\",\"sequence\":{++_sequence},\"requestId\":{request},\"frame\":1,\"qpc\":1,\"dropped\":0{fields}}}"));
            await _responses.Writer.WriteAsync(response.ToArray());
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { _current?.Dispose(); _written.Dispose(); } base.Dispose(disposing); }
    }

    [Fact]
    public async Task Profile_preparation_copies_inputs_without_claiming_engine_activation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bmt-runtime-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = Path.Combine(directory, "Fallout.ini");
            var saves = Path.Combine(directory, "saves");
            Directory.CreateDirectory(saves);
            await File.WriteAllTextAsync(config, "[General]\\nvalue=1");
            await File.WriteAllTextAsync(Path.Combine(saves, "test.fos"), "fixture");
            var profile = await RuntimeRunProfile.PrepareAsync([config], saves, Path.Combine(directory, "profile"));

            Assert.Equal("prepared-not-engine-verified", profile.Activation);
            Assert.Equal(2, profile.Files.Count);
            await profile.VerifyOriginalsAsync();
            using var identity = JsonDocument.Parse("""{"processId":123,"executableFileSha256":"abc"}""");
            await Assert.ThrowsAsync<InvalidOperationException>(() => profile.ValidateForRunAsync(identity.RootElement));
            Assert.Equal(await File.ReadAllTextAsync(config),
                await File.ReadAllTextAsync(Path.Combine(profile.Root, "config", "Fallout.ini")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Already_cancelled_capture_sends_no_engine_action_and_records_its_terminal_status()
    {
        using var identity = JsonDocument.Parse("""{"processId":123,"sequence":1}""");
        using var duplex = new ScriptedDuplexStream([]);
        await using var connection = new RuntimeConnection(duplex, identity.RootElement.Clone());
        using var trace = new MemoryStream();

        var result = await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromSeconds(1),
            cancellationToken: new CancellationToken(true));

        Assert.Equal("cancelled-before-start", result.Status);
        Assert.Equal(0, duplex.Written.Length);
        trace.Position = 0;
        var imported = await RuntimeTraceImporter.ImportAsync(trace);
        Assert.False(imported.Complete);
        Assert.Equal("cancelled-before-start", imported.Status);
    }

    [Fact]
    public async Task Disconnect_writes_partial_footer_and_never_claims_complete_capture()
    {
        using var identity = JsonDocument.Parse("""{"processId":123,"sequence":1}""");
        using var duplex = new ScriptedDuplexStream([]);
        await using var connection = new RuntimeConnection(duplex, identity.RootElement.Clone());
        using var trace = new MemoryStream();
        var result = await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromSeconds(1));
        Assert.Equal("disconnected", result.Status);
        trace.Position = 0;
        var imported = await RuntimeTraceImporter.ImportAsync(trace);
        Assert.False(imported.Complete);
        Assert.Contains("missing-engine-capture-end", imported.Diagnostics);
    }

    private sealed class OneByteReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private sealed class ScriptedDuplexStream(byte[] input, bool waitForCancel = false, Action? onStart = null,
        Action<RuntimeRequestKind, long>? onFrame = null) : Stream
    {
        private readonly MemoryStream _input = new(input);
        private readonly TaskCompletionSource<byte[]> _cancelAcknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private MemoryStream? _tail;
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_input.Position < _input.Length || !waitForCancel) return await _input.ReadAsync(buffer, token);
            _tail ??= new MemoryStream(await _cancelAcknowledgement.Task.WaitAsync(token));
            return await _tail.ReadAsync(buffer, token);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await Written.WriteAsync(buffer, token);
            if (buffer.Length == RuntimeProtocol.HeaderLength)
                onFrame?.Invoke((RuntimeRequestKind)BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span[6..]), _input.Position);
            if (buffer.Length == RuntimeProtocol.HeaderLength &&
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span[6..]) == (ushort)RuntimeRequestKind.Start)
                onStart?.Invoke();
            if (waitForCancel && buffer.Length == RuntimeProtocol.HeaderLength &&
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span[6..]) == (ushort)RuntimeRequestKind.Cancel)
            {
                var id = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Span[8..]);
                using var response = new MemoryStream();
                await RuntimeProtocol.WriteAsync(response, new(RuntimeRequestKind.Event, id,
                    "{\"protocol\":1,\"kind\":\"capture-end\",\"sequence\":3,\"requestId\":" + id +
                    ",\"frame\":2,\"qpc\":2,\"dropped\":0,\"status\":\"cancelled\"}"), token);
                _cancelAcknowledgement.TrySetResult(response.ToArray());
            }
        }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _input.Dispose(); _tail?.Dispose(); Written.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
