using System.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Media;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Media;

public sealed class FfmpegPipeRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Drains_output_and_error_before_the_child_reads_input(bool largeError)
    {
        var start = PipeProcessFixture.StartInfo(largeError ? "backpressure-error" : "backpressure");
        var input = Enumerable.Repeat((byte)90, 512 * 1024).ToArray();
        var result = await FfmpegPipeRunner.RunAsync(start, input, timeout: TimeSpan.FromSeconds(15));
        Assert.True(result.Success, result.Error);
        Assert.Equal(1048576 + input.Length, result.Output.Length);
        Assert.Equal(-1, result.Output.AsSpan(0, 1048576).IndexOfAnyExcept((byte)65));
        Assert.Equal(input, result.Output.AsSpan(1048576).ToArray());
    }

    [Fact]
    public async Task Early_child_failure_closes_input_and_preserves_decoder_error()
    {
        var start = PipeProcessFixture.StartInfo("early-error");
        var result = await FfmpegPipeRunner.RunAsync(start, new byte[4 * 1024 * 1024],
            timeout: TimeSpan.FromSeconds(15));
        Assert.False(result.Success);
        Assert.Contains("fixture decoder failure", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Output);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Bounds_stalled_pipes_and_cleans_up_a_live_encoder_tree(bool cancel, bool parentExits)
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"bmt-pipe-test-{Guid.NewGuid():N}.txt");
        var start = PipeProcessFixture.StartInfo(parentExits ? "exit-with-child" : "stall");
        start.Environment[PipeProcessFixture.PidFileVariable] = pidFile;
        using var cancellation = new CancellationTokenSource();
        int[] pids = [];
        Task<FfmpegPipeRunner.Result>? run = null;
        var elapsed = Stopwatch.StartNew();
        try
        {
            run = FfmpegPipeRunner.RunAsync(start, new byte[4 * 1024 * 1024], cancellation.Token,
                TimeSpan.FromSeconds(5));
            var ready = Stopwatch.StartNew();
            while (!File.Exists(pidFile) && !run.IsCompleted && ready.Elapsed < TimeSpan.FromSeconds(4))
                await Task.Delay(25);
            Assert.True(File.Exists(pidFile), "The child did not publish its process identities.");
            pids = (await File.ReadAllTextAsync(pidFile)).Split(',').Select(int.Parse).ToArray();
            if (cancel)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);
            }
            else
            {
                var result = await run;
                Assert.False(result.Success);
                Assert.Contains("timed out", result.Error, StringComparison.Ordinal);
                Assert.Empty(result.Output);
            }
            var stopped = Stopwatch.StartNew();
            while (!parentExits && pids.Any(IsRunning) && stopped.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(25);
            if (parentExits) Assert.False(IsRunning(pids[0]));
            else Assert.DoesNotContain(pids, IsRunning);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), "Pipe cleanup exceeded the bounded return interval.");
        }
        finally
        {
            await cancellation.CancelAsync();
            if (run is not null)
            {
                try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
            }
            // A failed assertion still cleans up only the identities written by this test's child.
            foreach (var pid in pids)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task Asset_packer_preserves_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var converter = new PrototypeAssetConverter();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await converter.ConvertAsync(new byte[16], "sound/test.xma", cancellation.Token));
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }
}
