using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

public sealed record RuntimeLiveLaunchState(string Schema, int Version, string Status, string ProfileRoot,
    string UpdatedUtc, int ControllerProcessId, int? GameProcessId, string? ProcessStartedUtc,
    string ExecutableSha256, string BridgeSha256, string? Diagnostic = null,
    string? BridgeMappedFile = null, int? EngineExitCode = null);

/// <summary>Reuses an explicitly prepared live profile while owning its VFS and installation lease.</summary>
public static class RuntimeLiveLauncher
{
    public const string StateFileName = "live-launch-state.json";
    private const int MaximumProfileBytes = 4 * 1024 * 1024;

    public static async Task LaunchAsync(string profilePath, string? bridgeBinary = null, Action<RuntimeLiveLaunchState>? progress = null,
        CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Live launch requires a 64-bit Windows controller.");
        profilePath = Path.GetFullPath(profilePath);
        if (Directory.Exists(profilePath)) profilePath = Path.Combine(profilePath, RuntimeRunProfile.FileName);
        RuntimeIsolationService.RejectReparseAncestors(profilePath);
        var file = new FileInfo(profilePath);
        if (file.Length > MaximumProfileBytes) throw new InvalidDataException("Live profile exceeds 4 MiB.");
        var profile = JsonSerializer.Deserialize(await File.ReadAllTextAsync(profilePath, token),
            RuntimeJsonContext.Default.RuntimeRunProfile) ?? throw new InvalidDataException("Live profile is empty.");
        var layout = ValidateProfile(profile, profilePath);
        // This lease rejects other Fallout/NVSE processes and remains held until
        // the owned game exits. Historical capture state is left untouched.
        using var lease = RuntimeInstallationLease.AcquireForProfile(profile)
            ?? throw new InvalidDataException("Live launch requires an explicitly reused installation.");
        await RuntimeLiveSaveIsolation.PrepareAsync(profile, token);
        await ValidateConfigurationAsync(profile, token);
        await RuntimeIsolationService.VerifyUsvfsAsync(layout.UsvfsDirectory, token);
        if (await RuntimeIsolationService.Hash(layout.ProbeExecutable, token) != layout.ProbeSha256)
            throw new InvalidDataException("The isolation probe changed after preparation.");
        var executable = Path.Combine(layout.GameCopy, "FalloutNV.exe");
        var expectedExe = profile.ExecutableSha256 ?? profile.Files.LastOrDefault(binding => SamePath(binding.CopyPath, executable))?.Sha256;
        if (expectedExe is not { Length: 64 } || !expectedExe.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("The prepared profile has no executable hash.");
        var expectedBridge = bridgeBinary is null
            ? await PreviousBridgePinAsync(profile, expectedExe, token)
            : await StageBridgeAsync(bridgeBinary, layout.BridgePath, token);
        await using var executableGuard = OpenBinary(executable);
        await using var bridgeGuard = OpenBinary(layout.BridgePath);
        var exeHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(executableGuard, token));
        var bridgeHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(bridgeGuard, token));
        if (!exeHash.Equals(expectedExe, StringComparison.OrdinalIgnoreCase) ||
            !bridgeHash.Equals(expectedBridge, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The executable or bridge differs from the prepared profile.");

        using var vfs = new NativeUsvfsSession(layout.UsvfsDirectory, "bmt-live-" + Guid.NewGuid().ToString("N"));
        Process? game = null;
        string? started = null, mappedFile = null;
        var phase = "probing-mappings";
        try
        {
            await SetState(phase);
            // Exercise the same paths and inherited read/write probes as ordinary
            // isolation. Prepared asset inventories are not recopied or rehashed.
            await RuntimeIsolationService.ProbeMappings(profile, vfs, token, verifyOriginals: false);
            token.ThrowIfCancellationRequested();
            phase = "starting-game";
            await SetState(phase);
            using var loader = vfs.Start(Path.Combine(layout.GameCopy, "nvse_loader.exe"), [], layout.GameCopy, hidden: false);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(30))
            {
                game = FindGame(vfs.ProcessIds(), executable);
                if (game is not null) break;
                // Once the loader starts, locate its child before responding to
                // cancellation so the VFS cannot disappear under a new game.
                await Task.Delay(100, CancellationToken.None);
            }
            if (game is null) throw new IOException("No copied FalloutNV process joined the live VFS within 30 seconds.");
            started = game.StartTime.ToUniversalTime().ToString("O");
            phase = "waiting-for-bridge";
            watch.Restart();
            while (!game.HasExited && watch.Elapsed < TimeSpan.FromSeconds(30))
            {
                token.ThrowIfCancellationRequested();
                game.Refresh();
                foreach (ProcessModule module in game.Modules)
                {
                    if (!module.ModuleName.Equals(Path.GetFileName(layout.BridgePath), StringComparison.OrdinalIgnoreCase)) continue;
                    var candidate = RuntimeModuleIdentity.MappedFile(game, module);
                    if (!RuntimeModuleIdentity.IsBackingFile(candidate, layout.BridgePath))
                        throw new IOException("The live bridge module is backed by a different file.");
                    mappedFile = candidate;
                    break;
                }
                if (mappedFile is not null) break;
                await Task.Delay(100, token);
            }
            if (mappedFile is null) throw new IOException("The copied game did not load its verified live bridge within 30 seconds.");
            await SetState("active"); // Announce the admitted game PID once.
            var closeRequested = false;
            phase = "monitoring-game";
            while (!game.HasExited)
            {
                if (token.IsCancellationRequested && !closeRequested)
                {
                    closeRequested = true;
                    TryClose(game);
                    await SetState("close-requested");
                }
                if (!vfs.ProcessIds().Contains(game.Id))
                    throw new IOException("The game left the live VFS membership.");
                await Task.Delay(500, CancellationToken.None);
            }
            await SetState("engine-exited");
        }
        catch (Exception error)
        {
            // Startup can be interrupted between child creation and discovery.
            // Find our VFS child before releasing the controller's shared maps.
            game ??= FindGame(vfs.ProcessIds(), executable);
            if (game is not null) started ??= game.StartTime.ToUniversalTime().ToString("O");
            var diagnostic = $"{phase}: {error.GetType().Name}: {error.Message}";
            try { await SetState(game is { HasExited: false } ? "failed-awaiting-engine-exit" : "failed", diagnostic); }
            finally
            {
                if (game is { HasExited: false })
                {
                    TryClose(game);
                    await game.WaitForExitAsync(CancellationToken.None);
                }
            }
            await SetState("failed-engine-exited", diagnostic);
            throw;
        }
        finally { game?.Dispose(); }

        async Task SetState(string status, string? diagnostic = null)
        {
            var state = new RuntimeLiveLaunchState("bmt/runtime-live-launch", 1, status, profile.Root,
                DateTimeOffset.UtcNow.ToString("O"), Environment.ProcessId, game?.Id, started,
                exeHash, bridgeHash, diagnostic, mappedFile, game is { HasExited: true } ? game.ExitCode : null);
            var path = Path.Combine(profile.Root, StateFileName);
            RuntimeIsolationService.RejectReparseAncestors(path);
            await RuntimeIsolationService.AtomicWrite(path,
                JsonSerializer.Serialize(state, RuntimeJsonContext.Default.RuntimeLiveLaunchState), CancellationToken.None);
            progress?.Invoke(state);
        }
    }

