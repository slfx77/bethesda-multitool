using System.Text.RegularExpressions;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Guards the 0xc000027b crash class. Every managed blocking wait on the STA UI thread
///     (<c>Task.Wait</c>, <c>WaitAll</c>, <c>GetAwaiter().GetResult()</c>, <c>WaitOne</c>) becomes a
///     COM pumping wait, which can dispatch input back into XAML mid-callback and make
///     <c>CXcpDispatcher::CheckReentrancy</c> fail-fast the PROCESS — bypassing every managed
///     exception handler, so nothing is logged and no dump names our code.
///     <para>
///         This shipped three times: WER bucket <c>StackHash12_f74</c> at
///         <c>Microsoft.UI.Xaml.dll+0x3ace5d</c> on 2026-08-07 and twice on 2026-08-11, from a raw
///         30-second <c>Task.Wait</c> in <c>ReferenceMeshCache12.Dispose</c> — one line below a
///         drain that had ALREADY been converted to the non-pumping helper. Use
///         <c>Core.Orchestration.NonPumpingWait</c> on any UI-thread path that must block.
///     </para>
/// </summary>
public sealed class UiThreadPumpingWaitGuardTests
{
    // Renderer/cache files whose Dispose or resource-load paths are reached from the UI thread
    // (worldspace switch teardown, control unload, one-shot loads).
    private static readonly string[][] UiThreadReachableSources =
    [
        ["src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", "ReferenceMeshCache12.cs"],
        ["src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", "TerrainRenderer12.cs"],
        ["src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", "ReferenceRenderer12.cs"]
    ];

    // `.Wait(` / `.WaitAll(` / `GetAwaiter().GetResult()` — but NOT when the receiver is the
    // NonPumpingWait helper itself, and not inside a comment.
    private static readonly Regex PumpingWait = new(
        @"(?<!NonPumpingWait)\.\s*(Wait|WaitAll|WaitAny)\s*\(|GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void UiThreadReachablePaths_UseNonPumpingWait()
    {
        var offenders = new List<string>();
        foreach (var parts in UiThreadReachableSources)
        {
            var source = SourceContract.ReadSource(parts);
            var file = parts[^1];
            var lines = source.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) ||
                    code.StartsWith("///", StringComparison.Ordinal) ||
                    code.StartsWith('*'))
                {
                    continue;
                }

                // A GetResult() on an already-completed task is safe; the established pattern is a
                // NonPumpingWait on the preceding line.
                if (code.Contains("GetAwaiter", StringComparison.Ordinal) &&
                    i > 0 && lines[i - 1].Contains("NonPumpingWait", StringComparison.Ordinal))
                {
                    continue;
                }

                if (PumpingWait.IsMatch(line))
                {
                    offenders.Add($"{file}:{i + 1}: {code.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Managed blocking wait on a UI-thread-reachable path — use Core.Orchestration.NonPumpingWait " +
            "(see this class's docs; this exact pattern fail-fasted the process 3x):\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>The dispose drain and the persist-writer flush must BOTH be non-pumping.</summary>
    [Fact]
    public void MeshCacheDispose_DrainsAndFlushesWithoutPumping()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceMeshCache12.cs");

        Assert.Contains("_decodeTasks.WaitForDrainLogged();", source, StringComparison.Ordinal);
        Assert.Contains(
            "Core.Orchestration.NonPumpingWait.Wait(persistWriterTask, TimeSpan.FromSeconds(30))",
            source, StringComparison.Ordinal);
        // NonPumpingWait returns false on timeout and never throws on a faulted task, so the fault
        // must be observed explicitly or a persist failure would vanish.
        Assert.Contains("persistWriterTask.Exception is { } persistWriterFault", source,
            StringComparison.Ordinal);
    }
}