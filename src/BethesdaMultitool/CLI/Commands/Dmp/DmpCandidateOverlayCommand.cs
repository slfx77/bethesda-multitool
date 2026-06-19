using System.Buffers.Binary;
using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Orchestration;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Dmp;

/// <summary>
///     Builds a thin, VIEW-ONLY overlay ESP from a reviewed candidate CSV so the unaligned
///     FormID-NN candidate clusters can be inspected WITH MODELS in the 3D world viewer.
///     <para>
///     For each candidate (a heap-resident REFR the cell-traversal pipeline never attributed to a
///     cell), this scans the DMP corpus to locate the struct, resolves its base-object FormID +
///     world position + rotation + scale, then emits the candidate as a REFR inside a SYNTHETIC NEW
///     interior cell (fresh 0x01 plugin FormID) named <c>CAND_&lt;cellEdid&gt;_&lt;status&gt;</c>.
///     The REFR's NAME still points at the master base FormID, so when the user loads
///     FalloutNV.esm + this overlay the viewer resolves each base's MODL and draws the cluster.
///     </para>
///     <para>
///     Synthetic NEW cells (not master-cell overrides) are deliberate: the GUI load-order merge is
///     record-level (an override CELL would REPLACE the master cell's whole placed-object list), so
///     a synthetic cell keeps each cluster isolated and browsable side-by-side with the real cell.
///     A coherent room-shaped arrangement reads as real content; a pile at the origin or scattered
///     floaters read as junk / misattribution.
///     </para>
///     This NEVER touches the conversion pipeline or the authority JSON — it is an analysis aid only.
/// </summary>
internal static class DmpCandidateOverlayCommand
{
    // TESObjectREFR final layout (xex30+). Early-era dumps use shift = -4. TESForm header
    // fields (vtable@0, formType@4, FormID@12) are NOT shifted; OBJ_REFR data fields are.
    private const int VftableOffset = 0;
    private const int FormIdOffset = 12;
    private const int BaseObjectPtrOffset = 48; // pObjectReference (TESForm*)
    private const int AngleXOffset = 52;
    private const int AngleYOffset = 56;
    private const int AngleZOffset = 60;
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

    // First master-dependent plugin index is 0x01 (single master: FalloutNV.esm).
    private const uint PluginIndexBase = 0x01000000u;

    public static Command Create()
    {
        var command = new Command(
            "candidate-overlay",
            "Build a VIEW-ONLY overlay ESP that places reviewed dangling-ref candidates into synthetic " +
            "interior cells (with their dump base/position) so you can inspect the clusters WITH MODELS " +
            "in the 3D viewer. Load FalloutNV.esm + this ESP via the GUI Load Order.");

        var csvArg = new Argument<string>("csv")
        {
            Description =
                "Candidate CSV (highstrong_spatial.csv or corpus_candidates_clean.csv). Header-driven: " +
                "needs FormID + PredCell columns; uses EDID/PredCellEDID and Spatial columns when present."
        };
        var dumpsOpt = new Option<string>("--dumps")
        {
            Description = "A .dmp file or a directory of .dmp files to scan for the candidate structs",
            Required = true
        };
        var outOpt = new Option<string?>("-o", "--output")
        {
            Description = "Output overlay ESP path (default: TestOutput/candidate_overlay.esp)"
        };
        var statusFilterOpt = new Option<string?>("--status")
        {
            Description =
                "Comma-separated Spatial statuses to include (e.g. OK,FLAG,NO_CLUSTER). " +
                "Default: all statuses present in the CSV. Ignored if the CSV has no Spatial column."
        };
        var splitByStatusOpt = new Option<bool>("--split-by-status")
        {
            Description =
                "Emit a separate synthetic cell per (PredCell, status) so OK / FLAG clusters are " +
                "browsable apart. Default true; set false to put a PredCell's candidates in one cell.",
            DefaultValueFactory = _ => true
        };
        var maxParallelOpt = new Option<int>("--max-parallel")
        {
            Description = "Max DMPs to scan in parallel",
            DefaultValueFactory = _ => Math.Max(2, Environment.ProcessorCount / 2)
        };

        command.Arguments.Add(csvArg);
        command.Options.Add(dumpsOpt);
        command.Options.Add(outOpt);
        command.Options.Add(statusFilterOpt);
        command.Options.Add(splitByStatusOpt);
        command.Options.Add(maxParallelOpt);

        command.SetAction(parseResult =>
        {
            var csv = parseResult.GetValue(csvArg)!;
            var dumps = parseResult.GetValue(dumpsOpt)!;
            var output = parseResult.GetValue(outOpt) ?? Path.Combine("TestOutput", "candidate_overlay.esp");
            var statusFilter = parseResult.GetValue(statusFilterOpt);
            var splitByStatus = parseResult.GetValue(splitByStatusOpt);
            var maxParallel = parseResult.GetValue(maxParallelOpt);
            return Run(csv, dumps, output, statusFilter, splitByStatus, maxParallel);
        });

        return command;
    }

