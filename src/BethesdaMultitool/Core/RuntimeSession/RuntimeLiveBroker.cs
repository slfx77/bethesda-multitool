using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Current-user-only local endpoint; each client request shares one persistent engine connection.</summary>
public static class RuntimeLiveBroker
{
    public static async Task RunAsync(RuntimeLiveSession session, string name, Action? ready = null,
        CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Live control uses Windows named pipes.");
        var pipeName = RuntimeLiveRequest.PipeName(name);
        var lockDirectory = Path.Combine(Path.GetTempPath(), "BMT.RuntimeLive");
        Directory.CreateDirectory(lockDirectory);
        await using var lease = new FileStream(Path.Combine(lockDirectory, name + ".lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var clients = new ConcurrentDictionary<int, Task>();
        var nextClient = 0;
        var completion = session.Completion.ContinueWith(_ => lifetime.Cancel(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var announced = false;
            while (!lifetime.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
                try
                {
                    if (!announced) { ready?.Invoke(); announced = true; }
                    await pipe.WaitForConnectionAsync(lifetime.Token);
                }
                catch { await pipe.DisposeAsync(); throw; }
                var id = ++nextClient;
                clients[id] = ServeAsync(pipe, session, lifetime.Token);
                // Keep at most the live tasks. No client can monopolize the listener while waiting for a job.
                foreach (var finished in clients.Where(pair => pair.Value.IsCompleted))
                    clients.TryRemove(finished.Key, out _);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(clients.Values);
            // Do not retain a continuation referring to the disposed cancellation source.
            await session.DisposeAsync();
            await completion;
        }
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe, RuntimeLiveSession session, CancellationToken token)
    {
        await using (pipe)
        {
            ulong clientId = 0;
            try
            {
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                readDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                var frame = await RuntimeProtocol.ReadAsync(pipe, readDeadline.Token)
                    ?? throw new IOException("Client disconnected before its request.");
                clientId = frame.RequestId;
                if (frame.Kind != RuntimeRequestKind.Live) throw new InvalidDataException("Expected a live request.");
                using var envelope = JsonDocument.Parse(frame.Payload);
                var request = RuntimeLiveRequest.Parse(envelope.RootElement.GetProperty("request").GetRawText());
                var wait = envelope.RootElement.TryGetProperty("wait", out var waiting) && waiting.ValueKind == JsonValueKind.True;
                var response = await session.SendAsync(request, token);
                await WriteAsync(response);
                if (wait && response.TryGetProperty("status", out var status) && status.GetString() == "running" &&
                    response.TryGetProperty("jobId", out var jobId) && jobId.GetString() is { } job)
                {
                    var previous = response.GetRawText();
                    await foreach (var update in session.WatchJobAsync(job, token))
                    {
                        if (update.GetRawText() == previous) continue;
                        await WriteAsync(update);
                        previous = update.GetRawText();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
                                       JsonException or KeyNotFoundException or TimeoutException or OperationCanceledException)
            {
                if (token.IsCancellationRequested) return;
                try { await WriteAsync(RuntimeLiveRequest.Result("failed", ex.Message)); }
                catch (Exception writeFailure) when (writeFailure is IOException or OperationCanceledException) { }
            }

            async Task WriteAsync(JsonElement value)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                await RuntimeProtocol.WriteAsync(pipe, new(RuntimeRequestKind.Event, clientId, value.GetRawText()), deadline.Token);
            }
        }
    }

    public static async Task<JsonElement> SendAsync(string name, JsonElement request, bool wait = false,
        Action<JsonElement>? received = null, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Live control uses Windows named pipes.");
        await using var pipe = new NamedPipeClientStream(".", RuntimeLiveRequest.PipeName(name), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000, token);
        var envelope = new JsonObject { ["request"] = JsonNode.Parse(request.GetRawText()), ["wait"] = wait };
        await RuntimeProtocol.WriteAsync(pipe, new(RuntimeRequestKind.Live, 1, envelope.ToJsonString()), token);
        JsonElement result = default;
        while (await RuntimeProtocol.ReadAsync(pipe, token) is { } frame)
        {
            if (frame.Kind != RuntimeRequestKind.Event || frame.RequestId != 1)
                throw new InvalidDataException("Live broker response identity mismatch.");
            using var json = JsonDocument.Parse(frame.Payload);
            result = json.RootElement.Clone();
            received?.Invoke(result);
            if (!wait || RuntimeLiveRequest.IsTerminal(result) ||
                !result.TryGetProperty("status", out var status) || status.GetString() != "running") break;
        }
        if (result.ValueKind == JsonValueKind.Undefined) throw new IOException("Live broker disconnected without a result.");
        if (wait && result.TryGetProperty("status", out var finalStatus) && finalStatus.GetString() == "running")
            throw new IOException("Live broker disconnected before the job completed.");
        return result;
    }
}
