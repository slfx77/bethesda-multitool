using System.Diagnostics;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

public sealed record RuntimeGuestRunProcessIdentity(int ProcessId, string ProcessStartedUtc,
    string ExecutablePath, string ExecutableSha256);
public sealed record RuntimeGuestRunCloseResult(string Status, bool NormalCloseRequested, bool Exited, string? Diagnostic);
public sealed record RuntimeGuestRunLaunchResult(string Status, string ProfilePath, string ProfileSha256,
    string StartedUtc, string? EndedUtc, RuntimeGuestRunProcessIdentity? Process, IReadOnlyList<string> Arguments,
    bool NormalCloseRequested, bool OriginalsVerified, string? Diagnostic, string? CompletedUtc = null,
    string? StandardOutputPath = null, string? StandardErrorPath = null, string? OutputCaptureStatus = null,
    string? OutputRecoveryStatus = null, long StandardOutputBytes = 0, long StandardErrorBytes = 0,
    string? OutputCaptureDiagnostic = null, string? EngineLogPath = null);

/// <summary>Owns only the process it starts. A refused normal close is reported, never replaced with termination.</summary>
public static class RuntimeGuestRunLauncher
{
    public const string ReceiptFileName = "runtime-guest-launch.json";
    public const string StandardOutputFileName = "runtime-guest-stdout.log";
    public const string StandardErrorFileName = "runtime-guest-stderr.log";
    public const string EngineLogFileName = "runtime-guest-engine.log";

