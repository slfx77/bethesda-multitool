using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeCombatCaptureTests
{
    [Theory]
    [InlineData("capture-deadline", "completed", true)]
    [InlineData("cancel", "cancelled", true)]
    [InlineData("milestone-timeout", "completed", true)]
    [InlineData("protocol-error", "protocol-error", true)]
    [InlineData("second-protocol-error", "protocol-error", false)]
    [InlineData("rejected-start", "completed", true)]
    [InlineData("disconnect", "disconnected", false)]
    public async Task Abort_paths_stop_and_drain_cleanup_before_the_footer_without_hiding_the_original_outcome(
        string mode, string status, bool cleanupCaptured)
    {
        var actor = new RuntimeAction("start-combat-leased", Plugin: "Control.esp", FormId: "800",
            OtherTarget: "player", TimeoutMilliseconds: 1000);
        RuntimeAction[] actions = mode == "capture-deadline" ? [actor] : [actor, new("read-actor-value", "player", "Health")];
        using var cancelled = new CancellationTokenSource();
        using var identity = JsonDocument.Parse("""
            {"sequence":1,"activePluginIdentityStatus":"complete","capabilities":{"combatCleanupLease":true,"actionBoundaries":true},
             "activePlugins":[{"name":"FalloutNV.esm","index":0,"status":"verified"},
                              {"name":"Control.esp","index":1,"status":"verified"}]}
            """);
        var sent = new List<RuntimeRequestKind>();
        var session = "";
        ulong sequence = 1, leaseRequest = 0;
        JsonObject? admitted = null;
        var disconnect = false;
        using var duplex = new ReplyStream(async (request, reply, close) =>
        {
            sent.Add(request.Kind);
            if (disconnect) throw new IOException("Synthetic bridge disconnect after arming.");
            if (request.Kind == RuntimeRequestKind.Start)
            {
                session = request.Payload.Split('\n')[0];
                var start = Native("capture-start", request.RequestId);
                start["session"] = session; start["identity"] = JsonNode.Parse(identity.RootElement.GetRawText());
                await reply(start);
            }
            else if (request.Kind == RuntimeRequestKind.ReferenceAction)
            {
                Assert.Equal("start-combat-leased\tControl.esp\t000800\t@player\t000014\t1000", request.Payload);
                leaseRequest = request.RequestId;
                var begin = Native("action-begin", leaseRequest);
                begin["requestKind"] = 16; begin["evidence"] = "game-thread-dispatch";
                await reply(begin);
                admitted = Native("action-result", leaseRequest);
                AddLease(admitted, session);
                admitted["accepted"] = mode != "rejected-start";
                await reply(admitted);
                if (mode == "cancel") cancelled.Cancel();
                if (mode is "protocol-error" or "second-protocol-error")
                {
                    var invalid = Native("command", leaseRequest); invalid["protocol"] = 2;
                    await reply(invalid);
                }
                if (mode == "disconnect") { disconnect = true; close(); }
            }
            else if (request.Kind is RuntimeRequestKind.Stop or RuntimeRequestKind.Cancel)
            {
                if (mode == "second-protocol-error")
                {
                    var invalid = Native("combat-cleanup", leaseRequest); invalid["protocol"] = 2;
                    await reply(invalid);
                    return;
                }
                Assert.NotNull(admitted);
                var terminal = Native("combat-cleanup", leaseRequest);
                AddLease(terminal, session);
                terminal["status"] = "observed";
                terminal["trigger"] = mode == "rejected-start" ? "start-rejected" : request.Kind == RuntimeRequestKind.Cancel ? "cancel" : "stop";
                terminal["cleanupStartedMonotonicMilliseconds"] = 200;
                terminal["completedMonotonicMilliseconds"] = 201;
                terminal["evidence"] = "game-thread-fixed-cleanup-and-independent-readback";
                terminal["steps"] = new JsonArray(Step("stop-attacker", false, false), Step("stop-target", true, false), Step("disable-attacker", false, true));
                await reply(terminal);
                var end = Native("capture-end", request.RequestId);
                end["status"] = request.Kind == RuntimeRequestKind.Cancel ? "cancelled" : "completed";
                await reply(end);
            }
            else if (request.Kind == RuntimeRequestKind.Evaluate)
            {
                // Let a defective implementation terminate promptly; the assertion below rejects this action.
                var state = Native("snapshot", request.RequestId); state["value"] = 100;
                await reply(state);
            }

            JsonObject Native(string kind, ulong id) => new()
            {
                ["protocol"] = 1, ["kind"] = kind, ["sequence"] = ++sequence, ["requestId"] = id,
                ["frame"] = sequence, ["qpc"] = sequence, ["dropped"] = 0
            };
        });
        RuntimeMilestone[] milestones = mode is "milestone-timeout" or "protocol-error" or "second-protocol-error" or "disconnect"
            ? [new("expected-hit", 0, "actor-hit", [new("damage", JsonSerializer.SerializeToElement(1))],
                TimeoutMilliseconds: 250, Forms: [new("engineTargetFormId", Target: "player")])]
            : [];
        await using var connection = new RuntimeConnection(duplex, identity.RootElement.Clone());
        using var trace = new MemoryStream();
        var duration = TimeSpan.FromMilliseconds(mode == "capture-deadline" ? 500 : 3000);
        var result = await RuntimeCaptureService.CaptureAsync(connection, trace, duration, actions,
            cancelled.Token, milestones: milestones).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(status, result.Status);
        Assert.DoesNotContain(RuntimeRequestKind.Evaluate, sent);
        Assert.Equal(mode == "disconnect" ? 0 : 1, sent.Count(kind => kind is RuntimeRequestKind.Stop or RuntimeRequestKind.Cancel));
        var lines = System.Text.Encoding.UTF8.GetString(trace.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var rows = lines.Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        Assert.Equal("capture-footer", rows[^1]["kind"]!.GetValue<string>());
        Assert.Single(rows.Where(row => row["kind"]!.GetValue<string>() == "capture-footer"));
        var cleanup = rows.Where(row => row["kind"]!.GetValue<string>() == "combat-cleanup").ToArray();
        Assert.Equal(cleanupCaptured ? 1 : 0, cleanup.Length);
        if (cleanupCaptured)
        {
            Assert.Equal(leaseRequest, cleanup[0]["requestId"]!.GetValue<ulong>());
            Assert.Equal("observed", cleanup[0]["status"]!.GetValue<string>());
            Assert.True(Array.IndexOf(rows, cleanup[0]) < Array.FindIndex(rows, row => row["kind"]!.GetValue<string>() == "capture-end"));
        }
        if (mode is "capture-deadline" or "cancel" or "milestone-timeout" or "rejected-start")
            Assert.Empty(rows[^1]["combatCleanupDiagnostics"]!.AsArray());
        else Assert.NotEmpty(rows[^1]["combatCleanupDiagnostics"]!.AsArray());
        if (mode is "milestone-timeout" or "protocol-error" or "second-protocol-error" or "rejected-start" or "disconnect")
            Assert.True(result.Errors > 0);
        trace.Position = 0;
        var imported = await RuntimeTraceImporter.ImportAsync(trace, TestContext.Current.CancellationToken);
        Assert.Equal(result.Errors, imported.Errors);
        if (mode == "capture-deadline") Assert.True(imported.Complete);
        if (mode is "protocol-error" or "second-protocol-error" or "disconnect") Assert.False(imported.Complete);
    }

    private static void AddLease(JsonObject row, string session)
    {
        row["operation"] = "start-combat-leased"; row["leaseId"] = 7; row["originSession"] = session;
        row["captureGeneration"] = 2; row["connectionGeneration"] = 3; row["loadEpoch"] = 1;
        row["engineTargetFormId"] = 0x01000800; row["engineTargetBaseFormId"] = 0x01000801;
        row["engineOtherFormId"] = 0x14; row["engineOtherBaseFormId"] = 7;
        row["attackerAddress"] = 0x100000; row["targetAddress"] = 0x200000;
        row["attackerBaseAddress"] = 0x300000; row["targetBaseAddress"] = 0x400000;
        row["durationMilliseconds"] = 1000; row["armedMonotonicMilliseconds"] = 100; row["deadlineMonotonicMilliseconds"] = 1100;
    }

    private static JsonObject Step(string name, bool target, bool disabled) => new()
    {
        ["step"] = name, ["status"] = "observed", ["accepted"] = true, ["identityResolved"] = true,
        ["engineTargetFormId"] = target ? 0x14 : 0x01000800, ["engineTargetBaseFormId"] = target ? 7 : 0x01000801,
        ["readback"] = new JsonObject { ["statistic"] = disabled ? "Disabled" : "IsInCombat", ["status"] = "observed",
            ["value"] = disabled ? 1 : 0, ["reason"] = null }, ["error"] = null
    };

    private sealed class ReplyStream(Func<RuntimeFrame, Func<JsonObject, Task>, Action, Task> respond) : Stream
    {
        private readonly Channel<byte> _incoming = Channel.CreateUnbounded<byte>();
        private readonly MemoryStream _outgoing = new();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0 || !await _incoming.Reader.WaitToReadAsync(cancellationToken)) return 0;
            var count = 0;
            while (count < buffer.Length && _incoming.Reader.TryRead(out var value)) buffer.Span[count++] = value;
            return count;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _outgoing.WriteAsync(buffer, cancellationToken);
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            using var input = new MemoryStream(_outgoing.ToArray());
            _outgoing.SetLength(0);
            var frame = await RuntimeProtocol.ReadAsync(input, cancellationToken) ?? throw new IOException();
            await respond(frame, async row =>
            {
                using var output = new MemoryStream();
                await RuntimeProtocol.WriteAsync(output, new(RuntimeRequestKind.Event, row["requestId"]!.GetValue<ulong>(), row.ToJsonString()), cancellationToken);
                foreach (var value in output.ToArray()) await _incoming.Writer.WriteAsync(value, cancellationToken);
            }, () => _incoming.Writer.TryComplete());
        }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _incoming.Writer.TryComplete(); _outgoing.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
