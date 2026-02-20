using System.CommandLine;
using System.Text;
using FalloutXbox360Utils.Core.Formats.SaveGame;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for inspecting Fallout 3/NV save files (.fxs / .fos).
/// </summary>
public static class SaveCommand
{
    private const string InputArgName = "input";

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
        var typeOpt = new Option<string?>("-t", "--type") { Description = "Filter by change type (e.g., ACHR, REFR, QUST)" };
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
        batchCommand.Arguments.Add(new Argument<string>(InputArgName) { Description = "Path to directory of .fxs files" });
        batchCommand.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue<string>(InputArgName)!;
            return Task.FromResult(ExecuteBatch(input));
        });

        var hexdumpCommand = new Command("hexdump", "Hex-dump global data or changed form raw bytes");
        hexdumpCommand.Arguments.Add(new Argument<string>(InputArgName) { Description = "Path to save file" });
        var globalOpt = new Option<int?>("--global", "-g") { Description = "Global data type ID to dump (e.g., 0 for Misc Stats)" };
        var formOpt = new Option<string?>("--form") { Description = "Changed form RefID to dump (e.g., 0x000014 for player)" };
        var decodeOpt = new Option<bool>("--decode", "-d") { Description = "Also show decoded fields", DefaultValueFactory = _ => true };
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

        command.Subcommands.Add(infoCommand);
        command.Subcommands.Add(changesCommand);
        command.Subcommands.Add(playerCommand);
        command.Subcommands.Add(batchCommand);
        command.Subcommands.Add(hexdumpCommand);
        command.Subcommands.Add(statsCommand);
        command.Subcommands.Add(decodeCommand);

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
            headerTable.AddRow("Player Name", string.IsNullOrEmpty(h.PlayerName) ? "[grey](empty)[/]" : Markup.Escape(h.PlayerName));
            headerTable.AddRow("Player Status", Markup.Escape(h.PlayerStatus));
            headerTable.AddRow("Player Level", h.PlayerLevel.ToString());
            headerTable.AddRow("Current Cell", Markup.Escape(h.PlayerCell));
            headerTable.AddRow("Playtime", Markup.Escape(h.SaveDuration));
            headerTable.AddRow("Screenshot", $"{h.ScreenshotWidth}x{h.ScreenshotHeight} ({h.ScreenshotDataSize:N0} bytes)");
            headerTable.AddRow("Form Version", h.FormVersion.ToString());
            AnsiConsole.Write(headerTable);

            // Plugins
            AnsiConsole.MarkupLine($"\n[bold]Plugins ({h.Plugins.Count}):[/]");
            foreach (var plugin in h.Plugins)
            {
                AnsiConsole.MarkupLine($"  {Markup.Escape(plugin)}");
            }

            // Body summary
            AnsiConsole.MarkupLine($"\n[bold]Body Summary:[/]");
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
            AnsiConsole.MarkupLine($"[red]Error parsing {Markup.Escape(Path.GetFileName(path))}:[/] {Markup.Escape(ex.Message)}");
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
                int withPos = group.Count(f => f.Initial != null);
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

                foreach (var form in displayForms)
                {
                    string posStr = form.Initial != null
                        ? $"({form.Initial.PosX:F0}, {form.Initial.PosY:F0}, {form.Initial.PosZ:F0})"
                        : "[grey]-[/]";

                    var activeFlags = ChangeFlagRegistry.DescribeFlags(form.ChangeType, form.ChangeFlags);
                    string flagStr = activeFlags.Count > 0
                        ? Markup.Escape(string.Join("|", activeFlags))
                        : $"0x{form.ChangeFlags:X8}";

                    detailTable.AddRow(
                        Markup.Escape(form.RefId.ToString()),
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
            AnsiConsole.MarkupLine($"  Name: {(string.IsNullOrEmpty(h.PlayerName) ? "(empty)" : Markup.Escape(h.PlayerName))}");
            AnsiConsole.MarkupLine($"  Level: {h.PlayerLevel}");
            AnsiConsole.MarkupLine($"  Status: {Markup.Escape(h.PlayerStatus)}");
            AnsiConsole.MarkupLine($"  Cell: {Markup.Escape(h.PlayerCell)}");
            AnsiConsole.MarkupLine($"  Playtime: {Markup.Escape(h.SaveDuration)}");

            if (save.PlayerLocation != null)
            {
                var loc = save.PlayerLocation;
                AnsiConsole.MarkupLine($"\n[bold]World Position (Global Data Type 1):[/]");
                AnsiConsole.MarkupLine($"  Worldspace: {loc.WorldspaceRefId}");
                AnsiConsole.MarkupLine($"  Grid: ({loc.CoordX}, {loc.CoordY})");
                AnsiConsole.MarkupLine($"  Cell: {loc.CellRefId}");
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
                    AnsiConsole.MarkupLine($"  {Markup.Escape(form.RefId.ToString())} ({form.TypeName}): ({form.Initial!.PosX:F0}, {form.Initial.PosY:F0}, {form.Initial.PosZ:F0}) flags=0x{form.ChangeFlags:X8}");
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

                AnsiConsole.MarkupLine($"[bold green]Global Data Type {entry.Type}: {entry.TypeName}[/] ({entry.Data.Length:N0} bytes)\n");
                PrintHexDump(entry.Data);
            }
            else if (formRef is not null)
            {
                // Parse refid as hex
                uint targetRaw = Convert.ToUInt32(formRef.Replace("0x", ""), 16);
                var form = save.ChangedForms.FirstOrDefault(f => f.RefId.Raw == targetRaw);
                if (form is null)
                {
                    // Also try matching resolved FormID against the array
                    form = save.ChangedForms.FirstOrDefault(f =>
                    {
                        try { return f.RefId.ResolveFormId(save.FormIdArray.ToArray()) == targetRaw; }
                        catch { return false; }
                    });
                }

                if (form is null)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] Changed form with RefID {Markup.Escape(formRef)} not found.");
                    return 1;
                }

                AnsiConsole.MarkupLine($"[bold green]Changed Form {Markup.Escape(form.RefId.ToString())} ({form.TypeName})[/]");
                var flagNames = ChangeFlagRegistry.DescribeFlags(form.ChangeType, form.ChangeFlags);
                AnsiConsole.MarkupLine($"  Flags: 0x{form.ChangeFlags:X8}  Version: {form.Version}  Data: {form.Data.Length:N0} bytes");
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

            for (int i = 0; i < save.Statistics.Count; i++)
            {
                string label = i < SaveStatistics.Labels.Length
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

        for (int offset = 0; offset < data.Length; offset += bytesPerLine)
        {
            sb.Append($"  {offset:X6}  ");

            int lineLen = Math.Min(bytesPerLine, data.Length - offset);
            for (int i = 0; i < bytesPerLine; i++)
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
            for (int i = 0; i < lineLen; i++)
            {
                byte b = data[offset + i];
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

        AnsiConsole.MarkupLine($"\n[bold yellow]Decoded Fields ({decoded.BytesConsumed}/{decoded.TotalBytes} bytes consumed):[/]");

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
            var typeStats = new Dictionary<string, (int Total, int Full, int Partial, int Fail, long TotalBytes, long DecodedBytes)>();
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
                string typeName = form.TypeName;
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
                        errors.Add($"{form.RefId} ({typeName}) flags=0x{form.ChangeFlags:X8} [{string.Join("|", flagNames)}] data={form.Data.Length}b");
                    }
                }

                if (result.Warnings.Count > 0 && errors.Count < 20)
                {
                    errors.Add($"{form.RefId} ({typeName}): {string.Join("; ", result.Warnings)}");
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
            overallTable.AddRow("[bold]Decode coverage[/]", totalBytes > 0 ? $"{100.0 * decodedBytes / totalBytes:F1}%" : "N/A");
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
                string coverage = kvp.Value.TotalBytes > 0
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
