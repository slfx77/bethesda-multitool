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
    // Directories whose code runs on (or is reached from) the STA UI thread: the WinUI viewer
    // control itself, every D3D12 renderer it drives from CompositionTarget.Rendering, and the
    // shared caches those touch per frame.
    //
    // Deliberately a GLOB, not a file list. It was a hard-coded list of three files, and the crash
    // still shipped a fourth time — the offending file WAS in the list, but any new renderer or
    // cache was invisible by default. Opt-out beats opt-in for a guard against a process-killing
    // failure mode.
    private static readonly string[][] UiThreadReachableDirectories =
    [
        ["src", "BethesdaMultitool", "App", "Controls", "WorldView3D"],
        ["src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12"],
        ["src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12"],
        ["src", "BethesdaMultitool", "Core", "Resources"],
        ["src", "BethesdaMultitool", "Core", "WorldData"]
    ];

    /// <summary>Every <c>.cs</c> under the UI-thread-reachable roots, as (displayName, source) pairs.</summary>
    private static IEnumerable<(string File, string Source)> EnumerateUiThreadReachableSources()
    {
        foreach (var parts in UiThreadReachableDirectories)
        {
            var directory = Path.Combine(SourceContract.RepoRoot, Path.Combine(parts));
            Assert.True(Directory.Exists(directory),
                $"UI-thread-reachable root moved or was renamed: {directory}. Fix the list — a guard " +
                "that silently scans nothing is worse than no guard.");
            foreach (var path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(SourceContract.RepoRoot, path);
                yield return (relative, SourceContract.ReadSource(relative));
            }
        }
    }

    /// <summary>
    ///     True when the statement at <paramref name="lineNumber" /> (1-based) carries a
    ///     completed-task justification: a <c>NonPumpingWait</c>/<c>IsCompleted</c> check on the
    ///     line immediately above, or a <c>non-pumping:</c> marker anywhere in the contiguous
    ///     comment block attached above it.
    /// </summary>
    private static bool IsJustifiedAbove(string[] lines, int lineNumber)
    {
        var index = lineNumber - 2; // 0-based line directly above
        if (index < 0)
        {
            return false;
        }

        if (lines[index].Contains("NonPumpingWait", StringComparison.Ordinal) ||
            lines[index].Contains("IsCompleted", StringComparison.Ordinal))
        {
            return true;
        }

        // Walk up through the attached comment block only — a blank or code line ends it, so the
        // marker cannot be borrowed from an unrelated comment elsewhere in the method.
        while (index >= 0)
        {
            var trimmed = lines[index].TrimStart();
            if (!trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }

            if (trimmed.Contains("non-pumping:", StringComparison.Ordinal))
            {
                return true;
            }

            index--;
        }

        return false;
    }

    /// <summary>Non-comment code lines, paired with their 1-based line number.</summary>
    private static IEnumerable<(int Number, string Raw, string Code)> CodeLines(string source)
    {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var code = lines[i].TrimStart();
            if (code.StartsWith("//", StringComparison.Ordinal) ||
                code.StartsWith("///", StringComparison.Ordinal) ||
                code.StartsWith('*'))
            {
                continue;
            }

            yield return (i + 1, lines[i], code.Trim());
        }
    }

    // Managed blocking waits. `WaitOne`, `Thread.Join` and `.Result` were previously missing even
    // though the class docs claimed WaitOne was covered — `\.Wait\(` cannot match `.WaitOne(`.
    private static readonly Regex PumpingWait = new(
        @"(?<!NonPumpingWait)\.\s*(Wait|WaitAll|WaitAny|WaitOne)\s*\(" +
        @"|GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\(" +
        @"|\.\s*Join\s*\(\s*\)" +
        @"|\.\s*Result\b",
        RegexOptions.Compiled);

    // Files that legitimately contain a matching token. Each entry is a deliberate, reviewed
    // exemption — NOT a place to append new offenders.
    private static readonly Dictionary<string, string> WaitExemptions = new(StringComparer.Ordinal)
    {
        // The non-pumping primitives themselves: NonPumpingWait polls IsCompleted with a native
        // non-alertable sleep; NonPumpingParallel joins through it.
        ["NonPumpingWait.cs"] = "the non-pumping primitive",
        ["NonPumpingParallel.cs"] = "the non-pumping primitive",
        // Raw WaitForSingleObject P/Invoke — non-pumping by construction, which is the point.
        ["D3D12FenceWaiter.cs"] = "native WaitForSingleObject, never a managed wait"
    };

    // The SYNCHRONOUS Parallel loops block the caller through ManualResetEventSlim.Wait, so on the
    // UI thread they pump exactly like a raw Task.Wait — but they do not LOOK like a wait, which is
    // precisely why the regex above passed while `Parallel.For` in the per-frame reference cull
    // fail-fasted the process repeatedly (2026-08-07 → 08-23). `ForEachAsync` is excluded: it is
    // awaited, never blocking. `NonPumpingParallel` is the safe replacement.
    private static readonly Regex BlockingParallelLoop = new(
        @"(?<!NonPumping)Parallel\s*\.\s*(For|ForEach|Invoke)\s*(<[^>]*>\s*)?\(",
        RegexOptions.Compiled);

    [Fact]
    public void UiThreadReachablePaths_UseNonPumpingWait()
    {
        var offenders = new List<string>();
        foreach (var (file, source) in EnumerateUiThreadReachableSources())
        {
            if (WaitExemptions.ContainsKey(Path.GetFileName(file)))
            {
                continue;
            }

            var lines = source.Split('\n');
            foreach (var (number, raw, code) in CodeLines(source))
            {
                // Reading Result/GetResult on an ALREADY-COMPLETED task neither blocks nor pumps.
                // Justification must be attached to the statement — either a NonPumpingWait /
                // IsCompleted check on the line above, or an explicit `non-pumping:` marker
                // somewhere in the contiguous comment block directly above it. Requiring it AT the
                // call site keeps the claim reviewable, rather than inferring safety from a
                // window scan that could silently absolve a genuine offender further down.
                if (IsJustifiedAbove(lines, number))
                {
                    continue;
                }

                if (PumpingWait.IsMatch(raw))
                {
                    offenders.Add($"{file}:{number}: {code}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Managed blocking wait on a UI-thread-reachable path — use Core.Orchestration.NonPumpingWait " +
            "(see this class's docs; this exact pattern fail-fasted the process 4x):\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     A synchronous <c>Parallel</c> loop is a blocking wait wearing a different name. This is
    ///     the gap that let the crash recur: the wait regex above found nothing, so a
    ///     <c>Parallel.For</c> sat in the per-frame cull — on the STA UI thread, inside
    ///     <c>CompositionTarget.Rendering</c> — through four crash reports before a dump named it.
    /// </summary>
    [Fact]
    public void UiThreadReachablePaths_DoNotUseBlockingParallelLoops()
    {
        var offenders = new List<string>();
        foreach (var (file, source) in EnumerateUiThreadReachableSources())
        {
            foreach (var (number, raw, code) in CodeLines(source))
            {
                if (BlockingParallelLoop.IsMatch(raw))
                {
                    offenders.Add($"{file}:{number}: {code}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Synchronous Parallel loop on a UI-thread-reachable path. It blocks the caller via " +
            "ManualResetEventSlim.Wait, which on the STA thread is a COM pumping wait and can " +
            "fail-fast the process through XAML reentrancy (0xc000027b). Use " +
            "Core.Orchestration.NonPumpingParallel, or await Parallel.ForEachAsync:\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     The guard must actually be scanning a meaningful surface. A glob that silently resolves
    ///     to nothing (directory renamed, test working directory changed) would report "no
    ///     offenders" forever — the failure mode this guard exists to prevent.
    /// </summary>
    [Fact]
    public void TheGuardScansAMeaningfulNumberOfFiles()
    {
        var files = EnumerateUiThreadReachableSources().Select(static f => f.File).ToList();

        Assert.True(files.Count > 40, $"guard scanned only {files.Count} files — the roots look wrong");
        Assert.Contains(files, f => f.EndsWith("ReferenceRenderer12.cs", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("TerrainRenderer12.cs", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("WorldView3DControl.Frame.cs", StringComparison.Ordinal));
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