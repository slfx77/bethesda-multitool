using System.Text.Json;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeOwnerConditionActionTests
{
    private static RuntimeAction Pilot => new("read-owner-conditions", Target: "player",
        Plugin: "BMTConditionControl.esp", FormId: "800", OtherTarget: "player");

    [Theory]
    [InlineData(false, "@player\t000014")]
    [InlineData(true, "FalloutNV.esm\t104C0F")]
    public async Task Pilot_identities_use_request22_and_the_list_terminal(bool doc, string target)
    {
        var action = Pilot with { Plugin = "bmtconditioncontrol.ESP", FormId = "0x00000800" };
        if (doc) action = action with { OtherTarget = null, OtherPlugin = "falloutnv.ESM", OtherFormId = "0x00104c0f" };
        var expected = new RuntimeFrame(RuntimeRequestKind.OwnerConditions, 17,
            "quest-conditions-v1\tBMTConditionControl.esp\t000800\t@player\t000014\t" + target);
        Assert.Equal((ushort)22, (ushort)expected.Kind);
        Assert.Equal(expected, action.ToFrame(17));
        Assert.Equal("condition-list-exit", RuntimeResponse.Kind(expected.Kind, action.Kind));
        using var identity = JsonDocument.Parse("""{"capabilities":{"ownerConditionListProbe":true,"conditionTrace":false}}""");
        using var wire = new MemoryStream();
        await using var connection = new RuntimeConnection(wire, identity.RootElement.Clone());
        var id = await connection.SendActionAsync(action);
        wire.Position = 0;
        Assert.Equal(expected with { RequestId = id }, await RuntimeProtocol.ReadAsync(wire));
    }

    [Theory]
    [InlineData("owner-plugin")]
    [InlineData("owner-runtime-id")]
    [InlineData("owner-control-character")]
    [InlineData("subject")]
    [InlineData("doc-base")]
    [InlineData("mixed-target")]
    [InlineData("missing-target")]
    [InlineData("other-target")]
    public void Unsupported_or_ambiguous_identities_are_rejected(string fault)
    {
        var action = fault switch
        {
            "owner-plugin" => Pilot with { Plugin = "Other.esp" },
            "owner-runtime-id" => Pilot with { FormId = "01000800" },
            "owner-control-character" => Pilot with { FormId = "800\t" },
            "subject" => Pilot with { Target = "DocMitchell" },
            "doc-base" => Pilot with { OtherTarget = null, OtherPlugin = "FalloutNV.esm", OtherFormId = "104C0C" },
            "mixed-target" => Pilot with { OtherPlugin = "FalloutNV.esm", OtherFormId = "14" },
            "missing-target" => Pilot with { OtherTarget = null },
            _ => Pilot with { OtherTarget = "DocMitchell" }
        };
        Assert.Throws<ArgumentException>(() => action.ToFrame(1));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("value")]
    [InlineData("command")]
    [InlineData("message")]
    [InlineData("timeout")]
    [InlineData("count")]
    public void Unrelated_action_data_cannot_change_the_pilot(string field)
    {
        var action = field switch
        {
            "name" => Pilot with { Name = "GetIsID" },
            "value" => Pilot with { Value = 1d },
            "command" => Pilot with { Command = "player.GetDead" },
            "message" => Pilot with { Message = "Text" },
            "timeout" => Pilot with { TimeoutMilliseconds = 1000 },
            _ => Pilot with { Count = 1 }
        };
        Assert.Throws<ArgumentException>(() => action.ToFrame(1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("\"true\"")]
    public async Task Pilot_capability_is_required_before_direct_capture_or_probe_writes(string? capability)
    {
        var property = capability is null ? "" : ",\"ownerConditionListProbe\":" + capability;
        using var identity = JsonDocument.Parse("{\"capabilities\":{\"conditionTrace\":true" + property + "}}");
        using var wire = new MemoryStream();
        await using var connection = new RuntimeConnection(wire, identity.RootElement.Clone());
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendActionAsync(Pilot));
        foreach (var asProbe in new[] { false, true })
        {
            using var trace = new MemoryStream();
            RuntimeMilestone[] milestones = asProbe
                ? [new("conditions", -1, "condition-list-exit", [new("status", JsonSerializer.SerializeToElement("complete"))], Probe: Pilot)]
                : [];
            await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeCaptureService.CaptureAsync(connection,
                trace, TimeSpan.FromSeconds(1), asProbe ? [] : [Pilot], milestones: milestones));
            Assert.Equal(0, trace.Length);
        }
        Assert.Equal(0, wire.Length);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("unavailable")]
    [InlineData("error")]
    public async Task Capture_import_retains_the_native_outcome_without_promoting_partial_evidence(string status)
    {
        using var incoming = new MemoryStream();
        var terminalKind = status == "error" ? "error" : "condition-list-exit";
        var payload = status == "error" ? ",\"error\":\"owner-condition-live-proof-unavailable\""
            : ",\"status\":\"" + status + "\",\"nativeResult\":true,\"orGroupStatus\":\"unavailable-no-frame-probe\",\"fullConditionTrace\":false";
        await Event("capture-start", 2, 1, "");
        await Event(terminalKind, 3, 2, payload);
        await Event("capture-end", 4, 3, ",\"status\":\"completed\"");
        using var identity = JsonDocument.Parse("""{"sequence":1,"capabilities":{"ownerConditionListProbe":true,"conditionTrace":false}}""");
        using var duplex = new SplitStream(incoming.ToArray());
        await using var connection = new RuntimeConnection(duplex, identity.RootElement.Clone());
        using var trace = new MemoryStream();
        var result = await RuntimeCaptureService.CaptureAsync(connection, trace, TimeSpan.FromSeconds(1), [Pilot]);
        Assert.Equal("completed", result.Status);
        Assert.Equal(status == "error" ? 1 : 0, result.Errors);
        trace.Position = 0;
        var imported = await RuntimeTraceImporter.ReadDocumentAsync(trace);
        var observed = Assert.Single(imported.Events, item => item.Kind == terminalKind);
        using var original = JsonDocument.Parse(imported.ReadSourceLine(observed));
        Assert.False(original.RootElement.TryGetProperty("accepted", out _));
        if (status != "error")
        {
            Assert.Equal(status, original.RootElement.GetProperty("status").GetString());
            Assert.True(original.RootElement.GetProperty("nativeResult").GetBoolean());
            Assert.False(original.RootElement.GetProperty("fullConditionTrace").GetBoolean());
        }
        else Assert.Equal("owner-condition-live-proof-unavailable", original.RootElement.GetProperty("error").GetString());
        var monitor = new RuntimeMilestoneMonitor([new("full", -1, "condition-list-exit",
            [new("status", JsonSerializer.SerializeToElement("complete"))], Probe: Pilot)], 0);
        monitor.Begin(-1, identity.RootElement);
        var pending = Assert.Single(monitor.Active); pending.ProbeRequest = 2;
        monitor.Observe(original.RootElement, false);
        Assert.False(pending.Matched);
        if (status == "error") Assert.Equal("native-error", pending.Failure);
        duplex.Written.Position = 0;
        Assert.Equal(RuntimeRequestKind.Start, (await RuntimeProtocol.ReadAsync(duplex.Written))!.Kind);
        Assert.Equal(RuntimeRequestKind.OwnerConditions, (await RuntimeProtocol.ReadAsync(duplex.Written))!.Kind);

        async Task Event(string kind, ulong sequence, ulong request, string extra)
        {
            var json = "{\"protocol\":1,\"kind\":\"" + kind + "\",\"sequence\":" + sequence +
                ",\"requestId\":" + request + ",\"frame\":10,\"qpc\":100,\"dropped\":0" + extra + "}";
            await RuntimeProtocol.WriteAsync(incoming, new(RuntimeRequestKind.Event, request, json));
        }
    }

    private sealed class SplitStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _input = new(bytes);
        internal MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => Written.WriteAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _input.Dispose(); Written.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
