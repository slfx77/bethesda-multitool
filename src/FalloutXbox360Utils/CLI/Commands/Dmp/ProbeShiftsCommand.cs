using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     Research command: sweeps every DMP in a directory, captures the
///     <see cref="RuntimeProbeResults" /> the runtime probes discover, and writes both a
///     flat CSV (one row per DMP) and a nested JSON for downstream analysis.
///     Used to answer the empirical question "do the runtime probes ever discover non-zero
///     shifts for the FNV builds in scope?" — the answer drives the layout-class refactor
///     plan (kill the probes vs. extend PdbStructView with shift support).
/// </summary>
internal static class ProbeShiftsCommand
{
    public static Command Create()
    {
        var command = new Command("probe-shifts",
            "Sweep DMP files and capture runtime probe-discovered layout shifts (research command)");

        var pathArg = new Argument<string>("path")
        {
            Description = "Path to a .dmp file or a directory containing .dmp files"
        };
        var csvOpt = new Option<string>("--output", "-o")
        {
            Description = "Path to write the flat CSV (one row per DMP)",
            DefaultValueFactory = _ => Path.Combine("TestOutput", "probe-shifts.csv")
        };
        var jsonOpt = new Option<string>("--json")
        {
            Description = "Path to write the nested JSON detail",
            DefaultValueFactory = _ => Path.Combine("TestOutput", "probe-shifts.json")
        };

        command.Arguments.Add(pathArg);
        command.Options.Add(csvOpt);
        command.Options.Add(jsonOpt);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(pathArg)!;
            var csvPath = parseResult.GetValue(csvOpt)!;
            var jsonPath = parseResult.GetValue(jsonOpt)!;
            return RunAsync(path, csvPath, jsonPath, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(string path, string csvPath, string jsonPath,
        CancellationToken cancellationToken)
    {
        List<string> dmpFiles;
        if (Directory.Exists(path))
        {
            dmpFiles = Directory.GetFiles(path, "*.dmp", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
        else if (File.Exists(path))
        {
            dmpFiles = [path];
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Path not found: {Markup.Escape(path)}");
            return;
        }

        if (dmpFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .dmp files found in:[/] {Markup.Escape(path)}");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[blue]Sweeping {dmpFiles.Count} DMP file(s) for runtime probe shifts...[/]");
        AnsiConsole.WriteLine();

        var rows = new List<ProbeRow>();
        var analyzer = new MinidumpAnalyzer();

        foreach (var dmpFile in dmpFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(dmpFile);
            try
            {
                var row = await SweepOneDmpAsync(analyzer, dmpFile, cancellationToken);
                rows.Add(row);
                AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(fileName)} ({row.BuildType})");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"  [red]✗[/] {Markup.Escape(fileName)}: {Markup.Escape(ex.Message)}");
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        EnsureDirectory(csvPath);
        EnsureDirectory(jsonPath);
        WriteCsv(rows, csvPath);
        WriteJson(rows, jsonPath);
        PrintSummaryTable(rows);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]CSV:[/]  {Markup.Escape(Path.GetFullPath(csvPath))}");
        AnsiConsole.MarkupLine($"[bold]JSON:[/] {Markup.Escape(Path.GetFullPath(jsonPath))}");
    }

    private static async Task<ProbeRow> SweepOneDmpAsync(MinidumpAnalyzer analyzer,
        string dmpFile, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(dmpFile);
        using var mmf = MemoryMappedFile.CreateFromFile(dmpFile, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length,
            MemoryMappedFileAccess.Read);

        // Run the full DMP analysis to populate EsmRecords + entries.
        var analysisResult = await analyzer.AnalyzeAsync(dmpFile, accessor, fileInfo.Length,
            progress: null, includeMetadata: true, verbose: false, cancellationToken);

        if (analysisResult.EsmRecords == null || analysisResult.MinidumpInfo == null)
        {
            throw new InvalidOperationException(
                "Analysis did not produce EsmRecords or MinidumpInfo (corrupt DMP?)");
        }

        var scan = analysisResult.EsmRecords;
        var npcEntries = scan.RuntimeEditorIds.Where(e => e.FormType == 0x2A).ToList();
        var worldEntries = scan.RuntimeEditorIds.Where(e => e.FormType == 0x41).ToList();
        var cellEntries = scan.RuntimeEditorIds.Where(e => e.FormType == 0x39).ToList();

        var reader = RuntimeStructReader.CreateWithAutoDetect(
            accessor,
            fileInfo.Length,
            analysisResult.MinidumpInfo,
            scan.RuntimeRefrFormEntries,
            npcEntries,
            worldEntries,
            cellEntries,
            scan.RuntimeEditorIds,
            scan.RuntimeLandFormEntries);

        var buildType = MinidumpAnalyzer.DetectBuildType(analysisResult.MinidumpInfo) ?? "Unknown";

        return new ProbeRow
        {
            DmpFile = Path.GetFileName(dmpFile),
            BuildType = buildType,
            IsEarlyBuild = reader.IsEarlyBuild,
            EntriesAll = scan.RuntimeEditorIds.Count,
            EntriesNpc = npcEntries.Count,
            EntriesRefr = scan.RuntimeRefrFormEntries.Count,
            EntriesWorld = worldEntries.Count,
            EntriesCell = cellEntries.Count,
            EntriesLand = scan.RuntimeLandFormEntries.Count,
            Probes = ProbeSnapshot.FromReader(reader)
        };
    }

    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static void WriteCsv(List<ProbeRow> rows, string csvPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", CsvHeaders()));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", CsvCells(row)));
        }

        File.WriteAllText(csvPath, sb.ToString());
    }

    private static IEnumerable<string> CsvHeaders()
    {
        yield return "DmpFile";
        yield return "BuildType";
        yield return "IsEarlyBuild";
        yield return "EntriesAll";
        yield return "EntriesNpc";
        yield return "EntriesRefr";
        yield return "EntriesWorld";
        yield return "EntriesCell";
        yield return "EntriesLand";

        // Race (2 shifts)
        yield return "RaceS0";
        yield return "RaceS1";
        yield return "RaceLabel";
        yield return "RaceScore";
        yield return "RaceRunnerUp";
        yield return "RaceMargin";
        yield return "RaceSamples";

        // Effect (1 shift)
        yield return "EffectS0";
        yield return "EffectLabel";
        yield return "EffectScore";
        yield return "EffectRunnerUp";
        yield return "EffectMargin";
        yield return "EffectSamples";

        // NPC
        yield return "NpcCore";
        yield return "NpcApp";
        yield return "NpcLateApp";
        yield return "NpcReadSize";
        yield return "NpcFaceGenMode";
        yield return "NpcHighConf";
        yield return "NpcScore";
        yield return "NpcRunnerUp";
        yield return "NpcMargin";
        yield return "NpcSamples";

        // World/Cell
        yield return "WorldShift";
        yield return "CellShift";
        yield return "WorldCellHighConf";
        yield return "WorldCellScore";
        yield return "WorldCellRunnerUp";
        yield return "WorldCellMargin";
        yield return "WorldCellSamples";

        // Weapon sound (Variant only — V1 vs V2 selection)
        yield return "WeapSoundVariant";
        yield return "WeapSoundHighConf";
        yield return "WeapSoundScore";
        yield return "WeapSoundRunnerUp";
        yield return "WeapSoundMargin";
        yield return "WeapSoundSamples";

        // Generic type shifts — flattened as "0x28:0|0x29:4|..."
        yield return "GenericTypeShifts";
        yield return "GenericNonZeroCount";
    }

    private static IEnumerable<string> CsvCells(ProbeRow row)
    {
        yield return Csv(row.DmpFile);
        yield return Csv(row.BuildType);
        yield return Csv(row.IsEarlyBuild);
        yield return Csv(row.EntriesAll);
        yield return Csv(row.EntriesNpc);
        yield return Csv(row.EntriesRefr);
        yield return Csv(row.EntriesWorld);
        yield return Csv(row.EntriesCell);
        yield return Csv(row.EntriesLand);

        var p = row.Probes;
        yield return Csv(p.RaceShifts.ElementAtOrDefault(0));
        yield return Csv(p.RaceShifts.ElementAtOrDefault(1));
        yield return Csv(p.RaceLabel);
        yield return Csv(p.RaceScore);
        yield return Csv(p.RaceRunnerUp);
        yield return Csv(p.RaceMargin);
        yield return Csv(p.RaceSamples);

        yield return Csv(p.EffectShifts.ElementAtOrDefault(0));
        yield return Csv(p.EffectLabel);
        yield return Csv(p.EffectScore);
        yield return Csv(p.EffectRunnerUp);
        yield return Csv(p.EffectMargin);
        yield return Csv(p.EffectSamples);

        yield return Csv(p.NpcCoreShift);
        yield return Csv(p.NpcAppearanceShift);
        yield return Csv(p.NpcLateAppearanceShift);
        yield return Csv(p.NpcReadSize);
        yield return Csv(p.NpcFaceGenMode);
        yield return Csv(p.NpcHighConfidence);
        yield return Csv(p.NpcScore);
        yield return Csv(p.NpcRunnerUp);
        yield return Csv(p.NpcMargin);
        yield return Csv(p.NpcSamples);

        yield return Csv(p.WorldShift);
        yield return Csv(p.CellShift);
        yield return Csv(p.WorldCellHighConfidence);
        yield return Csv(p.WorldCellScore);
        yield return Csv(p.WorldCellRunnerUp);
        yield return Csv(p.WorldCellMargin);
        yield return Csv(p.WorldCellSamples);

        yield return Csv(p.WeaponSoundVariant);
        yield return Csv(p.WeaponSoundHighConfidence);
        yield return Csv(p.WeaponSoundScore);
        yield return Csv(p.WeaponSoundRunnerUp);
        yield return Csv(p.WeaponSoundMargin);
        yield return Csv(p.WeaponSoundSamples);

        var generic = p.GenericTypeShifts;
        yield return Csv(generic == null
            ? ""
            : string.Join("|", generic.OrderBy(kv => kv.Key)
                .Select(kv => $"0x{kv.Key:X2}:{kv.Value}")));
        yield return Csv(generic?.Count(kv => kv.Value != 0) ?? 0);
    }

    private static string Csv(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Csv(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Csv(bool? value) =>
        value?.ToString().ToLowerInvariant() ?? "";

    private static string Csv(bool value) =>
        value.ToString().ToLowerInvariant();

    private static string Csv(string? value)
    {
        value ??= "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    private static void WriteJson(List<ProbeRow> rows, string jsonPath)
    {
        // Manual emit avoids hitting the trimmer-disabled reflection serializer.
        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('\n');
            AppendJsonRow(sb, rows[i]);
        }

        if (rows.Count > 0) sb.Append('\n');
        sb.Append(']');
        File.WriteAllText(jsonPath, sb.ToString());
    }

    private static void AppendJsonRow(StringBuilder sb, ProbeRow row)
    {
        var p = row.Probes;
        sb.Append("  {\n");
        AppendJsonField(sb, "DmpFile", row.DmpFile, indent: 4);
        AppendJsonField(sb, "BuildType", row.BuildType, indent: 4);
        AppendJsonField(sb, "IsEarlyBuild", row.IsEarlyBuild, indent: 4);
        AppendJsonField(sb, "EntriesAll", row.EntriesAll, indent: 4);
        AppendJsonField(sb, "EntriesNpc", row.EntriesNpc, indent: 4);
        AppendJsonField(sb, "EntriesRefr", row.EntriesRefr, indent: 4);
        AppendJsonField(sb, "EntriesWorld", row.EntriesWorld, indent: 4);
        AppendJsonField(sb, "EntriesCell", row.EntriesCell, indent: 4);
        AppendJsonField(sb, "EntriesLand", row.EntriesLand, indent: 4);
        AppendJsonField(sb, "RaceShifts", p.RaceShifts, indent: 4);
        AppendJsonField(sb, "RaceLabel", p.RaceLabel, indent: 4);
        AppendJsonField(sb, "RaceScore", p.RaceScore, indent: 4);
        AppendJsonField(sb, "RaceRunnerUp", p.RaceRunnerUp, indent: 4);
        AppendJsonField(sb, "RaceMargin", p.RaceMargin, indent: 4);
        AppendJsonField(sb, "RaceSamples", p.RaceSamples, indent: 4);
        AppendJsonField(sb, "EffectShifts", p.EffectShifts, indent: 4);
        AppendJsonField(sb, "EffectLabel", p.EffectLabel, indent: 4);
        AppendJsonField(sb, "EffectScore", p.EffectScore, indent: 4);
        AppendJsonField(sb, "EffectRunnerUp", p.EffectRunnerUp, indent: 4);
        AppendJsonField(sb, "EffectMargin", p.EffectMargin, indent: 4);
        AppendJsonField(sb, "EffectSamples", p.EffectSamples, indent: 4);
        AppendJsonField(sb, "NpcCoreShift", p.NpcCoreShift, indent: 4);
        AppendJsonField(sb, "NpcAppearanceShift", p.NpcAppearanceShift, indent: 4);
        AppendJsonField(sb, "NpcLateAppearanceShift", p.NpcLateAppearanceShift, indent: 4);
        AppendJsonField(sb, "NpcReadSize", p.NpcReadSize, indent: 4);
        AppendJsonField(sb, "NpcFaceGenMode", p.NpcFaceGenMode, indent: 4);
        AppendJsonField(sb, "NpcHighConfidence", p.NpcHighConfidence, indent: 4);
        AppendJsonField(sb, "NpcScore", p.NpcScore, indent: 4);
        AppendJsonField(sb, "NpcRunnerUp", p.NpcRunnerUp, indent: 4);
        AppendJsonField(sb, "NpcMargin", p.NpcMargin, indent: 4);
        AppendJsonField(sb, "NpcSamples", p.NpcSamples, indent: 4);
        AppendJsonField(sb, "WorldShift", p.WorldShift, indent: 4);
        AppendJsonField(sb, "CellShift", p.CellShift, indent: 4);
        AppendJsonField(sb, "WorldCellHighConfidence", p.WorldCellHighConfidence, indent: 4);
        AppendJsonField(sb, "WorldCellScore", p.WorldCellScore, indent: 4);
        AppendJsonField(sb, "WorldCellRunnerUp", p.WorldCellRunnerUp, indent: 4);
        AppendJsonField(sb, "WorldCellMargin", p.WorldCellMargin, indent: 4);
        AppendJsonField(sb, "WorldCellSamples", p.WorldCellSamples, indent: 4);
        AppendJsonField(sb, "WeaponSoundVariant", p.WeaponSoundVariant, indent: 4);
        AppendJsonField(sb, "WeaponSoundHighConfidence", p.WeaponSoundHighConfidence, indent: 4);
        AppendJsonField(sb, "WeaponSoundScore", p.WeaponSoundScore, indent: 4);
        AppendJsonField(sb, "WeaponSoundRunnerUp", p.WeaponSoundRunnerUp, indent: 4);
        AppendJsonField(sb, "WeaponSoundMargin", p.WeaponSoundMargin, indent: 4);
        AppendJsonField(sb, "WeaponSoundSamples", p.WeaponSoundSamples, indent: 4);
        AppendJsonFieldGenericShifts(sb, "GenericTypeShifts", p.GenericTypeShifts, indent: 4);
        // Strip trailing comma+newline from the last field.
        if (sb.Length >= 2 && sb[^2] == ',' && sb[^1] == '\n')
        {
            sb.Length -= 2;
            sb.Append('\n');
        }

        sb.Append("  }");
    }

    private static void AppendJsonField(StringBuilder sb, string name, int? value, int indent)
    {
        AppendIndent(sb, indent);
        sb.Append('"').Append(name).Append("\": ");
        sb.Append(value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null");
        sb.Append(",\n");
    }

    private static void AppendJsonField(StringBuilder sb, string name, int value, int indent)
    {
        AppendIndent(sb, indent);
        sb.Append('"').Append(name).Append("\": ");
        sb.Append(value.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\n");
    }

    private static void AppendJsonField(StringBuilder sb, string name, bool? value, int indent)
    {
        AppendIndent(sb, indent);
        sb.Append('"').Append(name).Append("\": ");
        string literal;
        if (!value.HasValue)
        {
            literal = "null";
        }
        else
        {
            literal = value.Value ? "true" : "false";
        }

        sb.Append(literal);
        sb.Append(",\n");
    }

    private static void AppendJsonField(StringBuilder sb, string name, bool value, int indent)
    {
        AppendIndent(sb, indent);
        sb.Append('"').Append(name).Append("\": ");
        sb.Append(value ? "true" : "false");
        sb.Append(",\n");
    }

    private static void AppendJsonField(StringBuilder sb, string name, string? value, int indent)
    {
        AppendIndent(sb, indent);
        sb.Append('"').Append(name).Append("\": ");
        if (value == null)
        {
            sb.Append("null");
        }
        else
        {
            sb.Append('"').Append(JsonEscape(value)).Append('"');
        }

        sb.Append(",\n");
    }

    private static void AppendJsonField(StringBuilder sb, string name, IReadOnlyList<int>? values, int indent)
    {
        AppendIndent(sb, indent);
        sb.Append('"').Append(name).Append("\": ");
        if (values == null || values.Count == 0)
        {
            sb.Append("[]");
        }
        else
        {
            sb.Append('[');
            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }

            sb.Append(']');
        }

        sb.Append(",\n");
    }

    private static void AppendJsonFieldGenericShifts(StringBuilder sb, string name,
        IReadOnlyDictionary<byte, int>? values, int indent)
    {
        AppendIndent(sb, indent);
        sb.Append('"').Append(name).Append("\": ");
        if (values == null || values.Count == 0)
        {
            sb.Append("{}");
        }
        else
        {
            sb.Append('{');
            var first = true;
            foreach (var kv in values.OrderBy(kv => kv.Key))
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append("\"0x").Append(kv.Key.ToString("X2", CultureInfo.InvariantCulture))
                    .Append("\": ").Append(kv.Value.ToString(CultureInfo.InvariantCulture));
            }

            sb.Append('}');
        }

        sb.Append(",\n");
    }

    private static void AppendIndent(StringBuilder sb, int indent)
    {
        for (var i = 0; i < indent; i++)
        {
            sb.Append(' ');
        }
    }

    private static string JsonEscape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    private static void PrintSummaryTable(List<ProbeRow> rows)
    {
        AnsiConsole.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Probe Shift Summary[/]")
            .AddColumn(new TableColumn("[bold]File[/]"))
            .AddColumn(new TableColumn("[bold]Build[/]"))
            .AddColumn(new TableColumn("[bold]Race[/]").Centered())
            .AddColumn(new TableColumn("[bold]Effect[/]").Centered())
            .AddColumn(new TableColumn("[bold]NPC C/A/L[/]").Centered())
            .AddColumn(new TableColumn("[bold]W/C[/]").Centered())
            .AddColumn(new TableColumn("[bold]WSV[/]").Centered())
            .AddColumn(new TableColumn("[bold]GenNZ[/]").RightAligned());

        foreach (var r in rows)
        {
            var name = r.DmpFile.Length > 30 ? r.DmpFile[..30] + "…" : r.DmpFile;
            var p = r.Probes;
            table.AddRow(
                Markup.Escape(name),
                Markup.Escape(r.BuildType),
                FormatPair(p.RaceShifts),
                FormatPair(p.EffectShifts),
                FormatTriple(p.NpcCoreShift, p.NpcAppearanceShift, p.NpcLateAppearanceShift),
                FormatPair(p.WorldShift, p.CellShift),
                p.WeaponSoundVariant?.ToString(CultureInfo.InvariantCulture) ?? "—",
                (p.GenericTypeShifts?.Count(kv => kv.Value != 0) ?? 0).ToString(CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);

        // Aggregate findings
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold yellow]Distinct shift values observed across sweep:[/]");
        AggregateLine("Race S0", rows.Select(r => ShiftAt(r.Probes.RaceShifts, 0)));
        AggregateLine("Race S1", rows.Select(r => ShiftAt(r.Probes.RaceShifts, 1)));
        AggregateLine("Effect S0", rows.Select(r => ShiftAt(r.Probes.EffectShifts, 0)));
        AggregateLine("NPC Core", rows.Select(r => r.Probes.NpcCoreShift));
        AggregateLine("NPC Appearance", rows.Select(r => r.Probes.NpcAppearanceShift));
        AggregateLine("NPC LateApp", rows.Select(r => r.Probes.NpcLateAppearanceShift));
        AggregateLine("World Shift", rows.Select(r => r.Probes.WorldShift));
        AggregateLine("Cell Shift", rows.Select(r => r.Probes.CellShift));
        AggregateLine("WeapSound Variant", rows.Select(r => r.Probes.WeaponSoundVariant));
    }

    private static string FormatPair(IReadOnlyList<int>? shifts)
    {
        if (shifts == null || shifts.Count == 0)
        {
            return "—";
        }

        return string.Join("/", shifts.Select(s => s.ToString(CultureInfo.InvariantCulture)));
    }

    private static string FormatPair(int? a, int? b)
    {
        var aStr = a?.ToString(CultureInfo.InvariantCulture) ?? "—";
        var bStr = b?.ToString(CultureInfo.InvariantCulture) ?? "—";
        return $"{aStr}/{bStr}";
    }

    private static string FormatTriple(int? a, int? b, int? c)
    {
        var aStr = a?.ToString(CultureInfo.InvariantCulture) ?? "—";
        var bStr = b?.ToString(CultureInfo.InvariantCulture) ?? "—";
        var cStr = c?.ToString(CultureInfo.InvariantCulture) ?? "—";
        return $"{aStr}/{bStr}/{cStr}";
    }

    private static int? ShiftAt(IReadOnlyList<int> shifts, int index)
    {
        return index < shifts.Count ? shifts[index] : null;
    }

    private static void AggregateLine(string label, IEnumerable<int?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (present.Count == 0)
        {
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(label),-18}[/] [grey](no probe ran)[/]");
            return;
        }

        var distinct = present.Distinct().OrderBy(v => v).ToList();
        var allZero = distinct.Count == 1 && distinct[0] == 0;
        string color;
        if (allZero)
        {
            color = "green";
        }
        else
        {
            color = distinct.Count == 1 ? "cyan" : "yellow";
        }
        var values_str = string.Join(", ", distinct.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        AnsiConsole.MarkupLine(
            $"  [{color}]{Markup.Escape(label),-18}[/]  values={{ {values_str} }} ({present.Count}/{present.Count} samples)");
    }

    private sealed class ProbeRow
    {
        public string DmpFile { get; set; } = "";
        public string BuildType { get; set; } = "";
        public bool IsEarlyBuild { get; set; }
        public int EntriesAll { get; set; }
        public int EntriesNpc { get; set; }
        public int EntriesRefr { get; set; }
        public int EntriesWorld { get; set; }
        public int EntriesCell { get; set; }
        public int EntriesLand { get; set; }
        public ProbeSnapshot Probes { get; set; } = new();
    }

    private sealed class ProbeSnapshot
    {
        public IReadOnlyList<int> RaceShifts { get; set; } = Array.Empty<int>();
        public string? RaceLabel { get; set; }
        public int? RaceScore { get; set; }
        public int? RaceRunnerUp { get; set; }
        public int? RaceMargin { get; set; }
        public int? RaceSamples { get; set; }

        public IReadOnlyList<int> EffectShifts { get; set; } = Array.Empty<int>();
        public string? EffectLabel { get; set; }
        public int? EffectScore { get; set; }
        public int? EffectRunnerUp { get; set; }
        public int? EffectMargin { get; set; }
        public int? EffectSamples { get; set; }

        public int? NpcCoreShift { get; set; }
        public int? NpcAppearanceShift { get; set; }
        public int? NpcLateAppearanceShift { get; set; }
        public int? NpcReadSize { get; set; }
        public string? NpcFaceGenMode { get; set; }
        public bool? NpcHighConfidence { get; set; }
        public int? NpcScore { get; set; }
        public int? NpcRunnerUp { get; set; }
        public int? NpcMargin { get; set; }
        public int? NpcSamples { get; set; }

        public int? WorldShift { get; set; }
        public int? CellShift { get; set; }
        public bool? WorldCellHighConfidence { get; set; }
        public int? WorldCellScore { get; set; }
        public int? WorldCellRunnerUp { get; set; }
        public int? WorldCellMargin { get; set; }
        public int? WorldCellSamples { get; set; }

        public int? WeaponSoundVariant { get; set; } // V2=0, V1=1
        public bool? WeaponSoundHighConfidence { get; set; }
        public int? WeaponSoundScore { get; set; }
        public int? WeaponSoundRunnerUp { get; set; }
        public int? WeaponSoundMargin { get; set; }
        public int? WeaponSoundSamples { get; set; }

        public IReadOnlyDictionary<byte, int>? GenericTypeShifts { get; set; }

        public static ProbeSnapshot FromReader(RuntimeStructReader reader)
        {
            var snap = new ProbeSnapshot();
            var probes = reader.ProbeResults;
            if (probes == null)
            {
                return snap;
            }

            if (probes.RaceLayout is { } race)
            {
                snap.RaceShifts = race.Winner.Layout;
                snap.RaceLabel = race.Winner.Label;
                snap.RaceScore = race.WinnerScore;
                snap.RaceRunnerUp = race.RunnerUpScore;
                snap.RaceMargin = race.Margin;
                snap.RaceSamples = race.SampleCount;
            }

            if (probes.EffectLayout is { } effect)
            {
                snap.EffectShifts = effect.Winner.Layout;
                snap.EffectLabel = effect.Winner.Label;
                snap.EffectScore = effect.WinnerScore;
                snap.EffectRunnerUp = effect.RunnerUpScore;
                snap.EffectMargin = effect.Margin;
                snap.EffectSamples = effect.SampleCount;
            }

            if (probes.NpcLayout is { } npc)
            {
                snap.NpcCoreShift = npc.Layout.CoreShift;
                snap.NpcAppearanceShift = npc.Layout.AppearanceShift;
                snap.NpcLateAppearanceShift = npc.Layout.LateAppearanceShift;
                snap.NpcReadSize = npc.Layout.ReadSize;
                snap.NpcFaceGenMode = npc.Layout.FaceGenMode.ToString();
                snap.NpcHighConfidence = npc.IsHighConfidence;
                snap.NpcScore = npc.WinnerScore;
                snap.NpcRunnerUp = npc.RunnerUpScore;
                snap.NpcMargin = npc.Margin;
                snap.NpcSamples = npc.SampleCount;
            }

            if (probes.WorldCellLayout is { } wc)
            {
                snap.WorldShift = wc.Layout.WorldShift;
                snap.CellShift = wc.Layout.CellShift;
                snap.WorldCellHighConfidence = wc.IsHighConfidence;
                snap.WorldCellScore = wc.WinnerScore;
                snap.WorldCellRunnerUp = wc.RunnerUpScore;
                snap.WorldCellMargin = wc.Margin;
                snap.WorldCellSamples = wc.SampleCount;
            }

            if (probes.WeaponSoundLayout is { } ws)
            {
                snap.WeaponSoundVariant = (int)ws.Variant;
                snap.WeaponSoundHighConfidence = ws.IsHighConfidence;
                snap.WeaponSoundScore = ws.WinnerScore;
                snap.WeaponSoundRunnerUp = ws.RunnerUpScore;
                snap.WeaponSoundMargin = ws.Margin;
                snap.WeaponSoundSamples = ws.SampleCount;
            }

            snap.GenericTypeShifts = probes.GenericTypeShifts;
            return snap;
        }
    }
}
