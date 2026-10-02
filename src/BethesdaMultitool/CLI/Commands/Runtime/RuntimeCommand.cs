using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using BethesdaMultitool.Core.RuntimeSession;

namespace BethesdaMultitool.CLI.Commands.Runtime;

public static class RuntimeCommand
{
    public static Command Create()
    {
        var root = new Command("runtime", "Capture actual game behavior through a local runtime bridge");
        foreach (var name in new[] { "connect", "capabilities" })
        {
            var command = new Command(name, "Identify a running runtime bridge and its available capabilities");
            var pid = new Option<int>("--pid") { Required = true, Description = "Engine or emulator process ID" };
            command.Options.Add(pid);
            command.SetAction(async (parse, token) => await Guard(async () =>
            {
                await using var connection = await RuntimeConnection.ConnectAsync(parse.GetValue(pid), token);
                Console.WriteLine(connection.Identity.GetRawText());
                return 0;
            }));
            root.Subcommands.Add(command);
        }
        root.Subcommands.Add(CaptureCommand(false));
        root.Subcommands.Add(CaptureCommand(true));
        RuntimeLiveCommand.AddTo(root);

        var import = new Command("import", "Validate a runtime trace without changing its observations");
        var trace = new Argument<string>("trace");
        var scriptCalls = new Option<string?>("--script-calls") { Description = "Write observed script-call validation to a new JSON file" };
        var scriptMap = new Option<string?>("--script-map") { Description = "Write script source evidence to a new JSON file" };
        var conditionMap = new Option<string?>("--condition-map") { Description = "Write QUST condition source evidence to a new JSON file" };
        var commands = new Option<string?>("--commands") { Description = "Write command results and script-call attribution to a new JSON file" };
        var gamepad = new Option<string?>("--gamepad") { Description = "Write guest-observed controller receipts to a new JSON file" };
        var sourcePlugins = new Option<string[]>("--source-plugin")
        {
            Description = "Complete plugin list in captured load order (required with --script-map or --condition-map)",
            AllowMultipleArgumentsPerToken = true
        };
        import.Arguments.Add(trace);
        import.Options.Add(scriptCalls);
        import.Options.Add(scriptMap);
        import.Options.Add(conditionMap);
        import.Options.Add(commands);
        import.Options.Add(gamepad);
        import.Options.Add(sourcePlugins);
        import.SetAction(async (parse, token) => await Guard(async () =>
        {
            var callsPath = parse.GetValue(scriptCalls);
            var mapPath = parse.GetValue(scriptMap);
            var conditionPath = parse.GetValue(conditionMap);
            var commandsPath = parse.GetValue(commands);
            var gamepadPath = parse.GetValue(gamepad);
            var sources = parse.GetValue(sourcePlugins) ?? [];
            if ((mapPath is null && conditionPath is null) != (sources.Length == 0))
                throw new ArgumentException("Use --script-map or --condition-map with the complete ordered --source-plugin list.");
            var outputs = new[] { callsPath, mapPath, conditionPath, commandsPath, gamepadPath }.Where(path => path is not null)
                .Select(path => Path.GetFullPath(path!)).ToArray();
            if (outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length)
                throw new ArgumentException("Report output paths must be distinct.");
            foreach (var path in outputs)
                if (File.Exists(path) || Directory.Exists(path))
                    throw new IOException($"Report output already exists: {path}");
            await using var input = File.OpenRead(parse.GetValue(trace)!);
            var document = await RuntimeTraceImporter.ReadDocumentAsync(input, token);
            var map = mapPath is not null ? await RuntimeScriptSourceMap.BuildAsync(document, sources, token) : null;
            var conditionReport = conditionPath is not null ? await RuntimeQuestConditionSourceMap.BuildAsync(document, sources, token) : null;
            var commandReport = commandsPath is not null ? RuntimeCommandTrace.Build(document, token) : null;
            if (callsPath is not null)
                await WriteNewReport(callsPath, JsonSerializer.Serialize(document.ScriptCalls,
                    RuntimeJsonContext.Default.RuntimeScriptCallReport), token);
            if (mapPath is not null)
                await WriteNewReport(mapPath, RuntimeScriptSourceMap.Serialize(map!), token);
            if (conditionPath is not null)
                await WriteNewReport(conditionPath, RuntimeQuestConditionSourceMap.Serialize(conditionReport!), token);
            if (commandsPath is not null)
                await WriteNewReport(commandsPath, RuntimeCommandTrace.Serialize(commandReport!), token);
            if (gamepadPath is not null)
                await WriteNewReport(gamepadPath, JsonSerializer.Serialize(document.Gamepad,
                    RuntimeJsonContext.Default.RuntimeGamepadTraceReport), token);
            Console.WriteLine(JsonSerializer.Serialize(document.Summary, RuntimeJsonContext.Default.RuntimeTraceSummary));
            return document.Summary.Complete ? 0 : 2;
        }));
        root.Subcommands.Add(import);

        var prepare = new Command("prepare", "Copy configs and saves into a new isolated-run profile");
        var config = new Option<string[]>("--config") { Required = true, Description = "Configuration files to copy", AllowMultipleArgumentsPerToken = true };
        var saves = new Option<string>("--saves") { Required = true };
        var output = new Option<string>("--output") { Required = true };
        prepare.Options.Add(config); prepare.Options.Add(saves); prepare.Options.Add(output);
        prepare.SetAction(async (parse, token) => await Guard(async () =>
        {
            var profile = await RuntimeRunProfile.PrepareAsync(parse.GetValue(config)!, parse.GetValue(saves)!, parse.GetValue(output)!, token);
            Console.WriteLine(JsonSerializer.Serialize(profile, RuntimeJsonContext.Default.RuntimeRunProfile));
            return 0;
        }));
        root.Subcommands.Add(prepare);
        AddGuestCommand(root);
        AddIsolationCommands(root);
        return root;
    }

