using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>One reader and one connection for the lifetime of a live engine session.</summary>
public sealed class RuntimeLiveSession : IAsyncDisposable
{
    private const int RetainedJobs = 512;
    private readonly RuntimeConnection _connection;
    private readonly RuntimeLiveLog? _log;
    private readonly SemaphoreSlim _routing = new(1, 1);
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly RuntimeLiveIdentity _identityReader = new();
    private IdentitySnapshot _identity;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<ulong, TaskCompletionSource<JsonElement>> _requests = [];
    private readonly Dictionary<ulong, string?> _operations = [];
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Task _reader;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _disconnected;
    private int _disposed;
    private sealed record IdentitySnapshot(JsonElement Value);

    private sealed class Job(JsonElement latest)
    {
        public JsonElement Latest = latest;
        public TaskCompletionSource<JsonElement> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public RuntimeLiveSession(RuntimeConnection connection, RuntimeLiveLog? log = null)
    {
        if (!connection.Identity.TryGetProperty("capabilities", out var capabilities) ||
            !capabilities.TryGetProperty("liveControl", out var live) || live.ValueKind != JsonValueKind.True ||
            !capabilities.TryGetProperty("liveProtocolVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)
            throw new InvalidOperationException("This runtime backend does not support live control. Install a compatible PC bridge.");
        _connection = connection;
        _identity = new(connection.Identity);
        _log = log;
        _log?.Write("identity", connection.Identity);
        _reader = ReadLoopAsync();
    }

    public Task Completion => _completion.Task;
    public JsonElement Identity => Volatile.Read(ref _identity).Value;
    public string TerminationStatus { get; private set; } = "connected";
    public string? TerminationReason { get; private set; }
    public bool ProtocolFailed { get; private set; }

    public async Task<JsonElement> SendAsync(JsonElement request, CancellationToken token = default)
    {
        request = RuntimeLiveRequest.Parse(request.GetRawText());
        var operation = request.GetProperty("op").GetString();
        if (operation == "session.status") return await StatusAsync(token);
        if (operation == "session.refresh") return await RefreshIdentityAsync(token);
        if (operation == "jobs") return await JobsAsync(token);
        if (operation == "job.status" && request.TryGetProperty("jobId", out var id) && id.ValueKind == JsonValueKind.String)
        {
            await _routing.WaitAsync(token);
            try { if (_jobs.TryGetValue(id.GetString()!, out var job) && job.Terminal.Task.IsCompleted) return job.Latest; }
            finally { _routing.Release(); }
        }
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong requestId = 0;
        await _routing.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_disconnected is not null) throw new IOException("The engine connection is unavailable.", _disconnected);
            token.ThrowIfCancellationRequested();
            // Registration and dispatch share this gate: even an immediate native response cannot outrun registration.
            requestId = await _connection.SendAsync(RuntimeLiveRequest.IsPriority(request)
                ? RuntimeRequestKind.LiveStop : RuntimeRequestKind.Live, request.GetRawText(), _lifetime.Token);
            if (_log is not null)
            {
                var logged = JsonNode.Parse(request.GetRawText())!.AsObject();
                logged["requestId"] = requestId;
                _log.Write("request", Element(logged));
            }
            _requests.Add(requestId, pending);
            _operations.Add(requestId, operation);
        }
        finally { _routing.Release(); }
        try { return await pending.Task.WaitAsync(TimeSpan.FromSeconds(35), token); }
        finally
        {
            await _routing.WaitAsync(CancellationToken.None);
            try { _requests.Remove(requestId); _operations.Remove(requestId); }
            finally { _routing.Release(); }
        }
    }

    private async Task<JsonElement> RefreshIdentityAsync(CancellationToken token)
    {
        await _refresh.WaitAsync(token);
        try
        {
            var identity = await _identityReader.RefreshAsync(Identity, SendAsync, token);
            Interlocked.Exchange(ref _identity, new(identity));
            _log?.Write("identity", identity);
            return Element(new JsonObject { ["kind"] = "live-identity", ["status"] = "completed",
                ["identity"] = JsonNode.Parse(identity.GetRawText()) });
        }
        finally { _refresh.Release(); }
    }

    public async Task<JsonElement> WaitForJobAsync(string jobId, CancellationToken token = default)
    {
        Task<JsonElement> terminal;
        await _routing.WaitAsync(token);
        try
        {
            if (!_jobs.TryGetValue(jobId, out var job))
                throw new InvalidOperationException("Unknown or expired live job: " + jobId);
            terminal = job.Terminal.Task;
        }
        finally { _routing.Release(); }
        return await terminal.WaitAsync(token);
    }

    public async IAsyncEnumerable<JsonElement> WatchJobAsync(string jobId,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        Job job;
        await _routing.WaitAsync(token);
        try
        {
            if (!_jobs.TryGetValue(jobId, out job!))
                throw new InvalidOperationException("Unknown or expired live job: " + jobId);
        }
        finally { _routing.Release(); }
        while (true)
        {
            JsonElement latest;
            Task changed;
            await _routing.WaitAsync(token);
            try { latest = job.Latest; changed = job.Changed.Task; }
            finally { _routing.Release(); }
            yield return latest;
            if (RuntimeLiveRequest.IsTerminal(latest)) yield break;
            await changed.WaitAsync(token);
        }
    }

