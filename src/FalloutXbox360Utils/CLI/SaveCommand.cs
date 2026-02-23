using System.CommandLine;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.SaveGame;
using FalloutXbox360Utils.Core.Formats.SaveGame.Export;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for inspecting Fallout 3/NV save files (.fxs / .fos).
/// </summary>
public static class SaveCommand
{
    private const string InputArgName = "input";

    /// <summary>
    ///     Resolves a SaveRefId to a consistent 0x{FormID:X8} string for display.
    ///     Falls back to raw RefID notation if resolution fails.
    /// </summary>
    private static string FormatFormId(SaveRefId refId, ReadOnlySpan<uint> formIdArray)
    {
        var resolved = refId.ResolveFormId(formIdArray);
        return resolved != 0 ? $"0x{resolved:X8}" : refId.ToString();
    }

    public static Command Create()
    {
        var command = new Command("save", "Inspect Fallout 3/NV save files (.fxs / .fos)");

        var infoCommand = new Command("info", "Show save file header, statistics, and metadata");
        infoCommand.Arguments.Add(new Argument<string>(InputArgName) { Description = "Path to save file" });
        infoCommand.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue<string>(InputArgName)!;
            return Task.FromResult(ExecuteInfo(input));
        });

        var changesCommand = new Command("changes", "Show changed form summary");
        changesCommand.Arguments.Add(new Argument<string>(InputArgName) { Description = "Path to save file" });
        var limitOpt = new Option<int>("-l", "--limit")
        {
            Description = "Max forms to display",
            DefaultValueFactory = _ => 50
        };
        changesCommand.Options.Add(limitOpt);
        var typeOpt = new Option<string?>("-t", "--type")
            { Description = "Filter by change type (e.g., ACHR, REFR, QUST)" };
        changesCommand.Options.Add(typeOpt);
        changesCommand.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue<string>(InputArgName)!;
            var limit = parseResult.GetValue(limitOpt);
            var type = parseResult.GetValue(typeOpt);
            return Task.FromResult(ExecuteChanges(input, limit, type));
        });

        var playerCommand = new Command("player", "Show player position and state");
        playerCommand.Arguments.Add(new Argument<string>(InputArgName) { Description = "Path to save file" });
        playerCommand.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue<string>(InputArgName)!;
            return Task.FromResult(ExecutePlayer(input));
        });

        var batchCommand = new Command("batch", "Parse all save files in a directory");
        batchCommand.Arguments.Add(new Argument<string>(InputArgName)
            { Description = "Path to directory of .fxs files" });
        batchCommand.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue<string>(InputArgName)!;
            return Task.FromResult(ExecuteBatch(input));
        });

        var hexdumpCommand = new Command("hexdump", "Hex-dump global data or changed form raw bytes");
        hexdumpCommand.Arguments.Add(new Argument<string>(InputArgName) { Description = "Path to save file" });
        var globalOpt = new Option<int?>("--global", "-g")
            { Description = "Global data type ID to dump (e.g., 0 for Misc Stats)" };
        var formOpt = new Option<string?>("--form")
            { Description = "Changed form RefID to dump (e.g., 0x000014 for player)" };
        var decodeOpt = new Option<bool>("--decode", "-d")
            { Description = "Also show decoded fields", DefaultValueFactory = _ => true };
        hexdumpCommand.Options.Add(globalOpt);
        hexdumpCommand.Options.Add(formOpt);
        hexdumpCommand.Options.Add(decodeOpt);
        hexdumpCommand.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue<string>(InputArgName)!;
            var globalType = parseResult.GetValue(globalOpt);
            var formRef = parseResult.GetValue(formOpt);
            var decode = parseResult.GetValue(decodeOpt);
            return Task.FromResult(ExecuteHexdump(input, globalType, formRef, decode));
        });

        var statsCommand = new Command("stats", "Show gameplay statistics from Misc Stats (Global Data Type 0)");
        statsCommand.Arguments.Add(new Argument<string>(InputArgName) { Description = "Path to save file" });
        statsCommand.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue<string>(InputArgName)!;
            return Task.FromResult(ExecuteStats(input));
        });

        var decodeCommand = new Command("decode", "Test decode all changed forms and show statistics");
        decodeCommand.Arguments.Add(new Argument<string>(InputArgName) { Description = "Path to save file" });
        decodeCommand.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue<string>(InputArgName)!;
            return Task.FromResult(ExecuteDecodeStats(input));
        });

        var stfsInfoCommand = new Command("stfs-info", "Show STFS container structure and extraction diagnostics");
        stfsInfoCommand.Arguments.Add(new Argument<string>(InputArgName) { Description = "Path to .fxs save file" });
        stfsInfoCommand.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue<string>(InputArgName)!;
            return Task.FromResult(ExecuteStfsInfo(input));
        });

        var reportsCommand = new Command("reports", "Generate save file reports (CSV + TXT) for reconciliation");
        reportsCommand.Arguments.Add(new Argument<string>(InputArgName) { Description = "Path to save file" });
        var esmOpt = new Option<string?>("--esm") { Description = "ESM/DMP file for name resolution enrichment" };
        var outputOpt = new Option<string?>("-o", "--output")
            { Description = "Output directory (default: ./save_reports/)" };
        reportsCommand.Options.Add(esmOpt);
        reportsCommand.Options.Add(outputOpt);
        reportsCommand.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue<string>(InputArgName)!;
            var esm = parseResult.GetValue(esmOpt);
            var output = parseResult.GetValue(outputOpt) ?? "./save_reports";
            return Task.FromResult(ExecuteReports(input, esm, output));
        });

        command.Subcommands.Add(infoCommand);
        command.Subcommands.Add(changesCommand);
        command.Subcommands.Add(playerCommand);
        command.Subcommands.Add(batchCommand);
        command.Subcommands.Add(hexdumpCommand);
        command.Subcommands.Add(statsCommand);
        command.Subcommands.Add(decodeCommand);
        command.Subcommands.Add(stfsInfoCommand);
        command.Subcommands.Add(reportsCommand);

        return command;
    }

    private static SaveFile ParseFile(string path)
    {
        var data = File.ReadAllBytes(path);
        return SaveFileParser.Parse(data);
    }

    private static int ExecuteInfo(string path)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(path)}");
            return 1;
        }

        try
        {
            var save = ParseFile(path);
            var h = save.Header;

            AnsiConsole.MarkupLine($"[bold green]Save File:[/] {Markup.Escape(Path.GetFileName(path))}");
            if (save.StfsPayloadOffset > 0)
            {
                AnsiConsole.MarkupLine($"[grey]STFS payload at offset 0x{save.StfsPayloadOffset:X}[/]");
            }

            AnsiConsole.WriteLine();

            // Header table
            var headerTable = new Table().Border(TableBorder.Rounded);
            headerTable.AddColumn("Field");
            headerTable.AddColumn("Value");
            headerTable.AddRow("Version", $"0x{h.Version:X}");
            headerTable.AddRow("Save Number", h.SaveNumber.ToString());
            headerTable.AddRow("Player Name",
                string.IsNullOrEmpty(h.PlayerName) ? "[grey](empty)[/]" : Markup.Escape(h.PlayerName));
            headerTable.AddRow("Player Status", Markup.Escape(h.PlayerStatus));
            headerTable.AddRow("Player Level", h.PlayerLevel.ToString());
            headerTable.AddRow("Current Cell", Markup.Escape(h.PlayerCell));
            headerTable.AddRow("Playtime", Markup.Escape(h.SaveDuration));
            headerTable.AddRow("Screenshot",
                $"{h.ScreenshotWidth}x{h.ScreenshotHeight} ({h.ScreenshotDataSize:N0} bytes)");
            headerTable.AddRow("Form Version", h.FormVersion.ToString());
            AnsiConsole.Write(headerTable);

            // Plugins
            AnsiConsole.MarkupLine($"\n[bold]Plugins ({h.Plugins.Count}):[/]");
            foreach (var plugin in h.Plugins)
            {
                AnsiConsole.MarkupLine($"  {Markup.Escape(plugin)}");
            }

            // Body summary
            AnsiConsole.MarkupLine("\n[bold]Body Summary:[/]");
            AnsiConsole.MarkupLine($"  Global Data 1 entries: {save.GlobalData1.Count}");
            AnsiConsole.MarkupLine($"  Global Data 2 entries: {save.GlobalData2.Count}");
            AnsiConsole.MarkupLine($"  Changed Forms: {save.ChangedForms.Count}");
            AnsiConsole.MarkupLine($"  FormID Array: {save.FormIdArray.Count} entries");
            AnsiConsole.MarkupLine($"  Visited Worldspaces: {save.VisitedWorldspaces.Count}");
            AnsiConsole.MarkupLine($"  Global Variables: {save.GlobalVariables.Count}");

            // Global Data types
            if (save.GlobalData1.Count > 0)
            {
                AnsiConsole.MarkupLine("\n[bold]Global Data Types:[/]");
                foreach (var gd in save.GlobalData1.Concat(save.GlobalData2))
                {
                    AnsiConsole.MarkupLine($"  Type {gd.Type}: {gd.TypeName} ({gd.Data.Length:N0} bytes)");
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(
                $"[red]Error parsing {Markup.Escape(Path.GetFileName(path))}:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private static int ExecuteChanges(string path, int limit, string? typeFilter)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(path)}");
            return 1;
        }

        try
        {
            var save = ParseFile(path);

            // Summary by type
            var typeCounts = save.ChangedForms
                .GroupBy(f => f.TypeName)
                .OrderByDescending(g => g.Count())
                .ToList();

            AnsiConsole.MarkupLine($"[bold green]Changed Forms Summary ({save.ChangedForms.Count} total):[/]\n");
            var summaryTable = new Table().Border(TableBorder.Rounded);
            summaryTable.AddColumn("Type");
            summaryTable.AddColumn(new TableColumn("Count").RightAligned());
            summaryTable.AddColumn("With Position");
            foreach (var group in typeCounts)
            {
                var withPos = group.Count(f => f.Initial != null);
                summaryTable.AddRow(
                    group.Key,
                    group.Count().ToString(),
                    withPos > 0 ? $"[green]{withPos}[/]" : "[grey]0[/]");
            }

            AnsiConsole.Write(summaryTable);

            // Detail view
            var forms = save.ChangedForms.AsEnumerable();
            if (typeFilter != null)
            {
                forms = forms.Where(f => f.TypeName.Equals(typeFilter, StringComparison.OrdinalIgnoreCase));
            }

            var displayForms = forms.Take(limit).ToList();
            if (displayForms.Count > 0)
            {
                AnsiConsole.MarkupLine($"\n[bold]Detail ({displayForms.Count} shown):[/]\n");
                var detailTable = new Table().Border(TableBorder.Simple);
                detailTable.AddColumn("RefID");
                detailTable.AddColumn("Type");
                detailTable.AddColumn("Flags");
                detailTable.AddColumn("Data Size");
                detailTable.AddColumn("Position");

                var formIdArray = save.FormIdArray.ToArray().AsSpan();
                foreach (var form in displayForms)
                {
                    var posStr = form.Initial != null
                        ? $"({form.Initial.PosX:F0}, {form.Initial.PosY:F0}, {form.Initial.PosZ:F0})"
                        : "[grey]-[/]";

                    var activeFlags = ChangeFlagRegistry.DescribeFlags(form.ChangeType, form.ChangeFlags);
                    var flagStr = activeFlags.Count > 0
                        ? Markup.Escape(string.Join("|", activeFlags))
                        : $"0x{form.ChangeFlags:X8}";

                    detailTable.AddRow(
                        Markup.Escape(FormatFormId(form.RefId, formIdArray)),
                        form.TypeName,
                        flagStr,
                        $"{form.Data.Length}",
                        posStr);
                }

                AnsiConsole.Write(detailTable);
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static int ExecutePlayer(string path)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(path)}");
            return 1;
        }

        try
        {
            var save = ParseFile(path);
            var h = save.Header;

            AnsiConsole.MarkupLine($"[bold green]Player State:[/] {Markup.Escape(Path.GetFileName(path))}");
            AnsiConsole.MarkupLine(
                $"  Name: {(string.IsNullOrEmpty(h.PlayerName) ? "(empty)" : Markup.Escape(h.PlayerName))}");
            AnsiConsole.MarkupLine($"  Level: {h.PlayerLevel}");
            AnsiConsole.MarkupLine($"  Status: {Markup.Escape(h.PlayerStatus)}");
            AnsiConsole.MarkupLine($"  Cell: {Markup.Escape(h.PlayerCell)}");
            AnsiConsole.MarkupLine($"  Playtime: {Markup.Escape(h.SaveDuration)}");

            var formIdArray = save.FormIdArray.ToArray().AsSpan();

            if (save.PlayerLocation != null)
            {
                var loc = save.PlayerLocation;
                AnsiConsole.MarkupLine("\n[bold]World Position (Global Data Type 1):[/]");
                AnsiConsole.MarkupLine($"  Worldspace: {FormatFormId(loc.WorldspaceRefId, formIdArray)}");
                AnsiConsole.MarkupLine($"  Grid: ({loc.CoordX}, {loc.CoordY})");
                AnsiConsole.MarkupLine($"  Cell: {FormatFormId(loc.CellRefId, formIdArray)}");
                AnsiConsole.MarkupLine($"  Position: ({loc.PosX:F2}, {loc.PosY:F2}, {loc.PosZ:F2})");
            }

            // Find the player's ACHR changed form (FormID 0x14 = player ref)
            var playerForms = save.ChangedForms
                .Where(f => f.IsActorType && f.Initial != null)
                .ToList();

            if (playerForms.Count > 0)
            {
                AnsiConsole.MarkupLine($"\n[bold]Actor Changed Forms with Position ({playerForms.Count}):[/]");
                foreach (var form in playerForms.Take(10))
                {
                    AnsiConsole.MarkupLine(
                        $"  {Markup.Escape(FormatFormId(form.RefId, formIdArray))} ({form.TypeName}): ({form.Initial!.PosX:F0}, {form.Initial.PosY:F0}, {form.Initial.PosZ:F0}) flags=0x{form.ChangeFlags:X8}");
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static int ExecuteHexdump(string path, int? globalType, string? formRef, bool decode = true)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(path)}");
            return 1;
        }

        if (globalType is null && formRef is null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Specify --global <typeId> or --form <refid>");
            return 1;
        }

        try
        {
            var save = ParseFile(path);

            if (globalType is not null)
            {
                var allGlobal = save.GlobalData1.Concat(save.GlobalData2).ToList();
                var entry = allGlobal.FirstOrDefault(g => g.Type == (uint)globalType.Value);
                if (entry is null)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Global data type {globalType.Value} not found.");
                    AnsiConsole.MarkupLine("[grey]Available types:[/]");
                    foreach (var g in allGlobal)
                    {
                        AnsiConsole.MarkupLine($"  Type {g.Type}: {g.TypeName} ({g.Data.Length:N0} bytes)");
                    }

                    return 1;
                }

                AnsiConsole.MarkupLine(
                    $"[bold green]Global Data Type {entry.Type}: {entry.TypeName}[/] ({entry.Data.Length:N0} bytes)\n");
                PrintHexDump(entry.Data);
            }
            else if (formRef is not null)
            {
                // Parse refid as hex
                var targetRaw = Convert.ToUInt32(formRef.Replace("0x", ""), 16);
                var form = save.ChangedForms.FirstOrDefault(f => f.RefId.Raw == targetRaw);
                if (form is null)
                {
                    // Also try matching resolved FormID against the array
                    form = save.ChangedForms.FirstOrDefault(f =>
                    {
                        try
                        {
                            return f.RefId.ResolveFormId(save.FormIdArray.ToArray()) == targetRaw;
                        }
                        catch
                        {
                            return false;
                        }
                    });
                }

                if (form is null)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Error:[/] Changed form with RefID {Markup.Escape(formRef)} not found.");
                    return 1;
                }

                var formIdArray = save.FormIdArray.ToArray().AsSpan();
                AnsiConsole.MarkupLine(
                    $"[bold green]Changed Form {Markup.Escape(FormatFormId(form.RefId, formIdArray))} ({form.TypeName})[/]");
                var flagNames = ChangeFlagRegistry.DescribeFlags(form.ChangeType, form.ChangeFlags);
                AnsiConsole.MarkupLine(
                    $"  Flags: 0x{form.ChangeFlags:X8}  Version: {form.Version}  Data: {form.Data.Length:N0} bytes");
                if (flagNames.Count > 0)
                {
                    AnsiConsole.MarkupLine($"  Active: [cyan]{Markup.Escape(string.Join(" | ", flagNames))}[/]");
                }

                AnsiConsole.WriteLine();
                PrintHexDump(form.Data);

                if (decode)
                {
                    PrintDecodedFields(save, form);
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static int ExecuteStats(string path)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(path)}");
            return 1;
        }

        try
        {
            var save = ParseFile(path);

            AnsiConsole.MarkupLine($"[bold green]Misc Stats:[/] {Markup.Escape(Path.GetFileName(path))}\n");

            if (save.Statistics.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No statistics parsed (Global Data Type 0 may be missing or empty).[/]");
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Stat");
            table.AddColumn(new TableColumn("Value").RightAligned());

            for (var i = 0; i < save.Statistics.Count; i++)
            {
                var label = i < SaveStatistics.Labels.Length
                    ? SaveStatistics.Labels[i]
                    : $"Unknown Stat {i}";
                table.AddRow(label, save.Statistics.Values[i].ToString("N0"));
            }

            AnsiConsole.Write(table);
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static void PrintHexDump(ReadOnlySpan<byte> data)
    {
        const int bytesPerLine = 16;
        var sb = new StringBuilder();

        for (var offset = 0; offset < data.Length; offset += bytesPerLine)
        {
            sb.Append($"  {offset:X6}  ");

            var lineLen = Math.Min(bytesPerLine, data.Length - offset);
            for (var i = 0; i < bytesPerLine; i++)
            {
                if (i < lineLen)
                {
                    sb.Append($"{data[offset + i]:X2} ");
                }
                else
                {
                    sb.Append("   ");
                }

                if (i == 7)
                {
                    sb.Append(' ');
                }
            }

            sb.Append(" |");
            for (var i = 0; i < lineLen; i++)
            {
                var b = data[offset + i];
                sb.Append(b is >= 32 and < 127 ? (char)b : '.');
            }

            sb.Append('|');
            AnsiConsole.WriteLine(sb.ToString());
            sb.Clear();
        }
    }

    private static void PrintDecodedFields(SaveFile save, ChangedForm form)
    {
        var decoded = ChangedFormDecoder.Decode(form, save.FormIdArray.ToArray());
        if (decoded is null || decoded.Fields.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine(
            $"\n[bold yellow]Decoded Fields ({decoded.BytesConsumed}/{decoded.TotalBytes} bytes consumed):[/]");

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Flag/Field");
        table.AddColumn("Value");
        table.AddColumn(new TableColumn("Offset").RightAligned());
        table.AddColumn(new TableColumn("Size").RightAligned());

        foreach (var field in decoded.Fields)
        {
            table.AddRow(
                $"[cyan]{Markup.Escape(field.Name)}[/]",
                Markup.Escape(field.DisplayValue),
                $"0x{field.DataOffset:X}",
                field.DataLength.ToString());

            if (field.Children is { Count: > 0 })
            {
                foreach (var child in field.Children.Take(20))
                {
                    table.AddRow(
                        $"  [grey]{Markup.Escape(child.Name)}[/]",
                        Markup.Escape(child.DisplayValue),
                        $"0x{child.DataOffset:X}",
                        child.DataLength.ToString());
                }

                if (field.Children.Count > 20)
                {
                    table.AddRow("", $"[grey]... and {field.Children.Count - 20} more[/]", "", "");
                }
            }
        }

        AnsiConsole.Write(table);

        if (decoded.UndecodedBytes > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]  {decoded.UndecodedBytes} bytes remaining undecoded[/]");
        }

        foreach (var warning in decoded.Warnings)
        {
            AnsiConsole.MarkupLine($"[red]  Warning: {Markup.Escape(warning)}[/]");
        }
    }

    private static int ExecuteDecodeStats(string path)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(path)}");
            return 1;
        }

        try
        {
            var save = ParseFile(path);
            var formIdArray = save.FormIdArray.ToArray();

            int totalForms = 0, decoded = 0, fullyDecoded = 0, partiallyDecoded = 0, failed = 0, unsupported = 0;
            long totalBytes = 0, decodedBytes = 0;
            var typeStats =
                new Dictionary<string, (int Total, int Full, int Partial, int Fail, long TotalBytes, long DecodedBytes
                    )>();
            var errors = new List<string>();

            foreach (var form in save.ChangedForms)
            {
                totalForms++;
                totalBytes += form.Data.Length;

                if (form.Data.Length == 0)
                {
                    continue;
                }

                var result = ChangedFormDecoder.Decode(form, formIdArray);
                var typeName = form.TypeName;
                if (!typeStats.TryGetValue(typeName, out var s))
                {
                    s = (0, 0, 0, 0, 0, 0);
                }

                s.Total++;
                s.TotalBytes += form.Data.Length;

                if (result is null)
                {
                    unsupported++;
                    typeStats[typeName] = s;
                    continue;
                }

                decoded++;
                decodedBytes += result.BytesConsumed;
                s.DecodedBytes += result.BytesConsumed;

                if (result.FullyDecoded)
                {
                    fullyDecoded++;
                    s.Full++;
                }
                else if (result.BytesConsumed > 0)
                {
                    partiallyDecoded++;
                    s.Partial++;
                }
                else
                {
                    failed++;
                    s.Fail++;
                    if (errors.Count < 10)
                    {
                        var flagNames = ChangeFlagRegistry.DescribeFlags(form.ChangeType, form.ChangeFlags);
                        errors.Add(
                            $"{FormatFormId(form.RefId, formIdArray)} ({typeName}) flags=0x{form.ChangeFlags:X8} [{string.Join("|", flagNames)}] data={form.Data.Length}b");
                    }
                }

                if (result.Warnings.Count > 0 && errors.Count < 20)
                {
                    errors.Add(
                        $"{FormatFormId(form.RefId, formIdArray)} ({typeName}): {string.Join("; ", result.Warnings)}");
                }

                typeStats[typeName] = s;
            }

            AnsiConsole.MarkupLine($"[bold green]Decode Statistics:[/] {Markup.Escape(Path.GetFileName(path))}\n");

            // Overall summary
            var overallTable = new Table().Border(TableBorder.Rounded);
            overallTable.AddColumn("Metric");
            overallTable.AddColumn(new TableColumn("Value").RightAligned());
            overallTable.AddRow("Total forms", totalForms.ToString());
            overallTable.AddRow("[green]Fully decoded[/]", fullyDecoded.ToString());
            overallTable.AddRow("[yellow]Partially decoded[/]", partiallyDecoded.ToString());
            overallTable.AddRow("[red]Failed[/]", failed.ToString());
            overallTable.AddRow("[grey]Unsupported type[/]", unsupported.ToString());
            overallTable.AddRow("Total data bytes", totalBytes.ToString("N0"));
            overallTable.AddRow("Decoded bytes", decodedBytes.ToString("N0"));
            overallTable.AddRow("[bold]Decode coverage[/]",
                totalBytes > 0 ? $"{100.0 * decodedBytes / totalBytes:F1}%" : "N/A");
            AnsiConsole.Write(overallTable);

            // Per-type breakdown
            AnsiConsole.MarkupLine("\n[bold]Per-Type Breakdown:[/]\n");
            var typeTable = new Table().Border(TableBorder.Rounded);
            typeTable.AddColumn("Type");
            typeTable.AddColumn(new TableColumn("Total").RightAligned());
            typeTable.AddColumn(new TableColumn("Full").RightAligned());
            typeTable.AddColumn(new TableColumn("Partial").RightAligned());
            typeTable.AddColumn(new TableColumn("Fail").RightAligned());
            typeTable.AddColumn(new TableColumn("Coverage").RightAligned());

            foreach (var kvp in typeStats.OrderByDescending(x => x.Value.Total))
            {
                var coverage = kvp.Value.TotalBytes > 0
                    ? $"{100.0 * kvp.Value.DecodedBytes / kvp.Value.TotalBytes:F1}%"
                    : "N/A";
                typeTable.AddRow(
                    kvp.Key,
                    kvp.Value.Total.ToString(),
                    $"[green]{kvp.Value.Full}[/]",
                    kvp.Value.Partial > 0 ? $"[yellow]{kvp.Value.Partial}[/]" : "0",
                    kvp.Value.Fail > 0 ? $"[red]{kvp.Value.Fail}[/]" : "0",
                    coverage);
            }

            AnsiConsole.Write(typeTable);

            // List partial forms with their last decoded field
            var partials =
                new List<(string RefId, string Type, uint Flags, List<string> FlagNames, int DataLen, int Consumed,
                    string LastField)>();
            foreach (var form in save.ChangedForms)
            {
                if (form.Data.Length == 0) continue;
                var r = ChangedFormDecoder.Decode(form, formIdArray);
                if (r is null || r.FullyDecoded || r.BytesConsumed == 0) continue;
                var fNames = ChangeFlagRegistry.DescribeFlags(form.ChangeType, form.ChangeFlags);
                var lastField = r.Fields.Count > 0 ? r.Fields[^1].Name : "(none)";
                partials.Add((FormatFormId(form.RefId, formIdArray), form.TypeName, form.ChangeFlags, fNames,
                    form.Data.Length, r.BytesConsumed, lastField));
            }

            // Analyze fully-decoded REFR flag combos
            Console.WriteLine("\nFully-decoded REFR flag distribution:");
            var fullRefrs = new Dictionary<uint, (int Count, int TotalLen, List<int> Sizes)>();
            foreach (var form in save.ChangedForms)
            {
                if (form.Data.Length == 0 || form.TypeName != "REFR") continue;
                var r2 = ChangedFormDecoder.Decode(form, formIdArray);
                if (r2 is null || !r2.FullyDecoded) continue;
                if (!fullRefrs.TryGetValue(form.ChangeFlags, out var stat))
                    stat = (0, 0, new List<int>());
                stat.Count++;
                stat.TotalLen += form.Data.Length;
                if (stat.Sizes.Count < 3) stat.Sizes.Add(form.Data.Length);
                fullRefrs[form.ChangeFlags] = stat;
            }

            foreach (var kvp in fullRefrs.OrderByDescending(x => x.Value.Count).Take(15))
            {
                var fNames = ChangeFlagRegistry.DescribeFlags(0x03, kvp.Key); // 0x03 = REFR type
                var avg = kvp.Value.TotalLen / Math.Max(1, kvp.Value.Count);
                var sizes = string.Join(",", kvp.Value.Sizes);
                Console.WriteLine(
                    $"  flags=0x{kvp.Key:X8} count={kvp.Value.Count,5} avg_len={avg,4}b sizes=[{sizes}] [{string.Join("|", fNames)}]");
            }

            if (partials.Count > 0)
            {
                // Group by type and last field to show patterns
                AnsiConsole.MarkupLine($"\n[bold yellow]Partial Decode Patterns ({partials.Count} forms):[/]\n");
                var groups = partials
                    .GroupBy(p => (p.Type, p.LastField))
                    .OrderByDescending(g => g.Count());
                foreach (var g in groups.Take(20))
                {
                    var sample = g.First();
                    var flagStr = string.Join("|", sample.FlagNames);
                    var avgRemaining = (int)g.Average(x => x.DataLen - x.Consumed);
                    Console.WriteLine(
                        $"  {g.Count(),4}x {g.Key.Type} last={g.Key.LastField} avg_remaining={avgRemaining}b sample_flags={flagStr}");
                    foreach (var p in g.Take(3))
                        Console.WriteLine(
                            $"       {p.RefId} flags=0x{p.Flags:X8} data={p.DataLen}b consumed={p.Consumed}b remaining={p.DataLen - p.Consumed}b");
                }
            }

            if (errors.Count > 0)
            {
                AnsiConsole.MarkupLine("\n[bold red]Decode Warnings:[/]");
                foreach (var err in errors)
                {
                    AnsiConsole.MarkupLine($"  [red]{Markup.Escape(err)}[/]");
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static int ExecuteStfsInfo(string path)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(path)}");
            return 1;
        }

        try
        {
            var data = File.ReadAllBytes(path);
            AnsiConsole.MarkupLine($"[bold green]STFS Container:[/] {Markup.Escape(Path.GetFileName(path))}");
            AnsiConsole.MarkupLine($"  File size: {data.Length:N0} bytes\n");

            // Check for CON/LIVE/PIRS magic
            if (data.Length < 4)
            {
                AnsiConsole.MarkupLine("[red]File too small[/]");
                return 1;
            }

            var magic = Encoding.ASCII.GetString(data, 0, 4);
            if (magic is not ("CON " or "LIVE" or "PIRS"))
            {
                // Check if it's a raw FO3SAVEGAME
                if (data.Length >= 11 && Encoding.ASCII.GetString(data, 0, 11) == "FO3SAVEGAME")
                {
                    AnsiConsole.MarkupLine("[yellow]Not an STFS container — raw FO3SAVEGAME file[/]");
                    AnsiConsole.MarkupLine("Use [cyan]save info[/] to inspect the save data.");
                    return 0;
                }

                AnsiConsole.MarkupLine($"[red]Not an STFS container (magic: {Markup.Escape(magic)})[/]");
                return 1;
            }

            // Parse header
            var header = StfsContainer.ParseHeader(data);

            var headerTable = new Table().Border(TableBorder.Rounded);
            headerTable.AddColumn("Field");
            headerTable.AddColumn("Value");
            headerTable.AddRow("Magic", header.Magic.Trim());
            headerTable.AddRow("Content Type",
                $"0x{header.ContentType:X8}{(header.ContentType == 1 ? " (Save Game)" : "")}");
            headerTable.AddRow("Metadata Version", header.MetadataVersion.ToString());
            headerTable.AddRow("Block Separation",
                $"{header.BlockSeparation} ({(header.BlockSeparation == 0 ? "male" : "female")})");
            headerTable.AddRow("File Table Blocks", header.FileTableBlockCount.ToString());
            headerTable.AddRow("File Table Start Block", header.FileTableBlockNumber.ToString());
            headerTable.AddRow("Total Allocated",
                $"{header.TotalAllocatedBlocks} blocks ({header.TotalAllocatedBlocks * 4096:N0} bytes)");
            headerTable.AddRow("Total Unallocated", $"{header.TotalUnallocatedBlocks} blocks");
            AnsiConsole.Write(headerTable);

            // Try extraction with full diagnostics
            AnsiConsole.MarkupLine("\n[bold]Extraction Attempt:[/]\n");
            var result = StfsContainer.TryExtract(data);

            foreach (var diag in result.Diagnostics)
            {
                string color;
                if (diag.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                    diag.Contains("INVALID", StringComparison.OrdinalIgnoreCase) ||
                    diag.Contains("corrupted", StringComparison.OrdinalIgnoreCase))
                {
                    color = "red";
                }
                else if (diag.Contains("confirmed", StringComparison.OrdinalIgnoreCase) ||
                         diag.Contains("Found:", StringComparison.OrdinalIgnoreCase))
                {
                    color = "green";
                }
                else
                {
                    color = "grey";
                }
                AnsiConsole.MarkupLine($"  [{color}]{Markup.Escape(diag)}[/]");
            }

            AnsiConsole.MarkupLine($"\n  Method: [bold]{result.Method}[/]");

            if (result.FileEntry != null)
            {
                var fe = result.FileEntry;
                AnsiConsole.MarkupLine("\n[bold]File Entry:[/]");
                AnsiConsole.MarkupLine($"  Filename: {Markup.Escape(fe.Filename)}");
                AnsiConsole.MarkupLine($"  File Size: {fe.FileSize:N0} bytes");
                AnsiConsole.MarkupLine(
                    $"  Start Block: {fe.StartBlock} (offset 0x{StfsContainer.DataBlockToRawOffset(fe.StartBlock):X})");
                AnsiConsole.MarkupLine($"  Valid Blocks: {fe.ValidBlocks}");
                AnsiConsole.MarkupLine($"  Allocated Blocks: {fe.AllocatedBlocks}");
                AnsiConsole.MarkupLine($"  Consecutive: {fe.IsConsecutive}");
            }

            if (result.Success)
            {
                AnsiConsole.MarkupLine($"\n[bold green]Payload extracted:[/] {result.Payload!.Length:N0} bytes");

                // Try to parse the header from the extracted payload
                try
                {
                    var save = SaveFileParser.Parse(data);
                    var h = save.Header;
                    AnsiConsole.MarkupLine("\n[bold]Save Header:[/]");
                    AnsiConsole.MarkupLine($"  Player: {Markup.Escape(h.PlayerName)} (Level {h.PlayerLevel})");
                    AnsiConsole.MarkupLine($"  Cell: {Markup.Escape(h.PlayerCell)}");
                    AnsiConsole.MarkupLine($"  Save #{h.SaveNumber}, Playtime: {Markup.Escape(h.SaveDuration)}");
                    AnsiConsole.MarkupLine($"  FormVersion: {h.FormVersion}");
                    AnsiConsole.MarkupLine(
                        $"  Screenshot: {h.ScreenshotWidth}x{h.ScreenshotHeight} ({h.ScreenshotDataSize:N0} bytes)");
                    AnsiConsole.MarkupLine($"  Plugins: {h.Plugins.Count}");
                    AnsiConsole.MarkupLine($"  Changed Forms: {save.ChangedForms.Count}");
                    AnsiConsole.MarkupLine($"  FormID Array: {save.FormIdArray.Count} entries");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"\n[yellow]Save parse failed:[/] {Markup.Escape(ex.Message)}");
                }
            }
            else
            {
                AnsiConsole.MarkupLine("\n[red]Extraction failed[/]");
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static int ExecuteReports(string path, string? esmPath, string outputDir)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(path)}");
            return 1;
        }

        try
        {
            AnsiConsole.MarkupLine("[bold green]Generating save reports...[/]");
            var save = ParseFile(path);
            var formIdArray = save.FormIdArray.ToArray();

            // Decode all changed forms
            AnsiConsole.MarkupLine("  Decoding changed forms...");
            var decodedForms = new Dictionary<int, DecodedFormData>();
            for (var i = 0; i < save.ChangedForms.Count; i++)
            {
                var form = save.ChangedForms[i];
                if (form.Data.Length == 0) continue;
                var decoded = ChangedFormDecoder.Decode(form, formIdArray);
                if (decoded != null)
                {
                    decodedForms[i] = decoded;
                }
            }

            // Optional ESM/DMP enrichment for name resolution
            FormIdResolver? resolver = null;
            if (!string.IsNullOrEmpty(esmPath))
            {
                if (!File.Exists(esmPath))
                {
                    AnsiConsole.MarkupLine($"[yellow]Warning:[/] ESM/DMP not found: {Markup.Escape(esmPath)}");
                }
                else
                {
                    resolver = LoadResolverFromFile(esmPath);
                }
            }

            // Generate reports
            AnsiConsole.MarkupLine("  Generating reports...");
            var reports = SaveReportGenerator.GenerateAllReports(save, decodedForms, resolver);

            // Write to output directory
            Directory.CreateDirectory(outputDir);
            foreach (var (filename, content) in reports)
            {
                var filePath = Path.Combine(outputDir, filename);
                File.WriteAllText(filePath, content);
            }

            AnsiConsole.MarkupLine(
                $"\n[bold green]Generated {reports.Count} reports to {Markup.Escape(outputDir)}:[/]");
            foreach (var (filename, content) in reports.OrderBy(kvp => kvp.Key))
            {
                AnsiConsole.MarkupLine($"  {filename} ({content.Length:N0} bytes)");
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private static FormIdResolver? LoadResolverFromFile(string path)
    {
        try
        {
            AnsiConsole.MarkupLine($"  Loading ESM/DMP: {Markup.Escape(Path.GetFileName(path))}...");
            var fileType = FileTypeDetector.Detect(path);

            AnalysisResult analysisResult;
            if (fileType == AnalysisFileType.EsmFile)
            {
                analysisResult = EsmFileAnalyzer.AnalyzeAsync(path).GetAwaiter().GetResult();
            }
            else if (fileType == AnalysisFileType.Minidump)
            {
                analysisResult = new MinidumpAnalyzer().AnalyzeAsync(path).GetAwaiter().GetResult();
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]  Not an ESM/DMP file, skipping enrichment.[/]");
                return null;
            }

            if (analysisResult.EsmRecords == null)
            {
                AnsiConsole.MarkupLine("[yellow]  No ESM records found, skipping enrichment.[/]");
                return null;
            }

            AnsiConsole.MarkupLine("  Reconstructing records...");
            var fileSize = new FileInfo(path).Length;
            using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read);

            var parser = new RecordParser(
                analysisResult.EsmRecords,
                analysisResult.FormIdMap,
                accessor,
                fileSize,
                analysisResult.MinidumpInfo);
            var records = parser.ReconstructAll();
            var resolver = records.CreateResolver();

            AnsiConsole.MarkupLine(
                $"  [green]Loaded {records.TotalRecordsReconstructed:N0} records for name resolution.[/]");
            return resolver;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]  ESM/DMP load failed: {Markup.Escape(ex.Message)}[/]");
            return null;
        }
    }

    private static int ExecuteBatch(string dirPath)
    {
        if (!Directory.Exists(dirPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Directory not found: {Markup.Escape(dirPath)}");
            return 1;
        }

        var files = Directory.GetFiles(dirPath, "*.fxs")
            .Concat(Directory.GetFiles(dirPath, "*.fos"))
            .OrderBy(f => f)
            .ToArray();

        if (files.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .fxs or .fos files found.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold green]Batch parsing {files.Length} save files...[/]\n");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Save #");
        table.AddColumn("Player");
        table.AddColumn("Lvl");
        table.AddColumn("Status");
        table.AddColumn("Location");
        table.AddColumn("Playtime");
        table.AddColumn("Changes");
        table.AddColumn("FormIDs");

        foreach (var file in files)
        {
            try
            {
                var save = ParseFile(file);
                var h = save.Header;

                table.AddRow(
                    h.SaveNumber.ToString(),
                    string.IsNullOrEmpty(h.PlayerName) ? "[grey](empty)[/]" : Markup.Escape(h.PlayerName),
                    h.PlayerLevel.ToString(),
                    Markup.Escape(h.PlayerStatus),
                    Markup.Escape(h.PlayerCell),
                    Markup.Escape(h.SaveDuration),
                    save.ChangedForms.Count.ToString(),
                    save.FormIdArray.Count.ToString());
            }
            catch (Exception ex)
            {
                table.AddRow(
                    Path.GetFileName(file),
                    $"[red]Error: {Markup.Escape(ex.Message)}[/]",
                    "", "", "", "", "", "");
            }
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
