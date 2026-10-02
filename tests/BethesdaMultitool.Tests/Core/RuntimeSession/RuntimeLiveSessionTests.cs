using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeLiveSessionTests
{
    private static JsonElement Request(string json) => RuntimeLiveRequest.Parse(json);

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"capabilities\":{\"liveControl\":false}}")]
    [InlineData("{\"capabilities\":{\"liveControl\":true}}")]
    [InlineData("{\"capabilities\":{\"liveControl\":true,\"liveProtocolVersion\":2}}")]
    [InlineData("{\"capabilities\":{\"liveControl\":true,\"liveProtocolVersion\":\"1\"}}")]
    public async Task Old_backends_are_rejected_without_sending_frames(string identity)
    {
        using var json = JsonDocument.Parse(identity);
        await using var wire = new EngineStream();
        await using var connection = new RuntimeConnection(wire, json.RootElement.Clone());
        Assert.Throws<InvalidOperationException>(() => new RuntimeLiveSession(connection));
        Assert.False(wire.Requests.TryRead(out _));
    }

    [Fact]
    public async Task Concurrent_responses_are_routed_by_request_id_without_starting_capture()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        await using var engine = new EngineStream();
        await using var connection = Connection(engine);
        var path = Path.Combine(Path.GetTempPath(), "bmt-live-routing-" + Guid.NewGuid().ToString("N") + ".ndjson");
        try
        {
            using var log = new RuntimeLiveLog(path);
            await using var session = new RuntimeLiveSession(connection, log);
            var first = session.SendAsync(Request("""{"op":"eval","expression":"1","requestId":999}"""), token);
            var second = session.SendAsync(Request("""{"op":"eval","expression":"2"}"""), token);
            var a = await engine.Requests.ReadAsync(token);
            var b = await engine.Requests.ReadAsync(token);
            Assert.Equal(RuntimeRequestKind.Live, a.Kind);
            Assert.Equal(RuntimeRequestKind.Live, b.Kind);
            await engine.ReplyAsync(b.RequestId, "live-result", "completed", value: 2);
            await engine.ReplyAsync(a.RequestId, "live-result", "completed", value: 1);
            Assert.Equal(1, (await first).GetProperty("value").GetInt32());
            Assert.Equal(2, (await second).GetProperty("value").GetInt32());
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var text = new StreamReader(input);
            var rows = (await text.ReadToEndAsync(token)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(row => JsonNode.Parse(row)!.AsObject()).ToArray();
            var requests = rows.Where(row => row["direction"]!.GetValue<string>() == "request")
                .ToDictionary(row => row["data"]!["requestId"]!.GetValue<ulong>(), row => row["data"]!["expression"]!.GetValue<string>());
            Assert.Equal("1", requests[a.RequestId]);
            Assert.Equal("2", requests[b.RequestId]);
            foreach (var row in rows.Where(row => row["direction"]!.GetValue<string>() == "response"))
                Assert.Equal(requests[row["data"]!["requestId"]!.GetValue<ulong>()], row["data"]!["value"]!.GetValue<int>().ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Long_job_returns_immediately_and_allows_reads_and_priority_stop()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        await using var engine = new EngineStream();
        await using var connection = Connection(engine);
        await using var session = new RuntimeLiveSession(connection);
        var run = session.SendAsync(Request("""{"op":"script.run","name":"probe","intervalMs":50}"""), token);
        var runFrame = await engine.Requests.ReadAsync(token);
        await engine.ReplyAsync(runFrame.RequestId, "live-result", "running", "9");
        Assert.Equal("9", (await run).GetProperty("jobId").GetString());
        var terminal = session.WaitForJobAsync("9", token);
        Assert.False(terminal.IsCompleted);
        var read = session.SendAsync(Request("""{"op":"snapshot","fields":["player.position"]}"""), token);
        var readFrame = await engine.Requests.ReadAsync(token);
        await engine.ReplyAsync(readFrame.RequestId, "live-result", "completed");
        Assert.Equal("completed", (await read).GetProperty("status").GetString());
        var stop = session.SendAsync(Request("""{"op":"job.stop","jobId":"9"}"""), token);
        var stopFrame = await engine.Requests.ReadAsync(token);
        Assert.Equal(RuntimeRequestKind.LiveStop, stopFrame.Kind);
        await engine.ReplyAsync(runFrame.RequestId, "live-job", "cancelled", "9");
        await engine.ReplyAsync(stopFrame.RequestId, "live-result", "completed");
        await stop;
        Assert.Equal("cancelled", (await terminal).GetProperty("status").GetString());
        var retained = await session.SendAsync(Request("""{"op":"job.status","jobId":"9"}"""), token);
        Assert.Equal("cancelled", retained.GetProperty("status").GetString());
        Assert.False(engine.Requests.TryRead(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnect_finishes_pending_requests_and_running_jobs(bool malformed)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        await using var engine = new EngineStream();
        await using var connection = Connection(engine);
        await using var session = new RuntimeLiveSession(connection);
        var run = session.SendAsync(Request("""{"op":"sequence.run","sequence":{"version":1,"steps":[]}}"""), token);
        var runFrame = await engine.Requests.ReadAsync(token);
        await engine.ReplyAsync(runFrame.RequestId, "live-result", "running", "12");
        await run;
        var pending = session.SendAsync(Request("""{"op":"eval","expression":"1"}"""), token);
        var pendingFrame = await engine.Requests.ReadAsync(token);
        if (malformed) await engine.RawReplyAsync(new(RuntimeRequestKind.Event, pendingFrame.RequestId, "{}"));
        else engine.Disconnect();
        if (malformed) await Assert.ThrowsAsync<InvalidDataException>(() => pending);
        else await Assert.ThrowsAsync<IOException>(() => pending);
        var terminal = await session.WaitForJobAsync("12", token);
        Assert.Equal("disconnected", terminal.GetProperty("status").GetString());
        await session.Completion.WaitAsync(token);
        Assert.Equal(malformed, session.ProtocolFailed);
        Assert.Equal(malformed ? "failed" : "disconnected", session.TerminationStatus);
        Assert.NotNull(session.TerminationReason);
    }

    [Fact]
    public async Task Malformed_event_records_durable_session_failure_without_claiming_job_completion()
    {
        var path = Path.Combine(Path.GetTempPath(), "bmt-live-failure-" + Guid.NewGuid().ToString("N") + ".ndjson");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var log = new RuntimeLiveLog(path);
            await using var engine = new EngineStream();
            await using var connection = Connection(engine);
            await using var session = new RuntimeLiveSession(connection, log);
            var run = session.SendAsync(Request("""{"op":"script.run","name":"probe"}"""), deadline.Token);
            var frame = await engine.Requests.ReadAsync(deadline.Token);
            await engine.ReplyAsync(frame.RequestId, "live-result", "running", "23");
            await run;
            await engine.RawReplyAsync(new(RuntimeRequestKind.Event, frame.RequestId,
                "{\"protocol\":1,\"requestId\":" + frame.RequestId + ",\"kind\":\"live-job\",\"sequence\":{},\"dropped\":0,\"status\":\"completed\",\"jobId\":\"23\"}"));
            await session.Completion.WaitAsync(deadline.Token);
            Assert.True(session.ProtocolFailed);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var text = new StreamReader(input);
            var lines = (await text.ReadToEndAsync(deadline.Token)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            using var terminal = JsonDocument.Parse(lines[^1]);
            Assert.Equal("session-end", terminal.RootElement.GetProperty("direction").GetString());
            var evidence = terminal.RootElement.GetProperty("data");
            Assert.Equal("failed", evidence.GetProperty("status").GetString());
            Assert.Contains("Malformed runtime event", evidence.GetProperty("reason").GetString(), StringComparison.Ordinal);
            Assert.Equal("23", evidence.GetProperty("unfinishedJobs")[0].GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Cancelling_one_wait_does_not_abandon_the_shared_reader_or_cancel_another_request()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        using var cancelled = new CancellationTokenSource();
        await using var engine = new EngineStream();
        await using var connection = Connection(engine);
        await using var session = new RuntimeLiveSession(connection);
        var abandoned = session.SendAsync(Request("""{"op":"eval","expression":"1"}"""), cancelled.Token);
        var abandonedFrame = await engine.Requests.ReadAsync(token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        var active = session.SendAsync(Request("""{"op":"eval","expression":"2"}"""), token);
        var activeFrame = await engine.Requests.ReadAsync(token);
        await engine.ReplyAsync(abandonedFrame.RequestId, "live-result", "completed", value: 1);
        await engine.ReplyAsync(activeFrame.RequestId, "live-result", "completed", value: 2);
        Assert.Equal(2, (await active).GetProperty("value").GetInt32());
        Assert.False(engine.Requests.TryRead(out _));
    }

    [Fact]
    public async Task Running_status_is_refreshed_from_engine_and_nested_job_is_normalized()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        await using var engine = new EngineStream();
        await using var connection = Connection(engine);
        await using var session = new RuntimeLiveSession(connection);
        var run = session.SendAsync(Request("""{"op":"script.run","name":"probe","intervalMs":100}"""), token);
        var runFrame = await engine.Requests.ReadAsync(token);
        await engine.ReplyAsync(runFrame.RequestId, "live-result", "running", "7");
        await run;
        var status = session.SendAsync(Request("""{"op":"job.status","jobId":"7"}"""), token);
        var statusFrame = await engine.Requests.ReadAsync(token);
        await engine.RawReplyAsync(new(RuntimeRequestKind.Event, statusFrame.RequestId,
            "{\"protocol\":1,\"requestId\":" + statusFrame.RequestId + ",\"sequence\":2,\"dropped\":0,\"kind\":\"live-result\",\"status\":\"completed\",\"job\":{\"jobId\":\"7\",\"status\":\"running\",\"calls\":9,\"revision\":2}}"));
        var updated = await status;
        Assert.Equal("running", updated.GetProperty("status").GetString());
        Assert.Equal(9, updated.GetProperty("calls").GetInt32());
        Assert.Equal(2, updated.GetProperty("revision").GetInt32());
        Assert.False(updated.TryGetProperty("job", out _));
        Assert.False(session.WaitForJobAsync("7", token).IsCompleted);
    }

    [Theory]
    [InlineData("status", "completed", false)]
    [InlineData("script.status", "completed", false)]
    [InlineData("job.status", "completed", true)]
    [InlineData("job.status", "failed", false)]
    [InlineData("sequence.run", "failed", false)]
    public async Task Only_successful_job_status_requests_flatten_nested_job_state(string operation, string outcome, bool flatten)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        await using var engine = new EngineStream();
        await using var connection = Connection(engine);
        await using var session = new RuntimeLiveSession(connection);
        var request = session.SendAsync(Request("{\"op\":\"" + operation + "\",\"jobId\":\"7\"}"), token);
        var frame = await engine.Requests.ReadAsync(token);
        await engine.RawReplyAsync(new(RuntimeRequestKind.Event, frame.RequestId,
            "{\"protocol\":1,\"requestId\":" + frame.RequestId + ",\"sequence\":1,\"dropped\":0,\"kind\":\"live-result\",\"status\":\"" + outcome + "\",\"job\":{\"jobId\":\"7\",\"status\":\"running\",\"calls\":3},\"scripts\":[]}"));
        var response = await request;
        Assert.Equal(flatten ? "running" : outcome, response.GetProperty("status").GetString());
        Assert.Equal(!flatten, response.TryGetProperty("job", out _));
        Assert.True(response.TryGetProperty("scripts", out _));
    }

    [Fact]
    public async Task Broker_keeps_accepting_clients_while_another_client_waits_for_completion()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var shutdown = new CancellationTokenSource();
        var token = deadline.Token;
        await using var engine = new EngineStream();
        await using var connection = Connection(engine);
        await using var session = new RuntimeLiveSession(connection);
        var name = "test_" + Guid.NewGuid().ToString("N");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = RuntimeLiveBroker.RunAsync(session, name, () => ready.SetResult(), shutdown.Token);
        await ready.Task.WaitAsync(token);
        var progress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = RuntimeLiveBroker.SendAsync(name, Request("""{"op":"script.run","name":"probe"}"""), true,
            row => { if (row.TryGetProperty("value", out var value) && value.GetInt32() == 1) progress.TrySetResult(); }, token);
        var run = await engine.Requests.ReadAsync(token);
        await engine.ReplyAsync(run.RequestId, "live-result", "running", "5");
        await engine.ReplyAsync(run.RequestId, "live-job", "running", "5", 1);
        await progress.Task.WaitAsync(token);
        var stop = RuntimeLiveBroker.SendAsync(name, Request("""{"op":"job.stop","jobId":"5"}"""), token: token);
        var stopFrame = await engine.Requests.ReadAsync(token);
        Assert.Equal(RuntimeRequestKind.LiveStop, stopFrame.Kind);
        await engine.ReplyAsync(run.RequestId, "live-job", "cancelled", "5");
        await engine.ReplyAsync(stopFrame.RequestId, "live-result", "completed");
        Assert.Equal("completed", (await stop).GetProperty("status").GetString());
        Assert.Equal("cancelled", (await waiting).GetProperty("status").GetString());
        await shutdown.CancelAsync();
        await broker.WaitAsync(token);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("scriptFile")]
    public async Task Script_files_are_read_relative_to_request_and_do_not_change_source(string property)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bmt-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string source = "scn Probe\nbegin Function {}\nSetFunctionValue 7\nend";
            var path = Path.Combine(directory, "request.json");
            await File.WriteAllTextAsync(Path.Combine(directory, "probe.gek"), source, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(path, "{\"op\":\"script.load\",\"name\":\"probe\",\"" + property + "\":\"probe.gek\"}", TestContext.Current.CancellationToken);
            var request = await RuntimeLiveRequest.ReadFileAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(source, request.GetProperty("source").GetString());
            Assert.False(request.TryGetProperty(property, out _));
            Assert.Equal(source, await File.ReadAllTextAsync(Path.Combine(directory, "probe.gek"), TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("sequence")]
    [InlineData("steps")]
    public void Compact_log_hashes_large_request_fields_and_stops_at_its_size_bound(string field)
    {
        var path = Path.Combine(Path.GetTempPath(), "bmt-live-log-" + Guid.NewGuid().ToString("N") + ".ndjson");
        try
        {
            using (var log = new RuntimeLiveLog(path, 1024))
            {
                var request = Request(field switch
                {
                    "source" => """{"op":"script.load","name":"probe","source":"private script text"}""",
                    "sequence" => """{"op":"sequence.run","sequence":{"version":1,"steps":[{"type":"wait","durationMs":100}]}}""",
                    _ => """{"op":"sequence.run","version":1,"steps":[{"type":"wait","durationMs":100}]}"""
                });
                for (var i = 0; i < 100; ++i) log.Write("request", request);
                Assert.True(log.Full);
            }
            Assert.InRange(new FileInfo(path).Length, 1, 1024);
            foreach (var line in File.ReadLines(path))
            {
                using var row = JsonDocument.Parse(line);
                var data = row.RootElement.GetProperty("data");
                Assert.False(data.TryGetProperty(field, out _));
                Assert.Equal(64, data.GetProperty(field + "Sha256").GetString()!.Length);
                Assert.Equal(field == "source" ? "script.load" : "sequence.run", data.GetProperty("op").GetString());
                if (field == "steps") Assert.Equal(1, data.GetProperty("version").GetInt32());
            }
        }
        finally { File.Delete(path); }
    }

    private static RuntimeConnection Connection(Stream stream)
    {
        using var identity = JsonDocument.Parse("""{"protocol":1,"capabilities":{"liveControl":true,"liveProtocolVersion":1}}""");
        return new RuntimeConnection(stream, identity.RootElement.Clone());
    }

    private sealed class EngineStream : Stream
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<RuntimeFrame> _requests = Channel.CreateUnbounded<RuntimeFrame>();
        private readonly MemoryStream _written = new();
        private MemoryStream? _current;
        private ulong _sequence;
        public ChannelReader<RuntimeFrame> Requests => _requests.Reader;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public void Disconnect() => _responses.Writer.TryComplete();
        public async Task ReplyAsync(ulong id, string kind, string status, string? jobId = null, int? value = null)
        {
            var row = new JsonObject
            {
                ["protocol"] = 1, ["requestId"] = id, ["sequence"] = ++_sequence, ["dropped"] = 0,
                ["kind"] = kind, ["status"] = status
            };
            if (jobId is not null) row["jobId"] = jobId;
            if (value is not null) row["value"] = value.Value;
            await RawReplyAsync(new(RuntimeRequestKind.Event, id, row.ToJsonString()));
        }
        public async Task RawReplyAsync(RuntimeFrame frame)
        {
            using var bytes = new MemoryStream();
            await RuntimeProtocol.WriteAsync(bytes, frame);
            await _responses.Writer.WriteAsync(bytes.ToArray());
        }
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
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => _written.WriteAsync(buffer, token);
        public override async Task FlushAsync(CancellationToken token)
        {
            _written.Position = 0;
            var frame = await RuntimeProtocol.ReadAsync(_written, token);
            await _requests.Writer.WriteAsync(frame!, token);
            _written.SetLength(0);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _current?.Dispose(); _written.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
