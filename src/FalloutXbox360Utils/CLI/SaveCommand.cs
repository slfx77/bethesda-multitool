using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.SaveGame;
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
    internal static string FormatFormId(SaveRefId refId, ReadOnlySpan<uint> formIdArray)
    {
        var resolved = refId.ResolveFormId(formIdArray);
        return resolved != 0 ? $"0x{resolved:X8}" : refId.ToString();
    }

    internal static SaveFile ParseFile(string path)
    {
        var data = File.ReadAllBytes(path);
        return SaveFileParser.Parse(data);
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

        var statsCommand = new Command("stats", "Show gameplay statistics from Misc Stats (Global Data Type 0)");
        statsCommand.Arguments.Add(new Argument<string>(InputArgName) { Description = "Path to save file" });
        statsCommand.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue<string>(InputArgName)!;
            return Task.FromResult(ExecuteStats(input));
        });

        command.Subcommands.Add(infoCommand);
        command.Subcommands.Add(changesCommand);
        command.Subcommands.Add(playerCommand);
        command.Subcommands.Add(batchCommand);
        command.Subcommands.Add(SaveReportCommand.CreateHexdumpCommand());
        command.Subcommands.Add(statsCommand);
        command.Subcommands.Add(SaveDecodeCommand.CreateDecodeCommand());
        command.Subcommands.Add(SaveDecodeCommand.CreateStfsInfoCommand());
        command.Subcommands.Add(SaveReportCommand.CreateReportsCommand());

        return command;
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
