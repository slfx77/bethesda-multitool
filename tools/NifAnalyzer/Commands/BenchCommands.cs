using System.CommandLine;
using System.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Utils;
using Spectre.Console;

namespace NifAnalyzer.Commands;

/// <summary>
///     Throughput benchmarks for the CPU mesh-decode pipeline. <c>bench decode</c> times
///     <see cref="NifConverter.Convert" /> (parse → packed half-float extraction → schema endian
///     swap → geometry write) over a directory of Xbox 360 NIFs, with the file bytes pre-loaded so
///     only CPU decode is measured (no disk I/O, no render loop). <c>bench half-micro</c> A/B's the
///     hardware <see cref="BinaryUtils.HalfToFloat" /> against the legacy <c>Math.Pow</c> form to
///     quantify that specific win. Use these to validate decode-pipeline optimizations deterministically.
/// </summary>
internal static class BenchCommands
{
    public static Command CreateBenchCommand()
    {
        var command = new Command("bench", "Decode-pipeline throughput benchmarks");
        command.Subcommands.Add(CreateDecodeCommand());
        command.Subcommands.Add(CreateHalfMicroCommand());
        command.Subcommands.Add(CreateProbeEndianCommand());
        return command;
    }

    private static Command CreateProbeEndianCommand()
    {
        var command = new Command("probe-endian",
            "Correctness audit: assert NifParser.TryProbeEndianness == NifParser.Parse().IsBigEndian over a corpus");
        var dirArg = new Argument<string>("dir")
        {
            Description = "Directory of (.nif) files, searched recursively"
        };
        var limitOpt = new Option<int>("--limit")
        {
            Description = "Maximum number of files (0 = all)",
            DefaultValueFactory = _ => 0
        };
        command.Arguments.Add(dirArg);
        command.Options.Add(limitOpt);
        command.SetAction(p => ProbeEndian(p.GetValue(dirArg)!, p.GetValue(limitOpt)));
        return command;
    }

    private static void ProbeEndian(string dir, int limit)
    {
        var root = new DirectoryInfo(dir);
        if (!root.Exists)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Directory not found: {dir}[/]");
            return;
        }

