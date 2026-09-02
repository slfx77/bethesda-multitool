using System.CommandLine;
using BethesdaMultitool.CLI.Rendering.Map;
using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Games;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Classic;

/// <summary>
///     <c>classic</c> command group — reads the pre-plugin-era games (Arena, Daggerfall,
///     Battlespire, Redguard, Fallout 1/2, Fallout Tactics), whose content lives in bespoke
///     containers rather than a plugin record stream. <c>classic text</c> today; the map and
///     audio arms join as their game verticals land.
/// </summary>
public static class ClassicCommand
{
    public static Command Create()
    {
        var command = new Command("classic", "Read classic (pre-Morrowind) game data");
        command.Subcommands.Add(CreateTextCommand());
        command.Subcommands.Add(CreateMapCommand());
        command.Subcommands.Add(CreateExeCommand());
        return command;
    }

    private static Command CreateExeCommand()
    {
        var command = new Command("exe", "Unpack a compressed classic game executable (Arena A.EXE)");
        var inputArg = new Argument<string>("input") { Description = "The packed executable" };
        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Where to write the unpacked image (default: alongside the input, .unpacked.exe)"
        };
        var infoOnlyOption = new Option<bool>("--info")
        {
            Description = "Report sizes without writing the unpacked image"
        };
        command.Arguments.Add(inputArg);
        command.Options.Add(outputOption);
        command.Options.Add(infoOnlyOption);
        command.SetAction((parseResult, _) => Guarded(() => RunExe(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(outputOption),
            parseResult.GetValue(infoOnlyOption))));
        return command;
    }

    private static void RunExe(string input, string? output, bool infoOnly)
    {
        if (!File.Exists(input))
        {
            throw new FileNotFoundException($"Input not found: {input}", input);
        }

        var name = Path.GetFileName(input);
        var bytes = File.ReadAllBytes(input);

        if (!ArenaExeUnpacker.LooksPacked(bytes))
        {
            throw new InvalidDataException(
                $"'{name}' does not look PKLITE-packed (no 0xFFFF terminator before its trailer). " +
                "An already-unpacked executable needs no processing.");
        }

        var declared = ArenaExeUnpacker.ReadDeclaredSize(bytes);
        var unpacked = ArenaExeUnpacker.Unpack(bytes, name);

        AnsiConsole.MarkupLine("[bold cyan]{0}[/]", Markup.Escape(name));

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Property");
        table.AddColumn(new TableColumn("Value").RightAligned());
        table.AddRow("Packed size", $"{bytes.Length:N0} bytes");
        table.AddRow("Declared size", $"{declared:N0} bytes");
        table.AddRow("Unpacked size", $"{unpacked.Length:N0} bytes");
        table.AddRow("Expansion", $"{(double)unpacked.Length / bytes.Length:F2}x");
        AnsiConsole.Write(table);

        if (infoOnly)
        {
            return;
        }

        var target = output ?? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".",
            Path.GetFileNameWithoutExtension(input) + ".unpacked.exe");

        var directory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(target, unpacked);
        AnsiConsole.MarkupLine("[green]Wrote[/] {0}", Markup.Escape(target));
    }

    private static Command CreateMapCommand()
    {
        var command = new Command("map", "Inspect or export classic voxel maps (Arena .MIF / .RMD)");
        command.Subcommands.Add(CreateMapInfoCommand());
        command.Subcommands.Add(CreateMapExportCommand());
        return command;
    }

    private static Command CreateMapInfoCommand()
    {
        var command = new Command("info", "Show a map's dimensions, levels and chunk inventory");
        var inputArg = new Argument<string>("input") { Description = "A .MIF or .RMD file" };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description = "Virtual path of the map inside an archive input"
        };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.SetAction((parseResult, _) => Guarded(() => RunMapInfo(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption))));
        return command;
    }

    private static Command CreateMapExportCommand()
    {
        var command = new Command("export", "Render a map's voxel layers to PNG (one image per layer)");
        var inputArg = new Argument<string>("input") { Description = "A .MIF or .RMD file, or an archive with --entry" };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description = "Virtual path of the map inside an archive input"
        };
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory for PNG layers",
            DefaultValueFactory = _ => "TestOutput/classic-maps"
        };
        var scaleOption = new Option<int>("--scale")
        {
            Description = "Pixels per voxel (default 4)",
            DefaultValueFactory = _ => 4
        };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.Options.Add(outputOption);
        command.Options.Add(scaleOption);
        command.SetAction((parseResult, _) => Guarded(() => RunMapExport(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption),
            parseResult.GetValue(outputOption)!,
            parseResult.GetValue(scaleOption))));
        return command;
    }

    private static Task<int> Guarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                       or NotSupportedException)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", Markup.Escape(ex.Message));
            return Task.FromResult(1);
        }

        return Task.FromResult(0);
    }

    private static (byte[] Bytes, string Name) LoadMapSource(string input, string? entryName)
    {
        if (!File.Exists(input))
        {
            throw new FileNotFoundException($"Input not found: {input}", input);
        }

        if (entryName is null)
        {
            return (File.ReadAllBytes(input), Path.GetFileName(input));
        }

        using var archive = ArchiveReader.Open(input);
        var bytes = archive.ReadFile(entryName)
                    ?? throw new FileNotFoundException(
                        $"Entry '{entryName}' not found in {Path.GetFileName(input)} " +
                        $"({archive.FormatName}, {archive.TotalFiles} files).");
        return (bytes, Path.GetFileName(entryName.Replace('/', '\\')));
    }

    private static void RunMapInfo(string input, string? entryName)
    {
        var (bytes, name) = LoadMapSource(input, entryName);

        if (name.EndsWith(".RMD", StringComparison.OrdinalIgnoreCase))
        {
            var chunk = ArenaRmdFile.Parse(bytes, name);
            AnsiConsole.MarkupLine(
                "[bold cyan]{0}[/] — wilderness chunk {1}x{2}, {3}",
                Markup.Escape(name),
                ArenaRmdFile.Width,
                ArenaRmdFile.Depth,
                chunk.WasCompressed ? "word-RLE compressed" : "stored uncompressed");
            AnsiConsole.MarkupLine(
                "  [grey]distinct voxels — FLOR {0}, MAP1 {1}, MAP2 {2}[/]",
                chunk.Floor.Distinct().Count(),
                chunk.Map1.Distinct().Count(),
                chunk.Map2.Distinct().Count());
            return;
        }

        var map = ArenaMifFile.Parse(bytes, name);
        AnsiConsole.MarkupLine(
            "[bold cyan]{0}[/] — {1}x{2} voxels, {3} level(s), starts on level {4}",
            Markup.Escape(map.Name),
            map.Width,
            map.Depth,
            map.Levels.Count,
            map.StartingLevelIndex);

        if (map.DeclaredLevelCount != map.Levels.Count)
        {
            AnsiConsole.MarkupLine(
                "  [yellow]header declares {0} level(s); {1} were actually present[/]",
                map.DeclaredLevelCount,
                map.Levels.Count);
        }

        var starts = map.StartPoints.Where(p => !p.IsUnset).ToList();
        if (starts.Count > 0)
        {
            AnsiConsole.MarkupLine("  [grey]start points:[/] {0}",
                string.Join(", ", starts.Select(p => $"({p.X}, {p.Y})")));
        }

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Level");
        table.AddColumn("Name");
        table.AddColumn("INF");
        table.AddColumn(new TableColumn("Floors").RightAligned());
        table.AddColumn(new TableColumn("Locks").RightAligned());
        table.AddColumn(new TableColumn("Triggers").RightAligned());
        table.AddColumn("Layers");

        for (var i = 0; i < map.Levels.Count; i++)
        {
            var level = map.Levels[i];
            var layers = new List<string>();
            if (level.Floor.Length > 0)
            {
                layers.Add("FLOR");
            }

            if (level.Map1.Length > 0)
            {
                layers.Add("MAP1");
            }

            if (level.Map2.Length > 0)
            {
                layers.Add("MAP2");
            }

            layers.AddRange(level.UndecodedChunks.Keys.Select(k => $"{k}?"));

            table.AddRow(
                i.ToString(),
                Markup.Escape(level.LevelName ?? "—"),
                Markup.Escape(level.InfoFile ?? "—"),
                level.FloorTextureCount.ToString(),
                level.Locks.Count.ToString(),
                level.Triggers.Count.ToString(),
                string.Join(" ", layers));
        }

        AnsiConsole.Write(table);

        var withText = map.Levels.SelectMany(l => l.Triggers).Count(t => t.HasText);
        var withSound = map.Levels.SelectMany(l => l.Triggers).Count(t => t.HasSound);
        if (withText + withSound > 0)
        {
            AnsiConsole.MarkupLine(
                "[grey]{0} trigger(s) reference *TEXT, {1} reference @SOUND (both in the level's .INF).[/]",
                withText,
                withSound);
        }
    }

    private static void RunMapExport(string input, string? entryName, string outputDir, int scale)
    {
        var (bytes, name) = LoadMapSource(input, entryName);

        var layers = name.EndsWith(".RMD", StringComparison.OrdinalIgnoreCase)
            ? ArenaMapRenderer.RenderRmd(ArenaRmdFile.Parse(bytes, name), name, outputDir, scale)
            : ArenaMapRenderer.RenderMif(ArenaMifFile.Parse(bytes, name), outputDir, scale);

        AnsiConsole.MarkupLine("[green]Wrote {0} layer image(s)[/]", layers.Count);
        foreach (var layer in layers)
        {
            AnsiConsole.MarkupLine(
                "  {0}  [grey]{1} {2}x{3}, {4} distinct voxel id(s)[/]",
                Markup.Escape(layer.Path),
                layer.Layer,
                layer.Width,
                layer.Height,
                layer.DistinctVoxels);
        }
    }

    private static Command CreateTextCommand()
    {
        var command = new Command(
            "text",
            "Dump a classic game's authored text (Arena: TEMPLATE.DAT strings + .INF on-screen text)");
        var inputArg = new Argument<string>("input")
        {
            Description = "Install/data directory, or a single TEMPLATE.DAT or .INF file"
        };
        var filterOption = new Option<string?>("--filter", "-f")
        {
            Description = "Only show entries whose text or name contains this substring (case-insensitive)"
        };
        var sourceOption = new Option<string>("--source", "-s")
        {
            Description = "Which sources to read: template, inf, or all",
            DefaultValueFactory = _ => "all"
        };
        var limitOption = new Option<int>("--limit", "-l")
        {
            Description = "Maximum entries to print (0 = no limit)",
            DefaultValueFactory = _ => 0
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(filterOption);
        command.Options.Add(sourceOption);
        command.Options.Add(limitOption);
        command.SetAction((parseResult, _) =>
        {
            try
            {
                Run(
                    parseResult.GetValue(inputArg)!,
                    parseResult.GetValue(sourceOption)!,
                    parseResult.GetValue(filterOption),
                    parseResult.GetValue(limitOption));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                           or NotSupportedException)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] {0}", Markup.Escape(ex.Message));
                return Task.FromResult(1);
            }

            return Task.FromResult(0);
        });
        return command;
    }

    private static void Run(string input, string source, string? filter, int limit)
    {
        var wantTemplate = source is "all" or "template";
        var wantInf = source is "all" or "inf";
        if (!wantTemplate && !wantInf)
        {
            throw new InvalidOperationException($"Unknown --source '{source}'. Use template, inf, or all.");
        }

        var printed = 0;

        if (File.Exists(input))
        {
            var name = Path.GetFileName(input);
            var bytes = File.ReadAllBytes(input);
            if (name.EndsWith(".INF", StringComparison.OrdinalIgnoreCase))
            {
                // A loose .INF is plaintext, but one the user extracted from GLOBAL.BSA is not.
                var inf = ArenaInfFile.Parse(bytes, name, ArenaInfFile.IsProbablyEncrypted(bytes));
                PrintInf(inf, filter, limit, ref printed);
            }
            else
            {
                PrintTemplate(ArenaTemplateDat.Parse(bytes), filter, limit, ref printed);
            }

            WriteFooter(printed);
            return;
        }

        if (!Directory.Exists(input))
        {
            throw new FileNotFoundException($"Input not found: {input}", input);
        }

        var root = Path.GetFullPath(input);
        var profile = ClassicGameLocator.DetectFromDirectory(root)
                      ?? throw new InvalidOperationException(
                          $"'{root}' is not a recognizable classic game install (no profile's markers matched).");

        if (profile.Game != BethesdaGame.Arena)
        {
            throw new NotSupportedException(
                $"'classic text' does not read {profile.Game} yet — its text formats land with its game vertical. " +
                "Arena is supported today.");
        }

        AnsiConsole.MarkupLine("[bold cyan]{0}[/] — [grey]{1}[/]", profile.Game, Markup.Escape(root));
        AnsiConsole.WriteLine();

        if (wantTemplate)
        {
            var templatePath = Path.Combine(root, "TEMPLATE.DAT");
            if (File.Exists(templatePath))
            {
                PrintTemplate(ArenaTemplateDat.Parse(File.ReadAllBytes(templatePath)), filter, limit, ref printed);
            }
        }

        if (wantInf && (limit == 0 || printed < limit))
        {
            foreach (var (name, plain) in ArenaRecordSource.EnumerateInfFiles(root))
            {
                if (limit > 0 && printed >= limit)
                {
                    break;
                }

                PrintInf(ArenaInfFile.ParseText(System.Text.Encoding.Latin1.GetString(plain), name),
                    filter, limit, ref printed);
            }
        }

        WriteFooter(printed);
    }

    private static void PrintTemplate(ArenaTemplateDat template, string? filter, int limit, ref int printed)
    {
        var wroteHeader = false;
        foreach (var entry in template.Entries)
        {
            if (limit > 0 && printed >= limit)
            {
                return;
            }

            var matches = entry.Values.Where(v => Matches(v, filter)).ToList();
            if (matches.Count == 0 && !Matches(entry.DisplayKey, filter))
            {
                continue;
            }

            if (!wroteHeader)
            {
                AnsiConsole.MarkupLine("[bold]TEMPLATE.DAT[/]");
                wroteHeader = true;
            }

            var label = entry.Copy > 0 ? $"{entry.DisplayKey} (tileset copy {entry.Copy})" : entry.DisplayKey;
            AnsiConsole.MarkupLine("  [yellow]{0}[/] [grey]{1} value(s)[/]", Markup.Escape(label), entry.Values.Count);
            foreach (var value in filter is null ? entry.Values : matches)
            {
                AnsiConsole.MarkupLine("    {0}", Markup.Escape(Collapse(value)));
            }

            printed++;
        }
    }

    private static void PrintInf(ArenaInfFile inf, string? filter, int limit, ref int printed)
    {
        var wroteHeader = false;
        foreach (var text in inf.Texts)
        {
            if (limit > 0 && printed >= limit)
            {
                return;
            }

            var body = text.Text ?? text.Riddle?.Riddle;
            if (body is null && text.KeyId is null)
            {
                continue;
            }

            if (!Matches(body, filter) && !Matches(inf.Name, filter))
            {
                continue;
            }

            if (!wroteHeader)
            {
                AnsiConsole.MarkupLine("[bold]{0}[/]", Markup.Escape(inf.Name));
                wroteHeader = true;
            }

            var tags = new List<string>();
            if (text.KeyId is { } key)
            {
                tags.Add($"key +{key}");
            }

            if (text.Riddle is not null)
            {
                tags.Add("riddle");
            }

            if (text.DisplayedOnce)
            {
                tags.Add("once");
            }

            var suffix = tags.Count > 0 ? $" [grey]({string.Join(", ", tags)})[/]" : string.Empty;
            AnsiConsole.MarkupLine("  [yellow]*TEXT {0}[/]{1}", text.Id, suffix);

            if (body is not null)
            {
                foreach (var line in body.Split('\n'))
                {
                    AnsiConsole.MarkupLine("    {0}", Markup.Escape(line));
                }
            }

            if (text.Riddle is { } riddle)
            {
                if (riddle.Answers.Count > 0)
                {
                    AnsiConsole.MarkupLine("    [grey]answers:[/] {0}",
                        Markup.Escape(string.Join(" | ", riddle.Answers.Select(a => a.Trim()))));
                }

                if (riddle.Correct.Length > 0)
                {
                    AnsiConsole.MarkupLine("    [green]correct:[/] {0}", Markup.Escape(Collapse(riddle.Correct)));
                }

                if (riddle.Wrong.Length > 0)
                {
                    AnsiConsole.MarkupLine("    [red]wrong:[/] {0}", Markup.Escape(Collapse(riddle.Wrong)));
                }
            }

            printed++;
        }
    }

    private static void WriteFooter(int printed)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]{0} entr{1} shown.[/]", printed, printed == 1 ? "y" : "ies");
    }

    private static bool Matches(string? value, string? filter)
    {
        return filter is null ||
               (value is not null && value.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private static string Collapse(string value)
    {
        return value.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
