using System.Globalization;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Walks every main record in an ESM, inflates compressed bodies via
///     <see cref="EsmHelpers.GetRecordData" />, and scans the decompressed bytes
///     for a hex pattern. Designed to find content that a raw file scan would
///     miss because it lives inside a zlib-compressed record.
///
///     This intentionally does NOT byte-swap or apply any conversion schema —
///     it's a pure decompress-then-scan, useful for auditing the converter.
/// </summary>
internal static class DumpCommandsRawHexSearch
{
    internal static int Run(string filePath, string hexPattern, string? typeFilter,
        int limit, int contextBytes)
    {
        var pattern = ParseHexPattern(hexPattern);
        if (pattern == null || pattern.Length == 0)
        {
            AnsiConsole.MarkupLine(
                "[red]ERROR:[/] Invalid hex pattern. Use \"07 07 05 01 03 07 06\" or \"07070501030706\".");
            return 1;
        }

        var esm = EsmFileLoader.Load(filePath);
        if (esm == null)
        {
            return 1;
        }

        var displayPattern = string.Join(" ", pattern.Select(b => b.ToString("X2")));
        AnsiConsole.MarkupLine($"[blue]Decompress-and-scan:[/] {Path.GetFileName(filePath)}");
        AnsiConsole.MarkupLine($"Pattern: [cyan]{displayPattern}[/] ({pattern.Length} bytes)");
        if (!string.IsNullOrWhiteSpace(typeFilter))
        {
            AnsiConsole.MarkupLine($"Type filter: [cyan]{typeFilter.ToUpperInvariant()}[/]");
        }
        AnsiConsole.WriteLine();

        List<AnalyzerRecordInfo> records = [];
        AnsiConsole.Status().Start("Scanning records...", _ =>
        {
            records = EsmRecordParser.ScanAllRecords(esm.Data, esm.IsBigEndian);
        });

        if (!string.IsNullOrWhiteSpace(typeFilter))
        {
            var t = typeFilter.ToUpperInvariant();
            records = records.Where(r => r.Signature == t).ToList();
        }

        var totalHits = 0;
        var rendered = 0;
        var recordsScanned = 0;
        var compressedScanned = 0;
        var decompressFailures = 0;
        var hitRecords = 0;

        foreach (var rec in records)
        {
            recordsScanned++;
            if (rec.IsCompressed)
            {
                compressedScanned++;
            }

            byte[] body;
            try
            {
                body = EsmHelpers.GetRecordData(esm.Data, rec, esm.IsBigEndian);
            }
            catch (Exception ex)
            {
                decompressFailures++;
                AnsiConsole.MarkupLine(
                    $"[yellow]WARN:[/] decompress failed for {rec.Signature} 0x{rec.FormId:X8} @0x{rec.Offset:X8}: {Markup.Escape(ex.Message)}");
                continue;
            }

            var hits = FindAll(body, pattern);
            if (hits.Count == 0)
            {
                continue;
            }

            hitRecords++;
            totalHits += hits.Count;

            var compTag = rec.IsCompressed ? " [yellow](compressed)[/]" : string.Empty;
            AnsiConsole.Write(new Rule(
                $"[cyan]{rec.Signature}[/] FormID [green]0x{rec.FormId:X8}[/] @ file 0x{rec.Offset:X8}{compTag}  [grey]{hits.Count} hit(s)[/]")
                .LeftJustified());

            foreach (var hit in hits)
            {
                if (limit > 0 && rendered >= limit)
                {
                    break;
                }

                AnsiConsole.MarkupLine($"  [dim]decompressed offset[/] 0x{hit:X} (body size {body.Length})");
                RenderContext(body, hit, pattern.Length, contextBytes);
                AnsiConsole.WriteLine();
                rendered++;
            }

            if (limit > 0 && rendered >= limit)
            {
                AnsiConsole.MarkupLine($"[grey]Render limit ({limit}) reached; continuing to count remaining hits...[/]");
                // Don't break — keep counting totalHits across remaining records.
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[bold]Summary:[/] scanned {recordsScanned} records ({compressedScanned} compressed), " +
            $"[green]{totalHits}[/] match(es) across [green]{hitRecords}[/] record(s)" +
            (decompressFailures > 0 ? $", [red]{decompressFailures} decompress failure(s)[/]" : string.Empty));

        return 0;
    }

    private static List<int> FindAll(byte[] haystack, byte[] needle)
    {
        var results = new List<int>();
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return results;
        }

        var span = haystack.AsSpan();
        var needleSpan = needle.AsSpan();
        var start = 0;
        while (start <= haystack.Length - needle.Length)
        {
            var idx = span[start..].IndexOf(needleSpan);
            if (idx < 0)
            {
                break;
            }
            results.Add(start + idx);
            start += idx + 1;
        }
        return results;
    }

    private static void RenderContext(byte[] data, int offset, int patternLength, int contextBytes)
    {
        var start = Math.Max(0, offset - contextBytes);
        var end = Math.Min(data.Length, offset + patternLength + contextBytes);
        var window = new byte[end - start];
        Array.Copy(data, start, window, 0, window.Length);
        var highlightStart = offset - start;
        EsmDisplayHelpers.RenderHexDump(window, start, highlightStart, patternLength);
    }

    private static byte[]? ParseHexPattern(string hex)
    {
        hex = hex.Replace(" ", string.Empty)
                 .Replace("-", string.Empty)
                 .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);

        if (hex.Length == 0 || hex.Length % 2 != 0)
        {
            return null;
        }

        try
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            return bytes;
        }
        catch
        {
            return null;
        }
    }
}
