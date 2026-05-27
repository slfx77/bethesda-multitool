using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Whole-DMP sweep for TESObjectREFR-shaped heap objects. Reports per-grid-cell
///     counts (total + NULL-pParentCell) so we can estimate how many placements the
///     cell-traversal pipeline misses. Companion to <c>dmp scan-cell</c> which scans
///     a single cell's bounds; this sweeps the whole memory and buckets by
///     <c>floor(X/4096), floor(Y/4096)</c>.
/// </summary>
internal static class DmpRefrSweepCommand
{
    private const float CellWorldSize = 4096f;

    // TESObjectREFR final layout (post-xex29). Early-era dumps use shift = -4.
    private const int VftableOffset = 0;
    private const int FormIdOffset = 12;
    private const int BaseObjectPtrOffset = 48; // pObjectReference (TESForm*)
    private const int LocationXOffset = 64;
    private const int LocationYOffset = 68;
    private const int LocationZOffset = 72;
    private const int RefScaleOffset = 76;
    private const int ParentCellOffset = 80;
    private const int RefrStructTail = 92;

    private const uint ModuleVaLo = 0x82000000u;
    private const uint ModuleVaHi = 0x84000000u;
    private const uint HeapVaLo = 0x40000000u;

    private const float WorldExtent = 500_000f;
    private const float MaxZ = 200_000f;

