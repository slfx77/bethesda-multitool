using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

public sealed record RuntimeSourceFile(string Path, string Sha256);
public sealed record RuntimeSourceRoot(string Path, bool Existed, IReadOnlyList<string> Directories);
public sealed record RuntimeIsolationLayout(string GameSource, string GameCopy, string DocumentsSource, string DocumentsCopy,
    string LocalDataSource, string LocalDataCopy, string UsvfsDirectory, string ProbeExecutable, string ProbeSha256,
    string BridgePath, string BridgeSha256, IReadOnlyList<RuntimeSourceRoot> SourceRoots, IReadOnlyList<RuntimeSourceFile> SourceFiles,
    string? NvseArchiveSha256 = null, string? ReusedInstallation = null,
    IReadOnlyList<RuntimeProfileFile>? GameEvidence = null);
public sealed record RuntimeIsolationOptions(string GameDirectory, string DocumentsDirectory, string LocalDataDirectory,
    string Destination, string UsvfsDirectory, string ProbeExecutable, string BridgeBinary, string? NvseDirectory = null,
    string? NvseArchivePath = null, string? ReuseInstallation = null, string? SaveFile = null, bool LiveControl = false);
public sealed record RuntimeIsolationState(string Status, string UpdatedUtc, int ControllerProcessId, int? GameProcessId,
    string? ProcessStartedUtc, string? ExecutableSha256, string? BridgeSha256, IReadOnlyList<int> VfsProcessIds, string? Diagnostic = null,
    string? BridgeLoadedPath = null, string? BridgeMappedFile = null);

/// <summary>Stages isolated user data and a copied or explicitly reused game, and owns the VFS controller lifetime.</summary>
public static class RuntimeIsolationService
{
    public const string StateFileName = "runtime-isolation-state.json";
    private static readonly SemaphoreSlim ControllerGate = new(1, 1);
    private static readonly string[] NvseNames = ["nvse_loader.exe", "nvse_1_4.dll", "nvse_steam_loader.dll"];
    private static readonly (string Name, string Sha256)[] UsvfsFiles =
    [
        ("usvfs_x86.dll", "de23207b87aa99a1c15ec29185e3cf1e50dce4b1473eeb894c02abdaa23313fc"),
        ("usvfs_x64.dll", "7ee7758433ab76713900e661056be8074b9c567971fde38fd0e514c76895e274"),
        ("usvfs_proxy_x86.exe", "e37a485fcebbde9583005913a34bfdd2bd49443438c30b6dc75bee83bb261b04"),
        ("usvfs_proxy_x64.exe", "491d4d7e3fce9876e904f8cccb648f43def428a2a527d41f0221d9c0ce1d408e")
    ];

