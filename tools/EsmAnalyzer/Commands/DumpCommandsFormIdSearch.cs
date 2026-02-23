using Spectre.Console;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;

namespace EsmAnalyzer.Commands;

/// <summary>
///     FormID search: find records by FormID with optional comparison.
/// </summary>
internal static class DumpCommandsFormIdSearch
{
    internal static int FindFormId(string filePath, string formIdStr, string? filterType, bool showHex,
        string? comparePath)
    {
        var targetFormId = EsmFileLoader.ParseFormId(formIdStr);
        if (!targetFormId.HasValue)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid FormID format: {formIdStr}");
            return 1;
        }

        var esm = EsmFileLoader.Load(filePath);
        if (esm == null)
        {
            return 1;
        }

        // Load comparison file if specified
        EsmFileLoadResult? compareEsm = null;
        if (!string.IsNullOrEmpty(comparePath))
        {
            compareEsm = EsmFileLoader.Load(comparePath);
            if (compareEsm == null)
            {
                return 1;
            }
        }

        AnsiConsole.MarkupLine($"[blue]Finding FormID:[/] 0x{targetFormId.Value:X8} in {Path.GetFileName(filePath)}");
        if (compareEsm != null)
        {
            AnsiConsole.MarkupLine($"[blue]Comparing with:[/] {Path.GetFileName(comparePath!)}");
        }

        if (!string.IsNullOrEmpty(filterType))
        {
            AnsiConsole.MarkupLine($"Filter: [cyan]{filterType.ToUpperInvariant()}[/] records only");
        }

        AnsiConsole.WriteLine();

        var matches = new List<AnalyzerRecordInfo>();
        var compareMatches = new List<AnalyzerRecordInfo>();

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning for records...",
                ctx =>
                {
                    matches = ScanForFormId(esm.Data, esm.IsBigEndian, esm.FirstGrupOffset, targetFormId.Value,
                        filterType);
                    if (compareEsm != null)
                    {
                        compareMatches = ScanForFormId(compareEsm.Data, compareEsm.IsBigEndian,
                            compareEsm.FirstGrupOffset,
                            targetFormId.Value, filterType);
                    }
                });

        AnsiConsole.MarkupLine(
            $"Found [cyan]{matches.Count}[/] records with FormID 0x{targetFormId.Value:X8} in primary file");
        if (compareEsm != null)
        {
            AnsiConsole.MarkupLine(
                $"Found [cyan]{compareMatches.Count}[/] records with FormID 0x{targetFormId.Value:X8} in comparison file");
        }

        AnsiConsole.WriteLine();

        if (compareEsm != null)
        {
            // Show comparison view
            DumpCommandsRecordCompare.DisplayRecordComparison(matches, esm, compareMatches, compareEsm, filePath,
                comparePath!, showHex);
        }
        else
        {
            // Standard view
            foreach (var rec in matches)
            {
                EsmDisplayHelpers.DisplayRecord(rec, esm.Data, esm.IsBigEndian, showHex, true);
            }
        }

        return 0;
    }

    internal static List<AnalyzerRecordInfo> ScanForFormId(byte[] data, bool bigEndian, int startOffset,
        uint targetFormId, string? filterType)
    {
        var matches = new List<AnalyzerRecordInfo>();
        var offset = startOffset;

        while (offset + EsmParser.MainRecordHeaderSize <= data.Length)
        {
            var recHeader = EsmParser.ParseRecordHeader(data.AsSpan(offset), bigEndian);
            if (recHeader == null)
            {
                break;
            }

            if (recHeader.Signature == "GRUP")
            {
                offset += EsmParser.MainRecordHeaderSize;
                continue;
            }

            if (recHeader.FormId == targetFormId)
            {
                if (string.IsNullOrEmpty(filterType) ||
                    recHeader.Signature.Equals(filterType, StringComparison.OrdinalIgnoreCase))
                {
                    var recordEnd = offset + EsmParser.MainRecordHeaderSize + (int)recHeader.DataSize;
                    matches.Add(new AnalyzerRecordInfo
                    {
                        Signature = recHeader.Signature,
                        FormId = recHeader.FormId,
                        Flags = recHeader.Flags,
                        DataSize = recHeader.DataSize,
                        Offset = (uint)offset,
                        TotalSize = (uint)(recordEnd - offset)
                    });
                }
            }

            offset += EsmParser.MainRecordHeaderSize + (int)recHeader.DataSize;
        }

        return matches;
    }
}
