using System.ComponentModel;
using System.Diagnostics;

namespace BethesdaMultitool.Core.Media;

/// <summary>Runs a bounded byte-in/byte-out encoder, draining both output pipes while writing input.</summary>
internal static class FfmpegPipeRunner
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);

    internal sealed record Result(bool Success, byte[] Output, string Error);

    internal static async Task<Result> RunAsync(ProcessStartInfo startInfo, ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var budget = timeout ?? DefaultTimeout;
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        var process = new Process { StartInfo = startInfo };
        var ioCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ioCancellation.CancelAfter(budget);
        var started = false;
        Task? pending = null;
        try
        {
            started = process.Start();
            if (!started) return new(false, [], "FFmpeg could not start");

            // Install both drains before submitting input. Each pipe can apply backpressure independently.
            var stderr = process.StandardError.ReadToEndAsync(ioCancellation.Token);
            var output = ReadOutputAsync(process.StandardOutput.BaseStream, ioCancellation.Token);
            var write = WriteInputAsync(process, input, ioCancellation.Token);
            var exit = process.WaitForExitAsync(ioCancellation.Token);
            pending = Task.WhenAll(stderr, output, write, exit);
            await pending.WaitAsync(budget, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var writeError = await write.ConfigureAwait(false);
            var error = (await stderr.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0)
                return new(false, [], string.IsNullOrEmpty(error)
                    ? $"FFmpeg exited with code {process.ExitCode}" : error);
            if (writeError is not null) return new(false, [], writeError);
            return new(true, await output.ConfigureAwait(false), string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return new(false, [], $"FFmpeg conversion timed out after {budget.TotalSeconds:g} seconds");
        }
        catch (OperationCanceledException) when (ioCancellation.IsCancellationRequested)
        {
            return new(false, [], $"FFmpeg conversion timed out after {budget.TotalSeconds:g} seconds");
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException)
        {
            return new(false, [], $"FFmpeg conversion failed: {exception.Message}");
        }
        finally
        {
            // Keep the return deadline bounded even if stream disposal or cancellation blocks.
            // An already-exited shim's descendants cannot be discovered by Process.Kill; their
            // inherited handles can trigger the deadline, but require external process ownership.
            var cleanup = Task.Run(async () =>
            {
                if (started)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(CleanupTimeout).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or Win32Exception
                                                         or NotSupportedException or TimeoutException) { }
                }
                try { await ioCancellation.CancelAsync().ConfigureAwait(false); }
                finally
                {
                    if (pending is not null) await ObserveBoundedAsync(pending).ConfigureAwait(false);
                    try { process.Dispose(); }
                    finally { ioCancellation.Dispose(); }
                }
            });
            await ObserveBoundedAsync(cleanup).ConfigureAwait(false);
        }
    }

    private static async Task ObserveBoundedAsync(Task pending)
    {
        try { await pending.WaitAsync(CleanupTimeout).ConfigureAwait(false); }
        catch
        {
            _ = pending.ContinueWith(static task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static async Task<string?> WriteInputAsync(Process process, ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (IOException exception)
        {
            return $"FFmpeg input failed: {exception.Message}";
        }
        finally
        {
            // EOF is required even after a partial write or early decoder failure.
            try { process.StandardInput.Close(); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        }
    }

    private static async Task<byte[]> ReadOutputAsync(Stream output, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await output.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
