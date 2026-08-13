using System.CommandLine;
using System.IO.MemoryMappedFiles;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.VersionTracking.Extraction;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Dmp;

/// <summary>
///     Scans all DMP files in a directory and produces a cross-build census of FormType byte values.
///     Identifies enum drift by finding FormType values present in some builds but not others,
///     with sample EditorIDs to confirm what record type each byte maps to.
/// </summary>
internal static class DmpFormTypeCensusCommand
{
    public static Command Create()
    {
        var dirArg = new Argument<string>("directory") { Description = "Directory containing .dmp files" };
        var verboseOpt = new Option<bool>("--verbose", "-v")
        {
            Description = "Show per-DMP detail table",
            DefaultValueFactory = _ => false
        };
        var csvOpt = new Option<string?>("--csv")
        {
            Description =
                "Write the machine-readable corpus audit to this directory: dump_inventory.csv, " +
                "formtype_by_dump.csv, esm_record_by_dump.csv, mapping_coverage.csv."
        };

        var command = new Command("formtype-census",
            "Audit FormType byte distributions across all DMP files to detect enum drift");
        command.Arguments.Add(dirArg);
        command.Options.Add(verboseOpt);
        command.Options.Add(csvOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var dir = parseResult.GetValue(dirArg)!;
            var verbose = parseResult.GetValue(verboseOpt);
            var csv = parseResult.GetValue(csvOpt);
            await RunAsync(dir, verbose, csv, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(
        string dirPath, bool verbose, string? csvDir, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(dirPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Directory not found: {dirPath}");
            return;
        }

        var dmpFiles = Directory.GetFiles(dirPath, "*.dmp")
            .Where(f => !Path.GetFileName(f).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => new FileInfo(f).LastWriteTimeUtc)
            .ToList();

        if (dmpFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .dmp files found in:[/] {dirPath}");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]FormType Census — scanning {dmpFiles.Count} DMP files...[/]");
        AnsiConsole.WriteLine();

        // Collect per-DMP data
        var entries = new List<CensusEntry>();

        foreach (var dmpFile in dmpFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(dmpFile);
            try
            {
                var entry = ProcessDmp(dmpFile);
                entries.Add(entry);
                AnsiConsole.MarkupLine(
                    $"  [green]✓[/] {Markup.Escape(fileName)} — {entry.TotalEditorIds:N0} EditorIDs, " +
                    $"{entry.FormTypeCounts.Count} types, {entry.FileDate:yyyy-MM-dd}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(fileName)}: {Markup.Escape(ex.Message)}");
            }
        }

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No DMP files could be processed.[/]");
            return;
        }

        AnsiConsole.WriteLine();

        // Collect all unique FormType bytes
        var allFormTypes = entries
            .SelectMany(e => e.FormTypeCounts.Keys)
            .Distinct()
            .OrderBy(b => b)
            .ToList();

        // Section 1: Overview table
        RenderOverviewTable(entries, allFormTypes);

        // Section 2: Drift candidates
        RenderDriftReport(entries, allFormTypes);

        // Section 3: Per-DMP detail (verbose)
        if (verbose)
        {
            RenderPerDmpDetail(entries, allFormTypes);
        }

        if (!string.IsNullOrEmpty(csvDir))
        {
            await WriteCsvAsync(csvDir, dirPath, entries, cancellationToken);
        }
    }

    /// <summary>
    ///     Emits the corpus audit as CSV. Build dates come from the PE timestamp of the game
    ///     module inside each dump (the build's identity); the file's last-write time is the
    ///     capture date (when that build actually crashed), which is the axis content questions
    ///     want. The two differ by 0-3 days.
    /// </summary>
    private static async Task WriteCsvAsync(
        string csvDir,
        string dumpsDir,
        List<CensusEntry> entries,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(csvDir);

        var buildsByFile = BuildDiscovery.DiscoverBuilds(null, dumpsDir)
            .Where(b => b.SourcePath != null)
            .GroupBy(b => Path.GetFileName(b.SourcePath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        string BuildDate(CensusEntry e) =>
            buildsByFile.TryGetValue(e.FileName, out var b) && b.BuildDate.HasValue
                ? b.BuildDate.Value.ToString("yyyy-MM-dd")
                : "";

        string BuildType(CensusEntry e) =>
            buildsByFile.TryGetValue(e.FileName, out var b) ? b.BuildType ?? "" : "";

        string PeStamp(CensusEntry e) =>
            buildsByFile.TryGetValue(e.FileName, out var b) && b.PeTimestamp.HasValue
                ? $"0x{b.PeTimestamp.Value:X8}"
                : "";

        var ordered = entries.OrderBy(e => BuildDate(e), StringComparer.Ordinal)
            .ThenBy(e => e.FileDate)
            .ToList();

        // Every CSV below reports canonical FormType codes. allFormTypes is the raw-byte union
        // built for the drift report, so recompute the union over corrected codes.
        var canonicalTypes = ordered
            .SelectMany(e => e.CorrectedFormTypeCounts.Keys)
            .Where(f => f != 0)
            .Distinct()
            .OrderBy(f => f)
            .ToList();

        static string DriftSummary(CensusEntry e)
        {
            if (e.DriftRemap is not { Count: > 0 })
            {
                return "none";
            }

            var deltas = e.DriftRemap.Select(kv => kv.Value - kv.Key).Distinct().ToList();
            var delta = deltas.Count == 1 ? $"{deltas[0]:+0;-0}" : "mixed";
            return $"{delta}@0x{e.DriftRemap.Keys.Min():X2}-0x{e.DriftRemap.Keys.Max():X2}";
        }

        // 1. Per-dump inventory — the "is this dump worth anything" table.
        var inventory = new StringBuilder();
        inventory.AppendLine(
            "dump,build_date,pe_timestamp,build_type,capture_date,file_size,has_runtime_form_data," +
            "pallforms_va,runtime_editorids,runtime_refr_forms,runtime_land_forms,distinct_formtypes," +
            "esm_main_records,esm_records_be,esm_records_le,esm_record_types,sctx_count,sctx_bytes," +
            "formtype_drift");
        foreach (var e in ordered)
        {
            inventory.AppendLine(string.Join(',',
                Csv(e.FileName),
                BuildDate(e),
                PeStamp(e),
                Csv(BuildType(e)),
                e.FileDate.ToString("yyyy-MM-dd"),
                e.FileSize.ToString(),
                e.HasRuntimeFormData ? "yes" : "no",
                e.PAllFormsVa != 0 ? $"0x{e.PAllFormsVa:X8}" : "",
                e.TotalEditorIds.ToString(),
                e.RuntimeRefrForms.ToString(),
                e.RuntimeLandForms.ToString(),
                e.CorrectedFormTypeCounts.Count(kv => kv.Key != 0).ToString(),
                e.EsmRecordCounts.Values.Sum(t => t.Total).ToString(),
                e.EsmRecordCounts.Values.Sum(t => t.BigEndian).ToString(),
                e.EsmRecordCounts.Values.Sum(t => t.LittleEndian).ToString(),
                e.EsmRecordCounts.Count.ToString(),
                e.SctxCount.ToString(),
                e.SctxBytes.ToString(),
                DriftSummary(e)));
        }

        // 2. Long-format runtime FormType counts, canonical codes.
        var byDump = new StringBuilder();
        byDump.AppendLine("dump,build_date,capture_date,formtype_byte,signature,count");
        foreach (var e in ordered)
        {
            foreach (var ft in canonicalTypes.Where(f => e.CorrectedFormTypeCounts.ContainsKey(f)))
            {
                byDump.AppendLine(string.Join(',',
                    Csv(e.FileName),
                    BuildDate(e),
                    e.FileDate.ToString("yyyy-MM-dd"),
                    $"0x{ft:X2}",
                    RuntimeBuildOffsets.GetRecordTypeCode(ft) ?? "",
                    e.CorrectedFormTypeCounts[ft].ToString()));
            }
        }

        // 3. Long-format embedded-ESM-record counts (the other half of a dump's content:
        //    raw record bytes the engine kept around, distinct from live TESForm objects).
        var esmByDump = new StringBuilder();
        esmByDump.AppendLine("dump,build_date,capture_date,record_type,total,big_endian,little_endian");
        foreach (var e in ordered)
        {
            foreach (var (type, tally) in e.EsmRecordCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                esmByDump.AppendLine(string.Join(',',
                    Csv(e.FileName),
                    BuildDate(e),
                    e.FileDate.ToString("yyyy-MM-dd"),
                    Csv(type),
                    tally.Total.ToString(),
                    tally.BigEndian.ToString(),
                    tally.LittleEndian.ToString()));
            }
        }

        // 4. The gap table: what we observe vs what the converter can actually emit.
        //
        // "Has an encoder" is NOT the same as "is emitted", and using the registry alone as the
        // oracle produces errors in both directions: it over-reports (GRAS/IMGS/PWAT/TREE were
        // all registered but unreachable) and under-reports (LAND and NAVM are emitted with no
        // registry entry, via the cell-child-static and byte-rewriter paths). The reachability
        // gate is PluginBuilder's yield set; emission additionally needs a registry entry, and
        // new (non-override) records need a dispatcher row.
        var encoders = RecordEncoderRegistry.CreateDefault().SupportedRecordTypes
            .ToHashSet(StringComparer.Ordinal);
        var planners = PlannedEncoders.KnownRecordTypes().ToHashSet(StringComparer.Ordinal);
        var reachable = PluginBuilder.EmittableTopLevelRecordTypes;
        var dispatchable = NewTopLevelRecordEncoderDispatcher.GetSupportedRecordTypes()
            .ToHashSet(StringComparer.Ordinal);

        // Types that reach disk outside the top-level loop, so they are absent from the yield set
        // by design rather than by omission. Each is cited in the audit doc.
        var nonTopLevelEmission = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LAND"] = "cell-child-static (PlannedLandEncoder -> LandEncoder)",
            ["NAVM"] = "byte-rewriter (Plugin/Nav)",
            ["NAVI"] = "EsmAssembler fallback",
            ["REFR"] = "cell-children pipeline",
            ["ACHR"] = "cell-children pipeline",
            ["ACRE"] = "cell-children pipeline",
            ["PGRE"] = "cell-children pipeline",
            ["CELL"] = "cell hierarchy",
            ["DIAL"] = "DialogGrupBuilder",
            ["INFO"] = "DialogGrupBuilder"
        };

        string EmissionStatus(string sig)
        {
            if (nonTopLevelEmission.TryGetValue(sig, out var path))
            {
                return path;
            }

            if (!reachable.Contains(sig))
            {
                return encoders.Contains(sig) ? "UNREACHABLE (encoder exists, never yielded)" : "none";
            }

            if (!encoders.Contains(sig))
            {
                return "yielded but NO ENCODER";
            }

            return dispatchable.Contains(sig) ? "emitted" : "overrides only (no dispatcher row)";
        }
        var dumpsWithData = entries.Count(e => e.HasRuntimeFormData);

        // Key on the union of both evidence channels. A type can be recoverable purely as
        // embedded ESM record bytes with no live TESForm ever seen (LAND and the navmesh types
        // carry no EditorID, so they never enter the EditorID-keyed hash table) — keying on
        // runtime FormTypes alone would hide exactly the types the terrain/navmesh work needs.
        var byFormType = canonicalTypes
            .ToDictionary(ft => RuntimeBuildOffsets.GetRecordTypeCode(ft) ?? $"0x{ft:X2}", ft => ft);
        var allSignatures = byFormType.Keys
            .Concat(entries.SelectMany(e => e.EsmRecordCounts.Keys))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var coverage = new StringBuilder();
        coverage.AppendLine(
            "signature,formtype_byte,runtime_dumps,dumps_with_form_data,runtime_min,runtime_max," +
            "struct_size,emission_status,has_encoder,reachable,has_dispatcher,has_planner," +
            "esm_record_dumps,esm_record_max,evidence");
        foreach (var sig in allSignatures)
        {
            var hasFt = byFormType.TryGetValue(sig, out var ft);
            var present = hasFt
                ? entries.Where(e => e.CorrectedFormTypeCounts.ContainsKey(ft)).ToList()
                : [];
            var counts = present.Select(e => e.CorrectedFormTypeCounts[ft]).ToList();
            var esmDumps = entries.Where(e => e.EsmRecordCounts.ContainsKey(sig)).ToList();

            var evidence = (present.Count > 0, esmDumps.Count > 0) switch
            {
                (true, true) => "runtime+esm",
                (true, false) => "runtime",
                (false, true) => "esm-only",
                _ => "none"
            };

            coverage.AppendLine(string.Join(',',
                Csv(sig),
                hasFt ? $"0x{ft:X2}" : "",
                present.Count.ToString(),
                dumpsWithData.ToString(),
                counts.Count > 0 ? counts.Min().ToString() : "0",
                counts.Count > 0 ? counts.Max().ToString() : "0",
                hasFt ? RuntimeBuildOffsets.GetStructSize(ft).ToString() : "",
                Csv(EmissionStatus(sig)),
                encoders.Contains(sig) ? "yes" : "NO",
                reachable.Contains(sig) || nonTopLevelEmission.ContainsKey(sig) ? "yes" : "NO",
                dispatchable.Contains(sig) ? "yes" : "NO",
                planners.Contains(sig) ? "yes" : "NO",
                esmDumps.Count.ToString(),
                esmDumps.Count > 0 ? esmDumps.Max(e => e.EsmRecordCounts[sig].Total).ToString() : "0",
                evidence));
        }

        var files = new (string Name, StringBuilder Content)[]
        {
            ("dump_inventory.csv", inventory),
            ("formtype_by_dump.csv", byDump),
            ("esm_record_by_dump.csv", esmByDump),
            ("mapping_coverage.csv", coverage)
        };

        foreach (var (name, content) in files)
        {
            var path = Path.Combine(csvDir, name);
            await File.WriteAllTextAsync(path, content.ToString(), cancellationToken);
            AnsiConsole.MarkupLine($"[green]Wrote:[/] {Markup.Escape(path)}");
        }
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        value = value.Replace("\r", " ").Replace("\n", " ");
        return value.Contains(',') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static CensusEntry ProcessDmp(string dmpFile)
    {
        var fileName = Path.GetFileName(dmpFile);
        var fileInfo = new FileInfo(dmpFile);

        using var mmf = MemoryMappedFile.CreateFromFile(dmpFile, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        // ESM record scan (needed to populate scanResult for EditorID extraction)
        var scanResult = EsmRecordScanner.ScanForRecordsMemoryMapped(accessor, fileInfo.Length);

        // Parse minidump and extract runtime EditorIDs
        var minidumpInfo = MinidumpParser.Parse(dmpFile);
        if (minidumpInfo.IsValid)
        {
            EsmEditorIdExtractor.ExtractRuntimeEditorIds(
                accessor, fileInfo.Length, minidumpInfo, scanResult);
        }

        // Build histogram and sample EditorIDs
        var counts = new Dictionary<byte, int>();
        var samples = new Dictionary<byte, List<string>>();

        foreach (var entry in scanResult.RuntimeEditorIds)
        {
            var ft = entry.FormType;
            counts.TryGetValue(ft, out var count);
            counts[ft] = count + 1;

            if (!samples.TryGetValue(ft, out var sampleList))
            {
                sampleList = [];
                samples[ft] = sampleList;
            }

            if (sampleList.Count < 5)
            {
                sampleList.Add(entry.EditorId);
            }
        }

        var esmCounts = new Dictionary<string, EsmRecordTally>(StringComparer.Ordinal);
        foreach (var record in scanResult.MainRecords)
        {
            esmCounts.TryGetValue(record.RecordType, out var tally);
            esmCounts[record.RecordType] = tally.Add(record.IsBigEndian);
        }

        // The raw histogram above is what the drift report needs — it must see the bytes exactly
        // as this build wrote them. Every other consumer wants canonical (final-build) codes, so
        // apply the same correction MinidumpAnalyzer does and keep a second, corrected histogram.
        // Without this, an enum shift reads as "this record type is absent from the build", which
        // is indistinguishable from the content genuinely not existing yet.
        var remap = RuntimeBuildOffsets.ApplyDriftCorrection(scanResult);
        var correctedCounts = new Dictionary<byte, int>();
        foreach (var entry in scanResult.RuntimeEditorIds)
        {
            correctedCounts.TryGetValue(entry.FormType, out var count);
            correctedCounts[entry.FormType] = count + 1;
        }

        return new CensusEntry(
            fileName,
            fileInfo.LastWriteTimeUtc,
            scanResult.RuntimeEditorIds.Count,
            counts,
            samples)
        {
            FileSize = fileInfo.Length,
            RuntimeRefrForms = scanResult.RuntimeRefrFormEntries.Count,
            RuntimeLandForms = scanResult.RuntimeLandFormEntries.Count,
            PAllFormsVa = scanResult.PAllFormsVa,
            EsmRecordCounts = esmCounts,
            SctxCount = scanResult.ScriptSources.Count,
            SctxBytes = scanResult.ScriptSources.Sum(s => (long)s.Length),
            CorrectedFormTypeCounts = correctedCounts,
            DriftRemap = remap
        };
    }

    private static void RenderOverviewTable(List<CensusEntry> entries, List<byte> allFormTypes)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]FormType Overview[/]");

        table.AddColumn(new TableColumn("[bold]Byte[/]"));
        table.AddColumn(new TableColumn("[bold]Name[/]"));
        table.AddColumn(new TableColumn("[bold]DMPs[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Min[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Max[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Avg[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]First Seen[/]"));
        table.AddColumn(new TableColumn("[bold]Last Seen[/]"));
        table.AddColumn("[bold]Sample EditorIDs[/]");

        foreach (var ft in allFormTypes)
        {
            if (ft == 0) continue; // Skip zero FormType (unresolved)

            var dmpsWithType = entries.Where(e => e.FormTypeCounts.ContainsKey(ft)).ToList();
            var dmpCount = dmpsWithType.Count;
            var counts = dmpsWithType.Select(e => e.FormTypeCounts[ft]).ToList();

            var knownName = RuntimeBuildOffsets.GetRecordTypeCode(ft) ?? "???";
            var isUniversal = dmpCount == entries.Count;
            var isDrift = !isUniversal && dmpCount > 0;

            var firstSeen = dmpsWithType.MinBy(e => e.FileDate)!;
            var lastSeen = dmpsWithType.MaxBy(e => e.FileDate)!;

            // Collect sample EditorIDs across all DMPs for this FormType
            var allSamples = dmpsWithType
                .SelectMany(e => e.SampleEditorIds.GetValueOrDefault(ft) ?? [])
                .Distinct()
                .Take(3)
                .ToList();
            var sampleStr = allSamples.Count > 0 ? string.Join(", ", allSamples) : "";

            var nameColor = isDrift ? "yellow" : "white";
            string countColor;
            if (isDrift)
            {
                countColor = "yellow";
            }
            else if (isUniversal)
            {
                countColor = "green";
            }
            else
            {
                countColor = "grey";
            }

            table.AddRow(
                $"0x{ft:X2}",
                $"[{nameColor}]{Markup.Escape(knownName)}[/]",
                $"[{countColor}]{dmpCount}/{entries.Count}[/]",
                counts.Min().ToString("N0"),
                counts.Max().ToString("N0"),
                ((int)counts.Average()).ToString("N0"),
                Markup.Escape(ShortName(firstSeen.FileName)),
                Markup.Escape(ShortName(lastSeen.FileName)),
                Markup.Escape(sampleStr.Length > 60 ? sampleStr[..57] + "..." : sampleStr));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static void RenderDriftReport(List<CensusEntry> entries, List<byte> allFormTypes)
    {
        // Find FormTypes not present in all DMPs (drift candidates)
        var driftCandidates = allFormTypes
            .Where(ft => ft != 0)
            .Where(ft =>
            {
                var count = entries.Count(e => e.FormTypeCounts.ContainsKey(ft));
                return count > 0 && count < entries.Count;
            })
            .ToList();

        if (driftCandidates.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No FormType drift detected — all types present in all DMPs.[/]");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[bold yellow]Drift Candidates ({driftCandidates.Count} FormTypes not in all DMPs):[/]");
        AnsiConsole.WriteLine();

        foreach (var ft in driftCandidates)
        {
            var knownName = RuntimeBuildOffsets.GetRecordTypeCode(ft) ?? "???";
            var dmpsWithType = entries.Where(e => e.FormTypeCounts.ContainsKey(ft)).ToList();
            var dmpsWithout = entries.Where(e => !e.FormTypeCounts.ContainsKey(ft)).ToList();

            AnsiConsole.MarkupLine(
                $"  [yellow]0x{ft:X2}[/] ({knownName}): present in [green]{dmpsWithType.Count}[/], " +
                $"absent in [red]{dmpsWithout.Count}[/] DMPs");

            // Show chronological transition
            // Sort all entries by date, mark presence/absence
            var timeline = entries
                .Select(e => (e.FileName, e.FileDate, HasType: e.FormTypeCounts.ContainsKey(ft)))
                .OrderBy(x => x.FileDate)
                .ToList();

            // Find transition points (where presence changes)
            var transitions = new List<string>();
            for (var i = 1; i < timeline.Count; i++)
            {
                if (timeline[i].HasType != timeline[i - 1].HasType)
                {
                    var direction = timeline[i].HasType ? "APPEARS" : "DISAPPEARS";
                    transitions.Add(
                        $"{direction} between {ShortName(timeline[i - 1].FileName)} " +
                        $"({timeline[i - 1].FileDate:yyyy-MM-dd}) → " +
                        $"{ShortName(timeline[i].FileName)} ({timeline[i].FileDate:yyyy-MM-dd})");
                }
            }

            foreach (var t in transitions)
            {
                AnsiConsole.MarkupLine($"    [cyan]→ {Markup.Escape(t)}[/]");
            }

            // Sample EditorIDs from DMPs that HAVE this type
            var haveSamples = dmpsWithType
                .SelectMany(e => e.SampleEditorIds.GetValueOrDefault(ft) ?? [])
                .Distinct()
                .Take(5)
                .ToList();

            if (haveSamples.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"    EditorIDs (present): [dim]{Markup.Escape(string.Join(", ", haveSamples))}[/]");
            }

            // Check what EditorIDs the NEIGHBORING byte values have in DMPs without this type
            // This helps identify if a type shifted ±1
            foreach (var neighbor in new[] { (byte)(ft - 1), (byte)(ft + 1) })
            {
                if (neighbor == 0) continue;

                // Get EditorIDs for this neighbor byte from DMPs that DON'T have our target type
                var neighborSamples = dmpsWithout
                    .Where(e => e.SampleEditorIds.ContainsKey(neighbor))
                    .SelectMany(e => e.SampleEditorIds[neighbor])
                    .Distinct()
                    .Take(3)
                    .ToList();

                var neighborSamplesInHave = dmpsWithType
                    .Where(e => e.SampleEditorIds.ContainsKey(neighbor))
                    .SelectMany(e => e.SampleEditorIds[neighbor])
                    .Distinct()
                    .Take(3)
                    .ToList();

                if (neighborSamples.Count > 0)
                {
                    var neighborName = RuntimeBuildOffsets.GetRecordTypeCode(neighbor) ?? "???";
                    AnsiConsole.MarkupLine(
                        $"    Neighbor 0x{neighbor:X2} ({neighborName}) in absent DMPs: " +
                        $"[dim]{Markup.Escape(string.Join(", ", neighborSamples))}[/]");
                }

                if (neighborSamplesInHave.Count > 0)
                {
                    var neighborName = RuntimeBuildOffsets.GetRecordTypeCode(neighbor) ?? "???";
                    AnsiConsole.MarkupLine(
                        $"    Neighbor 0x{neighbor:X2} ({neighborName}) in present DMPs: " +
                        $"[dim]{Markup.Escape(string.Join(", ", neighborSamplesInHave))}[/]");
                }
            }

            AnsiConsole.WriteLine();
        }

        // Also check for types where the SAME byte maps to DIFFERENT record types across DMPs
        // (detectable when EditorIDs look fundamentally different)
        AnsiConsole.MarkupLine("[bold]Unknown FormType bytes[/] (not in RuntimeBuildOffsets):");
        var unknownTypes = allFormTypes
            .Where(ft => ft != 0 && RuntimeBuildOffsets.GetRecordTypeCode(ft) == null)
            .ToList();

        if (unknownTypes.Count == 0)
        {
            AnsiConsole.MarkupLine("  [green]None — all observed FormType bytes have known mappings.[/]");
        }
        else
        {
            foreach (var ft in unknownTypes)
            {
                var dmpsWithType = entries.Where(e => e.FormTypeCounts.ContainsKey(ft)).ToList();
                var allSamples = dmpsWithType
                    .SelectMany(e => e.SampleEditorIds.GetValueOrDefault(ft) ?? [])
                    .Distinct()
                    .Take(5)
                    .ToList();

                AnsiConsole.MarkupLine(
                    $"  [yellow]0x{ft:X2}[/]: {dmpsWithType.Count}/{entries.Count} DMPs, " +
                    $"samples: [dim]{Markup.Escape(string.Join(", ", allSamples))}[/]");
            }
        }

        AnsiConsole.WriteLine();
    }

    private static void RenderPerDmpDetail(List<CensusEntry> entries, List<byte> allFormTypes)
    {
        AnsiConsole.MarkupLine("[bold]Per-DMP FormType Counts:[/]");
        AnsiConsole.WriteLine();

        // Build a compact table: rows = FormTypes, columns = DMPs
        // With 50 DMPs this is wide, so use abbreviated column names
        var table = new Table()
            .Border(TableBorder.Minimal)
            .Title("[bold]FormType × DMP Matrix[/]");

        table.AddColumn(new TableColumn("[bold]Type[/]"));

        foreach (var entry in entries)
        {
            // Abbreviate filename: "Fallout_Release_Beta.xex5.dmp" → "xex5"
            table.AddColumn(new TableColumn(Markup.Escape(ShortName(entry.FileName))).RightAligned());
        }

        foreach (var ft in allFormTypes)
        {
            if (ft == 0) continue;

            var knownName = RuntimeBuildOffsets.GetRecordTypeCode(ft);
            var label = knownName != null ? $"0x{ft:X2} {knownName}" : $"0x{ft:X2}";

            var cells = new List<string> { Markup.Escape(label) };

            foreach (var entry in entries)
            {
                if (entry.FormTypeCounts.TryGetValue(ft, out var count))
                {
                    cells.Add(count.ToString("N0"));
                }
                else
                {
                    cells.Add("[grey]-[/]");
                }
            }

            table.AddRow(cells.ToArray());
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static string ShortName(string fileName)
    {
        // "Fallout_Release_Beta.xex5.dmp" → "xex5"
        // "Fallout_Debug.xex.dmp" → "Debug.xex"
        // "Fallout_Release_MemDebug.xex.dmp" → "MemDebug"
        // "Jacobstown.dmp" → "Jacobstown"

        var name = Path.GetFileNameWithoutExtension(fileName); // strip .dmp

        if (name.StartsWith("Fallout_Release_Beta.", StringComparison.Ordinal))
        {
            var suffix = name["Fallout_Release_Beta.".Length..];
            return suffix.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)
                ? suffix[..^4]
                : suffix;
        }

        if (name.StartsWith("Fallout_Release_MemDebug", StringComparison.Ordinal))
        {
            return "MemDebug";
        }

        if (name.StartsWith("Fallout_Debug.", StringComparison.Ordinal))
        {
            var suffix = name["Fallout_Debug.".Length..];
            return $"Dbg.{suffix}";
        }

        return name;
    }

    private sealed record CensusEntry(
        string FileName,
        DateTime FileDate,
        int TotalEditorIds,
        Dictionary<byte, int> FormTypeCounts,
        Dictionary<byte, List<string>> SampleEditorIds)
    {
        public long FileSize { get; init; }
        public int RuntimeRefrForms { get; init; }
        public int RuntimeLandForms { get; init; }
        public uint PAllFormsVa { get; init; }
        public Dictionary<string, EsmRecordTally> EsmRecordCounts { get; init; } = [];
        public int SctxCount { get; init; }
        public long SctxBytes { get; init; }

        /// <summary>
        ///     FormType histogram after <see cref="RuntimeBuildOffsets.ApplyDriftCorrection" />, so
        ///     codes are comparable across builds. <see cref="FormTypeCounts" /> stays raw for the
        ///     drift report.
        /// </summary>
        public Dictionary<byte, int> CorrectedFormTypeCounts { get; init; } = [];

        /// <summary>Raw→canonical FormType remap applied to this dump, or null if no drift.</summary>
        public Dictionary<byte, byte>? DriftRemap { get; init; }

        /// <summary>
        ///     True when the engine's pAllForms hash table was found and yielded forms. Dumps
        ///     captured before the master finished loading parse cleanly as minidumps but hold
        ///     no TESForm objects at all, so they contribute nothing to recovery.
        /// </summary>
        public bool HasRuntimeFormData => TotalEditorIds > 0;
    }

    /// <summary>Big/little-endian split of embedded ESM main records for one record type.</summary>
    private readonly record struct EsmRecordTally(int BigEndian, int LittleEndian)
    {
        public int Total => BigEndian + LittleEndian;

        public EsmRecordTally Add(bool isBigEndian)
        {
            return isBigEndian
                ? this with { BigEndian = BigEndian + 1 }
                : this with { LittleEndian = LittleEndian + 1 };
        }
    }
}