    private static int Run(string csvPath, string dumpsPath, string outputPath, string? statusFilter,
        bool splitByStatus, int maxParallel)
    {
        if (!File.Exists(csvPath))
        {
            AnsiConsole.MarkupLine($"[red]Candidate CSV not found:[/] {csvPath}");
            return 1;
        }

        var dumps = ResolveDumps(dumpsPath);
        if (dumps.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .dmp files found at:[/] {dumpsPath}");
            return 1;
        }

        var statuses = statusFilter
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = ParseCandidateCsv(csvPath, statuses);
        if (candidates.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No candidates after filtering — nothing to emit.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Candidates:[/] {candidates.Count:N0}  (from {Path.GetFileName(csvPath)})");
        AnsiConsole.MarkupLine($"[blue]Scanning[/] {dumps.Count} dump(s) for the candidate structs " +
                               $"with up to [cyan]{maxParallel}[/] in parallel");

        var targetFids = candidates.Keys.ToHashSet();

        // FormID -> resolved struct (base, position, rotation, scale). First dump that yields a
        // non-zero base wins; a base-less hit is kept only until a base-resolved one replaces it.
        var resolved = new Dictionary<uint, ResolvedRef>();
        var resolvedLock = new object();
        var done = 0;

        ParallelWork.ForEach("dmp-overlay", dumps, ConcurrencyPolicy.Fixed(maxParallel), file =>
        {
            try
            {
                var hits = ScanDumpForTargets(file, targetFids);
                lock (resolvedLock)
                {
                    foreach (var (fid, hit) in hits)
                    {
                        if (!resolved.TryGetValue(fid, out var existing) ||
                            (existing.BaseFormId == 0 && hit.BaseFormId != 0))
                        {
                            resolved[fid] = hit;
                        }
                    }
                }

                var n = Interlocked.Increment(ref done);
                AnsiConsole.MarkupLine(
                    $"[grey]  ({n}/{dumps.Count})[/] {Path.GetFileName(file)} matched={hits.Count}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]ERROR scanning {Path.GetFileName(file)}: {ex.Message.EscapeMarkup()}[/]");
            }
        });

        var withBase = resolved.Where(kv => kv.Value.BaseFormId != 0).ToList();
        AnsiConsole.MarkupLine(
            $"\n[green]Located[/] {resolved.Count:N0}/{candidates.Count:N0} candidate struct(s); " +
            $"[green]{withBase.Count:N0}[/] resolved a base object.");

        if (withBase.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No candidate resolved a renderable base — nothing to emit.[/]");
            return 1;
        }

        // Group into synthetic interior cells, keyed by (PredCell[, status]).
        var groups = new Dictionary<(uint Cell, string Status), CellGroup>();
        foreach (var (fid, hit) in withBase)
        {
            var meta = candidates[fid];
            var status = splitByStatus ? meta.Status : "";
            var key = (meta.PredCell, status);
            if (!groups.TryGetValue(key, out var g))
            {
                g = new CellGroup { PredCell = meta.PredCell, Edid = meta.Edid, Status = status };
                groups[key] = g;
            }

            g.Refs.Add((fid, hit, meta.Status));
        }

        var (espBytes, traceRows) = BuildOverlay(groups);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllBytes(outputPath, espBytes);

        var tracePath = Path.ChangeExtension(outputPath, ".trace.csv");
        File.WriteAllText(tracePath, traceRows);

        PrintSummary(groups);
        AnsiConsole.MarkupLine($"\n[green]Wrote overlay ESP[/] {outputPath} ({espBytes.Length:N0} bytes)");
        AnsiConsole.MarkupLine($"[green]Wrote trace CSV[/]  {tracePath}");
        AnsiConsole.MarkupLine(
            "\n[bold]To view:[/] open [cyan]FalloutNV.esm[/] in the GUI, add [cyan]" +
            Path.GetFileName(outputPath).EscapeMarkup() +
            "[/] via Load Order, then browse the [yellow]CAND_*[/] interior cells in the 3D viewer.");

        return 0;
    }

    // ---- CSV parsing -------------------------------------------------------------------------

    private static Dictionary<uint, CandidateMeta> ParseCandidateCsv(
        string path, HashSet<string>? statusFilter)
    {
        var result = new Dictionary<uint, CandidateMeta>();

        // Shared read so it works even while the user has the CSV open for review.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);

        var header = sr.ReadLine();
        if (header == null)
        {
            return result;
        }

        var cols = header.Split(',');
        int Idx(params string[] names) => Array.FindIndex(cols,
            c => names.Any(n => string.Equals(c.Trim(), n, StringComparison.OrdinalIgnoreCase)));

        var fidIdx = Idx("FormID", "FormId");
        var cellIdx = Idx("PredCell");
        var edidIdx = Idx("EDID", "PredCellEDID");
        var statusIdx = Idx("Spatial", "Status");

        if (fidIdx < 0 || cellIdx < 0)
        {
            AnsiConsole.MarkupLine("[red]CSV missing required FormID / PredCell columns.[/]");
            return result;
        }

        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            var c = line.Split(',');
            if (c.Length <= Math.Max(fidIdx, cellIdx))
            {
                continue;
            }

            if (!TryParseHex(c[fidIdx], out var fid) || !TryParseHex(c[cellIdx], out var cell))
            {
                continue;
            }

            var status = statusIdx >= 0 && c.Length > statusIdx ? c[statusIdx].Trim().ToUpperInvariant() : "";
            if (statusFilter is { Count: > 0 } && status.Length > 0 && !statusFilter.Contains(status))
            {
                continue;
            }

            var edid = edidIdx >= 0 && c.Length > edidIdx ? c[edidIdx].Trim() : "";
            result[fid] = new CandidateMeta(cell, edid, status.Length == 0 ? "ALL" : status);
        }

        return result;
    }

