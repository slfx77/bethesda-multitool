using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeMenuMilestoneTests
{
    [Theory]
    [InlineData("menu-not-ready", "matched", 0)]
    [InlineData("menu-not-visible", "matched", 0)]
    [InlineData("previous-menu", "matched", 0)]
    [InlineData("wrong-request", "matched", 0)]
    [InlineData("timeout", "timeout", 1)]
    [InlineData("malformed-status", "mismatch", 1)]
    [InlineData("malformed-reason", "mismatch", 1)]
    [InlineData("unknown-reason", "mismatch", 1)]
    [InlineData("malformed-button", "mismatch", 1)]
    [InlineData("missing-text", "mismatch", 1)]
    [InlineData("native-error", "native-error", 1)]
    [InlineData("cancel", "cancelled", 0)]
    [InlineData("disconnect", "disconnected", 0)]
    public async Task Next_menu_poll_preserves_attribution_and_never_repeats_a_choice(string behavior, string expectedStatus, int errors)
    {
        using var cancellation = new CancellationTokenSource();
        using var engine = new MenuEngine(behavior, cancellation);
        var identity = Json("""{"processId":123,"sequence":1,"capabilities":{"messageStateAvailability":true}}""");
        await using var connection = new RuntimeConnection(engine, identity);
        using var trace = new MemoryStream();
        var milestone = new RuntimeMilestone("next-menu", 0, "message-state",
            [new("status", Json("\"visible\"")), new("text", Json("\"Classic\""), "not-equal")],
            TimeoutMilliseconds: behavior == "timeout" ? 130 : 350, Probe: new("read-message-state"), PollMilliseconds: 50);
        var result = await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromMilliseconds(650),
            [Choice("Classic"), Choice("Tribal")], cancellation.Token, milestones: [milestone]);
        trace.Position = 0;
        var document = await RuntimeTraceImporter.ReadDocumentAsync(trace);
        var outcome = Assert.Single(document.ControllerResults);
        Assert.Equal(expectedStatus, outcome.Status);
        Assert.Equal(errors, result.Errors);
        Assert.Equal(result.Errors, document.Summary.Errors);
        Assert.Equal(result.ControllerErrors, document.Summary.ControllerErrors);
        Assert.Equal(0, document.Summary.MissingSequences);
        Assert.DoesNotContain("footer-count-mismatch", document.Summary.Diagnostics);
        Assert.DoesNotContain("missing-milestone-results", document.Summary.Diagnostics);
        Assert.DoesNotContain("invalid-milestone-result", document.Summary.Diagnostics);
        var choices = engine.Requests.Where(frame => frame.Kind == RuntimeRequestKind.MessageMenu && frame.Payload.StartsWith("choose", StringComparison.Ordinal)).ToArray();
        Assert.Equal(expectedStatus == "matched" ? 2 : 1, choices.Length);
        Assert.Equal(Choice("Classic").ToFrame(1).Payload, choices[0].Payload);
        if (choices.Length == 2) Assert.Equal(Choice("Tribal").ToFrame(1).Payload, choices[1].Payload);
        var probes = engine.Requests.Where(frame => frame.Kind == RuntimeRequestKind.MessageMenu && frame.Payload == "inspect").ToArray();
        if (expectedStatus is "mismatch" or "native-error" or "cancelled" or "disconnected") Assert.Single(probes);
        if (expectedStatus == "matched") Assert.Equal(2, probes.Length);
        var rows = Encoding.UTF8.GetString(trace.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Json).ToArray();
        var resultRow = Assert.Single(rows.Where(row => Text(row, "kind") == "scenario-result"));
        Assert.Equal(3, rows[0].GetProperty("version").GetInt32());
        if (expectedStatus == "matched")
        {
            var observed = Assert.Single(rows.Where(row => Text(row, "kind") == "message-state" &&
                row.GetProperty("sequence").GetUInt64() == resultRow.GetProperty("observedSequence").GetUInt64()));
            Assert.Equal(probes[^1].RequestId, resultRow.GetProperty("observedRequestId").GetUInt64());
            Assert.Equal(probes[^1].RequestId, observed.GetProperty("requestId").GetUInt64());
            Assert.Equal("Tribal", Text(observed, "text"));
            Assert.True(document.Summary.Complete);
        }
        if (behavior is "menu-not-ready" or "menu-not-visible" or "timeout" or "cancel")
        {
            var unavailable = rows.First(row => Text(row, "kind") == "message-state" && Text(row, "status") == "unavailable");
            var inspection = unavailable.GetProperty("menuInspection");
            Assert.Equal("unavailable", Text(inspection, "availability"));
            Assert.Equal(0, inspection.GetProperty("fields")[0].GetProperty("values")[0].GetInt32());
        }
    }

    [Theory]
    [InlineData("\"menu-not-visible\"", true)]
    [InlineData("\"menu-not-ready\"", true)]
    [InlineData("\"owner-unreadable\"", false)]
    [InlineData("null", false)]
    [InlineData("17", false)]
    public void Explicit_absence_expectation_accepts_only_known_menu_inspection_states(string reason, bool expected)
    {
        var milestone = new RuntimeMilestone("absent", -1, "message-state",
            [new("status", Json("\"unavailable\""))], Probe: new("read-message-state"));
        var row = Json("{\"kind\":\"message-state\",\"requestId\":7,\"status\":\"unavailable\",\"reason\":" + reason + "}");
        var identity = Json("{}");
        var monitor = new RuntimeMilestoneMonitor([milestone], 0);
        monitor.Begin(-1, identity);
        var pending = Assert.Single(monitor.Active);
        pending.ProbeRequest = 7;
        monitor.Observe(row, false);
        Assert.Equal(expected, pending.Matched);
        Assert.Equal(expected ? null : "mismatch", pending.Failure);
        Assert.Equal(expected, RuntimeMilestoneMonitor.MatchesEvent(milestone, identity, row));
    }

    [Fact]
    public void Generic_unavailable_probe_expectation_is_unchanged()
    {
        var milestone = new RuntimeMilestone("actor", -1, "actor-state",
            [new("status", Json("\"unavailable\""))], Probe: new("read-actor-state", Target: "player"));
        var row = Json("""{"kind":"actor-state","requestId":7,"status":"unavailable","reason":"base-unavailable"}""");
        var monitor = new RuntimeMilestoneMonitor([milestone], 0);
        monitor.Begin(-1, Json("{}"));
        var pending = Assert.Single(monitor.Active);
        pending.ProbeRequest = 7;
        monitor.Observe(row, false);
        Assert.True(pending.Matched);
        Assert.True(RuntimeMilestoneMonitor.MatchesEvent(milestone, Json("{}"), row));
    }

    private static RuntimeAction Choice(string text) => new("choose-message", Target: "visible", Message: text, Button: "Ok", ButtonIndex: 0);
    private static JsonElement Json(string text) { using var value = JsonDocument.Parse(text); return value.RootElement.Clone(); }
    private static string? Text(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class MenuEngine(string behavior, CancellationTokenSource cancellation) : Stream
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        private readonly MemoryStream _request = new();
        private MemoryStream? _current;
        private ulong _sequence = 1;
        private int _probes;
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
            if (frame.Kind == RuntimeRequestKind.Start) await Emit("capture-start", frame.RequestId);
            else if (frame.Kind is RuntimeRequestKind.Stop or RuntimeRequestKind.Cancel)
                await Emit("capture-end", frame.RequestId, new() { ["status"] = frame.Kind == RuntimeRequestKind.Cancel ? "cancelled" : "completed" });
            else if (frame.Kind == RuntimeRequestKind.MessageMenu && frame.Payload == "inspect")
            {
                ++_probes;
                if (behavior == "disconnect") { _responses.Writer.TryComplete(); return; }
                // An error with the readiness token is still an error, never a retryable observation.
                if (behavior == "native-error") { await Emit("error", frame.RequestId, new() { ["error"] = "menu-not-ready" }); return; }
                if (behavior == "wrong-request" && _probes == 1)
                    await Emit("message-state", frame.RequestId + 100, new() { ["status"] = 17 });
                var row = Visible(_probes == 1 && behavior == "previous-menu" ? "Classic" : "Tribal");
                if (behavior is "timeout" or "cancel" || _probes == 1 && (behavior is "menu-not-visible" or "menu-not-ready" or "wrong-request"))
                    row = new()
                    {
                        ["status"] = "unavailable", ["reason"] = behavior == "menu-not-visible" ? "menu-not-visible" : "menu-not-ready",
                        ["evidence"] = "engine-menu-availability",
                        ["menuInspection"] = new JsonObject
                        {
                            ["availability"] = "unavailable", ["failedCheck"] = "active-stack-empty",
                            ["fields"] = new JsonArray(new JsonObject { ["name"] = "active-stack", ["address"] = 100,
                                ["readable"] = true, ["values"] = new JsonArray(0) })
                        }
                    };
                if (behavior == "malformed-status") row["status"] = 17;
                if (behavior == "malformed-reason") row = new() { ["status"] = "unavailable", ["reason"] = 17 };
                if (behavior == "unknown-reason") row = new() { ["status"] = "unavailable", ["reason"] = "owner-unreadable" };
                if (behavior == "malformed-button") row["buttonIndex"] = "0";
                if (behavior == "missing-text") row.Remove("text");
                await Emit("message-state", frame.RequestId, row);
                if (behavior == "cancel") cancellation.Cancel();
            }
            else await Emit("action-result", frame.RequestId, new() { ["accepted"] = true });
        }
        private static JsonObject Visible(string text) => new()
        { ["status"] = "visible", ["text"] = text, ["buttonLabel"] = "Ok", ["buttonIndex"] = 0, ["owner"] = "explicit-visible-text" };
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
        protected override void Dispose(bool disposing)
        { if (disposing) { _request.Dispose(); _current?.Dispose(); _responses.Writer.TryComplete(); } base.Dispose(disposing); }
    }
}
