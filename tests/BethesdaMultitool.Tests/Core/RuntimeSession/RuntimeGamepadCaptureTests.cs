using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeGamepadCaptureTests
{
    [Theory]
    [InlineData(RuntimeRequestKind.Stop, false)]
    [InlineData(RuntimeRequestKind.Stop, true)]
    [InlineData(RuntimeRequestKind.Cancel, false)]
    [InlineData(RuntimeRequestKind.Cancel, true)]
    public async Task Late_binding_cannot_reopen_controller_admission_after_stop(RuntimeRequestKind stop, bool boundBeforeStop)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows guest-profile backing identity.");
        using var fixture = new RuntimeGuestRunProfileTests.Fixture();
        var profile = await fixture.Prepare(false);
        using var identity = fixture.Identity(profile);
        using var output = new MemoryStream();
        await using var connection = new RuntimeConnection(output, identity.RootElement.Clone());
        await connection.AttachGuestRunProfileAsync(profile, profile.Scenario.Sha256, TestContext.Current.CancellationToken);
        using var capture = connection.EnterCapture();
        var session = Guid.NewGuid().ToString("N");
        var bound = RuntimeGamepadTraceTests.NativePulse(3, 2, session, profile.ProfileSha256!, 123,
            (ulong)new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()).Bound;
        using var row = JsonDocument.Parse(bound.ToJsonString());
        if (boundBeforeStop) connection.AcceptGuestRunBinding(row.RootElement, session);
        await connection.SendAsync(stop, "", TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => connection.AcceptGuestRunBinding(row.RootElement, session));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendActionAsync(new("gamepad-pulse", Gamepad: new(16))));
        output.Position = 0;
        Assert.Equal(stop, (await RuntimeProtocol.ReadAsync(output))!.Kind);
        Assert.Null(await RuntimeProtocol.ReadAsync(output));
    }

    [Theory]
    [InlineData("observed", true)]
    [InlineData("bind-error", false)]
    [InlineData("wrong-bind", false)]
    [InlineData("missing-release", false)]
    [InlineData("loss", false)]
    [InlineData("cancel", false)]
    [InlineData("queued-only", false)]
    public async Task Only_guest_observed_press_and_release_admit_the_next_action(string mode, bool proceeds)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows guest-profile backing identity.");
        using var fixture = new RuntimeGuestRunProfileTests.Fixture();
        var scenario = new RuntimeScenario([new("gamepad-pulse", Gamepad: new(16, 100, 200)),
            new("read-actor-value", "player", "Health")], 3000);
        var profile = await fixture.Prepare(false, scenario: scenario);
        var identityNode = fixture.IdentityNode(profile);
        identityNode["sequence"] = 1;
        identityNode["capabilities"] = new JsonObject { ["gamepadBinding"] = true };
        using var identity = JsonDocument.Parse(identityNode.ToJsonString());
        using var cancelled = new CancellationTokenSource();
        var sent = new List<RuntimeRequestKind>();
        var session = "";
        ulong sequence = 1;
        using var duplex = new ReplyStream(async (request, reply) =>
        {
            sent.Add(request.Kind);
            if (request.Kind == RuntimeRequestKind.Start)
            {
                session = request.Payload.Split('\n')[0];
                await Emit("capture-start", request.RequestId);
            }
            else if (request.Kind == RuntimeRequestKind.GuestRunBind)
            {
                Assert.Equal($"guest-run-bind/1\n{profile.ProfilePath}\n{profile.ProfileSha256}\n{session}", request.Payload);
                if (mode == "bind-error") await Emit("error", request.RequestId);
                else
                {
                    var bound = RuntimeGamepadTraceTests.NativePulse(3, ++sequence, session, profile.ProfileSha256!, 123,
                        (ulong)new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()).Bound;
                    bound["requestId"] = request.RequestId;
                    if (mode == "wrong-bind") bound["processId"] = 999;
                    await reply(bound);
                }
            }
            else if (request.Kind == RuntimeRequestKind.GamepadPulse)
            {
                Assert.Equal("gamepad-pulse/1\n0\n16\n100\n200\n" + profile.ProfileSha256, request.Payload);
                var rows = RuntimeGamepadTraceTests.NativePulse(request.RequestId, 3, session, profile.ProfileSha256!, 123,
                    (ulong)new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc(),
                    deadlineMilliseconds: 200, firstPulseSequence: sequence + 1).PulseRows;
                if (mode == "loss") rows[0]["dropped"] = 1;
                foreach (var row in rows)
                {
                    if (mode is "queued-only" or "cancel" && row["kind"]!.GetValue<string>() != "gamepad-queued") break;
                    if (mode == "missing-release" && row["phase"]?.GetValue<string>() == "released") continue;
                    sequence = row["sequence"]!.GetValue<ulong>();
                    await reply(row);
                }
                if (mode == "cancel") cancelled.Cancel();
            }
            else if (request.Kind == RuntimeRequestKind.Evaluate)
            {
                await Emit("snapshot", request.RequestId);
                await Emit("capture-end", request.RequestId, "completed");
            }
            else if (request.Kind is RuntimeRequestKind.Stop or RuntimeRequestKind.Cancel)
                await Emit("capture-end", request.RequestId, request.Kind == RuntimeRequestKind.Cancel ? "cancelled" : "completed");

            async Task Emit(string kind, ulong id, string? status = null)
            {
                var row = new JsonObject { ["kind"] = kind, ["protocol"] = 1, ["sequence"] = ++sequence,
                    ["requestId"] = id, ["frame"] = 0, ["dropped"] = 0, ["session"] = session };
                if (status is not null) row["status"] = status;
                await reply(row);
            }
        });
        await using var connection = new RuntimeConnection(duplex, identity.RootElement.Clone());
        await connection.AttachGuestRunProfileAsync(profile, profile.Scenario.Sha256, TestContext.Current.CancellationToken);
        using var trace = new MemoryStream();
        var result = await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromMilliseconds(scenario.ObserveMilliseconds),
            scenario.Actions, cancelled.Token);
        Assert.Equal(proceeds, sent.Contains(RuntimeRequestKind.Evaluate));
        Assert.Equal(mode is not ("bind-error" or "wrong-bind"), sent.Contains(RuntimeRequestKind.GamepadPulse));
        if (!proceeds) Assert.Contains(sent, kind => kind is RuntimeRequestKind.Stop or RuntimeRequestKind.Cancel);
        trace.Position = 0;
        var document = await RuntimeTraceImporter.ReadDocumentAsync(trace, TestContext.Current.CancellationToken);
        if (proceeds)
        {
            Assert.Equal(0, result.Errors);
            Assert.True(document.Summary.Complete);
            Assert.Equal("Observed", Assert.Single(document.Gamepad.Pulses).Status);
        }
        else Assert.DoesNotContain(document.Gamepad.Pulses, pulse => pulse.Status == "Observed");
        await profile.VerifyOriginalsAsync(TestContext.Current.CancellationToken);
    }

    private sealed class ReplyStream(Func<RuntimeFrame, Func<JsonObject, Task>, Task> respond) : Stream
    {
        private readonly Channel<byte> _incoming = Channel.CreateUnbounded<byte>();
        private readonly MemoryStream _outgoing = new();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return 0;
            var first = await _incoming.Reader.ReadAsync(cancellationToken);
            buffer.Span[0] = first;
            var count = 1;
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
            });
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