    // ---- DMP scanning (struct locate + base/pos/rot/scale resolve) ---------------------------

    private static Dictionary<uint, ResolvedRef> ScanDumpForTargets(string dumpPath, IReadOnlySet<uint> targets)
    {
        var info = MinidumpParser.Parse(dumpPath);
        var fileInfo = new FileInfo(dumpPath);
        using var mmf = MemoryMappedFile.CreateFromFile(
            dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var shift = ProbeLayout(accessor, info);

        var hits = new Dictionary<uint, ResolvedRef>();
        foreach (var region in info.MemoryRegions)
        {
            ScanRegion(accessor, region, shift, targets, hits);
        }

        // Resolve each hit's base-object pointer: pObjectReference VA -> file offset, read TESForm
        // (formType@+4, FormID@+12). When the pointer is outside captured memory, base stays 0.
        var fileSize = fileInfo.Length;
        var tesFormBuf = new byte[24];
        foreach (var fid in hits.Keys.ToList())
        {
            var hit = hits[fid];
            if (hit.BaseObjectPtr == 0)
            {
                continue;
            }

            var baseFo = info.VirtualAddressToFileOffset(hit.BaseObjectPtr);
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

            hits[fid] = hit with { BaseFormId = bfid, BaseFormType = ft };
        }

        return hits;
    }

    private static void ScanRegion(
        MemoryMappedViewAccessor accessor,
        MinidumpMemoryRegion region,
        int shift,
        IReadOnlySet<uint> targets,
        Dictionary<uint, ResolvedRef> output)
    {
        var size = region.Size;
        if (size < RefrStructTail)
        {
            return;
        }

        var buf = new byte[size];
        accessor.ReadArray(region.FileOffset, buf, 0, (int)size);

        var rxOff = AngleXOffset + shift;
        var ryOff = AngleYOffset + shift;
        var rzOff = AngleZOffset + shift;
        var xOff = LocationXOffset + shift;
        var yOff = LocationYOffset + shift;
        var zOff = LocationZOffset + shift;
        var scaleOff = RefScaleOffset + shift;
        var pCellOff = ParentCellOffset + shift;
        var baseOff = BaseObjectPtrOffset + shift;
        var maxStart = (int)(size - Math.Max(zOff + 4, pCellOff + 4));

        for (var start = 0; start <= maxStart; start += 4)
        {
            var vft = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + VftableOffset));
            if (vft < ModuleVaLo || vft >= ModuleVaHi)
            {
                continue;
            }

            var fid = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + FormIdOffset));
            if (!targets.Contains(fid) || output.ContainsKey(fid))
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

            var rx = ReadAngle(buf, start + rxOff);
            var ry = ReadAngle(buf, start + ryOff);
            var rz = ReadAngle(buf, start + rzOff);
            var pBase = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + baseOff));

            output[fid] = new ResolvedRef(x, y, z, rx, ry, rz, scale, pBase, 0, 0);
        }
    }

    private static float ReadAngle(byte[] buf, int offset)
    {
        var v = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(offset));
        return float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;
    }

    private static int ProbeLayout(MemoryMappedViewAccessor accessor, MinidumpInfo info)
    {
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
            if (bytesToRead < RefrStructTail)
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

    // ---- Overlay ESP emission ----------------------------------------------------------------

    private static (byte[] Esp, string TraceCsv) BuildOverlay(
        IReadOnlyDictionary<(uint Cell, string Status), CellGroup> groups)
    {
        var trace = new StringBuilder();
        trace.AppendLine("SyntheticCellFormId,CellEditorId,SyntheticRefFormId,CandidateFormId,BaseFormId,BaseFormType,Spatial,X,Y,Z,Scale");

        uint local = 0x800;
        uint NextFormId() => PluginIndexBase | local++;

        // Build the interior CELL section: GRUP(0,"CELL") -> GRUP(2,block) -> GRUP(3,subblock) -> cells.
        using var cellSection = new MemoryStream();
        var topPos = WriteGrup(cellSection, "CELL"u8.ToArray(), 0);
        var blockPos = WriteGrup(cellSection, LeBytes(0), 2);
        var subblockPos = WriteGrup(cellSection, LeBytes(0), 3);

        var recordCount = 0;
        foreach (var group in groups.Values.OrderBy(g => g.PredCell).ThenBy(g => g.Status, StringComparer.Ordinal))
        {
            var cellFid = NextFormId();
            var edid = MakeCellEditorId(group);

            var cellBytes = BuildCellRecord(cellFid, edid);
            cellSection.Write(cellBytes);
            recordCount++;

            var childPos = WriteGrup(cellSection, LeBytes(cellFid), 6);
            var tempPos = WriteGrup(cellSection, LeBytes(cellFid), 9);

            foreach (var (candFid, hit, status) in group.Refs.OrderBy(r => r.Item1))
            {
                var refFid = NextFormId();
                var refrBytes = BuildRefrRecord(refFid, hit);
                cellSection.Write(refrBytes);
                recordCount++;

                trace.AppendLine(string.Join(",",
                    $"0x{cellFid:X8}", edid, $"0x{refFid:X8}", $"0x{candFid:X8}",
                    $"0x{hit.BaseFormId:X8}", $"0x{hit.BaseFormType:X2}", status,
                    F(hit.X), F(hit.Y), F(hit.Z), hit.Scale.ToString("F3", CultureInfo.InvariantCulture)));
            }

            RecordHeaderProcessor.FinalizeGrupSize(cellSection, tempPos);
            RecordHeaderProcessor.FinalizeGrupSize(cellSection, childPos);
        }

        RecordHeaderProcessor.FinalizeGrupSize(cellSection, subblockPos);
        RecordHeaderProcessor.FinalizeGrupSize(cellSection, blockPos);
        RecordHeaderProcessor.FinalizeGrupSize(cellSection, topPos);

        var options = new PluginBuildOptions
        {
            MasterFileName = "FalloutNV.esm",
            Author = "Xbox360MemoryCarver",
            Description = "Candidate cluster overlay — VIEW ONLY, do not ship",
            // Leave this a plain ESP (no ESM/master flag): viewer-only, no navmesh edits.
            EmitMasterCellNavmAugmentation = false,
            ValidateOutput = false
        };
        var tes4 = Tes4HeaderBuilder.Build(options, (uint)recordCount, local);

        using var esp = new MemoryStream();
        esp.Write(tes4);
        esp.Write(cellSection.ToArray());
        return (esp.ToArray(), trace.ToString());
    }

    private static byte[] BuildCellRecord(uint formId, string editorId)
    {
        var edidBytes = Latin1Z(editorId);
        var subs = new List<EncodedSubrecord>
        {
            new("EDID", edidBytes),
            new("FULL", edidBytes),
            new("DATA", [0x01]) // interior flag (bit 0)
        };
        return PluginRecordByteBuilder.BuildNewRecordBytes("CELL", formId, 0, subs);
    }

    private static byte[] BuildRefrRecord(uint formId, ResolvedRef hit)
    {
        var name = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(name, hit.BaseFormId);

        var data = new byte[24];
        SubrecordEncoder.WriteFloat(data, 0, hit.X);
        SubrecordEncoder.WriteFloat(data, 4, hit.Y);
        SubrecordEncoder.WriteFloat(data, 8, hit.Z);
        SubrecordEncoder.WriteFloat(data, 12, hit.RotX);
        SubrecordEncoder.WriteFloat(data, 16, hit.RotY);
        SubrecordEncoder.WriteFloat(data, 20, hit.RotZ);

        var subs = new List<EncodedSubrecord> { new("NAME", name) };
        if (Math.Abs(hit.Scale - 1f) > 0.001f)
        {
            var xscl = new byte[4];
            SubrecordEncoder.WriteFloat(xscl, 0, hit.Scale);
            subs.Add(new EncodedSubrecord("XSCL", xscl));
        }

        subs.Add(new EncodedSubrecord("DATA", data));
        return PluginRecordByteBuilder.BuildNewRecordBytes("REFR", formId, 0, subs);
    }

    private static string MakeCellEditorId(CellGroup group)
    {
        var stem = string.IsNullOrEmpty(group.Edid) ? $"{group.PredCell:X8}" : group.Edid;
        var name = group.Status.Length == 0 || group.Status == "ALL"
            ? $"CAND_{stem}"
            : $"CAND_{stem}_{group.Status}";
        // Keep EDID to a sane length / charset.
        var clean = new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
        return clean.Length > 0 ? clean : $"CAND_{group.PredCell:X8}";
    }

    private static long WriteGrup(Stream stream, byte[] label, int groupType)
    {
        return RecordHeaderProcessor.WriteGrupHeader(stream, new GroupHeader
        {
            GroupSize = 0,
            Label = label,
            GroupType = groupType,
            Stamp = 0,
            Unknown = 0
        });
    }

    private static byte[] LeBytes(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }

    private static byte[] Latin1Z(string value)
    {
        var n = Encoding.Latin1.GetByteCount(value);
        var buf = new byte[n + 1];
        Encoding.Latin1.GetBytes(value, buf);
        return buf; // trailing null already zero
    }

    private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);

    // ---- summary / helpers -------------------------------------------------------------------

    private static void PrintSummary(
        IReadOnlyDictionary<(uint Cell, string Status), CellGroup> groups)
    {
        AnsiConsole.WriteLine();
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Synthetic cell");
        table.AddColumn("PredCell");
        table.AddColumn(new TableColumn("Refs").RightAligned());
        table.AddColumn(new TableColumn("Base in master").RightAligned());
        table.AddColumn("Status");

        foreach (var g in groups.Values.OrderByDescending(g => g.Refs.Count).ThenBy(g => g.PredCell))
        {
            var inMaster = g.Refs.Count(r => r.Item2.BaseFormId < PluginIndexBase);
            table.AddRow(
                MakeCellEditorId(g).EscapeMarkup(),
                $"0x{g.PredCell:X8}",
                g.Refs.Count.ToString("N0", CultureInfo.InvariantCulture),
                inMaster.ToString("N0", CultureInfo.InvariantCulture),
                g.Status.EscapeMarkup());
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"[dim]Synthetic interior cells: {groups.Count:N0}; total candidate REFRs emitted: " +
            $"{groups.Values.Sum(g => g.Refs.Count):N0}[/]");
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

    private static bool TryParseHex(string s, out uint value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private readonly record struct CandidateMeta(uint PredCell, string Edid, string Status);

    private readonly record struct ResolvedRef(
        float X, float Y, float Z,
        float RotX, float RotY, float RotZ,
        float Scale, uint BaseObjectPtr, uint BaseFormId, byte BaseFormType);

    private sealed class CellGroup
    {
        public uint PredCell { get; init; }
        public string Edid { get; init; } = "";
        public string Status { get; init; } = "";
        public List<(uint, ResolvedRef, string)> Refs { get; } = [];
    }
}
