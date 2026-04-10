using System.CommandLine;
using System.Text;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.SaveGame;
using FalloutXbox360Utils.Core.Formats.SaveGame.Export;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Save;

/// <summary>
///     CLI commands for save file hexdump and report generation.
/// </summary>
internal static class SaveReportCommand
{
    private const string InputArgName = "input";

    public static Command CreateHexdumpCommand()
    {
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

        return hexdumpCommand;
    }

    public static Command CreateReportsCommand()
    {
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

        return reportsCommand;
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
            var save = SaveCommand.ParseFile(path);

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
                    $"[bold green]Changed Form {Markup.Escape(SaveCommand.FormatFormId(form.RefId, formIdArray))} ({form.TypeName})[/]");
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
            var save = SaveCommand.ParseFile(path);
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
            if (fileType != AnalysisFileType.EsmFile && fileType != AnalysisFileType.Minidump)
            {
                AnsiConsole.MarkupLine("[yellow]  Not an ESM/DMP file, skipping enrichment.[/]");
                return null;
            }

            using var loaded = SemanticFileLoader.LoadAsync(
                    path,
                    new SemanticFileLoadOptions { FileType = fileType })
                .GetAwaiter()
                .GetResult();

            AnsiConsole.MarkupLine(
                $"  [green]Loaded {loaded.Records.TotalRecordsParsed:N0} records for name resolution.[/]");
            return loaded.Resolver;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]  ESM/DMP load failed: {Markup.Escape(ex.Message)}[/]");
            return null;
        }
    }

    internal static void PrintHexDump(ReadOnlySpan<byte> data)
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

    internal static void PrintDecodedFields(SaveFile save, ChangedForm form)
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
}
