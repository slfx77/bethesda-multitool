using System.CommandLine;
using System.Text;
using FalloutXbox360Utils.Core.Formats.SaveGame;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI commands for decode coverage testing and STFS container inspection.
/// </summary>
internal static class SaveDecodeCommand
{
    private const string InputArgName = "input";

    public static Command CreateDecodeCommand()
    {
        var decodeCommand = new Command("decode", "Test decode all changed forms and show statistics");
        decodeCommand.Arguments.Add(new Argument<string>(InputArgName) { Description = "Path to save file" });
        decodeCommand.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue<string>(InputArgName)!;
            return Task.FromResult(ExecuteDecodeStats(input));
        });

        return decodeCommand;
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
            var save = SaveCommand.ParseFile(path);
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
                            $"{SaveCommand.FormatFormId(form.RefId, formIdArray)} ({typeName}) flags=0x{form.ChangeFlags:X8} [{string.Join("|", flagNames)}] data={form.Data.Length}b");
                    }
                }

                if (result.Warnings.Count > 0 && errors.Count < 20)
                {
                    errors.Add(
                        $"{SaveCommand.FormatFormId(form.RefId, formIdArray)} ({typeName}): {string.Join("; ", result.Warnings)}");
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
                partials.Add((SaveCommand.FormatFormId(form.RefId, formIdArray), form.TypeName, form.ChangeFlags,
                    fNames,
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

    public static Command CreateStfsInfoCommand()
    {
        var stfsInfoCommand = new Command("stfs-info", "Show STFS container structure and extraction diagnostics");
        stfsInfoCommand.Arguments.Add(new Argument<string>(InputArgName) { Description = "Path to .fxs save file" });
        stfsInfoCommand.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue<string>(InputArgName)!;
            return Task.FromResult(ExecuteStfsInfo(input));
        });

        return stfsInfoCommand;
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
}
