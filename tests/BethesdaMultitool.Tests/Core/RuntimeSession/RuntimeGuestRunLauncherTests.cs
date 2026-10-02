using System.Diagnostics;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeGuestRunLauncherTests
{
    [Theory]
    [InlineData("unchanged")]
    [InlineData("changed-original")]
    [InlineData("changed-config-copy")]
    [InlineData("unexpected-cache")]
    public async Task Launch_arguments_are_individual_private_routes_and_require_untouched_initial_inputs(string state)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows guest profile admission.");
        using var fixture = new RuntimeGuestRunProfileTests.Fixture();
        var profile = await fixture.Prepare(true);
        if (state == "changed-original") await File.AppendAllTextAsync(fixture.Config, "changed");
        if (state == "changed-config-copy") await File.AppendAllTextAsync(profile.Configuration.CopyPath, "changed");
        if (state == "unexpected-cache") await File.WriteAllTextAsync(Path.Combine(profile.Environment.CacheRoot, "prior-run.bin"), "old");
        if (state != "unchanged")
        {
            await Assert.ThrowsAsync<IOException>(() => RuntimeGuestRunLauncher.PrepareStartInfoAsync(profile));
            return;
        }
        var start = await RuntimeGuestRunLauncher.PrepareStartInfoAsync(profile);
        Assert.Equal(profile.GuestManifest.Emulator.Path, start.FileName);
        Assert.Equal(profile.Environment.StorageRoot, start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Equal(new[]
        {
            profile.GuestManifest.Guest.Path,
            "--config=" + profile.Configuration.CopyPath,
            "--storage_root=" + Path.Combine(profile.Root, "storage"),
            "--content_root=" + Path.Combine(profile.Root, "content"),
            "--cache_root=" + Path.Combine(profile.Root, "cache"),
            "--log_file=" + Path.Combine(profile.Root, "storage", RuntimeGuestRunLauncher.EngineLogFileName),
            "--allow_game_relative_writes=false", "--allow_plugins=false", "--bmt_runtime=true", "--bmt_gamepad=true"
        }, start.ArgumentList);
        Assert.Empty(start.Arguments);
        Assert.False(File.Exists(Path.Combine(profile.Root, RuntimeGuestRunLauncher.ReceiptFileName)));
    }

    [Fact]
    public async Task Identity_and_close_refuse_an_unrelated_actual_process_without_sending_a_close()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows process identity.");
        using var fixture = new RuntimeGuestRunProfileTests.Fixture();
        var profile = await fixture.Prepare(false);
        using var current = Process.GetCurrentProcess();
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeGuestRunLauncher.CaptureIdentityAsync(profile, current));
        var unrelated = new RuntimeGuestRunProcessIdentity(current.Id, current.StartTime.ToUniversalTime().ToString("O"),
            current.MainModule!.FileName, new string('0', 64));
        var result = await RuntimeGuestRunLauncher.CloseOwnedAsync(profile, unrelated, TimeSpan.FromSeconds(1));
        Assert.Equal("IdentityMismatch", result.Status);
        Assert.False(result.NormalCloseRequested);
        Assert.False(result.Exited);
        Assert.False(current.HasExited);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(3601, 1000)]
    [InlineData(10, 0)]
    [InlineData(10, 120001)]
    public async Task Invalid_deadlines_are_rejected_before_any_process_is_started(int seconds, int closeMilliseconds)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows guest profile admission.");
        using var fixture = new RuntimeGuestRunProfileTests.Fixture();
        var profile = await fixture.Prepare(false);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RuntimeGuestRunLauncher.LaunchAsync(profile,
            TimeSpan.FromSeconds(seconds), TimeSpan.FromMilliseconds(closeMilliseconds)));
        Assert.False(File.Exists(Path.Combine(profile.Root, RuntimeGuestRunLauncher.ReceiptFileName)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task Child_output_above_pipe_capacity_is_drained_to_distinct_files_on_exit(int exitCode)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows synthetic child.");
        using var fixture = new ChildOutputFixture();
        using var process = fixture.Start($"[Console]::Out.Write(('o' * 196608)); [Console]::Error.Write(('e' * 196608)); exit {exitCode}");
        fixture.Capture.Start(process);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(deadline.Token);
        var result = await fixture.Capture.CompleteAsync(true, TimeSpan.FromSeconds(2));
        Assert.Equal(exitCode, process.ExitCode);
        Assert.Equal("Complete", result.Status);
        Assert.Equal("Complete", result.RecoveryStatus);
        Assert.Equal(196608, result.StandardOutputBytes);
        Assert.Equal(196608, result.StandardErrorBytes);
        Assert.Equal(new string('o', 196608), await File.ReadAllTextAsync(fixture.Stdout));
        Assert.Equal(new string('e', 196608), await File.ReadAllTextAsync(fixture.Stderr));
    }

    [Fact]
    public async Task Still_running_child_log_completion_is_bounded_and_retains_partial_output()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows synthetic child.");
        using var fixture = new ChildOutputFixture();
        using var process = fixture.Start("[Console]::Out.Write('ready-out'); [Console]::Error.Write('ready-err'); Start-Sleep -Seconds 3");
        fixture.Capture.Start(process);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            while (fixture.Capture.StandardOutputBytes < 9 || fixture.Capture.StandardErrorBytes < 9)
                await Task.Delay(10, deadline.Token);
            var started = Stopwatch.StartNew();
            var result = await fixture.Capture.CompleteAsync(false, TimeSpan.FromMilliseconds(100));
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
            Assert.False(process.HasExited);
            Assert.Equal("Partial", result.Status);
            Assert.Contains(result.RecoveryStatus, new[] { "StoppedEarly", "PendingDrain" });
            Assert.Equal(9, result.StandardOutputBytes);
            Assert.Equal(9, result.StandardErrorBytes);
        }
        finally
        {
            await process.WaitForExitAsync(deadline.Token);
            await fixture.Capture.DrainCompletion.WaitAsync(deadline.Token);
        }
        Assert.Equal("ready-out", await File.ReadAllTextAsync(fixture.Stdout));
        Assert.Equal("ready-err", await File.ReadAllTextAsync(fixture.Stderr));
    }

    [Fact]
    public void Existing_child_log_is_preserved_when_reservation_fails()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows synthetic child.");
        var root = Path.Combine(Path.GetTempPath(), "BmtChildLogTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "existing.log");
            File.WriteAllText(path, "preserved");
            Assert.Throws<IOException>(() => new RuntimeGuestRunOutputCapture(path, Path.Combine(root, "stderr.log")));
            Assert.Equal("preserved", File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ChildOutputFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "BmtChildLogTest-" + Guid.NewGuid().ToString("N"));
        internal string Stdout => Path.Combine(_root, "stdout.log");
        internal string Stderr => Path.Combine(_root, "stderr.log");
        internal RuntimeGuestRunOutputCapture Capture { get; }
        internal ChildOutputFixture()
        {
            Directory.CreateDirectory(_root);
            Capture = new(Stdout, Stderr);
        }
        internal Process Start(string script)
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) info.ArgumentList.Add(arg);
            return Process.Start(info) ?? throw new IOException("Synthetic child did not start.");
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