    public static Command CreateSweepRefrsCommand()
    {
        var command = new Command(
            "sweep-refrs",
            "Whole-DMP sweep for heap-resident TESObjectREFRs. Buckets by grid cell + NULL-pParentCell state. " +
            "Compares to cell_capture_audit_summary.csv when present to flag pipeline gaps.");

        var inputArg = new Argument<string>("input")
        {
            Description = "Path to a .dmp file or directory of .dmp files"
        };
        var outOpt = new Option<string?>("-o", "--output")
        {
            Description = "Output CSV path (default: TestOutput/dangling_refr_sweep.csv)"
        };
        var perRefrOpt = new Option<string?>("--per-refr-out")
        {
            Description =
                "Optional path for per-REFR positions CSV (FormId, X, Y, Z, Scale, GridX, GridY, " +
                "FoundInDumps). Includes only NULL-pParentCell refs in exterior grids (|grid| >= 5)."
        };
        var perRefrNearOriginOpt = new Option<int>("--per-refr-near-origin")
        {
            Description = "Squared-grid cutoff for excluding near-origin noise from per-REFR output",
            DefaultValueFactory = _ => 25
        };
        var auditOpt = new Option<string?>("--audit")
        {
            Description = "Path to cell_capture_audit_summary.csv (default: TestOutput/cell_capture_audit_summary.csv)"
        };
        var earlyOpt = new Option<bool>("--early")
        {
            Description = "Force early-era REFR offsets (shift -4). Default: probe per-DMP."
        };
        var maxParallelOpt = new Option<int>("--max-parallel")
        {
            Description = "Max DMPs to scan in parallel",
            DefaultValueFactory = _ => Math.Max(2, Environment.ProcessorCount / 2)
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(outOpt);
        command.Options.Add(auditOpt);
        command.Options.Add(earlyOpt);
        command.Options.Add(maxParallelOpt);
        command.Options.Add(perRefrOpt);
        command.Options.Add(perRefrNearOriginOpt);

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outOpt) ?? Path.Combine("TestOutput", "dangling_refr_sweep.csv");
            var audit = parseResult.GetValue(auditOpt) ?? Path.Combine("TestOutput", "cell_capture_audit_summary.csv");
            var early = parseResult.GetValue(earlyOpt);
            var maxParallel = parseResult.GetValue(maxParallelOpt);
            var perRefr = parseResult.GetValue(perRefrOpt);
            var perRefrCutoff = parseResult.GetValue(perRefrNearOriginOpt);
            return Run(input, output, audit, early, maxParallel, perRefr, perRefrCutoff);
        });

        return command;
    }

    private static int Run(string input, string outputCsv, string auditCsv, bool forceEarly, int maxParallel,
        string? perRefrCsv, int perRefrNearOriginCutoff)
    {
        var dmpFiles = ResolveDumps(input);
        if (dmpFiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No .dmp files found at:[/] " + input);
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Sweeping[/] {dmpFiles.Count} dump(s) with up to [cyan]{maxParallel}[/] in parallel");

        var auditLookup = LoadAuditLookup(auditCsv);
        AnsiConsole.MarkupLine(auditLookup == null
            ? "[yellow]No audit CSV — pipeline-gap column will be blank.[/]"
            : $"Loaded audit data for [cyan]{auditLookup.Count}[/] (dump,gx,gy) tuples from {auditCsv}");

        var results = new ConcurrentBag<DumpResult>();
        var done = 0;

        Parallel.ForEach(dmpFiles, new ParallelOptions { MaxDegreeOfParallelism = maxParallel }, file =>
        {
            try
            {
                var r = SweepOne(file, forceEarly, perRefrNearOriginCutoff);
                results.Add(r);
                var n = Interlocked.Increment(ref done);
                AnsiConsole.MarkupLine(
                    $"[grey]  ({n}/{dmpFiles.Count})[/] {Path.GetFileName(file)} " +
                    $"hits={r.TotalRefrs} null-parent={r.NullParentRefrs} cells={r.CellBuckets.Count} layout={(r.EarlyEra ? "early" : "final")}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]ERROR scanning {Path.GetFileName(file)}: {ex.Message.EscapeMarkup()}[/]");
            }
        });

        WriteCsv(outputCsv, results, auditLookup);
        AnsiConsole.MarkupLine($"\n[green]Wrote[/] {outputCsv}");

        if (!string.IsNullOrEmpty(perRefrCsv))
        {
            WritePerRefrCsv(perRefrCsv, results);
            AnsiConsole.MarkupLine($"[green]Wrote per-REFR positions[/] {perRefrCsv}");
        }

        PrintSummary(results, auditLookup);
        return 0;
    }

    private static List<string> ResolveDumps(string input)
    {
        if (Directory.Exists(input))
        {
            return Directory.GetFiles(input, "*.dmp")
                .Where(f => !Path.GetFileName(f).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }

        return File.Exists(input) ? [input] : [];
    }

    private static Dictionary<(string Dump, int Gx, int Gy), AuditEntry>? LoadAuditLookup(string auditPath)
    {
        if (!File.Exists(auditPath))
        {
            return null;
        }

        var lookup = new Dictionary<(string, int, int), AuditEntry>();
        using var reader = new StreamReader(auditPath);
        reader.ReadLine(); // header

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var cols = ParseCsvRow(line);
            if (cols.Count < 12)
            {
                continue;
            }

            // Dump=3, Worldspace=4, WorldspaceFormId=5, CellEditorId=6, CellFormId=7, Grid=8, PlacementCount=10, Status=11
            var dump = cols[3];
            var grid = cols[8];
            if (!int.TryParse(cols[10], out var count))
            {
                continue;
            }

            var (gx, gy) = ParseGrid(grid);
            if (gx == int.MinValue)
            {
                continue;
            }

            lookup[(dump, gx, gy)] = new AuditEntry
            {
                Worldspace = cols[4],
                WorldspaceFormId = cols[5],
                CellEditorId = cols[6],
                CellFormId = cols[7],
                PlacementCount = count
            };
        }

        return lookup;
    }

    private static List<string> ParseCsvRow(string line)
    {
        // Audit CSV uses "quoted","fields","..."
        var result = new List<string>();
        var cur = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(cur.ToString());
                cur.Clear();
            }
            else
            {
                cur.Append(c);
            }
        }

        result.Add(cur.ToString());
        return result;
    }

    private static (int, int) ParseGrid(string grid)
    {
        // Format: "[-14,-15]"
        var trimmed = grid.Trim('[', ']');
        var parts = trimmed.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var gx) || !int.TryParse(parts[1], out var gy))
        {
            return (int.MinValue, int.MinValue);
        }

        return (gx, gy);
    }

    private static DumpResult SweepOne(string dumpPath, bool forceEarly, int perRefrNearOriginCutoff)
    {
        var info = MinidumpParser.Parse(dumpPath);
        var fileInfo = new FileInfo(dumpPath);
        using var mmf = MemoryMappedFile.CreateFromFile(
            dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        // Probe layout: try both shifts on the first ~16 MB of region data, pick winner by hit count.
        int shift;
        if (forceEarly)
        {
            shift = -4;
        }
        else
        {
            shift = ProbeLayout(accessor, info);
        }

        var hits = new List<RefrHit>();
        foreach (var region in info.MemoryRegions)
        {
            ScanRegion(accessor, region, shift, hits);
        }

        // De-dupe by VA (each REFR struct can produce one hit at its base).
        var deduped = hits
            .GroupBy(h => h.VA)
            .Select(g => g.First())
            .ToList();

        // Resolve base-form pointer for each hit. Follow pObjectReference VA -> file offset,
        // read TESForm at +0 (vftable), +4 (formType), +12 (FormID). When the pointer
        // isn't in any captured region, BaseFormId stays 0.
        var fileSize = fileInfo.Length;
        var tesFormBuf = new byte[24];
        foreach (var h in deduped)
        {
            if (h.BaseObjectPtr == 0)
            {
                continue;
            }

            var pVaLong = (long)h.BaseObjectPtr;
            var baseFo = info.VirtualAddressToFileOffset(pVaLong);
            if (!baseFo.HasValue || baseFo.Value + 24 > fileSize)
            {
                continue;
            }

            try
            {
                accessor.ReadArray(baseFo.Value, tesFormBuf, 0, 24);
            }
            catch
            {
                continue;
            }

            var ft = tesFormBuf[4];
            if (ft > 200)
            {
                continue;
            }

            var bfid = BinaryPrimitives.ReadUInt32BigEndian(tesFormBuf.AsSpan(12));
            if (bfid == 0 || bfid == 0xFFFFFFFFu)
            {
                continue;
            }

            h.BaseFormId = bfid;
            h.BaseFormType = ft;
        }

        var buckets = new Dictionary<(int, int), CellBucket>();
        var nullParentByFormId = new Dictionary<uint, RefrHit>();
        foreach (var h in deduped)
        {
            var gx = (int)Math.Floor(h.X / CellWorldSize);
            var gy = (int)Math.Floor(h.Y / CellWorldSize);
            var key = (gx, gy);
            if (!buckets.TryGetValue(key, out var b))
            {
                b = new CellBucket();
                buckets[key] = b;
            }

            b.Total++;
            if (h.ParentCell == 0)
            {
                b.NullParent++;

                // Track per-FormID positions for the per-REFR output (filtered to exterior).
                if (gx * gx + gy * gy >= perRefrNearOriginCutoff)
                {
                    nullParentByFormId.TryAdd(h.FormId, h);
                }
            }

            b.FormIds.Add(h.FormId);
            b.Vftables.Add(h.Vftable);
            if (b.SampleFormIds.Count < 5 && h.FormId != 0)
            {
                b.SampleFormIds.Add(h.FormId);
            }
        }

        return new DumpResult
        {
            DumpName = Path.GetFileNameWithoutExtension(dumpPath),
            EarlyEra = shift == -4,
            TotalRefrs = deduped.Count,
            NullParentRefrs = deduped.Count(h => h.ParentCell == 0),
            CellBuckets = buckets,
            NullParentByFormId = nullParentByFormId
        };
    }

    private static int ProbeLayout(MemoryMappedViewAccessor accessor, MinidumpInfo info)
    {
        // Try both layouts on a sample. Whichever finds more REFR-shape hits wins.
        long probedBytes = 0;
        const long probeBudget = 32 * 1024 * 1024;

        var finalHits = 0;
        var earlyHits = 0;

        foreach (var region in info.MemoryRegions)
        {
            if (probedBytes >= probeBudget)
            {
                break;
            }

            var bytesToRead = (int)Math.Min(region.Size, probeBudget - probedBytes);
            if (bytesToRead < 96)
            {
                continue;
            }

            var buf = new byte[bytesToRead];
            accessor.ReadArray(region.FileOffset, buf, 0, bytesToRead);

            for (var i = 0; i + RefrStructTail <= bytesToRead; i += 4)
            {
                if (ProbeRefrShape(buf, i, 0))
                {
                    finalHits++;
                }

                if (ProbeRefrShape(buf, i, -4))
                {
                    earlyHits++;
                }
            }

            probedBytes += bytesToRead;
        }

        return earlyHits > finalHits * 1.2 ? -4 : 0;
    }

    private static bool ProbeRefrShape(byte[] buf, int start, int shift)
    {
        var xOff = LocationXOffset + shift;
        var pCellOff = ParentCellOffset + shift;
        if (start + pCellOff + 4 > buf.Length || start + xOff + 4 > buf.Length)
        {
            return false;
        }

        var vft = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + VftableOffset));
        if (vft < ModuleVaLo || vft >= ModuleVaHi)
        {
            return false;
        }

        var fid = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + FormIdOffset));
        if (fid == 0 || fid == 0xFFFFFFFFu)
        {
            return false;
        }

        var x = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + xOff));
        var y = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + xOff + 4));
        var z = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + xOff + 8));
        if (!IsBoundedFloat(x, WorldExtent) || !IsBoundedFloat(y, WorldExtent) || !IsBoundedFloat(z, MaxZ))
        {
            return false;
        }

        var scale = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + RefScaleOffset + shift));
        if (float.IsNaN(scale) || scale <= 0.01f || scale > 100f)
        {
            return false;
        }

        var pCell = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + pCellOff));
        return pCell == 0 || (pCell >= HeapVaLo && pCell < ModuleVaHi);
    }

    private static bool IsBoundedFloat(float v, float bound)
    {
        return !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) <= bound;
    }

    private static void ScanRegion(
        MemoryMappedViewAccessor accessor,
        MinidumpMemoryRegion region,
        int shift,
        List<RefrHit> output)
    {
        var size = region.Size;
        if (size < RefrStructTail)
        {
            return;
        }

        var buf = new byte[size];
        accessor.ReadArray(region.FileOffset, buf, 0, (int)size);

        var xOff = LocationXOffset + shift;
        var yOff = LocationYOffset + shift;
        var zOff = LocationZOffset + shift;
        var scaleOff = RefScaleOffset + shift;
        var pCellOff = ParentCellOffset + shift;
        var maxStart = (int)(size - Math.Max(zOff + 4, pCellOff + 4));

        for (var start = 0; start <= maxStart; start += 4)
        {
            var vft = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + VftableOffset));
            if (vft < ModuleVaLo || vft >= ModuleVaHi)
            {
                continue;
            }

            var fid = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + FormIdOffset));
            if (fid == 0 || fid == 0xFFFFFFFFu)
            {
                continue;
            }

            var x = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + xOff));
            if (!IsBoundedFloat(x, WorldExtent))
            {
                continue;
            }

            var y = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + yOff));
            if (!IsBoundedFloat(y, WorldExtent))
            {
                continue;
            }

            var z = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + zOff));
            if (!IsBoundedFloat(z, MaxZ))
            {
                continue;
            }

            var scale = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + scaleOff));
            if (float.IsNaN(scale) || scale <= 0.01f || scale > 100f)
            {
                continue;
            }

            var pCell = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + pCellOff));
            if (pCell != 0 && (pCell < HeapVaLo || pCell >= ModuleVaHi))
            {
                continue;
            }

            var pBase = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + BaseObjectPtrOffset + shift));
            output.Add(new RefrHit
            {
                VA = (uint)(region.VirtualAddress + start),
                FormId = fid,
                Vftable = vft,
                X = x,
                Y = y,
                Z = z,
                Scale = scale,
                ParentCell = pCell,
                BaseObjectPtr = pBase
            });
        }
    }

    private static void WriteCsv(
        string outputPath,
        IEnumerable<DumpResult> results,
        Dictionary<(string, int, int), AuditEntry>? audit)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var w = new StreamWriter(outputPath);
        w.WriteLine("Dump,Layout,GridX,GridY,TotalRefrs,NullParentRefrs,DistinctFormIds,DistinctVftables,AuditPlacementCount,AuditCellEditorId,AuditCellFormId,AuditWorldspace,SampleFormIds");

        foreach (var r in results.OrderBy(r => r.DumpName, StringComparer.Ordinal))
        {
            foreach (var ((gx, gy), b) in r.CellBuckets.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2))
            {
                AuditEntry? auditEntry = null;
                audit?.TryGetValue((r.DumpName, gx, gy), out auditEntry);

                var sample = string.Join(';', b.SampleFormIds.Select(f => $"0x{f:X8}"));
                w.WriteLine(string.Join(",",
                    Csv(r.DumpName),
                    r.EarlyEra ? "early" : "final",
                    gx.ToString(CultureInfo.InvariantCulture),
                    gy.ToString(CultureInfo.InvariantCulture),
                    b.Total.ToString(CultureInfo.InvariantCulture),
                    b.NullParent.ToString(CultureInfo.InvariantCulture),
                    b.FormIds.Count.ToString(CultureInfo.InvariantCulture),
                    b.Vftables.Count.ToString(CultureInfo.InvariantCulture),
                    auditEntry?.PlacementCount.ToString(CultureInfo.InvariantCulture) ?? "",
                    Csv(auditEntry?.CellEditorId ?? ""),
                    Csv(auditEntry?.CellFormId ?? ""),
                    Csv(auditEntry?.Worldspace ?? ""),
                    Csv(sample)));
            }
        }
    }

    private static string Csv(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
        {
            return $"\"{s.Replace("\"", "\"\"")}\"";
        }

        return s;
    }

    /// <summary>
    ///     Writes per-REFR positions, deduped by FormID across all dumps. Each row is one
    ///     unique TESObjectREFR FormID with the position observed in the most recent dump
    ///     it appeared in (alphabetical order, which roughly tracks chronology for the
    ///     Fallout_Release_Beta.xex* set). FoundInDumps counts how many dumps it appeared in.
    /// </summary>
    private static void WritePerRefrCsv(string outputPath, IEnumerable<DumpResult> results)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Merge: FormID -> (representative hit, dump count, list of dumps seen).
        var merged = new Dictionary<uint, (RefrHit Hit, int Count)>();
        foreach (var r in results.OrderBy(r => r.DumpName, StringComparer.Ordinal))
        {
            foreach (var (fid, hit) in r.NullParentByFormId)
            {
                if (merged.TryGetValue(fid, out var entry))
                {
                    // Keep more recent (lexically later) dump's position observation.
                    merged[fid] = (hit, entry.Count + 1);
                }
                else
                {
                    merged[fid] = (hit, 1);
                }
            }
        }

        using var w = new StreamWriter(outputPath);
        w.WriteLine("FormId,X,Y,Z,Scale,GridX,GridY,Vftable,BaseFormId,BaseFormType,FoundInDumps");

        foreach (var (fid, (hit, count)) in merged.OrderBy(kv => kv.Key))
        {
            // Position-derived grid (so consumers don't have to recompute)
            var gx = (int)Math.Floor(hit.X / CellWorldSize);
            var gy = (int)Math.Floor(hit.Y / CellWorldSize);

            w.WriteLine(string.Join(",",
                $"0x{fid:X8}",
                hit.X.ToString("F2", CultureInfo.InvariantCulture),
                hit.Y.ToString("F2", CultureInfo.InvariantCulture),
                hit.Z.ToString("F2", CultureInfo.InvariantCulture),
                hit.Scale.ToString("F2", CultureInfo.InvariantCulture),
                gx.ToString(CultureInfo.InvariantCulture),
                gy.ToString(CultureInfo.InvariantCulture),
                $"0x{hit.Vftable:X8}",
                hit.BaseFormId == 0 ? "" : $"0x{hit.BaseFormId:X8}",
                hit.BaseFormType == 0 ? "" : $"0x{hit.BaseFormType:X2}",
                count.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static void PrintSummary(IEnumerable<DumpResult> results, Dictionary<(string, int, int), AuditEntry>? audit)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Per-dump totals[/]");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Dump");
        table.AddColumn(new TableColumn("Layout"));
        table.AddColumn(new TableColumn("TotalRefrs").RightAligned());
        table.AddColumn(new TableColumn("NullParent").RightAligned());
        table.AddColumn(new TableColumn("Cells").RightAligned());
        table.AddColumn(new TableColumn("CellsNotInAudit").RightAligned());

        foreach (var r in results.OrderBy(r => r.DumpName, StringComparer.Ordinal))
        {
            var notInAudit = 0;
            if (audit != null)
            {
                foreach (var (key, _) in r.CellBuckets)
                {
                    if (!audit.ContainsKey((r.DumpName, key.Item1, key.Item2)))
                    {
                        notInAudit++;
                    }
                }
            }

            table.AddRow(
                r.DumpName,
                r.EarlyEra ? "early" : "final",
                r.TotalRefrs.ToString("N0", CultureInfo.InvariantCulture),
                r.NullParentRefrs.ToString("N0", CultureInfo.InvariantCulture),
                r.CellBuckets.Count.ToString(CultureInfo.InvariantCulture),
                audit == null ? "-" : notInAudit.ToString(CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);

        if (audit != null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Top 20 (dump, gx, gy) buckets NOT in audit, ranked by NullParent count[/]");

            var dangling = results
                .SelectMany(r => r.CellBuckets.Select(kv => (Dump: r.DumpName, Gx: kv.Key.Item1, Gy: kv.Key.Item2, Bucket: kv.Value)))
                .Where(t => !audit.ContainsKey((t.Dump, t.Gx, t.Gy)))
                .OrderByDescending(t => t.Bucket.NullParent)
                .Take(20)
                .ToList();

            var dt = new Table().Border(TableBorder.Rounded);
            dt.AddColumn("Dump");
            dt.AddColumn(new TableColumn("Grid").RightAligned());
            dt.AddColumn(new TableColumn("Total").RightAligned());
            dt.AddColumn(new TableColumn("NullParent").RightAligned());
            dt.AddColumn(new TableColumn("DistinctFormIds").RightAligned());

            foreach (var t in dangling)
            {
                dt.AddRow(
                    t.Dump,
                    $"({t.Gx}, {t.Gy})",
                    t.Bucket.Total.ToString("N0"),
                    t.Bucket.NullParent.ToString("N0"),
                    t.Bucket.FormIds.Count.ToString("N0"));
            }

            AnsiConsole.Write(dt);
        }
    }

    private sealed class RefrHit
    {
        public uint VA;
        public uint FormId;
        public uint Vftable;
        public float X;
        public float Y;
        public float Z;
        public float Scale;
        public uint ParentCell;

        /// <summary>VA of the base object pointer (pObjectReference at +48 in TESObjectREFR).</summary>
        public uint BaseObjectPtr;

        /// <summary>Resolved base form FormID (set in SweepOne after region scan via the dump-wide VA->FormID resolver).</summary>
        public uint BaseFormId;

        /// <summary>Resolved base form formType (e.g. 0x21 = STAT, 0x23 = MSTT, 0x26 = FLOR).</summary>
        public byte BaseFormType;
    }

    private sealed class CellBucket
    {
        public int Total;
        public int NullParent;
        public HashSet<uint> FormIds { get; } = [];
        public HashSet<uint> Vftables { get; } = [];
        public List<uint> SampleFormIds { get; } = [];
    }

    private sealed class DumpResult
    {
        public string DumpName { get; init; } = "";
        public bool EarlyEra { get; init; }
        public int TotalRefrs { get; init; }
        public int NullParentRefrs { get; init; }
        public Dictionary<(int, int), CellBucket> CellBuckets { get; init; } = [];

        /// <summary>
        ///     Per-FormID position data for NULL-pParentCell, exterior REFRs.
        ///     Keyed by FormID; if a FormID appears multiple times in the same dump
        ///     (rare — shouldn't happen for unique heap allocations), the first wins.
        /// </summary>
        public Dictionary<uint, RefrHit> NullParentByFormId { get; init; } = [];
    }

    private sealed class AuditEntry
    {
        public string Worldspace { get; init; } = "";
        public string WorldspaceFormId { get; init; } = "";
        public string CellEditorId { get; init; } = "";
        public string CellFormId { get; init; } = "";
        public int PlacementCount { get; init; }
    }
}