    internal static RuntimeIsolationLayout ValidateProfile(RuntimeRunProfile profile, string profilePath)
    {
        if (profile.Schema != "bmt/runtime-profile" || profile.Version != 1 || profile.Files is null ||
            !SamePath(Path.GetDirectoryName(Path.GetFullPath(profilePath))!, profile.Root))
            throw new InvalidDataException("Unsupported profile schema or mismatched profile root.");
        var layout = profile.Isolation ?? throw new InvalidDataException("The profile has no prepared isolation layout.");
        if (layout.ReusedInstallation is null) throw new InvalidDataException("Live launch requires a reusable installation.");
        RuntimeInstallationLease.ValidateProfileScope(profile);
        if (!SamePath(layout.BridgePath, Path.Combine(layout.GameCopy, "Data", "NVSE", "Plugins", "NvseRuntimeBridge.dll")))
            throw new InvalidDataException("Live bridge is outside its expected installation path.");
        foreach (var path in new[] { layout.GameCopy, layout.DocumentsCopy, layout.LocalDataCopy, layout.BridgePath,
            layout.ProbeExecutable, layout.UsvfsDirectory, Path.Combine(layout.GameCopy, "FalloutNV.exe"),
            Path.Combine(layout.GameCopy, "nvse_loader.exe") })
            RuntimeIsolationService.RejectReparseAncestors(path);
        foreach (var name in new[] { "Fallout.ini", "FalloutPrefs.ini", "nvse_config.ini" })
        {
            var marker = Path.Combine(profile.Root, "live-config", name);
            var target = name == "nvse_config.ini" ? Path.Combine(layout.GameCopy, "Data", "NVSE", name) : Path.Combine(layout.DocumentsCopy, name);
            RuntimeIsolationService.RejectReparseAncestors(marker);
            RuntimeIsolationService.RejectReparseAncestors(target);
            if (!File.Exists(marker) || !File.Exists(target) ||
                !profile.Files.Any(binding => SamePath(binding.OriginalPath, marker) && SamePath(binding.CopyPath, target)))
                throw new InvalidDataException("The profile was not prepared with live-control configuration.");
        }
        return layout;
    }

