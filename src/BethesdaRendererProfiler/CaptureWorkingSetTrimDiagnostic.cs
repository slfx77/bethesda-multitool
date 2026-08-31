using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;

namespace BethesdaRendererProfiler;

/// <summary>
///     Opt-in capture diagnostic which proves how much cold residency Windows can reclaim without
///     changing the scene. It deliberately does not classify the result: a working-set trim leaves
///     private commit and live object ownership unchanged, and the following settle may fault the
///     pages straight back in.
/// </summary>
internal static class CaptureWorkingSetTrimDiagnostic
{
    private const double BytesPerMiB = 1024d * 1024d;

    internal static CaptureWorkingSetTrimSession RunBeforeSettle()
    {
        var before = CaptureSnapshot();
        var totalTimer = Stopwatch.StartNew();
        var gcTimer = Stopwatch.StartNew();

        // Do not request LOH compaction here. The diagnostic is used specifically on machines with
        // little remaining headroom, where relocating a large live heap could increase peak pressure.
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: false);
        gcTimer.Stop();

        var emptyWorkingSetTimer = Stopwatch.StartNew();
        var emptyWorkingSetSucceeded = false;
        int? emptyWorkingSetError = null;
        string? emptyWorkingSetException = null;
        try
        {
            using var process = Process.GetCurrentProcess();
            emptyWorkingSetSucceeded = K32EmptyWorkingSet(process.Handle);
            if (!emptyWorkingSetSucceeded)
            {
                emptyWorkingSetError = Marshal.GetLastWin32Error();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            emptyWorkingSetException = $"{ex.GetType().Name}: {ex.Message}";
        }

        emptyWorkingSetTimer.Stop();
        var after = CaptureSnapshot();
        totalTimer.Stop();

        var trimFaults = CalculatePageFaultObservation(
            before.PageFaultCount,
            after.PageFaultCount,
            totalTimer.Elapsed);
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["gcMode"] = "forced-blocking-noncompacting",
            ["gcElapsedMilliseconds"] = gcTimer.Elapsed.TotalMilliseconds,
            ["emptyWorkingSetElapsedMilliseconds"] = emptyWorkingSetTimer.Elapsed.TotalMilliseconds,
            ["elapsedMilliseconds"] = totalTimer.Elapsed.TotalMilliseconds,
            ["emptyWorkingSetSucceeded"] = emptyWorkingSetSucceeded,
            ["emptyWorkingSetWin32Error"] = emptyWorkingSetError,
            ["emptyWorkingSetException"] = emptyWorkingSetException,
            ["pageFaultDeltaDuringTrim"] = trimFaults.Delta,
            ["pageFaultsPerSecondDuringTrim"] = trimFaults.PerSecond
        };
        AddSnapshotFields(fields, "before", before);
        AddSnapshotFields(fields, "after", after);
        RendererProfilerTrace.Event("capture-working-set-trim", fields);

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"[Capture] working-set trim diagnostic: GC={gcTimer.Elapsed.TotalMilliseconds:0.0} ms, " +
            $"EmptyWorkingSet={emptyWorkingSetSucceeded} error={FormatError(emptyWorkingSetError, emptyWorkingSetException)} " +
            $"({emptyWorkingSetTimer.Elapsed.TotalMilliseconds:0.0} ms), total={totalTimer.Elapsed.TotalMilliseconds:0.0} ms; " +
            $"managed {FormatMiB(before.ManagedHeapBytes)} -> {FormatMiB(after.ManagedHeapBytes)}, " +
            $"GC committed {FormatMiB(before.GcCommittedBytes)} -> {FormatMiB(after.GcCommittedBytes)}, " +
            $"fragmented {FormatMiB(before.GcFragmentedBytes)} -> {FormatMiB(after.GcFragmentedBytes)}, " +
            $"private {FormatMiB(before.PrivateBytes)} -> {FormatMiB(after.PrivateBytes)}, " +
            $"working set {FormatMiB(before.WorkingSetBytes)} -> {FormatMiB(after.WorkingSetBytes)}, " +
            $"system available {FormatMiB(before.SystemAvailableBytes)} -> {FormatMiB(after.SystemAvailableBytes)}, " +
            $"page faults {FormatCount(before.PageFaultCount)} -> {FormatCount(after.PageFaultCount)}.");
        Logger.Instance.Info(message);
        Console.WriteLine(message);

        // Begin the comparison interval only after the post-trim snapshot. Logging this result and
        // every subsequent settle touch are intentionally included in the observed fault traffic.
        return new CaptureWorkingSetTrimSession(Stopwatch.GetTimestamp(), after.PageFaultCount);
    }

    internal static void ObserveAfterSettle(
        CaptureWorkingSetTrimSession session,
        bool sceneQuiesced,
        TimeSpan quiescePollElapsed)
    {
        var afterSettle = CaptureSnapshot();
        var observationElapsed = Stopwatch.GetElapsedTime(session.CompletedTimestamp);
        var observation = CalculatePageFaultObservation(
            session.PageFaultCount,
            afterSettle.PageFaultCount,
            observationElapsed);

        RendererProfilerTrace.Event("capture-working-set-trim-settle-observation",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pageFaultCountAfterTrim"] = session.PageFaultCount,
                ["pageFaultCountAfterSettle"] = afterSettle.PageFaultCount,
                ["pageFaultDelta"] = observation.Delta,
                ["pageFaultsPerSecond"] = observation.PerSecond,
                ["observationSeconds"] = observationElapsed.TotalSeconds,
                ["quiescePollElapsedMilliseconds"] = quiescePollElapsed.TotalMilliseconds,
                ["sceneQuiesced"] = sceneQuiesced,
                ["acceptanceThresholdApplied"] = false
            });

        var message = observation.Delta is long delta && observation.PerSecond is double rate
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"[Capture] post-trim settle page-fault observation: +{delta:N0} over " +
                $"{observationElapsed.TotalSeconds:0.000} s ({rate:N0}/s), " +
                $"sceneQuiesced={sceneQuiesced}; observation only, no comparability threshold applied.")
            : $"[Capture] post-trim settle page-fault observation unavailable; " +
              $"sceneQuiesced={sceneQuiesced}; no comparability threshold applied.";
        Logger.Instance.Info(message);
        Console.WriteLine(message);
    }

    internal static CapturePageFaultObservation CalculatePageFaultObservation(
        uint? before,
        uint? after,
        TimeSpan elapsed)
    {
        if (before is null || after is null)
        {
            return default;
        }

        // PageFaultCount is a cumulative DWORD. Unsigned subtraction preserves the delta across a
        // single wrap, which is the only plausible wrap count during a bounded capture settle.
        var delta = (long)unchecked(after.Value - before.Value);
        var rate = elapsed > TimeSpan.Zero ? delta / elapsed.TotalSeconds : (double?)null;
        return new CapturePageFaultObservation(delta, rate);
    }

    private static CaptureMemorySnapshot CaptureSnapshot()
    {
        var gc = GC.GetGCMemoryInfo();
        long? privateBytes = null;
        long? workingSetBytes = null;
        uint? pageFaultCount = null;
        int? processMemoryError = null;
        string? processMemoryException = null;

        try
        {
            using var process = Process.GetCurrentProcess();
            var counters = new ProcessMemoryCountersEx
            {
                Size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>()
            };
            if (K32GetProcessMemoryInfo(process.Handle, ref counters, counters.Size))
            {
                privateBytes = checked((long)counters.PrivateUsage);
                workingSetBytes = checked((long)counters.WorkingSetSize);
                pageFaultCount = counters.PageFaultCount;
            }
            else
            {
                processMemoryError = Marshal.GetLastWin32Error();
                process.Refresh();
                privateBytes = process.PrivateMemorySize64;
                workingSetBytes = process.WorkingSet64;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            processMemoryException = $"{ex.GetType().Name}: {ex.Message}";
        }

        long? systemAvailableBytes = null;
        int? systemMemoryError = null;
        string? systemMemoryException = null;
        try
        {
            var status = new MemoryStatusEx
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
            };
            if (GlobalMemoryStatusEx(ref status))
            {
                systemAvailableBytes = checked((long)status.AvailablePhysicalBytes);
            }
            else
            {
                systemMemoryError = Marshal.GetLastWin32Error();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            systemMemoryException = $"{ex.GetType().Name}: {ex.Message}";
        }

        return new CaptureMemorySnapshot(
            GC.GetTotalMemory(forceFullCollection: false),
            gc.TotalCommittedBytes,
            gc.FragmentedBytes,
            privateBytes,
            workingSetBytes,
            systemAvailableBytes,
            pageFaultCount,
            processMemoryError,
            processMemoryException,
            systemMemoryError,
            systemMemoryException);
    }

    private static void AddSnapshotFields(
        Dictionary<string, object?> fields,
        string prefix,
        CaptureMemorySnapshot snapshot)
    {
        fields[$"{prefix}ManagedHeapBytes"] = snapshot.ManagedHeapBytes;
        fields[$"{prefix}GcCommittedBytes"] = snapshot.GcCommittedBytes;
        fields[$"{prefix}GcFragmentedBytes"] = snapshot.GcFragmentedBytes;
        fields[$"{prefix}PrivateBytes"] = snapshot.PrivateBytes;
        fields[$"{prefix}WorkingSetBytes"] = snapshot.WorkingSetBytes;
        fields[$"{prefix}SystemAvailableBytes"] = snapshot.SystemAvailableBytes;
        fields[$"{prefix}PageFaultCount"] = snapshot.PageFaultCount;
        fields[$"{prefix}ProcessMemoryWin32Error"] = snapshot.ProcessMemoryError;
        fields[$"{prefix}ProcessMemoryException"] = snapshot.ProcessMemoryException;
        fields[$"{prefix}SystemMemoryWin32Error"] = snapshot.SystemMemoryError;
        fields[$"{prefix}SystemMemoryException"] = snapshot.SystemMemoryException;
    }

    private static string FormatMiB(long? bytes)
    {
        return bytes is long value
            ? (value / BytesPerMiB).ToString("N1", CultureInfo.InvariantCulture) + " MiB"
            : "unavailable";
    }

    private static string FormatCount(uint? count)
    {
        return count?.ToString("N0", CultureInfo.InvariantCulture) ?? "unavailable";
    }

    private static string FormatError(int? error, string? exception)
    {
        if (!string.IsNullOrWhiteSpace(exception))
        {
            return exception;
        }

        return error?.ToString(CultureInfo.InvariantCulture) ?? "none";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32EmptyWorkingSet(IntPtr processHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(
        IntPtr processHandle,
        ref ProcessMemoryCountersEx counters,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        internal uint Size;
        internal uint PageFaultCount;
        internal nuint PeakWorkingSetSize;
        internal nuint WorkingSetSize;
        internal nuint QuotaPeakPagedPoolUsage;
        internal nuint QuotaPagedPoolUsage;
        internal nuint QuotaPeakNonPagedPoolUsage;
        internal nuint QuotaNonPagedPoolUsage;
        internal nuint PagefileUsage;
        internal nuint PeakPagefileUsage;
        internal nuint PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysicalBytes;
        internal ulong AvailablePhysicalBytes;
        internal ulong TotalPageFileBytes;
        internal ulong AvailablePageFileBytes;
        internal ulong TotalVirtualBytes;
        internal ulong AvailableVirtualBytes;
        internal ulong AvailableExtendedVirtualBytes;
    }

    private readonly record struct CaptureMemorySnapshot(
        long ManagedHeapBytes,
        long GcCommittedBytes,
        long GcFragmentedBytes,
        long? PrivateBytes,
        long? WorkingSetBytes,
        long? SystemAvailableBytes,
        uint? PageFaultCount,
        int? ProcessMemoryError,
        string? ProcessMemoryException,
        int? SystemMemoryError,
        string? SystemMemoryException);
}

internal readonly record struct CaptureWorkingSetTrimSession(
    long CompletedTimestamp,
    uint? PageFaultCount);

internal readonly record struct CapturePageFaultObservation(
    long? Delta,
    double? PerSecond);