    private static async Task WriteNewReport(string path, string json, CancellationToken token)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(output);
        await writer.WriteLineAsync(json.AsMemory(), token);
    }

    private static void AddGuestCommand(Command root)
    {
        var command = new Command("guest-prepare", "Pin emulator, guest and candidate plugin files");
        var emulator = new Option<string>("--emulator") { Required = true };
        var guest = new Option<string>("--guest") { Required = true };
        var data = new Option<string>("--data") { Required = true, Description = "Candidate plugin directory" };
        var output = new Option<string>("--output") { Required = true, Description = "New manifest path" };
        foreach (var option in new[] { emulator, guest, data, output }) command.Options.Add(option);
        command.SetAction(async (parse, token) => await Guard(async () =>
        {
            var manifest = await RuntimeGuestManifest.PrepareAsync(parse.GetValue(emulator)!, parse.GetValue(guest)!,
                parse.GetValue(data)!, parse.GetValue(output)!, token);
            Console.WriteLine(JsonSerializer.Serialize(manifest, RuntimeJsonContext.Default.RuntimeGuestManifest));
            return 0;
        }));
        root.Subcommands.Add(command);

        var isolated = new Command("guest-prepare-isolated", "Copy Xbox configs and saves into a new pinned run profile");
        var manifest = new Option<string>("--guest-manifest") { Required = true };
        var game = new Option<string>("--game") { Required = true, Description = "Original guest game directory" };
        var config = new Option<string>("--config") { Required = true };
        var gameConfig = new Option<string?>("--game-config");
        var saves = new Option<string>("--saves") { Required = true, Description = "Original content/save directory, or an empty directory" };
        var initialSave = new Option<string?>("--initial-save") { Description = "Selected initial save, relative to --saves" };
        var scenario = new Option<string>("--scenario") { Required = true };
        var destination = new Option<string>("--output") { Required = true, Description = "New isolated run directory" };
        foreach (var option in new Option[] { manifest, game, config, gameConfig, saves, initialSave, scenario, destination })
            isolated.Options.Add(option);
        isolated.SetAction(async (parse, token) => await Guard(async () =>
        {
            Console.Error.WriteLine("Preparing isolated guest profile and verifying source files.");
            var profile = await RuntimeGuestRunProfile.PrepareAsync(new(parse.GetValue(manifest)!, parse.GetValue(game)!,
                parse.GetValue(config)!, parse.GetValue(saves)!, parse.GetValue(scenario)!, parse.GetValue(destination)!,
                parse.GetValue(initialSave), parse.GetValue(gameConfig)), token);
            Console.WriteLine(profile.ProfilePath);
            return 0;
        }));
        root.Subcommands.Add(isolated);

        var launch = new Command("guest-launch", "Launch a pinned isolated Xbox session with a bounded lifetime");
        var launchProfile = new Option<string>("--profile") { Required = true };
        var lifetime = new Option<int>("--seconds") { DefaultValueFactory = _ => 300, Description = "Session lifetime before normal close (1–3600 seconds)" };
        var closeSeconds = new Option<int>("--close-seconds") { DefaultValueFactory = _ => 15, Description = "Normal close deadline (1–120 seconds)" };
        launch.Options.Add(launchProfile); launch.Options.Add(lifetime); launch.Options.Add(closeSeconds);
        launch.SetAction(async (parse, token) => await Guard(async () =>
        {
            var profile = await RuntimeGuestRunProfile.LoadAsync(parse.GetValue(launchProfile)!, token: token);
            Console.Error.WriteLine("Verifying isolated guest files before launch.");
            var result = await RuntimeGuestRunLauncher.LaunchAsync(profile, TimeSpan.FromSeconds(parse.GetValue(lifetime)),
                TimeSpan.FromSeconds(parse.GetValue(closeSeconds)), new CallbackProgress<RuntimeGuestRunLaunchResult>(state =>
                    Console.WriteLine(JsonSerializer.Serialize(state, RuntimeJsonContext.Default.RuntimeGuestRunLaunchResult))), token);
            return result.OriginalsVerified && result.Status is "Exited" or "TimedOut" ? 0 : 2;
        }));
        root.Subcommands.Add(launch);
    }

    private static void AddIsolationCommands(Command root)
    {
        var prepare = new Command("prepare-isolated", "Prepare private configs/saves with a copied or explicitly reused PC installation");
        var game = new Option<string>("--game") { Required = true, Description = "Original PC game directory" };
        var destination = new Option<string>("--output") { Required = true, Description = "New session directory" };
        var reuse = new Option<string?>("--reuse-installation") { Description = "Existing writable working installation, reserved sequentially; original assets must match --game" };
        var liveControl = new Option<bool>("--live-control") { Description = "Prepare private background-input and live-script settings" };
        var save = new Option<string?>("--save-file") { Description = "Copy only this .fos and optional .nvse sidecar instead of all Documents/Saves; configs remain private" };
        var usvfs = new Option<string>("--usvfs") { Required = true, Description = "Pinned usvfs 0.5.7.2 release bin directory" };
        var probe = new Option<string>("--probe") { Required = true, Description = "Built RuntimeIsolationPreflight.exe" };
        var bridge = new Option<string>("--bridge") { Required = true, Description = "Built NvseRuntimeBridge.dll" };
        var nvse = new Option<string?>("--nvse-directory") { Description = "Verified xNVSE package directory, if needed" };
        var nvseArchive = new Option<string?>("--nvse-archive") { Description = "Source xNVSE archive to hash in the dependency manifest" };
        var documents = new Option<string>("--documents")
        {
            Description = "Original New Vegas Documents directory (including Saves)",
            DefaultValueFactory = _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "FalloutNV")
        };
        var local = new Option<string>("--local-data")
        {
            Description = "Original New Vegas LocalAppData directory",
            DefaultValueFactory = _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FalloutNV")
        };
        foreach (var option in new Option[] { game, destination, usvfs, probe, bridge, nvse, nvseArchive, documents, local, reuse, save, liveControl }) prepare.Options.Add(option);
        prepare.SetAction(async (parse, token) => await Guard(async () =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var progress = new CallbackProgress<string>(message =>
            {
                if (watch.Elapsed < TimeSpan.FromSeconds(2)) return;
                Console.Error.WriteLine(message); watch.Restart();
            });
            var profile = await RuntimeIsolationService.PrepareAsync(new(parse.GetValue(game)!, parse.GetValue(documents)!, parse.GetValue(local)!,
                parse.GetValue(destination)!, parse.GetValue(usvfs)!, parse.GetValue(probe)!, parse.GetValue(bridge)!, parse.GetValue(nvse), parse.GetValue(nvseArchive), parse.GetValue(reuse), parse.GetValue(save), parse.GetValue(liveControl)), progress, token);
            Console.WriteLine(Path.Combine(profile.Root, RuntimeRunProfile.FileName));
            return 0;
        }));
        root.Subcommands.Add(prepare);
        foreach (var name in new[] { "verify-isolation", "launch", "release-installation" })
        {
            var description = name switch
            {
                "launch" => "Launch the staged game and maintain its isolation controller until exit",
                "release-installation" => "Release an unlaunched prepared working-installation reservation",
                _ => "Verify actual session path redirection without launching the game"
            };
            var command = new Command(name, description);
            var manifest = new Option<string>("--profile") { Required = true };
            command.Options.Add(manifest);
            command.SetAction(async (parse, token) => await Guard(async () =>
            {
                var profile = JsonSerializer.Deserialize(await RuntimeIsolationService.ReadSharedText(parse.GetValue(manifest)!, token),
                    RuntimeJsonContext.Default.RuntimeRunProfile) ?? throw new InvalidDataException("Profile is empty.");
                if (name == "release-installation")
                    await RuntimeIsolationService.ReleasePreparedInstallationAsync(profile, token);
                else if (name == "verify-isolation")
                {
                    var state = await RuntimeIsolationService.VerifyPreparedAsync(profile, token);
                    Console.WriteLine(JsonSerializer.Serialize(state, RuntimeJsonContext.Default.RuntimeIsolationState));
                }
                else
                {
                    string? previous = null;
                    await RuntimeIsolationService.LaunchAsync(profile, new CallbackProgress<RuntimeIsolationState>(state =>
                    {
                        if (state.Status == previous) return;
                        previous = state.Status;
                        Console.WriteLine(JsonSerializer.Serialize(state, RuntimeJsonContext.Default.RuntimeIsolationState));
                    }), token);
                }
                return 0;
            }));
            root.Subcommands.Add(command);
        }
    }

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static Command CaptureCommand(bool run)
    {
        var command = new Command(run ? "run" : "capture", run ? "Run a scenario in a verified isolated engine session" : "Capture runtime messages and engine snapshots");
        var pid = new Option<int>("--pid") { Required = true };
        var output = new Option<string>("--output") { Required = true, Description = "New NDJSON trace path" };
        var seconds = new Option<int>("--seconds") { DefaultValueFactory = _ => 10 };
        var scenario = new Option<string?>("--scenario") { Description = "JSON actions, expected milestones and observation deadline", Required = run };
        var profile = new Option<string?>("--profile") { Description = run ? "Verified isolated-run profile manifest" : "Unsupported by capture; use runtime run for isolated profiles", Required = run };
        var guestManifest = new Option<string?>("--guest-manifest") { Description = "Pinned Xbox guest and plugin files" };
        command.Options.Add(pid); command.Options.Add(output); command.Options.Add(seconds);
        command.Options.Add(scenario); command.Options.Add(profile);
        if (!run) command.Options.Add(guestManifest);
        command.SetAction(async (parse, token) => await Guard(async () =>
        {
            if (!run && parse.GetValue(profile) is not null)
                throw new InvalidOperationException("runtime capture does not support --profile. Use --guest-manifest for Xbox guest observation, or runtime run --profile with --scenario for an isolated run.");
            var guestPath = run ? null : parse.GetValue(guestManifest);
            RuntimeScenario? plan = null;
            string? scenarioSha256 = null;
            if (parse.GetValue(scenario) is { } scenarioPath)
            {
                if (new FileInfo(scenarioPath).Length > 65536) throw new InvalidDataException("Runtime scenario exceeds 64 KiB.");
                var scenarioBytes = await File.ReadAllBytesAsync(scenarioPath, token);
                if (scenarioBytes.Length > 65536) throw new InvalidDataException("Runtime scenario exceeds 64 KiB.");
                scenarioSha256 = Convert.ToHexStringLower(SHA256.HashData(scenarioBytes));
                plan = JsonSerializer.Deserialize(scenarioBytes, RuntimeJsonContext.Default.RuntimeScenario)
                    ?? throw new InvalidDataException("Scenario is empty.");
                if (plan.Actions is not { Count: <= 128 }) throw new InvalidDataException("Scenario requires at most 128 actions.");
                foreach (var action in plan.Actions) (action ?? throw new InvalidDataException("Scenario action is null.")).Validate();
                _ = new RuntimeMilestoneMonitor(plan.Milestones ?? [], plan.Actions.Count);
            }
            if (!run && plan?.Actions.Any(a => !a.Kind.StartsWith("read-", StringComparison.Ordinal) && a.Kind != "wait-message-state") == true)
                throw new InvalidOperationException("Mutating actions require runtime run and a verified isolated profile.");
            RuntimeRunProfile? isolated = null;
            RuntimeGuestRunProfile? isolatedGuest = null;
            await using var connection = await RuntimeConnection.ConnectAsync(parse.GetValue(pid), token);
            if (guestPath is not null) await connection.AttachGuestManifestAsync(guestPath, token);
            if (run)
            {
                var profilePath = parse.GetValue(profile)!;
                if (new FileInfo(profilePath).Length > 32 * 1024 * 1024) throw new InvalidDataException("Run profile exceeds 32 MiB.");
                var profileText = await RuntimeIsolationService.ReadSharedText(profilePath, token);
                using var profileJson = JsonDocument.Parse(profileText);
                if (profileJson.RootElement.TryGetProperty("schema", out var schema) && schema.GetString() == "bmt/runtime-guest-run-profile")
                {
                    isolatedGuest = await RuntimeGuestRunProfile.LoadAsync(profilePath, token: token);
                    Console.Error.WriteLine("Verifying guest run profile and source files.");
                    await connection.AttachGuestRunProfileAsync(isolatedGuest, scenarioSha256!, token);
                    Console.Error.WriteLine("Starting capture; native guest binding may take up to 120 seconds.");
                }
                else
                {
                    isolated = JsonSerializer.Deserialize(profileText, RuntimeJsonContext.Default.RuntimeRunProfile)
                        ?? throw new InvalidDataException("Profile is empty.");
                    Console.Error.WriteLine("Verifying isolated PC profile and original files.");
                    var verificationStarted = Stopwatch.GetTimestamp();
                    await isolated.ValidateForRunAsync(connection.Identity, token);
                    await connection.AttachIsolationEvidenceAsync(isolated, profilePath, token);
                    Console.Error.WriteLine($"PC verification completed in {Stopwatch.GetElapsedTime(verificationStarted).TotalSeconds:F1}s; starting capture.");
                }
            }
            await using var destination = new FileStream(parse.GetValue(output)!, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            RuntimeCaptureResult result;
            try
            {
                var duration = plan is null ? TimeSpan.FromSeconds(parse.GetValue(seconds))
                    : TimeSpan.FromMilliseconds(plan.ObserveMilliseconds);
                result = await RuntimeCaptureService.CaptureAsync(connection, destination, duration, plan?.Actions, token, plan?.ScriptTrace, plan?.Milestones,
                    new CallbackProgress<string>(Console.Error.WriteLine));
            }
            finally
            {
                // Cancellation still verifies the original copies.
                if (isolated is not null)
                {
                    Console.Error.WriteLine("Verifying original PC files after capture.");
                    var verificationStarted = Stopwatch.GetTimestamp();
                    await isolated.VerifyOriginalsAsync(CancellationToken.None);
                    Console.Error.WriteLine($"PC originals verified in {Stopwatch.GetElapsedTime(verificationStarted).TotalSeconds:F1}s.");
                }
                if (isolatedGuest is not null)
                {
                    Console.Error.WriteLine("Verifying original guest files after capture.");
                    await isolatedGuest.VerifyOriginalsAsync(CancellationToken.None);
                }
            }
            Console.WriteLine(JsonSerializer.Serialize(result, RuntimeJsonContext.Default.RuntimeCaptureResult));
            return result.Status == "completed" && result.Dropped == 0 && result.MissingSequences == 0 && result.Errors == 0 ? 0 : 2;
        }));
        return command;
    }

    private static async Task<int> Guard(Func<Task<int>> action)
    {
        try { return await action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or
                                   ArgumentException or JsonException or OperationCanceledException or TimeoutException or PlatformNotSupportedException or
                                   UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
