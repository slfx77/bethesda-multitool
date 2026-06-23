using System.CommandLine;
using Spectre.Console;

namespace SignatureScanner;

/// <summary>
/// Research tool for scanning memory dumps to find file signatures extracted from sample files.
/// Uses Aho-Corasick algorithm for efficient multi-pattern matching.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var rootCommand = new RootCommand("Scan memory dumps for file signatures extracted from sample files");

        rootCommand.Subcommands.Add(CreateExtractCommand());
        rootCommand.Subcommands.Add(CreateScanCommand());
        rootCommand.Subcommands.Add(CreateCompareCommand());

        return rootCommand.Parse(args).Invoke();
    }

    private static Command CreateExtractCommand()
    {
        var command = new Command("extract", "Extract file headers from a directory of sample files");

        var sampleDirArg = new Argument<string>("sample-dir") { Description = "Directory containing sample files to extract headers from" };
        var outputOption = new Option<string?>("-o", "--output") { Description = "Output JSON file for extracted signatures" };
        var headerSizeOption = new Option<int>("-s", "--size") { Description = "Number of header bytes to extract (default: 16)" };
        var extensionsOption = new Option<string[]>("-e", "--extensions") { Description = "File extensions to include (e.g., .nif .ddx)", AllowMultipleArgumentsPerToken = true };
        var recursiveOption = new Option<bool>("-r", "--recursive") { Description = "Search directories recursively (default: true)" };

        command.Arguments.Add(sampleDirArg);
        command.Options.Add(outputOption);
        command.Options.Add(headerSizeOption);
        command.Options.Add(extensionsOption);
        command.Options.Add(recursiveOption);

        command.SetAction(parseResult =>
        {
            var sampleDir = parseResult.GetValue(sampleDirArg)!;
            var output = parseResult.GetValue(outputOption);
            var headerSize = parseResult.GetValue(headerSizeOption);
            if (headerSize == 0) headerSize = 16;  // Default
            var extensions = parseResult.GetValue(extensionsOption) ?? [];
            var recursive = !parseResult.Tokens.Any(t => t.Value == "-r" || t.Value == "--recursive") || parseResult.GetValue(recursiveOption);  // Default true

            ExtractHandler(sampleDir, output, headerSize, extensions, recursive).GetAwaiter().GetResult();
        });

        return command;
    }

    private static Command CreateScanCommand()
    {
        var command = new Command("scan", "Scan memory dumps for file signatures");

        var dumpArg = new Argument<string>("dump") { Description = "Memory dump file to scan" };
        var signaturesOption = new Option<string?>("-i", "--signatures") { Description = "JSON file containing signatures (from extract command)" };
        var patternOption = new Option<string[]>("-p", "--pattern") { Description = "Hex pattern to search for (e.g., '42 49 4B')", AllowMultipleArgumentsPerToken = true };
        var maxMatchesOption = new Option<int>("-m", "--max-matches") { Description = "Maximum matches per signature (default: 100)" };
        var contextOption = new Option<int>("-c", "--context") { Description = "Bytes of context to show around matches (default: 32)" };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "Show detailed output" };

        command.Arguments.Add(dumpArg);
        command.Options.Add(signaturesOption);
        command.Options.Add(patternOption);
        command.Options.Add(maxMatchesOption);
        command.Options.Add(contextOption);
        command.Options.Add(verboseOption);

        command.SetAction(parseResult =>
        {
            var dump = parseResult.GetValue(dumpArg)!;
            var signatures = parseResult.GetValue(signaturesOption);
            var patterns = parseResult.GetValue(patternOption) ?? [];
            var maxMatches = parseResult.GetValue(maxMatchesOption);
            if (maxMatches == 0) maxMatches = 100;  // Default
            var context = parseResult.GetValue(contextOption);
            if (context == 0) context = 32;  // Default
            var verbose = parseResult.GetValue(verboseOption);

            ScanHandler(dump, signatures, patterns, maxMatches, context, verbose).GetAwaiter().GetResult();
        });

        return command;
    }

    private static Command CreateCompareCommand()
    {
        var command = new Command("compare", "Compare signature presence across multiple memory dumps");

        var dumpsArg = new Argument<string[]>("dumps") { Description = "Memory dump files to compare", Arity = ArgumentArity.OneOrMore };
        var signaturesOption = new Option<string>("-i", "--signatures") { Description = "JSON file containing signatures", Required = true };
        var outputOption = new Option<string?>("-o", "--output") { Description = "Output CSV file for comparison results" };

        command.Arguments.Add(dumpsArg);
        command.Options.Add(signaturesOption);
        command.Options.Add(outputOption);

        command.SetAction(parseResult =>
        {
            var dumps = parseResult.GetValue(dumpsArg)!;
            var signatures = parseResult.GetValue(signaturesOption)!;
            var output = parseResult.GetValue(outputOption);

            CompareHandler(dumps, signatures, output).GetAwaiter().GetResult();
        });

        return command;
    }

    private static async Task ExtractHandler(
        string sampleDirPath,
        string? outputPath,
        int headerSize,
        string[] extensions,
        bool recursive)
    {
        var sampleDir = new DirectoryInfo(sampleDirPath);
        if (!sampleDir.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Directory not found:[/] {sampleDir.FullName}");
            return;
        }

        AnsiConsole.MarkupLine($"[bold]Extracting headers from:[/] {sampleDir.FullName}");
        AnsiConsole.MarkupLine($"[dim]Header size: {headerSize} bytes, Recursive: {recursive}[/]");

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = sampleDir.EnumerateFiles("*", searchOption);

        // Filter by extensions if specified
        if (extensions.Length > 0)
        {
            var extSet = extensions.Select(e => e.StartsWith('.') ? e : "." + e).ToHashSet(StringComparer.OrdinalIgnoreCase);
            files = files.Where(f => extSet.Contains(f.Extension));
        }

        var signatures = new List<ExtractedSignature>();
        var byExtension = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);

        // Group files by extension
        foreach (var file in files)
        {
            var ext = file.Extension.ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) continue;

            if (!byExtension.TryGetValue(ext, out var list))
            {
                list = [];
                byExtension[ext] = list;
            }
            list.Add(file);
        }

        AnsiConsole.MarkupLine($"[green]Found {byExtension.Count} file types[/]");

        var table = new Table();
        table.AddColumn("Extension");
        table.AddColumn("Count");
        table.AddColumn("Sample Size");
        table.AddColumn("Header (Hex)");
        table.AddColumn("Header (ASCII)");

        foreach (var (ext, fileList) in byExtension.OrderBy(kv => kv.Key))
        {
            // Take first file as sample
            var sample = fileList.First();

            try
            {
                var bytes = await ReadHeaderAsync(sample.FullName, headerSize);
                if (bytes.Length == 0) continue;

                var hexString = BitConverter.ToString(bytes).Replace("-", " ");
                var asciiString = new string(bytes.Select(b => b >= 32 && b <= 126 ? (char)b : '.').ToArray());

                signatures.Add(new ExtractedSignature
                {
                    Extension = ext,
                    FileCount = fileList.Count,
                    SampleFile = sample.FullName,
                    SampleSize = sample.Length,
                    HeaderBytes = bytes,
                    HeaderHex = hexString
                });

                table.AddRow(
                    ext,
                    fileList.Count.ToString(),
                    FormatSize(sample.Length),
                    hexString.Length > 47 ? hexString[..47] + "..." : hexString,
                    asciiString
                );
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Could not read {sample.Name}: {ex.Message}[/]");
            }
        }

        AnsiConsole.Write(table);

        // Save to JSON if output specified
        if (outputPath != null)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(signatures, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(outputPath, json);
            AnsiConsole.MarkupLine($"[green]Saved {signatures.Count} signatures to:[/] {outputPath}");
        }
    }

    private static async Task ScanHandler(
        string dumpPath,
        string? signaturesFilePath,
        string[] patterns,
        int maxMatches,
        int context,
        bool verbose)
    {
        var dump = new FileInfo(dumpPath);
        if (!dump.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Dump file not found:[/] {dump.FullName}");
            return;
        }

        var signatures = new List<SearchSignature>();

        // Load signatures from JSON
        if (signaturesFilePath != null)
        {
            var signaturesFile = new FileInfo(signaturesFilePath);
            if (!signaturesFile.Exists)
            {
                AnsiConsole.MarkupLine($"[red]Signatures file not found:[/] {signaturesFile.FullName}");
                return;
            }

            var json = await File.ReadAllTextAsync(signaturesFile.FullName);
            var extracted = System.Text.Json.JsonSerializer.Deserialize<List<ExtractedSignature>>(json);
            if (extracted != null)
            {
                foreach (var sig in extracted)
                {
                    signatures.Add(new SearchSignature
                    {
                        Name = sig.Extension,
                        Pattern = sig.HeaderBytes,
                        Description = $"From {Path.GetFileName(sig.SampleFile)}"
                    });
                }
            }
        }

        // Add manual patterns
        foreach (var pattern in patterns)
        {
            var bytes = ParseHexPattern(pattern);
            if (bytes != null)
            {
                signatures.Add(new SearchSignature
                {
                    Name = $"Pattern_{signatures.Count}",
                    Pattern = bytes,
                    Description = pattern
                });
            }
        }

        if (signatures.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No signatures to search for. Use --signatures or --pattern[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[bold]Scanning:[/] {dump.FullName} ({FormatSize(dump.Length)})");
        AnsiConsole.MarkupLine($"[dim]Searching for {signatures.Count} signatures, max {maxMatches} matches each[/]");

        // Build Aho-Corasick matcher
        var matcher = new AhoCorasick();
        foreach (var sig in signatures)
        {
            matcher.AddPattern(sig.Name, sig.Pattern);
        }
        matcher.Build();

        // Read and scan dump
        var results = new Dictionary<string, List<long>>();
        foreach (var sig in signatures)
        {
            results[sig.Name] = [];
        }

        await AnsiConsole.Progress()
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Scanning dump[/]");

                const int chunkSize = 64 * 1024 * 1024; // 64 MB chunks
                var maxPatternLen = signatures.Max(s => s.Pattern.Length);
                var overlap = maxPatternLen - 1;

                await using var stream = File.OpenRead(dump.FullName);
                var buffer = new byte[chunkSize + overlap];
                long offset = 0;
                int bytesRead;
                int carryOver = 0;

                while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(carryOver, chunkSize))) > 0)
                {
                    var totalBytes = carryOver + bytesRead;
                    var matches = matcher.Search(buffer.AsSpan(0, totalBytes));

                    foreach (var (name, _, matchOffset) in matches)
                    {
                        var absoluteOffset = offset + matchOffset;
                        if (results[name].Count < maxMatches)
                        {
                            results[name].Add(absoluteOffset);
                        }
                    }

                    // Keep overlap for next iteration
                    if (bytesRead == chunkSize && stream.Position < stream.Length)
                    {
                        Buffer.BlockCopy(buffer, chunkSize, buffer, 0, overlap);
                        carryOver = overlap;
                        offset += chunkSize;
                    }
                    else
                    {
                        offset += totalBytes;
                        carryOver = 0;
                    }

                    task.Value = (double)stream.Position / stream.Length * 100;
                }

                task.Value = 100;
            });

        // Display results
        var resultTable = new Table();
        resultTable.AddColumn("Signature");
        resultTable.AddColumn("Matches");
        resultTable.AddColumn("First Offsets");

        foreach (var sig in signatures.OrderByDescending(s => results[s.Name].Count))
        {
            var matchList = results[sig.Name];
            var offsetStr = matchList.Count > 0
                ? string.Join(", ", matchList.Take(5).Select(o => $"0x{o:X8}"))
                : "[dim]none[/]";

            if (matchList.Count > 5)
                offsetStr += $" (+{matchList.Count - 5} more)";

            var countColor = matchList.Count > 0 ? "green" : "dim";
            resultTable.AddRow(
                sig.Name,
                $"[{countColor}]{matchList.Count}[/]",
                offsetStr
            );
        }

        AnsiConsole.Write(resultTable);

        // Show context for matches if verbose
        if (verbose)
        {
            await using var stream = File.OpenRead(dump.FullName);
            var contextBuffer = new byte[context];

            foreach (var sig in signatures.Where(s => results[s.Name].Count > 0).Take(5))
            {
                var matchList = results[sig.Name];
                AnsiConsole.MarkupLine($"\n[bold]{sig.Name}[/] - First match context:");

                var offset = matchList[0];
                stream.Position = offset;
                var read = await stream.ReadAsync(contextBuffer);

                var panel = new Panel(FormatHexDump(contextBuffer.AsSpan(0, read), offset))
                {
                    Header = new PanelHeader($"Offset 0x{offset:X8}")
                };
                AnsiConsole.Write(panel);
            }
        }
    }

    private static async Task CompareHandler(
        string[] dumpPaths,
        string signaturesFilePath,
        string? outputPath)
    {
        var signaturesFile = new FileInfo(signaturesFilePath);
        if (!signaturesFile.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Signatures file not found:[/] {signaturesFile.FullName}");
            return;
        }

        var json = await File.ReadAllTextAsync(signaturesFile.FullName);
        var extracted = System.Text.Json.JsonSerializer.Deserialize<List<ExtractedSignature>>(json);
        if (extracted == null || extracted.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No signatures found in file[/]");
            return;
        }

        var signatures = extracted.Select(e => new SearchSignature
        {
            Name = e.Extension,
            Pattern = e.HeaderBytes,
            Description = e.HeaderHex
        }).ToList();

        var dumps = dumpPaths.Select(p => new FileInfo(p)).ToArray();

        AnsiConsole.MarkupLine($"[bold]Comparing {signatures.Count} signatures across {dumps.Length} dumps[/]");

        // Build matcher
        var matcher = new AhoCorasick();
        foreach (var sig in signatures)
        {
            matcher.AddPattern(sig.Name, sig.Pattern);
        }
        matcher.Build();

        // Results: signature -> dump -> count
        var results = new Dictionary<string, Dictionary<string, int>>();
        foreach (var sig in signatures)
        {
            results[sig.Name] = [];
        }

        // Scan each dump
        foreach (var dump in dumps)
        {
            if (!dump.Exists)
            {
                AnsiConsole.MarkupLine($"[yellow]Skipping missing dump:[/] {dump.Name}");
                continue;
            }

            AnsiConsole.MarkupLine($"[dim]Scanning {dump.Name}...[/]");

            try
            {
                var dumpResults = await ScanDumpAsync(dump.FullName, matcher, signatures);
                foreach (var (name, count) in dumpResults)
                {
                    results[name][dump.Name] = count;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error scanning {dump.Name}: {ex.Message}[/]");
            }
        }

        // Display comparison table
        var table = new Table();
        table.AddColumn("Signature");
        foreach (var dump in dumps.Where(d => d.Exists))
        {
            table.AddColumn(dump.Name.Length > 20 ? dump.Name[..17] + "..." : dump.Name);
        }
        table.AddColumn("Total");

        foreach (var sig in signatures.OrderByDescending(s => results[s.Name].Values.Sum()))
        {
            var row = new List<string> { sig.Name };
            var total = 0;

            foreach (var dump in dumps.Where(d => d.Exists))
            {
                var count = results[sig.Name].GetValueOrDefault(dump.Name, 0);
                total += count;
                var color = count > 0 ? "green" : "dim";
                row.Add($"[{color}]{count}[/]");
            }

            row.Add($"[bold]{total}[/]");
            table.AddRow(row.ToArray());
        }

        AnsiConsole.Write(table);

        // Save to CSV if output specified
        if (outputPath != null)
        {
            await using var writer = new StreamWriter(outputPath);

            // Header
            await writer.WriteLineAsync("Signature," + string.Join(",", dumps.Where(d => d.Exists).Select(d => d.Name)));

            // Data
            foreach (var sig in signatures)
            {
                var counts = dumps.Where(d => d.Exists).Select(d => results[sig.Name].GetValueOrDefault(d.Name, 0));
                await writer.WriteLineAsync($"{sig.Name},{string.Join(",", counts)}");
            }

            AnsiConsole.MarkupLine($"[green]Saved comparison to:[/] {outputPath}");
        }
    }

    private static async Task<Dictionary<string, int>> ScanDumpAsync(
        string dumpPath,
        AhoCorasick matcher,
        List<SearchSignature> signatures)
    {
        var results = signatures.ToDictionary(s => s.Name, _ => 0);

        const int chunkSize = 64 * 1024 * 1024;
        var maxPatternLen = signatures.Max(s => s.Pattern.Length);
        var overlap = maxPatternLen - 1;

        await using var stream = File.OpenRead(dumpPath);
        var buffer = new byte[chunkSize + overlap];
        int carryOver = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(carryOver, chunkSize))) > 0)
        {
            var totalBytes = carryOver + bytesRead;
            var matches = matcher.Search(buffer.AsSpan(0, totalBytes));

            foreach (var (name, _, _) in matches)
            {
                results[name]++;
            }

            if (bytesRead == chunkSize && stream.Position < stream.Length)
            {
                Buffer.BlockCopy(buffer, chunkSize, buffer, 0, overlap);
                carryOver = overlap;
            }
            else
            {
                carryOver = 0;
            }
        }

        return results;
    }

    private static async Task<byte[]> ReadHeaderAsync(string path, int size)
    {
        await using var stream = File.OpenRead(path);
        var buffer = new byte[Math.Min(size, stream.Length)];
        var read = await stream.ReadAsync(buffer);
        return buffer.AsSpan(0, read).ToArray();
    }

    private static byte[]? ParseHexPattern(string pattern)
    {
        try
        {
            var hex = pattern.Replace(" ", "").Replace("-", "");
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
        catch
        {
            AnsiConsole.MarkupLine($"[yellow]Invalid hex pattern:[/] {pattern}");
            return null;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static string FormatHexDump(ReadOnlySpan<byte> data, long baseOffset)
    {
        var lines = new List<string>();
        for (int i = 0; i < data.Length; i += 16)
        {
            var lineOffset = baseOffset + i;
            var hex = new List<string>();
            var ascii = new List<char>();

            for (int j = 0; j < 16 && i + j < data.Length; j++)
            {
                var b = data[i + j];
                hex.Add(b.ToString("X2"));
                ascii.Add(b >= 32 && b <= 126 ? (char)b : '.');
            }

            var hexStr = string.Join(" ", hex).PadRight(48);
            var asciiStr = new string(ascii.ToArray());
            lines.Add($"[dim]{lineOffset:X8}[/]  {hexStr}  [blue]{asciiStr}[/]");
        }
        return string.Join("\n", lines);
    }
}

internal record ExtractedSignature
{
    public required string Extension { get; init; }
    public int FileCount { get; init; }
    public string? SampleFile { get; init; }
    public long SampleSize { get; init; }
    public required byte[] HeaderBytes { get; init; }
    public required string HeaderHex { get; init; }
}

internal record SearchSignature
{
    public required string Name { get; init; }
    public required byte[] Pattern { get; init; }
    public string? Description { get; init; }
}