    public static async Task<RuntimeRunProfile> PrepareAsync(RuntimeIsolationOptions options, IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        var root = Path.GetFullPath(options.Destination);
        if (Path.Exists(root)) throw new IOException("Use a new isolated-session directory.");
        var game = Path.GetFullPath(options.GameDirectory);
        var documents = Path.GetFullPath(options.DocumentsDirectory);
        var local = Path.GetFullPath(options.LocalDataDirectory);
        var sourcePaths = new[] { game, documents, local };
        foreach (var source in sourcePaths)
        {
            RejectReparseAncestors(source);
            if (ContainsPath(source, root) || ContainsPath(root, source)) throw new IOException("Session and original directories must not overlap.");
            if (Path.Exists(source) && (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A source root cannot be a reparse point.");
        }
        RejectReparseAncestors(root);
        for (var i = 0; i < sourcePaths.Length; ++i)
            for (var j = i + 1; j < sourcePaths.Length; ++j)
                if (ContainsPath(sourcePaths[i], sourcePaths[j]) || ContainsPath(sourcePaths[j], sourcePaths[i]))
                    throw new IOException("Original directory scopes must not overlap.");
        if (!File.Exists(Path.Combine(game, "FalloutNV.exe"))) throw new FileNotFoundException("The PC game directory must contain FalloutNV.exe.");
        var usvfs = Path.GetFullPath(options.UsvfsDirectory);
        await VerifyUsvfsAsync(usvfs, token);
        var probe = Path.GetFullPath(options.ProbeExecutable);
        var bridge = Path.GetFullPath(options.BridgeBinary);
        var probeHash = await Hash(probe, token);
        var bridgeHash = await Hash(bridge, token);
        var nvseArchiveHash = options.NvseArchivePath is { } archive ? await Hash(Path.GetFullPath(archive), token) : null;
        var reused = options.ReuseInstallation is { } reuse ? Path.GetFullPath(reuse) : null;
        using var installationLease = reused is null ? null : RuntimeInstallationLease.AcquireForPreparation(reused, root, sourcePaths);
        if (reused is not null) ValidateReusableFiles(reused, token);
        var selectedSaves = SelectSavePair(options.SaveFile);
        if (reused is not null && selectedSaves.Any(path => ContainsPath(reused, path)))
            throw new IOException("Selected save input cannot be inside the writable installation.");
        var scans = sourcePaths.Select(Scan).ToArray();
        var count = scans.Sum(s => s.Files.Length);
        var bytes = scans.Sum(s => s.Files.Sum(f => new FileInfo(f).Length));
        if (count > 100000 || bytes > 64L * 1024 * 1024 * 1024) throw new IOException("Isolated input exceeds 100,000 files or 64 GiB.");
        var copyBytes = scans.SelectMany((scan, index) => scan.Files.Where(file =>
            (index != 0 || reused is null) && (index != 1 || options.SaveFile is null || !IsSavePath(documents, file))))
            .Sum(file => new FileInfo(file).Length) + selectedSaves.Sum(file => new FileInfo(file).Length);
        if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < copyBytes + 1024L * 1024 * 1024)
            throw new IOException("Insufficient free space for verified game and profile copies.");
        Directory.CreateDirectory(root);
        var copies = new List<RuntimeProfileFile>();
        var originals = new List<RuntimeSourceFile>();
        var sourceRoots = new List<RuntimeSourceRoot>();
        if (options.NvseArchivePath is { } archivePath) originals.Add(new(Path.GetFullPath(archivePath), nvseArchiveHash!));
        var destinations = new[] { reused ?? Path.Combine(root, "game"), Path.Combine(root, "documents"), Path.Combine(root, "local-data") };
        var overlays = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine("Data", "NVSE", "Plugins", "NvseRuntimeBridge.dll") };
        if (options.NvseDirectory is not null) overlays.UnionWith(NvseNames);
        for (var index = 0; index < sourcePaths.Length; ++index)
        {
            var scan = scans[index];
            sourceRoots.Add(new(sourcePaths[index], Directory.Exists(sourcePaths[index]), scan.Directories));
            await StageDirectoryAsync(sourcePaths[index], destinations[index], index == 0 && reused is not null,
                index == 1 && options.SaveFile is not null, index == 0 ? overlays : null, copies, originals, progress, token);
        }
        await StageSelectedSavesAsync(selectedSaves, destinations[1], copies, originals, token);
        if (options.NvseDirectory is { } nvseDirectory)
        {
            foreach (var name in NvseNames)
            {
                var source = Path.GetFullPath(Path.Combine(nvseDirectory, name));
                if (!File.Exists(source)) throw new FileNotFoundException($"xNVSE package lacks {name}");
                await Overlay(source, Path.Combine(destinations[0], name));
            }
            var defaultConfig = Path.GetFullPath(Path.Combine(nvseDirectory, "Data", "NVSE", "nvse_config.ini"));
            var configCopy = Path.Combine(destinations[0], "Data", "NVSE", "nvse_config.ini");
            if (File.Exists(defaultConfig) && (reused is not null || !File.Exists(configCopy))) await Overlay(defaultConfig, configCopy);
        }
        if (!File.Exists(Path.Combine(destinations[0], "nvse_loader.exe")))
            throw new FileNotFoundException("xNVSE loader is missing; provide its verified package directory.");
        var bridgeCopy = Path.Combine(destinations[0], "Data", "NVSE", "Plugins", "NvseRuntimeBridge.dll");
        await Overlay(bridge, bridgeCopy);
        if (options.LiveControl)
        {
            var configured = await RuntimeLiveConfiguration.StageAsync(root, destinations[0], destinations[1], token);
            foreach (var (source, target) in configured) await Overlay(source, target);
        }
        var layout = new RuntimeIsolationLayout(game, destinations[0], documents, destinations[1], local, destinations[2],
            usvfs, probe, probeHash, bridgeCopy, bridgeHash, sourceRoots, originals, nvseArchiveHash, reused);
        var profile = new RuntimeRunProfile("bmt/runtime-profile", 1, root, "prepared-not-engine-verified", null, null, copies, layout);
        await profile.VerifyOriginalsAsync(token);
        await SaveProfile(profile, token);
        if (installationLease is not null) await installationLease.CommitAsync(token);
        progress?.Report($"Prepared {copies.Count} verified file bindings; engine activation is pending.");
        return profile;

        async Task Overlay(string source, string target)
        {
            // Only selected small overlays can replace working-installation files; original sources remain pinned.
            if (reused is not null && ContainsPath(reused, source))
                throw new IOException("An overlay input cannot be inside the writable installation.");
            copies.RemoveAll(f => string.Equals(f.CopyPath, target, StringComparison.OrdinalIgnoreCase));
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".staging";
            string sha;
            try
            {
                var hash = await CopyVerified(source, temporary, token);
                RejectReparseAncestors(target);
                File.Move(temporary, target, overwrite: true);
                sha = hash;
            }
            finally { File.Delete(temporary); }
            originals.Add(new(source, sha));
            copies.Add(new(source, target, sha));
        }
    }

