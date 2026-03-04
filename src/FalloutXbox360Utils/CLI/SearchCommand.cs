using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Format-agnostic binary search command. Works on ANY file (DMP, ESM, NIF, etc.).
///     No format parsing — pure byte-level pattern matching with hex context display.
///     Uses SIMD-accelerated Span.IndexOf for case-sensitive search and streaming I/O
///     for count-only mode to avoid large memory allocations.
/// </summary>
public static class SearchCommand
{
    private const int StreamBufferSize = 8 * 1024 * 1024; // 8 MB chunks for streaming

    public static Command Create()
    {
        var command = new Command("search", "Search any file for text or hex patterns");

        command.Subcommands.Add(CreateTextCommand());
        command.Subcommands.Add(CreateHexCommand());

        return command;
    }

    private static Command CreateTextCommand()
    {
        var command = new Command("text", "Search for ASCII text in any file or directory");

        var targetArg = new Argument<string>("target") { Description = "File or directory path" };
        var patternArg = new Argument<string>("pattern") { Description = "Text pattern to search for" };
        var contextOpt = new Option<int>("-C", "--context")
        {
            Description = "Bytes of context around each match",
            DefaultValueFactory = _ => 64
        };
        var caseInsensitiveOpt = new Option<bool>("-i", "--ignore-case")
        {
            Description = "Case-insensitive search"
        };
        var limitOpt = new Option<int>("-l", "--limit")
        {
            Description = "Max matches per file (0 = unlimited)",
            DefaultValueFactory = _ => 0
        };
        var countOnlyOpt = new Option<bool>("--count-only")
        {
            Description = "Show only match counts, no hex context"
        };

        command.Arguments.Add(targetArg);
        command.Arguments.Add(patternArg);
        command.Options.Add(contextOpt);
        command.Options.Add(caseInsensitiveOpt);
        command.Options.Add(limitOpt);
        command.Options.Add(countOnlyOpt);

        command.SetAction(parseResult =>
        {
            var target = parseResult.GetValue(targetArg)!;
            var pattern = parseResult.GetValue(patternArg)!;
            var context = parseResult.GetValue(contextOpt);
            var ignoreCase = parseResult.GetValue(caseInsensitiveOpt);
            var limit = parseResult.GetValue(limitOpt);
            var countOnly = parseResult.GetValue(countOnlyOpt);

            return RunTextSearch(target, pattern, context, ignoreCase, limit, countOnly);
        });

        return command;
    }

    private static Command CreateHexCommand()
    {
        var command = new Command("hex", "Search for hex byte pattern in any file or directory");

        var targetArg = new Argument<string>("target") { Description = "File or directory path" };
        var patternArg = new Argument<string>("pattern")
        {
            Description = "Hex pattern (e.g., \"6B F8 11 00\" or \"6BF81100\")"
        };
        var contextOpt = new Option<int>("-C", "--context")
        {
            Description = "Bytes of context around each match",
            DefaultValueFactory = _ => 64
        };
        var limitOpt = new Option<int>("-l", "--limit")
        {
            Description = "Max matches per file (0 = unlimited)",
            DefaultValueFactory = _ => 0
        };
        var countOnlyOpt = new Option<bool>("--count-only")
        {
            Description = "Show only match counts, no hex context"
        };

        command.Arguments.Add(targetArg);
        command.Arguments.Add(patternArg);
        command.Options.Add(contextOpt);
        command.Options.Add(limitOpt);
        command.Options.Add(countOnlyOpt);

        command.SetAction(parseResult =>
        {
            var target = parseResult.GetValue(targetArg)!;
            var hexPattern = parseResult.GetValue(patternArg)!;
            var context = parseResult.GetValue(contextOpt);
            var limit = parseResult.GetValue(limitOpt);
            var countOnly = parseResult.GetValue(countOnlyOpt);

            return RunHexSearch(target, hexPattern, context, limit, countOnly);
        });

        return command;
    }

    private static int RunTextSearch(string target, string pattern, int contextBytes,
        bool ignoreCase, int limit, bool countOnly)
    {
        var patternBytes = Encoding.ASCII.GetBytes(pattern);
        byte[]? patternLower = null;

        if (ignoreCase)
        {
            patternLower = Encoding.ASCII.GetBytes(pattern.ToLowerInvariant());
        }

        return RunSearch(target, patternBytes, patternLower, contextBytes, limit, countOnly, pattern);
    }

    private static int RunHexSearch(string target, string hexPattern, int contextBytes,
        int limit, bool countOnly)
    {
        var patternBytes = ParseHexPattern(hexPattern);
        if (patternBytes == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Invalid hex pattern. Use format like \"6B F8 11 00\" or \"6BF81100\".");
            return 1;
        }

