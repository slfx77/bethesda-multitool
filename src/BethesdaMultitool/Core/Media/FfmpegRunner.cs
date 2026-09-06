using System.Diagnostics;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Media;

/// <summary>
///     Runs ffmpeg to produce a FILE, with the four things the existing XMA converters lack and a
///     preview cannot work without: cancellation, a completion deadline, progress, and a
///     process-tree kill.
///     <para>
///         The three <c>Core/Formats/Xma/Xma*Converter</c> classes each own a private copy of the
///         same pipe plumbing, and none of them can be cancelled or timed out — fine for a
///         short audio clip converted during a batch, useless for a movie the user may click away
///         from mid-encode. This is the shared replacement for the file-output case.
///     </para>
///     <para>
///         ⚠ Output goes to a STAGED path and is only moved into place after ffmpeg exits 0 and
///         the file is non-empty. A zero exit code is not sufficient on its own: ffmpeg can leave
///         a truncated file behind when a pipe breaks, and a half-written movie that plays for two
///         seconds is worse than a reported failure.
///     </para>
/// </summary>
internal static class FfmpegRunner
{
    /// <summary>The placeholder callers put in their argument string for the output file.</summary>
    public const string OutputToken = "{OUTPUT}";

    /// <summary>How long to wait for the pipes to drain after ffmpeg exits.</summary>
    private static readonly TimeSpan DefaultCompletionGrace = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for a killed process to actually die before giving up on it.</summary>
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);

    /// <summary>True when an ffmpeg executable can be found; every caller must check first.</summary>
    public static bool IsAvailable => FfmpegLocator.IsAvailable;

    /// <summary>
    ///     Runs ffmpeg with <paramref name="arguments" />, expecting it to write
    ///     <paramref name="outputPath" />.
    /// </summary>
    /// <param name="arguments">
    ///     The full argument string, which MUST contain the literal token <c>{OUTPUT}</c> where
    ///     the output file belongs. That token is replaced with a quoted STAGED path, so the
    ///     caller never names the real destination on the command line and a failed run cannot
    ///     leave a partial file there. The caller owns all other quoting — this method does not
    ///     build or escape arguments, because the argument shape is format-specific.
    /// </param>
    /// <param name="outputPath">Where the finished file should end up.</param>
    /// <param name="totalSeconds">
    ///     Source duration, used only to turn ffmpeg's elapsed-time chatter into a fraction. Pass
    ///     0 when unknown; <paramref name="progress" /> then simply never reports.
    /// </param>
    /// <param name="progress">Optional 0..1 progress sink.</param>
    /// <param name="completionGrace">
    ///     How long to allow after the process exits for its pipes to drain. Defaults to 30s.
    /// </param>
    /// <param name="cancellationToken">Cancels the encode and kills the process tree.</param>
    public static async Task<FfmpegRunResult> RunToFileAsync(
        string arguments,
        string outputPath,
        double totalSeconds = 0,
        IProgress<double>? progress = null,
        TimeSpan? completionGrace = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var ffmpeg = FfmpegLocator.FfmpegPath;
        if (ffmpeg is null)
        {
            return FfmpegRunResult.Failed(
                "ffmpeg was not found. Install it and put it on PATH (or in C:\\ffmpeg\\bin).");
        }

        if (!arguments.Contains(OutputToken, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The argument string must contain the {OutputToken} token so the output can be " +
                "staged; writing straight to the destination would leave a partial file behind " +
                "on failure.",
                nameof(arguments));
        }

        var grace = completionGrace ?? DefaultCompletionGrace;
        if (grace <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(completionGrace));
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Encode to a sibling temp file so a failed or cancelled run can never leave a partial
        // file at the path a caller (or a cache) will treat as finished.
        var staged = $"{outputPath}.{Guid.NewGuid():N}.tmp";

        using var ioCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var process = new Process();
        var started = false;
        Task? drain = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            process.StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = arguments.Replace(OutputToken, Quote(staged), StringComparison.Ordinal),
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            started = process.Start();
            if (!started)
            {
                return FfmpegRunResult.Failed("ffmpeg could not be started.");
            }

            var stderr = DrainProgressAsync(
                process.StandardError, totalSeconds, progress, ioCancellation.Token);
            var exit = process.WaitForExitAsync(ioCancellation.Token);
            drain = Task.WhenAll(stderr, exit);

            // Observe whichever finishes first so a stalled reader is not hidden by a live
            // process, and vice versa, then allow the grace period for the rest to settle.
            var first = await Task.WhenAny(stderr, exit).WaitAsync(cancellationToken).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            await drain.WaitAsync(grace, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                return FfmpegRunResult.Failed($"ffmpeg exited with code {process.ExitCode}.");
            }

            if (!File.Exists(staged) || new FileInfo(staged).Length == 0)
            {
                return FfmpegRunResult.Failed("ffmpeg reported success but wrote no output.");
            }

            File.Move(staged, outputPath, overwrite: true);
            staged = null!;
            return FfmpegRunResult.Ok(outputPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return FfmpegRunResult.WasCancelled();
        }
        catch (TimeoutException)
        {
            return FfmpegRunResult.Failed("ffmpeg stopped responding while finishing the file.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return FfmpegRunResult.Failed(ex.Message);
        }
        finally
        {
            await ioCancellation.CancelAsync().ConfigureAwait(false);
            await KillIfRunningAsync(process, started).ConfigureAwait(false);
            await ObserveAsync(drain).ConfigureAwait(false);
            TryDeleteStaged(staged);
        }
    }

    /// <summary>Wraps a path in quotes for an ffmpeg argument string.</summary>
    public static string Quote(string path) => $"\"{path}\"";

    /// <summary>
    ///     Reads stderr line by line, reporting progress. Every line is consumed even when there
    ///     is no progress sink: an unread stderr pipe fills and deadlocks the child.
    /// </summary>
    private static async Task DrainProgressAsync(
        StreamReader stderr, double totalSeconds, IProgress<double>? progress, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await stderr.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            if (progress is null)
            {
                continue;
            }

            var elapsed = FfmpegProgressParser.TryReadElapsedSeconds(line);
            if (elapsed is not null &&
                FfmpegProgressParser.ToFraction(elapsed.Value, totalSeconds) is { } fraction)
            {
                progress.Report(fraction);
            }
        }
    }

    /// <summary>
    ///     Kills the whole tree, not just the launched process: a PATH entry may be a shim that
    ///     spawns the real ffmpeg as a child, and killing only the shim leaves an encoder running.
    /// </summary>
    private static async Task KillIfRunningAsync(Process process, bool started)
    {
        if (!started)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(KillGrace).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or TimeoutException or NotSupportedException)
        {
            // Cleanup that fails must never turn into a second failure for the caller.
        }
    }

    /// <summary>
    ///     Waits briefly for the pipe tasks, then keeps observing in the background so a late
    ///     failure cannot surface as an unobserved task exception.
    /// </summary>
    private static async Task ObserveAsync(Task? pending)
    {
        if (pending is null)
        {
            return;
        }

        try
        {
            await pending.WaitAsync(KillGrace).ConfigureAwait(false);
        }
        catch
        {
            _ = pending.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static void TryDeleteStaged(string? staged)
    {
        if (string.IsNullOrEmpty(staged))
        {
            return;
        }

        try
        {
            File.Delete(staged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leaked temp file is not worth failing a conversion over.
        }
    }
}