    public static async Task LaunchAsync(RuntimeRunProfile profile, IProgress<RuntimeIsolationState>? progress = null,
        CancellationToken token = default)
    {
        if (!await ControllerGate.WaitAsync(0, token)) throw new InvalidOperationException("This BMT process already owns an isolation controller.");
        try
        {
            using var lease = RuntimeInstallationLease.AcquireForProfile(profile);
            await LaunchCoreAsync(profile, progress, token);
        }
        finally { ControllerGate.Release(); }
    }

    public static async Task<RuntimeIsolationState> VerifyPreparedAsync(RuntimeRunProfile profile, CancellationToken token = default)
    {
        if (!await ControllerGate.WaitAsync(0, token)) throw new InvalidOperationException("This BMT process already owns an isolation controller.");
        try
        {
            using var lease = RuntimeInstallationLease.AcquireForProfile(profile);
            var layout = await ValidatePrepared(profile, token);
            using var vfs = new NativeUsvfsSession(layout.UsvfsDirectory, "bmt-probe-" + Guid.NewGuid().ToString("N"));
            await ProbeMappings(profile, vfs, token);
            var state = new RuntimeIsolationState("probes-passed", DateTimeOffset.UtcNow.ToString("O"), Environment.ProcessId,
                null, null, null, layout.BridgeSha256, []);
            await WriteState(profile.Root, state);
            return state;
        }
        finally { ControllerGate.Release(); }
    }

