using System.CommandLine;
using BethesdaMultitool.Core.Formats.Papyrus;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Papyrus;

/// <summary>Parser and archive surfaces for Skyrim, Fallout 4, and Fallout 76 Papyrus binaries.</summary>
public static class PapyrusCommand
{
    public static Command Create()
    {
        var command = new Command("papyrus", "Inspect and extract Papyrus PEX scripts");
        command.Aliases.Add("pex");
        command.Subcommands.Add(CreateListCommand());
        command.Subcommands.Add(CreateInspectCommand());
        command.Subcommands.Add(CreateDecompileCommand());
        command.Subcommands.Add(CreateDisassembleCommand());
        command.Subcommands.Add(CreateExtractCommand());
        command.Subcommands.Add(CreateValidateCommand());
        return command;
    }

    private static Command CreateDisassembleCommand()
    {
        var command = new Command("disassemble", "Write a label-based Papyrus VM instruction listing");
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a loose PEX, BSA, or BA2"
        };
        var scriptOption = new Option<string?>("--script", "-s")
        {
            Description = "Exact virtual path or unique substring when input is an archive"
        };
        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Optional listing output path (stdout when omitted)"
        };
        command.Arguments.Add(inputArgument);
        command.Options.Add(scriptOption);
        command.Options.Add(outputOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await RunDisassembleAsync(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(scriptOption),
                parseResult.GetValue(outputOption),
                cancellationToken);
        });
        return command;
    }

    private static Command CreateListCommand()
    {
        var command = new Command("list", "List PEX scripts in a BSA or BA2 archive");
        var inputArgument = new Argument<string>("input") { Description = "Path to a BSA or BA2 archive" };
        var filterOption = new Option<string?>("--filter", "-f")
        {
            Description = "Optional case-insensitive virtual-path substring"
        };
        var limitOption = new Option<int>("--limit", "-l")
        {
            Description = "Maximum entries to display (0 = all)",
            DefaultValueFactory = _ => 200
        };
        command.Arguments.Add(inputArgument);
        command.Options.Add(filterOption);
        command.Options.Add(limitOption);
        command.SetAction((parseResult, _) =>
        {
            RunList(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(filterOption),
                parseResult.GetValue(limitOption));
            return Task.CompletedTask;
        });
        return command;
    }

    private static Command CreateDecompileCommand()
    {
        var command = new Command("decompile", "Reconstruct Papyrus source from a loose or archived PEX");
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a loose PEX, BSA, or BA2"
        };
        var scriptOption = new Option<string?>("--script", "-s")
        {
            Description = "Exact virtual path or unique substring when input is an archive"
        };
        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Optional PSC output path (stdout when omitted)"
        };
        command.Arguments.Add(inputArgument);
        command.Options.Add(scriptOption);
        command.Options.Add(outputOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await RunDecompileAsync(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(scriptOption),
                parseResult.GetValue(outputOption),
                cancellationToken);
        });
        return command;
    }

    private static Command CreateInspectCommand()
    {
        var command = new Command("inspect", "Parse a loose PEX or one script inside a BSA/BA2 archive");
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a loose PEX, BSA, or BA2"
        };
        var scriptOption = new Option<string?>("--script", "-s")
        {
            Description = "Exact virtual path or unique substring when input is an archive"
        };
        command.Arguments.Add(inputArgument);
        command.Options.Add(scriptOption);
        command.SetAction((parseResult, _) =>
        {
            RunInspect(parseResult.GetValue(inputArgument)!, parseResult.GetValue(scriptOption));
            return Task.CompletedTask;
        });
        return command;
    }

    private static Command CreateExtractCommand()
    {
        var command = new Command("extract", "Extract PEX scripts from a BSA or BA2 archive");
        var inputArgument = new Argument<string>("input") { Description = "Path to a BSA or BA2 archive" };
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory",
            DefaultValueFactory = _ => "pex-output"
        };
        var filterOption = new Option<string?>("--filter", "-f")
        {
            Description = "Optional case-insensitive virtual-path substring"
        };
        var overwriteOption = new Option<bool>("--overwrite")
        {
            Description = "Replace existing files"
        };
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(filterOption);
        command.Options.Add(overwriteOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await RunExtractAsync(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(outputOption)!,
                parseResult.GetValue(filterOption),
                parseResult.GetValue(overwriteOption),
                cancellationToken);
        });
        return command;
    }

    private static Command CreateValidateCommand()
    {
        var command = new Command("validate", "Parse every selected PEX in a BSA/BA2 archive");
        var inputArgument = new Argument<string>("input") { Description = "Path to a BSA or BA2 archive" };
        var filterOption = new Option<string?>("--filter", "-f")
        {
            Description = "Optional case-insensitive virtual-path substring"
        };
        var limitOption = new Option<int>("--limit", "-l")
        {
            Description = "Maximum scripts to validate (0 = all)",
            DefaultValueFactory = _ => 0
        };
        var decompileOption = new Option<bool>("--decompile")
        {
            Description = "Also reconstruct source for every parsed script"
        };
        command.Arguments.Add(inputArgument);
        command.Options.Add(filterOption);
        command.Options.Add(limitOption);
        command.Options.Add(decompileOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await RunValidateAsync(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(filterOption),
                parseResult.GetValue(limitOption),
                parseResult.GetValue(decompileOption),
                cancellationToken);
        });
        return command;
    }

    private static void RunList(string input, string? filter, int limit)
    {
        if (!RequireFile(input))
        {
            return;
        }

        using var archive = PexArchiveReader.Open(input);
        var matching = Filter(archive.Entries, filter).ToArray();
        var shown = limit > 0 ? matching.Take(limit) : matching;

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Virtual path");
        table.AddColumn(new TableColumn("Size").RightAligned());
        table.AddColumn("Compressed");
        foreach (var entry in shown)
        {
            table.AddRow(
                Markup.Escape(entry.VirtualPath),
                $"{entry.UncompressedSize:N0}",
                entry.Compressed ? "[green]Yes[/]" : string.Empty);
        }

        AnsiConsole.MarkupLine(
            "[cyan]{0} Papyrus script(s)[/] in {1} ({2})",
            matching.Length,
            Markup.Escape(Path.GetFileName(input)),
            archive.FormatName);
        AnsiConsole.Write(table);
        if (limit > 0 && matching.Length > limit)
        {
            AnsiConsole.MarkupLine("[grey]Showing first {0}; use --limit 0 for all.[/]", limit);
        }
    }

    private static async Task RunDecompileAsync(
        string input,
        string? scriptSelector,
        string? output,
        CancellationToken cancellationToken)
    {
        if (!RequireFile(input))
        {
            return;
        }

        try
        {
            PexFile pex;
            string defaultName;
            if (Path.GetExtension(input).Equals(".pex", StringComparison.OrdinalIgnoreCase))
            {
                pex = PexParser.Parse(input);
                defaultName = Path.GetFileNameWithoutExtension(input) + ".psc";
            }
            else
            {
                using var archive = PexArchiveReader.Open(input);
                var entry = ResolveArchiveEntry(archive, scriptSelector);
                if (entry is null)
                {
                    return;
                }

                pex = archive.Parse(entry);
                defaultName = Path.ChangeExtension(Path.GetFileName(entry.VirtualPath), ".psc");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var source = PexDecompiler.Decompile(pex);
            if (string.IsNullOrWhiteSpace(output))
            {
                AnsiConsole.Write(new Text(source));
                AnsiConsole.WriteLine();
                return;
            }

            var outputPath = Directory.Exists(output)
                ? Path.Combine(output, defaultName)
                : output;
            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, source + Environment.NewLine, cancellationToken);
            AnsiConsole.MarkupLine("[green]Wrote decompiled Papyrus source[/] to {0}", Markup.Escape(fullPath));
        }
        catch (Exception ex) when (ex is PexParseException or IOException or InvalidDataException or
                                   InvalidOperationException)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", Markup.Escape(ex.Message));
        }
    }

    private static async Task RunDisassembleAsync(
        string input,
        string? scriptSelector,
        string? output,
        CancellationToken cancellationToken)
    {
        if (!RequireFile(input))
        {
            return;
        }

        try
        {
            PexFile pex;
            string defaultName;
            if (Path.GetExtension(input).Equals(".pex", StringComparison.OrdinalIgnoreCase))
            {
                pex = PexParser.Parse(input);
                defaultName = Path.GetFileNameWithoutExtension(input) + ".pexasm";
            }
            else
            {
                using var archive = PexArchiveReader.Open(input);
                var entry = ResolveArchiveEntry(archive, scriptSelector);
                if (entry is null)
                {
                    return;
                }

                pex = archive.Parse(entry);
                defaultName = Path.ChangeExtension(Path.GetFileName(entry.VirtualPath), ".pexasm");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var listing = PexDisassembler.Disassemble(pex);
            if (string.IsNullOrWhiteSpace(output))
            {
                AnsiConsole.Write(new Text(listing));
                AnsiConsole.WriteLine();
                return;
            }

            var outputPath = Directory.Exists(output)
                ? Path.Combine(output, defaultName)
                : output;
            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, listing + Environment.NewLine, cancellationToken);
            AnsiConsole.MarkupLine("[green]Wrote Papyrus VM listing[/] to {0}", Markup.Escape(fullPath));
        }
        catch (Exception ex) when (ex is PexParseException or IOException or InvalidDataException or
                                   InvalidOperationException)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", Markup.Escape(ex.Message));
        }
    }

    private static void RunInspect(string input, string? scriptSelector)
    {
        if (!RequireFile(input))
        {
            return;
        }

        try
        {
            PexFile pex;
            string label;
            if (Path.GetExtension(input).Equals(".pex", StringComparison.OrdinalIgnoreCase))
            {
                pex = PexParser.Parse(input);
                label = Path.GetFileName(input);
            }
            else
            {
                using var archive = PexArchiveReader.Open(input);
                var entry = ResolveArchiveEntry(archive, scriptSelector);
                if (entry is null)
                {
                    return;
                }

                pex = archive.Parse(entry);
                label = $"{Path.GetFileName(input)}::{entry.VirtualPath}";
            }

            WriteInspection(label, pex);
        }
        catch (Exception ex) when (ex is PexParseException or InvalidOperationException)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", Markup.Escape(ex.Message));
        }
    }

    private static async Task RunExtractAsync(
        string input,
        string output,
        string? filter,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (!RequireFile(input))
        {
            return;
        }

        using var archive = PexArchiveReader.Open(input);
        var entries = Filter(archive.Entries, filter).ToArray();
        var outputRoot = Path.GetFullPath(output);
        Directory.CreateDirectory(outputRoot);
        var rootPrefix = outputRoot.EndsWith(Path.DirectorySeparatorChar)
            ? outputRoot
            : outputRoot + Path.DirectorySeparatorChar;
        var written = 0;
        var skipped = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = entry.VirtualPath.Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(outputRoot, relativePath));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Archive entry escapes the output directory: {entry.VirtualPath}");
            }

            if (!overwrite && File.Exists(destination))
            {
                skipped++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, archive.Extract(entry), cancellationToken);
            written++;
        }

        AnsiConsole.MarkupLine(
            "[green]Extracted {0} Papyrus script(s)[/] to {1}{2}",
            written,
            Markup.Escape(outputRoot),
            skipped > 0 ? $"; skipped {skipped:N0} existing" : string.Empty);
    }

    private static async Task RunValidateAsync(
        string input,
        string? filter,
        int limit,
        bool decompile,
        CancellationToken cancellationToken)
    {
        if (!RequireFile(input))
        {
            return;
        }

        using var archive = PexArchiveReader.Open(input);
        IEnumerable<PexArchiveEntry> selected = Filter(archive.Entries, filter);
        if (limit > 0)
        {
            selected = selected.Take(limit);
        }

        var entries = selected.ToArray();
        var failures = new List<(string Path, string Error)>();
        var parsed = 0;
        var unstructuredBranches = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var pex = archive.Parse(entry);
                if (decompile)
                {
                    var source = PexDecompiler.Decompile(pex);
                    if (source.Contains("; Unsupported VM opcode", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("decompiler left an unsupported VM opcode");
                    }

                    unstructuredBranches += CountOccurrences(source, "; VM branch to instruction");
                }

                parsed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add((entry.VirtualPath, ex.Message));
            }

            if ((parsed + failures.Count) % 128 == 0)
            {
                await Task.Yield();
            }
        }

        timer.Stop();
        var color = failures.Count == 0 ? "green" : "yellow";
        AnsiConsole.MarkupLine(
            "[{0}]{1} {2:N0}/{3:N0} Papyrus scripts in {4:0.00}s; failures: {5:N0}{6}[/]",
            color,
            decompile ? "Decompiled" : "Parsed",
            parsed,
            entries.Length,
            timer.Elapsed.TotalSeconds,
            failures.Count,
            decompile ? $"; unstructured branches: {unstructuredBranches:N0}" : string.Empty);
        if (failures.Count == 0)
        {
            return;
        }

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Virtual path");
        table.AddColumn("Error");
        foreach (var failure in failures.Take(20))
        {
            table.AddRow(Markup.Escape(failure.Path), Markup.Escape(failure.Error));
        }

        AnsiConsole.Write(table);
        if (failures.Count > 20)
        {
            AnsiConsole.MarkupLine("[grey]Showing first 20 failures.[/]");
        }
    }

    private static PexArchiveEntry? ResolveArchiveEntry(PexArchiveReader archive, string? selector)
    {
        if (!string.IsNullOrWhiteSpace(selector))
        {
            var selected = archive.Find(selector);
            if (selected is null)
            {
                AnsiConsole.MarkupLine(
                    "[red]Error:[/] Papyrus script not found in archive: {0}",
                    Markup.Escape(selector));
            }

            return selected;
        }

        if (archive.Entries.Count == 1)
        {
            return archive.Entries[0];
        }

        AnsiConsole.MarkupLine(
            "[red]Error:[/] Archive contains {0} Papyrus scripts; select one with --script.",
            archive.Entries.Count);
        return null;
    }

    private static void WriteInspection(string label, PexFile pex)
    {
        var header = new Table().Border(TableBorder.Rounded);
        header.AddColumn("Property");
        header.AddColumn("Value");
        header.AddRow("Source", Markup.Escape(label));
        header.AddRow("Game", pex.Header.GameId.ToString());
        header.AddRow("Version", $"{pex.Header.MajorVersion}.{pex.Header.MinorVersion}");
        header.AddRow("Endian", pex.Header.Endianness.ToString());
        header.AddRow("Compiled source", Markup.Escape(pex.Header.SourceFileName));
        header.AddRow("Strings", $"{pex.StringTable.Length:N0}");
        header.AddRow("Objects", $"{pex.Objects.Length:N0}");
        header.AddRow("Debug data", pex.DebugInfo is null ? "No" : "Yes");
        AnsiConsole.Write(header);

        var objects = new Table().Border(TableBorder.Simple);
        objects.AddColumn("Object");
        objects.AddColumn("Parent");
        objects.AddColumn(new TableColumn("Structs").RightAligned());
        objects.AddColumn(new TableColumn("Variables").RightAligned());
        objects.AddColumn(new TableColumn("Properties").RightAligned());
        objects.AddColumn(new TableColumn("States").RightAligned());
        objects.AddColumn(new TableColumn("Functions").RightAligned());
        objects.AddColumn(new TableColumn("Instructions").RightAligned());
        foreach (var obj in pex.Objects)
        {
            var functions = obj.States.Sum(state => state.Functions.Length) +
                            obj.Properties.Sum(property =>
                                (property.ReadFunction is null ? 0 : 1) +
                                (property.WriteFunction is null ? 0 : 1));
            var instructions = obj.States.Sum(state =>
                state.Functions.Sum(function => function.Instructions.Length));
            instructions += obj.Properties.Sum(property =>
                (property.ReadFunction?.Instructions.Length ?? 0) +
                (property.WriteFunction?.Instructions.Length ?? 0));
            objects.AddRow(
                Markup.Escape(obj.Name.Value),
                Markup.Escape(obj.ParentClassName.Value),
                obj.Structs.Length.ToString("N0"),
                obj.Variables.Length.ToString("N0"),
                obj.Properties.Length.ToString("N0"),
                obj.States.Length.ToString("N0"),
                functions.ToString("N0"),
                instructions.ToString("N0"));
        }

        AnsiConsole.Write(objects);
    }

    private static IEnumerable<PexArchiveEntry> Filter(
        IReadOnlyList<PexArchiveEntry> entries,
        string? filter) => string.IsNullOrWhiteSpace(filter)
        ? entries
        : entries.Where(entry => entry.VirtualPath.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private static bool RequireFile(string path)
    {
        if (File.Exists(path))
        {
            return true;
        }

        AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", Markup.Escape(path));
        return false;
    }

    private static int CountOccurrences(string value, string marker)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += marker.Length;
        }

        return count;
    }
}
