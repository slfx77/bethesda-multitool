using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.RuntimeSession;

namespace BethesdaMultitool.CLI.Commands.Runtime;

internal static class RuntimeLiveCommand
{
    public static void AddTo(Command root)
    {
        var live = new Command("live", "Maintain one PC engine connection for console, scripts, input and reset");
        var pid = new Option<int>("--pid") { Required = true };
        var session = new Option<string>("--session") { Required = true };
        var log = new Option<string?>("--log") { Description = "New compact NDJSON log (default: artifacts/runtime-live/<session>/<timestamp>.ndjson; maximum 64 MiB)" };
        live.Options.Add(pid); live.Options.Add(session); live.Options.Add(log);
        live.SetAction(async (parse, token) => await Guard(async () =>
        {
            var name = parse.GetValue(session)!;
            _ = RuntimeLiveRequest.PipeName(name);
            Console.Error.WriteLine("Connecting to the PC live bridge.");
            await using var connection = await RuntimeConnection.ConnectAsync(parse.GetValue(pid), token);
            var logPath = parse.GetValue(log) ?? Path.Combine("artifacts", "runtime-live", name,
                DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".ndjson");
            using var evidence = new RuntimeLiveLog(logPath);
            await using var broker = new RuntimeLiveSession(connection, evidence);
            await RuntimeLiveBroker.RunAsync(broker, name, () => Console.WriteLine(new JsonObject
            {
                ["kind"] = "live-session", ["status"] = "ready", ["session"] = name,
                ["processId"] = parse.GetValue(pid), ["log"] = Path.GetFullPath(logPath)
            }.ToJsonString()), token);
            if (broker.TerminationReason is { } reason)
                await Console.Error.WriteLineAsync("Live session " + broker.TerminationStatus + ": " + reason);
            return broker.ProtocolFailed ? 2 : 0;
        }));
        root.Subcommands.Add(live);

        var launch = new Command("live-launch", "Launch an existing live profile with its reusable installation");
        var profile = new Option<string>("--profile") { Required = true };
        var bridge = new Option<string?>("--bridge") { Description = "Stage this bridge DLL before launch; otherwise reuse the pinned bridge" };
        launch.Options.Add(profile); launch.Options.Add(bridge);
        launch.SetAction(async (parse, token) => await Guard(async () =>
        {
            await RuntimeLiveLauncher.LaunchAsync(parse.GetValue(profile)!, parse.GetValue(bridge),
                state => Console.WriteLine(JsonSerializer.Serialize(state, RuntimeJsonContext.Default.RuntimeLiveLaunchState)), token);
            return 0;
        }));
        root.Subcommands.Add(launch);

        var send = new Command("send", "Send JSON to a running live session");
        var destination = new Option<string>("--session") { Required = true };
        var request = new Option<string>("--request") { Required = true, Description = "JSON request file; .gek file paths are relative to this file" };
        var wait = new Option<bool>("--wait") { Description = "Print the initial result and wait for a long job's terminal result" };
        send.Options.Add(destination); send.Options.Add(request); send.Options.Add(wait);
        send.SetAction(async (parse, token) => await Guard(async () =>
        {
            var input = await RuntimeLiveRequest.ReadFileAsync(parse.GetValue(request)!, token);
            var result = await RuntimeLiveBroker.SendAsync(parse.GetValue(destination)!, input, parse.GetValue(wait),
                row => Console.WriteLine(row.GetRawText()), token);
            return result.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String &&
                   status.GetString() is "failed" or "disconnected" or "unavailable" ||
                   result.TryGetProperty("kind", out var kind) && kind.GetString() == "error" ? 2 : 0;
        }));
        root.Subcommands.Add(send);
    }

    private static async Task<int> Guard(Func<Task<int>> action)
    {
        try { return await action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or JsonException or
                                   OperationCanceledException or TimeoutException or UnauthorizedAccessException or NotSupportedException or
                                   System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