    internal static async Task ValidateConfigurationAsync(RuntimeRunProfile profile, CancellationToken token)
    {
        var layout = profile.Isolation!;
        foreach (var name in new[] { "Fallout.ini", "FalloutPrefs.ini", "nvse_config.ini" })
        {
            var path = name == "nvse_config.ini" ? Path.Combine(layout.GameCopy, "Data", "NVSE", name) : Path.Combine(layout.DocumentsCopy, name);
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Live configuration exceeds 1 MiB.");
            var content = await File.ReadAllTextAsync(path, token);
            var expected = name == "nvse_config.ini"
                ? new[] { ("RELEASE", "bNoScriptRunnerCaching", "1") }
                : [("General", "bAlwaysActive", "1"), ("Display", "bFull Screen", "0"),
                    ("Controls", "bBackground Mouse", "1"), ("Controls", "bBackground Keyboard", "1"), ("Controls", "bUse Joystick", "0")];
            foreach (var (section, key, value) in expected)
                if (!HasIniValue(content, section, key, value)) throw new InvalidDataException($"Live setting is missing or conflicting: {name} [{section}] {key}={value}.");
        }
        foreach (var name in RuntimeLiveSaveIsolation.IniFiles)
        {
            var path = Path.Combine(layout.DocumentsCopy, name);
            if (!File.Exists(path)) continue;
            if (!HasIniValue(await File.ReadAllTextAsync(path, token), "General", "SLocalSavePath", RuntimeLiveSaveIsolation.IniValue(profile.Root)))
                throw new InvalidDataException($"The private save route is missing or conflicting: {name}.");
        }
    }

    internal static bool HasIniValue(string content, string section, string key, string expected)
    {
        var matches = 0;
        var currentSection = "";
        foreach (var source in content.Split('\n'))
        {
            var line = source.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { currentSection = line[1..^1].Trim(); continue; }
            if (!currentSection.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0 || !line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            ++matches;
            if (!line[(separator + 1)..].Trim().Equals(expected, StringComparison.Ordinal)) return false;
        }
        return matches > 0;
    }

    private static FileStream OpenBinary(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<string> PreviousBridgePinAsync(RuntimeRunProfile profile, string executableHash, CancellationToken token)
    {
        var path = Path.Combine(profile.Root, StateFileName);
        RuntimeIsolationService.RejectReparseAncestors(path);
        if (!File.Exists(path)) return profile.Isolation!.BridgeSha256;
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Live launch state exceeds 64 KiB.");
        var previous = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, token), RuntimeJsonContext.Default.RuntimeLiveLaunchState);
        if (previous is null || previous.Schema != "bmt/runtime-live-launch" || previous.Version != 1 ||
            !SamePath(previous.ProfileRoot, profile.Root) || !previous.ExecutableSha256.Equals(executableHash, StringComparison.OrdinalIgnoreCase) ||
            previous.BridgeSha256 is not { Length: 64 } || !previous.BridgeSha256.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("The live launch state does not match this prepared profile.");
        return previous.BridgeSha256;
    }

    private static async Task<string> StageBridgeAsync(string source, string destination, CancellationToken token)
    {
        source = Path.GetFullPath(source);
        RuntimeIsolationService.RejectReparseAncestors(source);
        RuntimeIsolationService.RejectReparseAncestors(destination);
        await using var input = OpenBinary(source);
        if (input.Length > 64 * 1024 * 1024) throw new InvalidDataException("The live bridge exceeds 64 MiB.");
        using (var image = new PEReader(input, PEStreamOptions.LeaveOpen))
        {
            if (image.PEHeaders.CoffHeader.Machine != Machine.I386 ||
                (image.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) == 0)
                throw new InvalidDataException("The live bridge must be a 32-bit Windows DLL.");
        }
        input.Position = 0;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
        if (SamePath(source, destination) || await RuntimeIsolationService.Hash(destination, token) == hash) return hash;
        if (OperatingSystem.IsWindows()) RuntimeGuestRunProfile.RejectSharedFileLinks(destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".writing";
        try
        {
            input.Position = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous))
                await input.CopyToAsync(output, token);
            if (await RuntimeIsolationService.Hash(temporary, token) != hash)
                throw new IOException("Live bridge copy verification failed.");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            return hash;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Process? FindGame(IEnumerable<int> members, string executable)
    {
        foreach (var id in members)
        {
            Process? candidate = null;
            try
            {
                candidate = Process.GetProcessById(id);
                if (!candidate.HasExited && SamePath(candidate.MainModule?.FileName ?? "", executable))
                {
                    var result = candidate;
                    candidate = null;
                    return result;
                }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
            finally { candidate?.Dispose(); }
        }
        return null;
    }

    private static void TryClose(Process game)
    {
        try { if (!game.HasExited) game.CloseMainWindow(); }
        catch (Win32Exception) { }
        catch (InvalidOperationException) when (game.HasExited) { }
    }

    private static bool SamePath(string left, string right) => left.Length != 0 && right.Length != 0 &&
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