        var files = root.EnumerateFiles("*.nif", SearchOption.AllDirectories).ToList();
        if (limit > 0 && files.Count > limit) files = files.Take(limit).ToList();
        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .nif files found.[/]");
            return;
        }

        int agree = 0, indeterminate = 0, parseNull = 0, readFailed = 0;
        var mismatches = new List<string>();
        foreach (var f in files)
        {
            byte[] data;
            try { data = File.ReadAllBytes(f.FullName); }
            catch { readFailed++; continue; }

            var parsed = NifParser.Parse(data);
            var probe = NifParser.TryProbeEndianness(data);

            if (parsed is null)
            {
                // The decode path also returns null here; the probe's value is irrelevant.
                parseNull++;
                continue;
            }

            if (!probe.HasValue)
            {
                // Indeterminate probe → DecodeMesh falls back to a full parse, so this is always safe.
                indeterminate++;
                continue;
            }

            if (probe.Value == parsed.IsBigEndian)
            {
                agree++;
            }
            else if (mismatches.Count < 25)
            {
                mismatches.Add($"{f.Name}: probe={probe.Value}, parse={parsed.IsBigEndian}");
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Endianness probe audit[/]");
        AnsiConsole.MarkupLineInterpolated($"  files:                 [cyan]{files.Count:N0}[/]");
        AnsiConsole.MarkupLineInterpolated($"  determinate & agree:   [green]{agree:N0}[/]");
        AnsiConsole.MarkupLineInterpolated($"  indeterminate (probe null → full-parse fallback): [cyan]{indeterminate:N0}[/]");
        AnsiConsole.MarkupLineInterpolated($"  parse returned null (decode also skips): [grey]{parseNull:N0}[/]");
        AnsiConsole.MarkupLineInterpolated($"  read failed:           [grey]{readFailed:N0}[/]");
        var mismatchColor = mismatches.Count == 0 ? "green" : "red";
        AnsiConsole.MarkupLineInterpolated($"  [{mismatchColor}]MISMATCHES:            {mismatches.Count:N0}[/] (must be 0)");
        foreach (var m in mismatches)
        {
            AnsiConsole.MarkupLineInterpolated($"    [red]{m}[/]");
        }
    }

    private static Command CreateDecodeCommand()
    {
        var command = new Command("decode",
            "Time NifConverter.Convert over a directory of Xbox 360 NIFs (bytes pre-loaded, CPU-only)");
        var dirArg = new Argument<string>("dir")
        {
            Description = "Directory of Xbox 360 (.nif) files, searched recursively"
        };
        var limitOpt = new Option<int>("--limit")
        {
            Description = "Maximum number of files (0 = all)",
            DefaultValueFactory = _ => 0
        };
        var repeatOpt = new Option<int>("--repeat")
        {
            Description = "Run the timed pass N times; best (fastest) pass is reported",
            DefaultValueFactory = _ => 3
        };
        var threadsOpt = new Option<int>("--threads")
        {
            Description = "Parallel decode threads (1 = serial, gives per-file percentiles; >1 = throughput only)",
            DefaultValueFactory = _ => 1
        };
        command.Arguments.Add(dirArg);
        command.Options.Add(limitOpt);
        command.Options.Add(repeatOpt);
        command.Options.Add(threadsOpt);
        command.SetAction(p => Decode(
            p.GetValue(dirArg)!, p.GetValue(limitOpt), p.GetValue(repeatOpt), p.GetValue(threadsOpt)));
        return command;
    }

    private static void Decode(string dir, int limit, int repeat, int threads)
    {
        var root = new DirectoryInfo(dir);
        if (!root.Exists)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Directory not found: {dir}[/]");
            return;
        }

        var files = root.EnumerateFiles("*.nif", SearchOption.AllDirectories).ToList();
        if (limit > 0 && files.Count > limit) files = files.Take(limit).ToList();
        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .nif files found.[/]");
            return;
        }

        // Pre-load all bytes so the timed section measures CPU decode only, not disk I/O.
        var inputs = new List<byte[]>(files.Count);
        long inputBytes = 0;
        foreach (var f in files)
        {
            try
            {
                var b = File.ReadAllBytes(f.FullName);
                inputs.Add(b);
                inputBytes += b.Length;
            }
            catch { /* skip unreadable */ }
        }
        AnsiConsole.MarkupLineInterpolated(
            $"Loaded [cyan]{inputs.Count:N0}[/] NIFs ([cyan]{inputBytes / (1024.0 * 1024.0):N1}[/] MB), threads=[cyan]{threads}[/], repeat=[cyan]{repeat}[/]");

        // Warmup: JIT the convert path + populate the schema Lazy<> once, untimed.
        var warm = Math.Min(inputs.Count, 16);
        for (var i = 0; i < warm; i++) { try { NifConverter.Convert(inputs[i]); } catch { } }

        var bestWallMs = double.MaxValue;
        double[]? bestPerFileMs = null;
        var ok = 0;
        var failed = 0;
        long outputBytes = 0;

        for (var pass = 0; pass < Math.Max(1, repeat); pass++)
        {
            var perFile = threads <= 1 ? new double[inputs.Count] : null;
            var passOk = 0;
            var passFailed = 0;
            long passOut = 0;
            var wall = Stopwatch.StartNew();

            if (threads <= 1)
            {
                for (var i = 0; i < inputs.Count; i++)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    var r = TryConvert(inputs[i]);
                    perFile![i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    if (r > 0) { passOk++; passOut += r; } else { passFailed++; }
                }
            }
            else
            {
                var localOk = 0;
                var localFailed = 0;
                long localOut = 0;
                Parallel.ForEach(
                    inputs,
                    new ParallelOptions { MaxDegreeOfParallelism = threads },
                    () => (ok: 0, failed: 0, outb: 0L),
                    (bytes, _, acc) =>
                    {
                        var r = TryConvert(bytes);
                        return r > 0 ? (acc.ok + 1, acc.failed, acc.outb + r) : (acc.ok, acc.failed + 1, acc.outb);
                    },
                    acc =>
                    {
                        Interlocked.Add(ref localOk, acc.ok);
                        Interlocked.Add(ref localFailed, acc.failed);
                        Interlocked.Add(ref localOut, acc.outb);
                    });
                passOk = localOk; passFailed = localFailed; passOut = localOut;
            }

            wall.Stop();
            if (wall.Elapsed.TotalMilliseconds < bestWallMs)
            {
                bestWallMs = wall.Elapsed.TotalMilliseconds;
                bestPerFileMs = perFile;
                ok = passOk; failed = passFailed; outputBytes = passOut;
            }
        }

        var sec = bestWallMs / 1000.0;
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Decode bench (best pass)[/]");
        AnsiConsole.MarkupLineInterpolated($"  converted ok:      [green]{ok:N0}[/]   failed/skipped: [yellow]{failed:N0}[/]");
        AnsiConsole.MarkupLineInterpolated($"  wall time:         [cyan]{bestWallMs:N1}[/] ms");
        AnsiConsole.MarkupLineInterpolated($"  throughput:        [cyan]{ok / Math.Max(1e-9, sec):N1}[/] files/s, [cyan]{inputBytes / (1024.0 * 1024.0) / Math.Max(1e-9, sec):N1}[/] MB/s (in)");
        AnsiConsole.MarkupLineInterpolated($"  output bytes:      [cyan]{outputBytes / (1024.0 * 1024.0):N1}[/] MB");

        if (bestPerFileMs is not null && ok > 0)
        {
            var sorted = bestPerFileMs.Where(static m => m > 0).OrderBy(static m => m).ToArray();
            if (sorted.Length > 0)
            {
                double Pct(double q) => sorted[Math.Clamp((int)(q * (sorted.Length - 1)), 0, sorted.Length - 1)];
                AnsiConsole.MarkupLineInterpolated(
                    $"  per-file ms:       mean [cyan]{sorted.Average():N3}[/]  p50 [cyan]{Pct(0.50):N3}[/]  p95 [cyan]{Pct(0.95):N3}[/]  p99 [cyan]{Pct(0.99):N3}[/]  max [cyan]{sorted[^1]:N3}[/]");
            }
        }
    }

    private static long TryConvert(byte[] bytes)
    {
        try
        {
            var r = NifConverter.Convert(bytes);
            return r.Success && r.OutputData is not null ? r.OutputData.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static Command CreateHalfMicroCommand()
    {
        var command = new Command("half-micro",
            "Micro-benchmark: hardware HalfToFloat vs legacy Math.Pow over all 65536 half values");
        var countOpt = new Option<int>("--passes")
        {
            Description = "Number of passes over the full 65536-value table",
            DefaultValueFactory = _ => 2000
        };
        command.Options.Add(countOpt);
        command.SetAction(p => HalfMicro(p.GetValue(countOpt)));
        return command;
    }

    private static void HalfMicro(int passes)
    {
        passes = Math.Max(1, passes);
        const int tableSize = 65536;
        var total = (long)passes * tableSize;

        // Warmup + correctness check: the hardware path must equal the legacy path for every finite value.
        var mismatches = 0;
        for (var h = 0; h < tableSize; h++)
        {
            var hw = BinaryUtils.HalfToFloat((ushort)h);
            var legacy = LegacyHalfToFloat((ushort)h);
            if (!float.IsNaN(hw) && !float.IsNaN(legacy) && Math.Abs(hw - legacy) > Math.Abs(legacy) * 1e-4f + 1e-7f)
            {
                mismatches++;
            }
        }

        float sink = 0;
        var swHw = Stopwatch.StartNew();
        for (var pass = 0; pass < passes; pass++)
        {
            for (var h = 0; h < tableSize; h++) sink += BinaryUtils.HalfToFloat((ushort)h);
        }
        swHw.Stop();

        var swLegacy = Stopwatch.StartNew();
        for (var pass = 0; pass < passes; pass++)
        {
            for (var h = 0; h < tableSize; h++) sink += LegacyHalfToFloat((ushort)h);
        }
        swLegacy.Stop();

        var hwNs = swHw.Elapsed.TotalMilliseconds * 1_000_000.0 / total;
        var legacyNs = swLegacy.Elapsed.TotalMilliseconds * 1_000_000.0 / total;
        AnsiConsole.MarkupLineInterpolated($"[grey](checksum {sink:E2}; correctness mismatches: {mismatches})[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]HalfToFloat micro-benchmark[/]");
        AnsiConsole.MarkupLineInterpolated($"  ops:               [cyan]{total:N0}[/]");
        AnsiConsole.MarkupLineInterpolated($"  hardware (current):[green]{hwNs:N3}[/] ns/op  ([cyan]{swHw.Elapsed.TotalMilliseconds:N1}[/] ms)");
        AnsiConsole.MarkupLineInterpolated($"  legacy Math.Pow:   [yellow]{legacyNs:N3}[/] ns/op  ([cyan]{swLegacy.Elapsed.TotalMilliseconds:N1}[/] ms)");
        AnsiConsole.MarkupLineInterpolated($"  speedup:           [bold green]{legacyNs / Math.Max(1e-9, hwNs):N1}×[/]");
    }

    /// <summary>The pre-optimization HalfToFloat (two Math.Pow calls per finite value), kept here
    /// only as a benchmark reference — production uses <see cref="BinaryUtils.HalfToFloat" />.</summary>
    private static float LegacyHalfToFloat(ushort half)
    {
        var sign = (half >> 15) & 1;
        var exp = (half >> 10) & 0x1F;
        var mant = half & 0x3FF;

        if (exp == 0)
        {
            if (mant == 0) return sign == 1 ? -0.0f : 0.0f;
            var value = (float)Math.Pow(2, -14) * (mant / 1024.0f);
            return sign == 1 ? -value : value;
        }

        if (exp == 31)
        {
            if (mant == 0) return sign == 1 ? float.NegativeInfinity : float.PositiveInfinity;
            return float.NaN;
        }

        var normalizedValue = (float)Math.Pow(2, exp - 15) * (1 + mant / 1024.0f);
        return sign == 1 ? -normalizedValue : normalizedValue;
    }
}