    public static async Task<ProcessStartInfo> PrepareStartInfoAsync(RuntimeGuestRunProfile profile,
        CancellationToken token = default)
    {
        await profile.VerifyOriginalsAsync(token);
        await profile.VerifyInitialCopiesAsync(token);
        var info = new ProcessStartInfo(profile.GuestManifest.Emulator.Path)
        {
            WorkingDirectory = profile.Environment.StorageRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // cvars are passed individually; no shell parsing or change to manual HID configuration.
        foreach (var argument in new[]
        {
            profile.GuestManifest.Guest.Path,
            "--config=" + profile.Environment.ConfigPath,
            "--storage_root=" + profile.Environment.StorageRoot,
            "--content_root=" + profile.Environment.ContentRoot,
            "--cache_root=" + profile.Environment.CacheRoot,
            "--log_file=" + Path.Combine(profile.Environment.StorageRoot, EngineLogFileName),
            "--allow_game_relative_writes=false", "--allow_plugins=false",
            "--bmt_runtime=true", "--bmt_gamepad=true"
        }) info.ArgumentList.Add(argument);
        return info;
    }

    public static async Task<RuntimeGuestRunProcessIdentity> CaptureIdentityAsync(RuntimeGuestRunProfile profile,
        Process process, CancellationToken token = default)
    {
        if (process.HasExited) throw new InvalidOperationException("Emulator exited before its identity was captured.");
        var started = process.StartTime.ToUniversalTime().ToString("O");
        var path = process.MainModule?.FileName ?? throw new IOException("Emulator executable path is unavailable.");
        if (!RuntimeGuestManifest.SamePath(path, profile.GuestManifest.Emulator.Path))
            throw new InvalidDataException("Started process does not use the pinned emulator executable.");
        var actual = await RuntimeGuestManifest.ReadBackingAsync(path, profile.GuestManifest.Emulator.Name, token);
        if (!RuntimeGuestManifest.SameBacking(profile.GuestManifest.Emulator, actual) || process.HasExited ||
            process.StartTime.ToUniversalTime().ToString("O") != started)
            throw new InvalidDataException("Emulator process or backing identity changed during observation.");
        return new(process.Id, started, actual.Path, actual.Sha256);
    }

    public static async Task<RuntimeGuestRunCloseResult> CloseOwnedAsync(RuntimeGuestRunProfile profile, RuntimeGuestRunProcessIdentity identity,
        TimeSpan timeout, CancellationToken token = default)
    {
        ValidateCloseTimeout(timeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            if (profile.ProfileSha256 is null) throw new InvalidDataException("Guest profile has not been pinned.");
            var pinned = await RuntimeGuestRunProfile.LoadAsync(profile.ProfilePath, profile.ProfileSha256, deadline.Token);
            if (identity.ProcessId <= 0 || !RuntimeGuestManifest.SamePath(identity.ExecutablePath, pinned.GuestManifest.Emulator.Path) ||
                identity.ExecutableSha256 != pinned.GuestManifest.Emulator.Sha256)
                return new("IdentityMismatch", false, false, "The close request is outside this guest profile.");
            await using var receipt = new FileStream(Path.Combine(profile.Root, ReceiptFileName), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (receipt.Length is 0 or > 1024 * 1024) throw new InvalidDataException("Guest launch receipt size is invalid.");
            var launched = await JsonSerializer.DeserializeAsync(receipt, RuntimeJsonContext.Default.RuntimeGuestRunLaunchResult, deadline.Token);
            if (launched?.Process != identity || launched.ProfileSha256 != pinned.ProfileSha256 ||
                !RuntimeGuestManifest.SamePath(launched.ProfilePath, pinned.ProfilePath))
                return new("IdentityMismatch", false, false, "No matching launch receipt owns this process.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new("IdentityMismatch", false, false, ex.Message); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new("CloseUnavailable", false, false, "Profile validation exceeded the normal close deadline."); }
        Process process;
        try { process = Process.GetProcessById(identity.ProcessId); }
        catch (ArgumentException) { return new("Exited", false, true, null); }
        using (process)
        {
            try
            {
                if (process.HasExited) return new("Exited", false, true, null);
                var path = process.MainModule?.FileName;
                if (process.StartTime.ToUniversalTime().ToString("O") != identity.ProcessStartedUtc ||
                    !RuntimeGuestManifest.SamePath(path, identity.ExecutablePath) || path is null ||
                    await RuntimeIsolationService.Hash(path, deadline.Token) != identity.ExecutableSha256)
                    return new("IdentityMismatch", false, false, "PID/start/executable guard rejected normal close.");
                // Recheck after hashing. The Process instance still refers to the same OS process.
                if (process.HasExited) return new("Exited", false, true, null);
                var requested = process.CloseMainWindow();
                try
                {
                    await process.WaitForExitAsync(deadline.Token);
                    return new("Exited", requested, true, null);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return new("StillRunning", requested, false, requested
                        ? "Normal close deadline expired; the owned emulator remains running."
                        : "No main window accepted normal close; the owned emulator remains running.");
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { return new("CloseUnavailable", false, false, "Identity verification exceeded the normal close deadline."); }
            catch (InvalidOperationException) when (process.HasExited) { return new("Exited", false, true, null); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                return new("CloseUnavailable", false, false, ex.Message);
            }
        }
    }

    public static async Task<RuntimeGuestRunLaunchResult> LaunchAsync(RuntimeGuestRunProfile profile,
        TimeSpan lifetime, TimeSpan closeTimeout, IProgress<RuntimeGuestRunLaunchResult>? progress = null,
        CancellationToken token = default)
    {
        if (lifetime < TimeSpan.FromSeconds(1) || lifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Guest lifetime must be between one second and one hour.");
        ValidateCloseTimeout(closeTimeout);
        var info = await PrepareStartInfoAsync(profile, token);
        token.ThrowIfCancellationRequested();
        var receiptPath = Path.Combine(profile.Root, ReceiptFileName);
        // Receipt reservation prevents a second launcher from sharing a prepared writable environment.
        await using (var reservation = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await reservation.WriteAsync("{}"u8.ToArray(), token);
        var stdoutPath = Path.Combine(profile.Root, StandardOutputFileName);
        var stderrPath = Path.Combine(profile.Root, StandardErrorFileName);
        var result = new RuntimeGuestRunLaunchResult("Starting", profile.ProfilePath, profile.ProfileSha256!,
            DateTimeOffset.UtcNow.ToString("O"), null, null, info.ArgumentList.ToArray(), false, false, null,
            StandardOutputPath: stdoutPath, StandardErrorPath: stderrPath, OutputCaptureStatus: "NotStarted",
            EngineLogPath: Path.Combine(profile.Environment.StorageRoot, EngineLogFileName));
        Process? process = null;
        RuntimeGuestRunOutputCapture? output = null;
        try
        {
            output = new RuntimeGuestRunOutputCapture(stdoutPath, stderrPath);
            await SaveAsync();
            token.ThrowIfCancellationRequested();
            process = Process.Start(info) ?? throw new IOException("Emulator launch returned no process.");
            output.Start(process);
            result = result with { OutputCaptureStatus = "Capturing" };
            // Ownership is pinned before publishing the process for independent connect/run commands.
            using var identityDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var identity = await CaptureIdentityAsync(profile, process, identityDeadline.Token);
            result = result with { Status = "Active", Process = identity };
            await SaveAsync();
            using var lifetimeDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            lifetimeDeadline.CancelAfter(lifetime);
            try
            {
                await process.WaitForExitAsync(lifetimeDeadline.Token);
                result = result with { Status = "Exited" };
            }
            catch (OperationCanceledException)
            {
                var close = await CloseOwnedAsync(profile, identity, closeTimeout, CancellationToken.None);
                if (!close.NormalCloseRequested && !close.Exited)
                    close = await CloseStartedProcessAsync(process, closeTimeout);
                result = result with { Status = close.Exited ? (token.IsCancellationRequested ? "Cancelled" : "TimedOut") : close.Status,
                    NormalCloseRequested = close.NormalCloseRequested, Diagnostic = close.Diagnostic };
            }
        }
        catch (Exception ex)
        {
            result = result with { Status = token.IsCancellationRequested ? "Cancelled" : "Failed", Diagnostic = ex.Message };
            if (process is not null && !process.HasExited)
            {
                // A pre-identity launch failure still owns this exact Process object; no PID lookup or forced termination.
                if (result.Process is { } identity)
                {
                    var close = await CloseOwnedAsync(profile, identity, closeTimeout, CancellationToken.None);
                    if (!close.NormalCloseRequested && !close.Exited)
                        close = await CloseStartedProcessAsync(process, closeTimeout);
                    result = result with { Status = close.Exited ? result.Status : close.Status,
                        NormalCloseRequested = close.NormalCloseRequested, Diagnostic = result.Diagnostic + "; " + close.Diagnostic };
                }
                else
                {
                    var close = await CloseStartedProcessAsync(process, closeTimeout);
                    result = result with { Status = close.Exited ? result.Status : close.Status,
                        NormalCloseRequested = close.NormalCloseRequested,
                        Diagnostic = result.Diagnostic + (close.Diagnostic is null ? "" : "; " + close.Diagnostic) };
                }
            }
        }
        finally
        {
            // EndedUtc describes observed engine exit; CompletedUtc describes this launcher's bounded work.
            if (process is not null && process.HasExited)
                result = result with { EndedUtc = DateTimeOffset.UtcNow.ToString("O") };
            if (output is not null)
            {
                var logs = await output.CompleteAsync(process is not null && process.HasExited, TimeSpan.FromSeconds(3));
                result = result with { OutputCaptureStatus = logs.Status, OutputRecoveryStatus = logs.RecoveryStatus,
                    StandardOutputBytes = logs.StandardOutputBytes, StandardErrorBytes = logs.StandardErrorBytes,
                    OutputCaptureDiagnostic = logs.Diagnostic };
            }
            else result = result with { OutputCaptureStatus = "Unavailable", OutputRecoveryStatus = "NotStarted",
                OutputCaptureDiagnostic = "Child log files could not be reserved." };
            process?.Dispose();
            using var verificationDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                await profile.VerifyOriginalsAsync(verificationDeadline.Token);
                result = result with { OriginalsVerified = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                result = result with { Status = result.Status == "StillRunning" ? result.Status : "IntegrityFailed",
                    Diagnostic = (result.Diagnostic is null ? "" : result.Diagnostic + "; ") + "Original verification: " + ex.Message };
            }
            result = result with { CompletedUtc = DateTimeOffset.UtcNow.ToString("O") };
            await SaveAsync();
        }
        return result;

        async Task SaveAsync()
        {
            await RuntimeIsolationService.AtomicWrite(receiptPath,
                JsonSerializer.Serialize(result, RuntimeJsonContext.Default.RuntimeGuestRunLaunchResult), CancellationToken.None);
            progress?.Report(result);
        }
    }

    private static void ValidateCloseTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.FromMilliseconds(100) || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Normal close timeout must be between 100 ms and two minutes.");
    }

    private static async Task<RuntimeGuestRunCloseResult> CloseStartedProcessAsync(Process process, TimeSpan timeout)
    {
        // Only called for the Process instance returned by this invocation's Process.Start, never a PID lookup.
        if (process.HasExited) return new("Exited", false, true, null);
        var requested = false;
        try { requested = process.CloseMainWindow(); }
        catch (InvalidOperationException) when (process.HasExited) { return new("Exited", false, true, null); }
        catch (System.ComponentModel.Win32Exception ex) { return new("StillRunning", false, false, ex.Message); }
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return new("Exited", requested, true, null);
        }
        catch (OperationCanceledException)
        { return new("StillRunning", requested, false, "Normal close deadline expired; the owned emulator remains running."); }
    }
}

internal sealed record RuntimeGuestRunOutputResult(string Status, string RecoveryStatus,
    long StandardOutputBytes, long StandardErrorBytes, string? Diagnostic);

/// <summary>Streams raw child bytes to disk; no console forwarding or whole-log buffering.</summary>
internal sealed class RuntimeGuestRunOutputCapture
{
    private readonly FileStream[] _files;
    private readonly CancellationTokenSource _stop = new();
    private readonly long[] _bytes = new long[2];
    private readonly string?[] _errors = new string?[2];
    private Task[] _drains = [];

    internal RuntimeGuestRunOutputCapture(string stdoutPath, string stderrPath)
    {
        var stdout = Open(stdoutPath);
        try { _files = [stdout, Open(stderrPath)]; }
        catch { stdout.Dispose(); _stop.Dispose(); throw; }
        static FileStream Open(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    internal void Start(Process process)
    {
        if (_drains.Length != 0) throw new InvalidOperationException("Child log capture is already running.");
        var stdout = process.StandardOutput.BaseStream;
        var stderr = process.StandardError.BaseStream;
        // Both workers are scheduled before waiting for identity or process exit.
        _drains = [Task.Run(() => DrainAsync(stdout, 0)), Task.Run(() => DrainAsync(stderr, 1))];
    }

    internal long StandardOutputBytes => Interlocked.Read(ref _bytes[0]);
    internal long StandardErrorBytes => Interlocked.Read(ref _bytes[1]);
    internal Task DrainCompletion => Task.WhenAll(_drains);

    internal async Task<RuntimeGuestRunOutputResult> CompleteAsync(bool processExited, TimeSpan drainTimeout)
    {
        if (drainTimeout <= TimeSpan.Zero || drainTimeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(drainTimeout));
        if (_drains.Length == 0)
        {
            foreach (var file in _files) file.Dispose();
            _stop.Dispose();
            return new("NotStarted", "NotStarted", 0, 0, null);
        }
        var completion = Task.WhenAll(_drains);
        var interrupted = !processExited && !completion.IsCompleted;
        var cancellation = interrupted ? _stop.CancelAsync() : Task.CompletedTask;
        try { await completion.WaitAsync(drainTimeout); }
        catch (TimeoutException)
        {
            interrupted = true;
            cancellation = _stop.CancelAsync();
            // Do not await a child/descendant that still owns a pipe. Drain workers
            // own their file handles until cancellation finishes independently.
        }
        var pending = !completion.IsCompleted;
        _ = Task.WhenAll(completion, cancellation).ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var errors = string.Join("; ", _errors.Where(e => e is not null));
        var complete = !interrupted && !pending && errors.Length == 0;
        string? diagnostic = null;
        if (interrupted) diagnostic = "Log capture stopped before both streams reached EOF.";
        if (pending) diagnostic = "Log drain remains pending; retained files may be incomplete.";
        if (errors.Length != 0) diagnostic = diagnostic is null ? errors : diagnostic + " " + errors;
        var recovery = "Complete";
        if (errors.Length != 0) recovery = "ReadFailed";
        if (interrupted) recovery = "StoppedEarly";
        if (pending) recovery = "PendingDrain";
        return new(complete ? "Complete" : "Partial", recovery,
            StandardOutputBytes, StandardErrorBytes, diagnostic);
    }

    private async Task DrainAsync(Stream source, int index)
    {
        var buffer = new byte[65536];
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (count == 0) break;
                await _files[index].WriteAsync(buffer.AsMemory(0, count), _stop.Token).ConfigureAwait(false);
                Interlocked.Add(ref _bytes[index], count);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { _errors[index] = ex.Message; }
        finally
        {
            try { await _files[index].DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _errors[index] = ex.Message; }
        }
    }
}