    private static async Task<RuntimeIsolationLayout> ValidatePrepared(RuntimeRunProfile profile, CancellationToken token)
    {
        var layout = profile.Isolation ?? throw new InvalidOperationException("Use prepare-isolated to copy the complete game and profile directories.");
        if (profile.Activation != "prepared-not-engine-verified") throw new InvalidOperationException("Use a fresh prepared session for each engine launch.");
        foreach (var copy in new[] { layout.GameCopy, layout.DocumentsCopy, layout.LocalDataCopy })
        {
            if (!ContainsPath(profile.Root, copy) &&
                !(layout.ReusedInstallation is not null && string.Equals(copy, layout.GameCopy, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("An isolated copy escapes its session directory.");
            RejectReparseAncestors(copy);
            _ = Scan(copy); // Reject newly introduced reparse points before linking any paths.
        }
        if (layout.ReusedInstallation is not null)
        {
            RuntimeInstallationLease.RequireOwner(profile);
            ValidateReusableFiles(layout.GameCopy, token);
        }
        if (!ContainsPath(layout.GameCopy, layout.BridgePath)) throw new InvalidDataException("Bridge path escapes the copied game.");
        await VerifyUsvfsAsync(layout.UsvfsDirectory, token);
        if (await Hash(layout.ProbeExecutable, token) != layout.ProbeSha256 || await Hash(layout.BridgePath, token) != layout.BridgeSha256)
            throw new InvalidDataException("Isolation probe or bridge hash changed.");
        await profile.VerifyCopiesAsync(token);
        await profile.VerifyOriginalsAsync(token);
        return layout;
    }

    internal static async Task ProbeMappings(RuntimeRunProfile profile, NativeUsvfsSession vfs, CancellationToken token,
        bool verifyOriginals = true)
    {
        var layout = profile.Isolation!;
        var mappings = new[] { (layout.GameCopy, layout.GameSource), (layout.DocumentsCopy, layout.DocumentsSource), (layout.LocalDataCopy, layout.LocalDataSource) };
        foreach (var (copy, original) in mappings) vfs.Map(copy, original);
        await File.WriteAllTextAsync(Path.Combine(profile.Root, "vfs-mapping.txt"), vfs.MappingDump(), token);
        var probeFolder = "__bmt_isolation_probe_" + Guid.NewGuid().ToString("N");
        foreach (var (copy, original) in mappings)
        {
            var childReleased = true;
            await WithOwnedProbeDirectoryAsync(copy, probeFolder, async physical =>
            {
                await File.WriteAllTextAsync(Path.Combine(physical, "seed.txt"), "profile-copy", token);
                await File.WriteAllTextAsync(Path.Combine(physical, "probe.ini"), "[General]\r\ntest=profile-copy\r\n", token);
                vfs.Map(copy, original);
                using var probe = vfs.Start(layout.ProbeExecutable, ["--probe", Path.Combine(original, probeFolder)], profile.Root, hidden: true);
                childReleased = false;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(35));
                try
                {
                    try { await probe.WaitForExitAsync(deadline.Token); }
                    catch
                    {
                        if (!probe.HasExited)
                        {
                            probe.Kill(entireProcessTree: true); // Only the owned preflight probe, never the game.
                            await probe.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                        }
                        throw;
                    }
                    if (probe.ExitCode != 0 || await File.ReadAllTextAsync(Path.Combine(physical, "child.txt"), token) != "inherited-hook" ||
                        await File.ReadAllTextAsync(Path.Combine(physical, "Saves", "probe.fos"), token) != "synthetic-save")
                        throw new IOException("The actual session path failed its write-redirection probe.");
                }
                finally { childReleased = probe.HasExited; }
            }, () => childReleased);
        }
        if (RuntimeLiveSaveIsolation.IsPrepared(profile))
        {
            await RuntimeLiveLauncher.ValidateConfigurationAsync(profile, token);
            var saves = RuntimeLiveSaveIsolation.Resolve(profile);
            Directory.CreateDirectory(saves.Physical);
            vfs.Map(saves.Physical, saves.Logical);
            await File.WriteAllTextAsync(Path.Combine(profile.Root, "vfs-mapping.txt"), vfs.MappingDump(), token);
        }
        if (verifyOriginals) await profile.VerifyOriginalsAsync(token);
    }

    internal static async Task WithOwnedProbeDirectoryAsync(string copyRoot, string folder,
        Func<string, Task> operation, Func<bool> childReleased)
    {
        const string prefix = "__bmt_isolation_probe_";
        if (!folder.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(folder[prefix.Length..], "N", out _))
            throw new InvalidDataException("Probe folder must have this invocation's exact GUID name.");
        var physical = Path.GetFullPath(Path.Combine(copyRoot, folder));
        if (!ContainsPath(copyRoot, physical) || string.Equals(Path.GetFullPath(copyRoot), physical, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Probe directory escapes its copied root.");
        RejectReparseAncestors(physical);
        if (Path.Exists(physical)) throw new IOException("Probe path already exists; preserve its evidence.");
        Directory.CreateDirectory(physical);
        try { await operation(physical); }
        finally
        {
            if (childReleased())
            {
                RejectReparseAncestors(physical);
                _ = Scan(physical); // Unknown reparse descendants retain the directory instead of following them.
                if (!ContainsPath(copyRoot, physical) || Path.GetFileName(physical) != folder)
                    throw new IOException("Probe ownership changed; retained evidence requires review.");
                Directory.Delete(physical, recursive: true);
            }
        }
    }

    private static async Task LaunchCoreAsync(RuntimeRunProfile profile, IProgress<RuntimeIsolationState>? progress,
        CancellationToken token = default)
    {
        var running = Process.GetProcessesByName("FalloutNV");
        foreach (var process in running) process.Dispose();
        if (running.Length != 0) throw new InvalidOperationException("A FalloutNV process is already running.");
        var layout = await ValidatePrepared(profile, token);
        if (layout.ReusedInstallation is not null)
        {
            var evidence = await SnapshotGameEvidenceAsync(profile, token);
            layout = layout with { GameEvidence = evidence };
            profile = profile with { Isolation = layout };
            await SaveProfile(profile, token);
        }
        token.ThrowIfCancellationRequested();
        using var vfs = new NativeUsvfsSession(layout.UsvfsDirectory, "bmt-runtime-" + Guid.NewGuid().ToString("N"));
        string? bridgeLoadedPath = null, bridgeMappedFile = null;
        await ProbeMappings(profile, vfs, token);
        await SetState("probes-passed", null, [], null);
        token.ThrowIfCancellationRequested();
        using var loader = vfs.Start(Path.Combine(layout.GameCopy, "nvse_loader.exe"), [], layout.GameCopy, hidden: false);
        Process? game = null;
        var watch = Stopwatch.StartNew();
        var phase = "locating-game";
        try
        {
            while (watch.Elapsed < TimeSpan.FromSeconds(90))
            {
                foreach (var id in vfs.ProcessIds())
                {
                    Process? candidate = null;
                    try
                    {
                        candidate = Process.GetProcessById(id);
                        if (!candidate.HasExited && string.Equals(candidate.MainModule?.FileName, Path.Combine(layout.GameCopy, "FalloutNV.exe"), StringComparison.OrdinalIgnoreCase))
                        { game = candidate; candidate = null; break; }
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    finally { candidate?.Dispose(); }
                }
                if (game is not null) break;
                await Task.Delay(250, CancellationToken.None);
            }
            if (game is null) throw new IOException("No copied FalloutNV process joined the VFS within 90 seconds.");
            phase = "executable-identity";
            var executable = game.MainModule?.FileName ?? throw new IOException("Game executable identity unavailable.");
            var exeHash = await Hash(executable, CancellationToken.None);
            var started = game.StartTime.ToUniversalTime().ToString("O");
            // The game may appear before xNVSE has loaded its plugins. Wait for the actual module, not a filename on disk.
            var moduleSeen = false;
            phase = "bridge-admission";
            watch.Restart();
            while (!game.HasExited && watch.Elapsed < TimeSpan.FromSeconds(60))
            {
                // Process caches its module collection. xNVSE loads this plugin after process creation.
                game.Refresh();
                foreach (ProcessModule module in game.Modules)
                {
                    if (!string.Equals(module.ModuleName, Path.GetFileName(layout.BridgePath), StringComparison.OrdinalIgnoreCase)) continue;
                    var mappedFile = RuntimeModuleIdentity.MappedFile(game, module);
                    if (!RuntimeModuleIdentity.IsBackingFile(mappedFile, layout.BridgePath))
                        throw new IOException("The loaded runtime bridge is backed by a different file than the verified copy.");
                    bridgeLoadedPath = module.FileName;
                    bridgeMappedFile = mappedFile;
                    moduleSeen = true;
                    break;
                }
                if (moduleSeen) break;
                await Task.Delay(250, CancellationToken.None);
            }
            if (!moduleSeen) throw new IOException("The copied game did not load the pinned runtime bridge module.");
            profile = profile with { Activation = "verified-usvfs", ProcessId = game.Id, ExecutableSha256 = exeHash, ProcessStartedUtc = started };
            phase = "saving-admission-profile";
            await SaveProfile(profile, CancellationToken.None);
            phase = "writing-active-state";
            await SetState("active", game, vfs.ProcessIds(), null);
            var requestedClose = false;
            while (!game.HasExited)
            {
                if (token.IsCancellationRequested && !requestedClose)
                {
                    requestedClose = true;
                    phase = "requesting-game-close";
                    game.CloseMainWindow(); // No forced termination of the game; controller remains until it exits.
                }
                phase = "monitoring-vfs-membership";
                var members = vfs.ProcessIds();
                if (!members.Contains(game.Id)) throw new IOException("Game process left the verified VFS membership.");
                phase = "writing-active-state";
                await SetState(requestedClose ? "close-requested" : "active", game, members, null);
                await Task.Delay(1000, CancellationToken.None);
            }
            phase = "retaining-game-logs";
            if (layout.ReusedInstallation is not null) await SnapshotGameLogsAsync(profile, CancellationToken.None);
            phase = "verifying-originals";
            await profile.VerifyOriginalsAsync(CancellationToken.None);
            phase = "writing-final-state";
            profile = profile with { Activation = "engine-exited" };
            await SaveProfile(profile, CancellationToken.None);
            await SetState("engine-exited-originals-unchanged", game, vfs.ProcessIds(), null);
        }
        catch (Exception ex)
        {
            var diagnostic = $"{phase}: {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}";
            profile = profile with { Activation = "activation-failed" };
            try
            {
                await SaveProfile(profile, CancellationToken.None);
                await SetState(game is not null && !game.HasExited ? "failed-awaiting-engine-exit" : "failed", game, [], diagnostic);
            }
            finally
            {
                if (game is not null && !game.HasExited)
                {
                    try { game.CloseMainWindow(); }
                    catch (System.ComponentModel.Win32Exception) { /* Keep the controller alive if a close request cannot be delivered. */ }
                    catch (InvalidOperationException) when (game.HasExited) { }
                    // Preserve VFS lifetime even when writing failure metadata failed.
                    await game.WaitForExitAsync(CancellationToken.None);
                }
            }
            if (layout.ReusedInstallation is not null && game is not null)
                await SnapshotGameLogsAsync(profile, CancellationToken.None);
            await profile.VerifyOriginalsAsync(CancellationToken.None);
            await SetState("failed-engine-exited-originals-unchanged", game, [], diagnostic);
            throw;
        }
        finally { game?.Dispose(); }

        async Task SetState(string status, Process? process, int[] members, string? diagnostic)
        {
            var state = new RuntimeIsolationState(status, DateTimeOffset.UtcNow.ToString("O"), Environment.ProcessId,
                process?.Id, profile.ProcessStartedUtc, profile.ExecutableSha256, layout.BridgeSha256, members, diagnostic,
                bridgeLoadedPath, bridgeMappedFile);
            await WriteState(profile.Root, state);
            progress?.Report(state);
        }
    }

    internal static void RejectReparseAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Isolation path traverses a reparse point: {current}");
        }
    }

    public static async Task ReleasePreparedInstallationAsync(RuntimeRunProfile profile, CancellationToken token = default)
    {
        using var lease = RuntimeInstallationLease.AcquireForProfile(profile)
            ?? throw new InvalidOperationException("Profile has no reusable installation reservation.");
        if (profile.Activation != "prepared-not-engine-verified")
            throw new InvalidOperationException("Only an unlaunched prepared installation can be released explicitly.");
        var statePath = Path.Combine(profile.Root, StateFileName);
        if (File.Exists(statePath))
        {
            RejectReparseAncestors(statePath);
            var state = JsonSerializer.Deserialize(await ReadSharedText(statePath, token), RuntimeJsonContext.Default.RuntimeIsolationState);
            if (state?.Status != "probes-passed") throw new InvalidOperationException("Controller state is not an unlaunched preparation.");
        }
        await lease.ReleaseAsync(token);
    }

    internal static async Task StageDirectoryAsync(string source, string destination, bool reuse, bool omitSaves,
        IReadOnlySet<string>? overlays, List<RuntimeProfileFile> copies, List<RuntimeSourceFile> originals,
        IProgress<string>? progress, CancellationToken token)
    {
        var scan = Scan(source);
        Directory.CreateDirectory(destination);
        foreach (var directory in scan.Directories)
            if (!omitSaves || !IsSavePath(source, Path.Combine(source, directory)))
                Directory.CreateDirectory(Path.Combine(destination, directory));
        foreach (var file in scan.Files)
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            if ((omitSaves && IsSavePath(source, file)) || (reuse && overlays?.Contains(relative) == true))
            {
                originals.Add(new(file, await Hash(file, token)));
                continue;
            }
            progress?.Report($"Verifying/staging {relative}");
            var target = Path.Combine(destination, relative);
            var sha = await StageInputFileAsync(file, target, reuse, token);
            originals.Add(new(file, sha));
            copies.Add(new(file, target, sha));
        }
    }

    internal static async Task StageSelectedSavesAsync(IEnumerable<string> saves, string documents,
        List<RuntimeProfileFile> copies, List<RuntimeSourceFile> originals, CancellationToken token)
    {
        foreach (var save in saves)
        {
            var target = Path.Combine(documents, "Saves", Path.GetFileName(save));
            var sha = await CopyVerified(save, target, token);
            originals.Add(new(save, sha));
            copies.Add(new(save, target, sha));
        }
    }

    internal static void ValidateReusableFiles(string installation, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("PC working-installation admission requires Windows file identity.");
        RejectReparseAncestors(installation);
        foreach (var file in Scan(installation).Files)
        {
            token.ThrowIfCancellationRequested();
            RuntimeGuestRunProfile.RejectSharedFileLinks(file);
        }
    }

    internal static string[] SelectSavePair(string? saveFile)
    {
        if (saveFile is null) return [];
        var save = Path.GetFullPath(saveFile);
        if (!string.Equals(Path.GetExtension(save), ".fos", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("--save-file requires one .fos save; its .nvse sidecar is copied when present.");
        RejectReparseAncestors(save);
        if (!File.Exists(save)) throw new FileNotFoundException("Selected save is missing.", save);
        var sidecar = Path.ChangeExtension(save, ".nvse");
        RejectReparseAncestors(sidecar);
        return File.Exists(sidecar) ? [save, sidecar] : [save];
    }

    internal static bool IsSavePath(string documents, string file) => ContainsPath(Path.Combine(documents, "Saves"), file);

    internal static async Task<string> StageInputFileAsync(string source, string target, bool reuse, CancellationToken token)
    {
        if (!reuse) return await CopyVerified(source, target, token);
        RejectReparseAncestors(target);
        var before = await Hash(source, token);
        if (await Hash(target, token) != before || await Hash(source, token) != before)
            throw new IOException($"Working installation differs from the original: {target}");
        return before;
    }

    private static async Task SnapshotGameLogsAsync(RuntimeRunProfile profile, CancellationToken token)
    {
        foreach (var log in Directory.EnumerateFiles(profile.Isolation!.GameCopy, "*.log", SearchOption.TopDirectoryOnly))
        {
            RejectReparseAncestors(log);
            if (new FileInfo(log).Length > 64L * 1024 * 1024)
                throw new IOException($"Game log exceeds the 64 MiB evidence bound: {log}");
            await CopyVerified(log, Path.Combine(profile.Root, "game-logs", Path.GetFileName(log)), token, overwrite: true);
        }
    }

    internal static async Task<IReadOnlyList<RuntimeProfileFile>> SnapshotGameEvidenceAsync(RuntimeRunProfile profile, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var layout = profile.Isolation!;
        var selected = profile.Files.Where(file => ContainsPath(layout.GameCopy, file.CopyPath) &&
            (Path.GetExtension(file.CopyPath).ToLowerInvariant() is ".exe" or ".dll" or ".esp" or ".ini" or ".cfg" ||
             (Path.GetExtension(file.CopyPath).Equals(".esm", StringComparison.OrdinalIgnoreCase) && !ContainsPath(layout.GameSource, file.OriginalPath)))).ToArray();
        if (selected.Sum(file => new FileInfo(file.CopyPath).Length) > 512L * 1024 * 1024)
            throw new IOException("Critical game evidence exceeds 512 MiB; inspect the selected overlays.");
        var cache = layout.ReusedInstallation is null ? null : GameEvidenceCacheRoot(profile);
        var evidence = new List<RuntimeProfileFile>();
        foreach (var file in selected)
        {
            token.ThrowIfCancellationRequested();
            if (cache is not null)
            {
                evidence.Add(await CacheGameEvidenceAsync(file, cache, token));
                continue;
            }
            var destination = Path.Combine(profile.Root, "game-evidence", Path.GetRelativePath(layout.GameCopy, file.CopyPath));
            RejectReparseAncestors(destination);
            var sha = await CopyVerified(file.CopyPath, destination, token);
            if (sha != file.Sha256) throw new IOException($"Game evidence changed after admission: {file.CopyPath}");
            evidence.Add(new(file.CopyPath, destination, sha));
        }
        return evidence;
    }

    private static string GameEvidenceCacheRoot(RuntimeRunProfile profile)
    {
        // Launch holds the installation lease. The cache is its sibling, never a VFS/game route.
        RuntimeInstallationLease.RequireOwner(profile);
        var layout = profile.Isolation!;
        var cache = Path.TrimEndingDirectorySeparator(Path.GetFullPath(layout.ReusedInstallation!)) + ".bmt-evidence";
        var excluded = new[] { profile.Root, layout.GameCopy, layout.DocumentsCopy, layout.LocalDataCopy,
            layout.GameSource, layout.DocumentsSource, layout.LocalDataSource }
            .Concat(layout.SourceRoots.Select(root => root.Path))
            .Concat(layout.SourceFiles.Select(file => file.Path))
            .Concat(profile.Files.Select(file => file.OriginalPath));
        foreach (var path in excluded)
            if (ContainsPath(path, cache) || ContainsPath(cache, path))
                throw new InvalidDataException("Game evidence cache overlaps a session, working route or original input.");
        RejectReparseAncestors(cache);
        return cache;
    }

    private static async Task<RuntimeProfileFile> CacheGameEvidenceAsync(RuntimeProfileFile file, string cache, CancellationToken token)
    {
        if (file.Sha256 is not { Length: 64 } || !file.Sha256.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Game evidence requires a SHA-256 file pin.");
        var expected = file.Sha256.ToLowerInvariant();
        var destination = Path.Combine(cache, "sha256", expected + ".blob");
        RejectReparseAncestors(file.CopyPath);
        RejectReparseAncestors(destination);
        if (OperatingSystem.IsWindows()) RuntimeGuestRunProfile.RejectSharedFileLinks(file.CopyPath);
        // Keep the admitted working bytes read-only and undeletable throughout publication.
        await using var input = new FileStream(file.CopyPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > 512L * 1024 * 1024)
            throw new IOException($"Game evidence exceeds the admitted size bound: {file.CopyPath}");
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)) != expected)
            throw new IOException($"Game evidence changed after admission: {file.CopyPath}");
        if (!File.Exists(destination))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            RejectReparseAncestors(destination);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".writing";
            var ownedTemporary = false;
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    ownedTemporary = true;
                    input.Position = 0;
                    await input.CopyToAsync(output, token);
                    await output.FlushAsync(token);
                }
                await VerifyCachedEvidenceAsync(temporary, expected, input.Length, token);
                input.Position = 0;
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)) != expected)
                    throw new IOException($"Game evidence changed while copying: {file.CopyPath}");
                token.ThrowIfCancellationRequested();
                RejectReparseAncestors(destination);
                try { File.Move(temporary, destination); }
                catch (IOException) when (File.Exists(destination))
                {
                    // A concurrent publisher may win. Its blob must pass the same verification below.
                }
            }
            finally
            {
                if (ownedTemporary && File.Exists(temporary))
                {
                    RejectReparseAncestors(temporary);
                    if (OperatingSystem.IsWindows()) RuntimeGuestRunProfile.RejectSharedFileLinks(temporary);
                    File.Delete(temporary);
                }
            }
        }
        await VerifyCachedEvidenceAsync(destination, expected, input.Length, token);
        input.Position = 0;
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)) != expected)
            throw new IOException($"Game evidence changed after caching: {file.CopyPath}");
        return new(file.CopyPath, destination, expected);
    }

    private static async Task VerifyCachedEvidenceAsync(string path, string expected, long length, CancellationToken token)
    {
        RejectReparseAncestors(path);
        if (OperatingSystem.IsWindows()) RuntimeGuestRunProfile.RejectSharedFileLinks(path);
        await using var input = File.OpenRead(path);
        if (input.Length != length || Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)) != expected)
            throw new IOException($"Retained game evidence hash mismatch; existing bytes were preserved: {path}");
    }

    internal static async Task VerifySourceInventoriesAsync(RuntimeIsolationLayout layout, CancellationToken token)
    {
        foreach (var root in layout.SourceRoots)
        {
            var current = Scan(root.Path);
            var expected = layout.SourceFiles.Where(f => ContainsPath(root.Path, f.Path)).Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (Directory.Exists(root.Path) != root.Existed || !current.Files.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase) ||
                !current.Directories.SequenceEqual(root.Directories, StringComparer.OrdinalIgnoreCase))
                throw new IOException($"Original directory inventory changed: {root.Path}");
        }
        foreach (var file in layout.SourceFiles)
            if (await Hash(file.Path, token) != file.Sha256) throw new IOException($"Original file changed: {file.Path}");
    }

    private static (string[] Files, string[] Directories) Scan(string root)
    {
        if (!Directory.Exists(root)) return ([], []);
        var files = new List<string>(); var directories = new List<string>();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Source contains a reparse point: {entry}");
                if ((attributes & FileAttributes.Directory) != 0) { directories.Add(Path.GetRelativePath(root, entry)); pending.Push(entry); }
                else files.Add(Path.GetFullPath(entry));
            }
        }
        return (files.Order(StringComparer.OrdinalIgnoreCase).ToArray(), directories.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static async Task<string> CopyVerified(string source, string destination, CancellationToken token, bool overwrite = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var before = await Hash(source, token);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await input.CopyToAsync(output, token);
        if (before != await Hash(destination, token) || before != await Hash(source, token)) throw new IOException($"Input changed or copy failed: {source}");
        return before;
    }

    internal static async Task VerifyUsvfsAsync(string path, CancellationToken token)
    {
        foreach (var (name, expected) in UsvfsFiles)
            if (await Hash(Path.Combine(path, name), token) != expected) throw new InvalidDataException($"Pinned usvfs hash mismatch: {name}");
    }
    internal static bool ContainsPath(string root, string path) => string.Equals(Path.GetFullPath(root), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static async Task<string> Hash(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
    }
    internal static Task SaveProfile(RuntimeRunProfile profile, CancellationToken token) => AtomicWrite(Path.Combine(profile.Root, RuntimeRunProfile.FileName),
        JsonSerializer.Serialize(profile, RuntimeJsonContext.Default.RuntimeRunProfile), token);
    private static Task WriteState(string root, RuntimeIsolationState state) => AtomicWrite(Path.Combine(root, StateFileName),
        JsonSerializer.Serialize(state, RuntimeJsonContext.Default.RuntimeIsolationState), CancellationToken.None);
    internal static async Task<string> ReadSharedText(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(token);
    }
    internal static async Task<string> HashShared(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
    internal static async Task AtomicWrite(string path, string text, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".writing";
        try
        {
            await File.WriteAllTextAsync(temporary, text, token);
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < 8 &&
                    (ex is IOException or UnauthorizedAccessException) && (ex.HResult & 0xFFFF) is 5 or 32 or 33)
                {
                    // Ordinary Windows readers may omit FileShare.Delete. Keep the prior complete
                    // state until they release it; a short status poll must not terminate the game.
                    await Task.Delay(100, token);
                }
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
