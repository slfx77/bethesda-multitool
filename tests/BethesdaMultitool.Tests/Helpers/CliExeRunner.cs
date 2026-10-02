using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Runs the SHIPPED CLI as a child process: the <c>BethesdaMultitool.exe</c> copied into this test
///     assembly's own output directory, with its own <c>BethesdaMultitool.runtimeconfig.json</c>.
///     <para>
///         Why a process and not an in-process call: the CLI is <c>PublishTrimmed</c>, so the SDK writes
///         <c>System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=false</c> into the exe's
///         runtimeconfig, and the test host's runtimeconfig does not carry it. Reflection-based JSON
///         therefore passes every in-process test and throws in the shipped tool; only a run under the
///         exe's own runtimeconfig can fail on it. <see cref="AssertShippedJsonReflectionIsDisabled" /> is
///         the control proving the harness really runs under that switch.
///     </para>
///     <para>
///         The test-bin copy is used deliberately, never <c>src/…/bin</c>: that one can be stale when a
///         locked file downgrades the copy to warning MSB3026 while the build still reports success.
///     </para>
/// </summary>
internal static class CliExeRunner
{
    /// <summary>The runtimeconfig switch the trimmed CLI ships with and the test host lacks.</summary>
    internal const string JsonReflectionSwitch = "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault";

    /// <summary>Default wall-clock budget for one CLI run before it is killed.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>The CLI apphost beside the test assembly.</summary>
    internal static string ExePath => Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "BethesdaMultitool.exe" : "BethesdaMultitool");

    /// <summary>The CLI's own runtimeconfig beside the test assembly.</summary>
    internal static string RuntimeConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "BethesdaMultitool.runtimeconfig.json");

    /// <summary>
    ///     Control: fails the test unless the CLI apphost exists and its runtimeconfig sets
    ///     <see cref="JsonReflectionSwitch" /> to <c>false</c>. Without that switch an exe smoke test could
    ///     not see reflection-only JSON fail, so it would pass vacuously.
    /// </summary>
    internal static void AssertShippedJsonReflectionIsDisabled()
    {
        if (!File.Exists(ExePath))
        {
            Assert.Fail($"The CLI apphost is missing from the test output directory: {ExePath}");
        }

        if (!File.Exists(RuntimeConfigPath))
        {
            Assert.Fail($"The CLI runtimeconfig is missing from the test output directory: {RuntimeConfigPath}");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(RuntimeConfigPath));
        var found = document.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions)
                    && runtimeOptions.TryGetProperty("configProperties", out var configProperties)
                    && configProperties.TryGetProperty(JsonReflectionSwitch, out var value)
                    && value.ValueKind == JsonValueKind.False;
        if (!found)
        {
            Assert.Fail(
                $"{RuntimeConfigPath} does not set {JsonReflectionSwitch}=false, so this harness no longer runs " +
                "the CLI the way it ships and cannot catch reflection-only JSON.");
        }
    }

    /// <summary>
    ///     Runs the CLI with <paramref name="arguments" /> (passed verbatim, one argv entry each), capturing
    ///     stdout and stderr separately as UTF-8. The child is killed and a <see cref="TimeoutException" />
    ///     thrown when it outlives <paramref name="timeout" /> (default <see cref="DefaultTimeout" />).
    ///     The child runs in <paramref name="workingDirectory" /> (default: the test output directory), so a
    ///     command that writes relative paths should be given a scratch directory. Every run first applies
    ///     the <see cref="AssertShippedJsonReflectionIsDisabled" /> control.
    /// </summary>
    internal static async Task<Result> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        string? workingDirectory = null)
    {
        AssertShippedJsonReflectionIsDisabled();

        var budget = timeout ?? DefaultTimeout;
        var startInfo = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Plain, deterministic output: no colour, and no opt-in end-of-run resource table on stdout.
        // Workstation GC keeps a parallel suite from paying one server-GC heap per core per child; the
        // JSON reflection switch under test is unaffected by the GC mode.
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["DOTNET_gcServer"] = "0";
        startInfo.Environment.Remove(global::BethesdaMultitool.Core.EnvironmentVariables.Diagnostics.ResourceStats);

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {ExePath}.");
        }

        process.StandardInput.Close();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(budget);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new Result(process.ExitCode, stdout, stderr, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainQuietlyAsync(stdoutTask, stderrTask);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException(
                $"{ExePath} {string.Join(' ', arguments)} did not exit within {budget.TotalSeconds:N0} s and was killed.");
        }
    }

    /// <summary>Creates a fresh, uniquely named scratch directory that deletes itself on dispose.</summary>
    internal static TempDirectory CreateTempDirectory()
    {
        return new TempDirectory();
    }

    /// <summary>
    ///     Writes <paramref name="builder" />'s plugin into <paramref name="directory" /> as
    ///     <c>FalloutNV.esm</c> — the master name, together with the builder's HEDR 1.34, is what makes the
    ///     semantic loader treat the synthetic file as Fallout: New Vegas — and returns its full path.
    /// </summary>
    internal static string WriteFalloutNvEsm(TempDirectory directory, EsmTestFileBuilder builder)
    {
        return WritePlugin(directory, "FalloutNV.esm", builder);
    }

    /// <summary>Writes <paramref name="builder" />'s plugin into <paramref name="directory" /> under <paramref name="fileName" />.</summary>
    internal static string WritePlugin(TempDirectory directory, string fileName, EsmTestFileBuilder builder)
    {
        var path = Path.Combine(directory.Path, fileName);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    /// <summary>Observes the reader tasks after a kill so neither surfaces as an unobserved exception.</summary>
    private static async Task DrainQuietlyAsync(params Task[] readers)
    {
        foreach (var reader in readers)
        {
            try
            {
                await reader;
            }
            catch (OperationCanceledException)
            {
                // Cancelled together with the run.
            }
            catch (IOException)
            {
                // The pipe closed under the kill.
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied while the process tears down; nothing left to do.
        }
    }

    /// <summary>One finished CLI run.</summary>
    /// <param name="ExitCode">The process exit code.</param>
    /// <param name="StandardOutput">Everything written to stdout, decoded as UTF-8.</param>
    /// <param name="StandardError">Everything written to stderr, decoded as UTF-8.</param>
    /// <param name="Elapsed">Wall-clock time from start to exit.</param>
    internal sealed record Result(int ExitCode, string StandardOutput, string StandardError, TimeSpan Elapsed)
    {
        /// <summary>A compact dump for assertion messages: exit code plus the head of both streams.</summary>
        public string Describe()
        {
            return $"exit={ExitCode} after {Elapsed.TotalSeconds:N1} s\n" +
                   $"--- stdout ---\n{Head(StandardOutput)}\n" +
                   $"--- stderr ---\n{Head(StandardError)}";
        }

        private static string Head(string text)
        {
            const int limit = 4000;
            return text.Length <= limit ? text : text[..limit] + $"\n… ({text.Length - limit:N0} more chars)";
        }
    }

    /// <summary>A unique scratch directory under the system temp path, deleted (best effort) on dispose.</summary>
    internal sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "bmt-cli-exe-smoke",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        /// <summary>Full path of the directory.</summary>
        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
            catch (IOException)
            {
                // Best effort: a lingering handle only leaves a temp directory behind.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }
}