        var displayPattern = BitConverter.ToString(patternBytes).Replace("-", " ");
        return RunSearch(target, patternBytes, null, contextBytes, limit, countOnly, displayPattern);
    }

    private static int RunSearch(string target, byte[] pattern, byte[]? patternLower,
        int contextBytes, int limit, bool countOnly, string displayPattern)
    {
        if (Directory.Exists(target))
        {
            return RunDirectorySearch(target, pattern, patternLower, contextBytes, limit, countOnly, displayPattern);
        }

        if (File.Exists(target))
        {
            return RunSingleFileSearch(target, pattern, patternLower, contextBytes, limit, countOnly, displayPattern);
        }

        AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {target}");
        return 1;
    }

    private static int RunSingleFileSearch(string filePath, byte[] pattern, byte[]? patternLower,
        int contextBytes, int limit, bool countOnly, string displayPattern)
    {
        var fileInfo = new FileInfo(filePath);
        AnsiConsole.MarkupLine($"[bold]Searching:[/] [cyan]{Path.GetFileName(filePath)}[/] ({fileInfo.Length:N0} bytes) for \"{Markup.Escape(displayPattern)}\"");
        AnsiConsole.WriteLine();

        if (countOnly)
        {
            var count = CountMatchesStreaming(filePath, pattern, patternLower);
            AnsiConsole.MarkupLine(count == 0
                ? "[grey]No matches found.[/]"
                : $"[green]{count} match(es) found[/]");
            return 0;
        }

        // For display mode, memory-map the file for random access to context bytes
        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);
        var data = new byte[fileInfo.Length];
        accessor.ReadArray(0, data, 0, data.Length);

        var matches = FindMatches(data, pattern, patternLower);

        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No matches found.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[green]{matches.Count} match(es) found[/]");
        AnsiConsole.WriteLine();

        var shown = 0;
        foreach (var offset in matches)
        {
            if (limit > 0 && shown >= limit)
            {
                AnsiConsole.MarkupLine($"[grey]... {matches.Count - shown} more matches (use --limit to show more)[/]");
                break;
            }

            DisplayMatch(data, offset, pattern.Length, contextBytes);
            shown++;
        }

        return 0;
    }

    private static int RunDirectorySearch(string dirPath, byte[] pattern, byte[]? patternLower,
        int contextBytes, int limit, bool countOnly, string displayPattern)
    {
        var files = Directory.GetFiles(dirPath, "*.*")
            .Where(f => !Path.GetFileName(f).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] No files found in {dirPath}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold]Searching {files.Count} files[/] in [cyan]{dirPath}[/] for \"{Markup.Escape(displayPattern)}\"");
        AnsiConsole.WriteLine();

        if (countOnly)
        {
            return RunDirectoryCountOnly(files, pattern, patternLower);
        }

        return RunDirectoryWithContext(files, pattern, patternLower, contextBytes, limit);
    }

    /// <summary>
    ///     Count-only directory search: streaming I/O + parallel processing.
    ///     No large byte[] allocations — processes each file in 8 MB chunks.
    /// </summary>
    private static int RunDirectoryCountOnly(List<string> files, byte[] pattern, byte[]? patternLower)
    {
        var results = new (string fileName, long fileSize, int matchCount)[files.Count];

        Parallel.For(0, files.Count, i =>
        {
            var file = files[i];
            try
            {
                var count = CountMatchesStreaming(file, pattern, patternLower);
                results[i] = (Path.GetFileName(file), new FileInfo(file).Length, count);
            }
            catch
            {
                results[i] = (Path.GetFileName(file), 0, -1);
            }
        });

        // Display summary table
        var table = new Table();
        table.AddColumn("File");
        table.AddColumn(new TableColumn("Size").RightAligned());
        table.AddColumn(new TableColumn("Matches").RightAligned());

        var totalMatches = 0;
        var filesWithHits = 0;

        foreach (var (fileName, fileSize, matchCount) in results)
        {
            var matchStr = matchCount switch
            {
                -1 => "[red]ERROR[/]",
                0 => "[grey]0[/]",
                _ => $"[green]{matchCount}[/]"
            };

            if (matchCount > 0)
            {
                filesWithHits++;
                totalMatches += matchCount;
            }

            table.AddRow(
                Markup.Escape(fileName),
                $"{fileSize:N0}",
                matchStr);
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"[bold]{totalMatches} total matches across {filesWithHits}/{results.Length} files[/]");

        return 0;
    }

    /// <summary>
    ///     Directory search with hex context display (non-count-only mode).
    ///     Processes sequentially since output must be ordered.
    /// </summary>
    private static int RunDirectoryWithContext(List<string> files, byte[] pattern, byte[]? patternLower,
        int contextBytes, int limit)
    {
        var results = new List<(string fileName, long fileSize, int matchCount)>();
        var totalMatches = 0;

        foreach (var file in files)
        {
            try
            {
                var fileInfo = new FileInfo(file);
                var fileName = Path.GetFileName(file);

                // Use streaming count first to check if there are matches
                var count = CountMatchesStreaming(file, pattern, patternLower);
                results.Add((fileName, fileInfo.Length, count));
                totalMatches += count;

                if (count > 0)
                {
                    AnsiConsole.MarkupLine($"[green]{count}[/] match(es) in [cyan]{fileName}[/]");

                    // Only load full file for context display
                    var data = File.ReadAllBytes(file);
                    var matches = FindMatches(data, pattern, patternLower);
                    var shown = 0;
                    foreach (var offset in matches)
                    {
                        if (limit > 0 && shown >= limit)
                        {
                            AnsiConsole.MarkupLine($"  [grey]... {matches.Count - shown} more[/]");
                            break;
                        }

                        DisplayMatch(data, offset, pattern.Length, contextBytes, indent: true);
                        shown++;
                    }

                    AnsiConsole.WriteLine();
                }
            }
            catch (Exception ex)
            {
                results.Add((Path.GetFileName(file), 0, -1));
                AnsiConsole.MarkupLine($"[red]Error reading {Path.GetFileName(file)}:[/] {ex.Message}");
            }
        }

        // Summary table
        AnsiConsole.WriteLine();
        var table = new Table();
        table.AddColumn("File");
        table.AddColumn(new TableColumn("Size").RightAligned());
        table.AddColumn(new TableColumn("Matches").RightAligned());

        var filesWithHits = 0;
        foreach (var (fileName, fileSize, matchCount) in results)
        {
            var matchStr = matchCount switch
            {
                -1 => "[red]ERROR[/]",
                0 => "[grey]0[/]",
                _ => $"[green]{matchCount}[/]"
            };

            if (matchCount > 0)
            {
                filesWithHits++;
            }

            table.AddRow(
                Markup.Escape(fileName),
                $"{fileSize:N0}",
                matchStr);
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"[bold]{totalMatches} total matches across {filesWithHits}/{results.Count} files[/]");

        return 0;
    }

    /// <summary>
    ///     Count matches using streaming I/O with 8 MB buffer chunks.
    ///     Avoids loading entire files into memory. Uses SIMD-accelerated
    ///     Span.IndexOf for case-sensitive patterns.
    /// </summary>
    private static int CountMatchesStreaming(string filePath, byte[] pattern, byte[]? patternLower)
    {
        var fileLength = new FileInfo(filePath).Length;
        if (fileLength < pattern.Length)
        {
            return 0;
        }

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            StreamBufferSize, FileOptions.SequentialScan);
        // Buffer size + overlap region for patterns that span chunk boundaries
        var overlap = pattern.Length - 1;
        var buffer = new byte[StreamBufferSize + overlap];
        var count = 0;
        var isFirstRead = true;

        while (true)
        {
            int readStart;
            if (isFirstRead)
            {
                readStart = 0;
                isFirstRead = false;
            }
            else
            {
                // Carry over overlap bytes from previous chunk's end
                Array.Copy(buffer, StreamBufferSize, buffer, 0, overlap);
                readStart = overlap;
            }

            var bytesRead = fs.Read(buffer, readStart, StreamBufferSize);
            if (bytesRead == 0)
            {
                break;
            }

            var totalBytes = readStart + bytesRead;
            var searchLength = totalBytes - pattern.Length + 1;
            if (searchLength <= 0)
            {
                break;
            }

            if (patternLower != null)
            {
                // Case-insensitive: manual scan with first-byte filter
                for (var i = 0; i < searchLength; i++)
                {
                    if (MatchesAtCaseInsensitive(buffer, i, pattern, patternLower))
                    {
                        count++;
                    }
                }
            }
            else
            {
                // Case-sensitive: SIMD-accelerated Span.IndexOf
                var span = buffer.AsSpan(0, totalBytes);
                var patternSpan = pattern.AsSpan();
                var offset = 0;

                while (offset < searchLength)
                {
                    var idx = span[offset..].IndexOf(patternSpan);
                    if (idx < 0)
                    {
                        break;
                    }

                    count++;
                    offset += idx + 1;
                }
            }
        }

        return count;
    }

    /// <summary>
    ///     Find all match offsets in a byte array. Uses SIMD-accelerated
    ///     Span.IndexOf for case-sensitive patterns (typically 10-100x faster
    ///     than naive byte-by-byte comparison).
    /// </summary>
    private static List<long> FindMatches(byte[] data, byte[] pattern, byte[]? patternLower)
    {
        var matches = new List<long>();

        if (patternLower != null)
        {
            // Case-insensitive: manual scan
            var searchLength = data.Length - pattern.Length;
            for (long i = 0; i <= searchLength; i++)
            {
                if (MatchesAtCaseInsensitive(data, i, pattern, patternLower))
                {
                    matches.Add(i);
                }
            }
        }
        else
        {
            // Case-sensitive: SIMD-accelerated Span.IndexOf
            var span = data.AsSpan();
            var patternSpan = pattern.AsSpan();
            var offset = 0;
            var searchLimit = data.Length - pattern.Length + 1;

            while (offset < searchLimit)
            {
                var idx = span[offset..].IndexOf(patternSpan);
                if (idx < 0)
                {
                    break;
                }

                matches.Add(offset + idx);
                offset += idx + 1;
            }
        }

        return matches;
    }

    private static bool MatchesAtCaseInsensitive(byte[] data, long offset, byte[] patternUpper, byte[] patternLower)
    {
        for (var j = 0; j < patternUpper.Length; j++)
        {
            var b = data[offset + j];
            // Lowercase the byte if it's an uppercase ASCII letter
            var bLower = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
            if (bLower != patternLower[j] && data[offset + j] != patternUpper[j])
            {
                return false;
            }
        }

        return true;
    }

    private static void DisplayMatch(byte[] data, long offset, int patternLength,
        int contextBytes, bool indent = false)
    {
        // Calculate context window
        var start = Math.Max(0, offset - contextBytes);
        var end = Math.Min(data.Length, offset + patternLength + contextBytes);
        var windowData = new byte[end - start];
        Array.Copy(data, start, windowData, 0, windowData.Length);

        var highlightStart = (int)(offset - start);
        var highlightLength = patternLength;

        AnsiConsole.Write(new Rule($"[cyan]Offset 0x{offset:X}[/]") { Style = Style.Parse("cyan dim") });

        if (indent)
        {
            RenderHexDumpIndented(windowData, start, highlightStart, highlightLength);
        }
        else
        {
            EsmDisplayHelpers.RenderHexDump(windowData, start, highlightStart, highlightLength);
        }
    }

    /// <summary>
    ///     Renders hex dump with 2-space indent for directory mode.
    ///     Simplified version of EsmDisplayHelpers.RenderHexDump.
    /// </summary>
    private static void RenderHexDumpIndented(byte[] data, long baseOffset,
        int highlightStart, int highlightLength)
    {
        for (var i = 0; i < data.Length; i += 16)
        {
            var lineOffset = baseOffset + i;
            Console.Write($"  [grey]{lineOffset:X8}[/]  ");

            // Hex bytes
            for (var j = 0; j < 16; j++)
            {
                if (i + j < data.Length)
                {
                    var byteIdx = i + j;
                    var isHighlighted = byteIdx >= highlightStart &&
                                        byteIdx < highlightStart + highlightLength;
                    var hex = data[byteIdx].ToString("X2");
                    Console.Write(isHighlighted ? $"[green bold]{hex}[/] " : $"{hex} ");
                }
                else
                {
                    Console.Write("   ");
                }

                if (j == 7)
                {
                    Console.Write(" ");
                }
            }

            // ASCII
            Console.Write(" ");
            for (var j = 0; j < 16 && i + j < data.Length; j++)
            {
                var b = data[i + j];
                var c = b is >= 0x20 and < 0x7F ? (char)b : '.';
                var byteIdx = i + j;
                var isHighlighted = byteIdx >= highlightStart &&
                                    byteIdx < highlightStart + highlightLength;
                Console.Write(isHighlighted ? $"[green bold]{Markup.Escape(c.ToString())}[/]" : Markup.Escape(c.ToString()));
            }

            Console.WriteLine();
        }
    }

    private static byte[]? ParseHexPattern(string hex)
    {
        // Remove common separators
        hex = hex.Replace(" ", "").Replace("-", "").Replace("0x", "").Replace("0X", "");

        if (hex.Length % 2 != 0)
        {
            return null;
        }

        try
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber);
            }

            return bytes;
        }
        catch
        {
            return null;
        }
    }
}