    private async Task<JsonElement> StatusAsync(CancellationToken token)
    {
        await _routing.WaitAsync(token);
        try
        {
            return Element(new JsonObject
            {
                ["kind"] = "live-session", ["status"] = _disconnected is null ? "connected" : "disconnected",
                ["pendingRequests"] = _requests.Count, ["retainedJobs"] = _jobs.Count,
                ["loggingFull"] = _log?.Full ?? false, ["identity"] = JsonNode.Parse(Identity.GetRawText())
            });
        }
        finally { _routing.Release(); }
    }

    private async Task<JsonElement> JobsAsync(CancellationToken token)
    {
        await _routing.WaitAsync(token);
        try
        {
            var jobs = new JsonArray();
            foreach (var pair in _jobs)
                jobs.Add(new JsonObject { ["jobId"] = pair.Key,
                    ["status"] = pair.Value.Latest.TryGetProperty("status", out var status) ? status.GetString() : "unknown" });
            return Element(new JsonObject { ["kind"] = "live-jobs", ["status"] = "completed", ["jobs"] = jobs });
        }
        finally { _routing.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (await _connection.ReadAsync(_lifetime.Token) is { } response)
            {
                await _routing.WaitAsync(_lifetime.Token);
                try
                {
                    var kind = response.GetProperty("kind").GetString();
                    // Live sessions do not start a trace or persist unrelated engine telemetry.
                    if (kind is not ("live-result" or "live-job" or "error")) continue;
                    if (_operations.TryGetValue(response.GetProperty("requestId").GetUInt64(), out var operation) &&
                        operation == "job.status" && response.TryGetProperty("status", out var outcome) &&
                        outcome.ValueKind == JsonValueKind.String && outcome.GetString() == "completed" &&
                        response.TryGetProperty("job", out var nested) && nested.ValueKind == JsonValueKind.Object)
                    {
                        var normalized = JsonNode.Parse(response.GetRawText())!.AsObject();
                        normalized.Remove("job");
                        foreach (var field in nested.EnumerateObject())
                            normalized[field.Name] = JsonNode.Parse(field.Value.GetRawText());
                        response = Element(normalized);
                    }
                    _log?.Write("response", response);
                    if (response.TryGetProperty("jobId", out var id) && id.ValueKind == JsonValueKind.String &&
                        id.GetString() is { Length: > 0 } jobId)
                    {
                        if (!_jobs.TryGetValue(jobId, out var job)) _jobs.Add(jobId, job = new Job(response));
                        job.Latest = response;
                        job.Changed.TrySetResult();
                        job.Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        if (RuntimeLiveRequest.IsTerminal(response)) job.Terminal.TrySetResult(response);
                        while (_jobs.Count > RetainedJobs)
                        {
                            var oldest = _jobs.FirstOrDefault(pair => pair.Value.Terminal.Task.IsCompleted);
                            if (oldest.Key is null) break;
                            _jobs.Remove(oldest.Key);
                        }
                    }
                    if (_requests.TryGetValue(response.GetProperty("requestId").GetUInt64(), out var pending))
                        pending.TrySetResult(response);
                }
                finally { _routing.Release(); }
            }
            failure = new IOException("Runtime bridge disconnected.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ObjectDisposedException)
        {
            failure = ex;
        }
        finally
        {
            await _routing.WaitAsync(CancellationToken.None);
            try
            {
                _disconnected = failure ?? new IOException("Live session closed.");
                ProtocolFailed = failure is InvalidDataException or JsonException ||
                    failure is InvalidOperationException and not ObjectDisposedException;
                TerminationStatus = ProtocolFailed ? "failed" : failure is null ? "cancelled" : "disconnected";
                TerminationReason = failure?.Message;
                foreach (var pending in _requests.Values) pending.TrySetException(_disconnected);
                foreach (var pair in _jobs)
                {
                    if (pair.Value.Terminal.Task.IsCompleted) continue;
                    var result = JsonNode.Parse(pair.Value.Latest.GetRawText())!.AsObject();
                    result["status"] = "disconnected";
                    result["error"] = _disconnected.Message;
                    pair.Value.Latest = Element(result);
                    pair.Value.Terminal.TrySetResult(pair.Value.Latest);
                    pair.Value.Changed.TrySetResult();
                }
                try
                {
                    _log?.Write("session-end", Element(new JsonObject
                    {
                        ["status"] = TerminationStatus, ["reason"] = TerminationReason,
                        ["protocolFailure"] = ProtocolFailed,
                        ["exceptionType"] = failure?.GetType().FullName,
                        ["detail"] = failure?.InnerException?.Message,
                        ["unfinishedJobs"] = new JsonArray(_jobs.Where(pair =>
                                pair.Value.Latest.TryGetProperty("status", out var status) && status.GetString() == "disconnected")
                            .Select(pair => (JsonNode?)JsonValue.Create(pair.Key)).ToArray())
                    }));
                }
                catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException)
                {
                    TerminationReason += " Log unavailable: " + logFailure.Message;
                }
            }
            finally { _routing.Release(); }
            // Completion is a lifetime signal; request/job results carry the disconnect error.
            _completion.TrySetResult();
        }
    }

    private static JsonElement Element(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (!_reader.IsCompleted)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _connection.SendAsync(RuntimeRequestKind.LiveStop, "{\"op\":\"job.stop\"}", deadline.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        finally
        {
            await _lifetime.CancelAsync();
            await _reader;
            _lifetime.Dispose();
            // Keep the routing semaphore alive for an in-flight sender's finally block.
        }
    }
}
