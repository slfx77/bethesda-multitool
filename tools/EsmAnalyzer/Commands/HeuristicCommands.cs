using System.CommandLine;
using System.Globalization;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Heuristic commands for finding PC geometry data in Xbox 360 ESM files.
/// </summary>
public static class HeuristicCommands
{
    public static Command CreateGeomSearchCommand()
    {
        var command = new Command("geom-search", "Search Xbox 360 ESM for matching PC geometry subrecord payloads");

        var pcArg = new Argument<string>("pc") { Description = "Path to the PC ESM file" };
        var xboxArg = new Argument<string>("xbox") { Description = "Path to the Xbox 360 ESM file" };
        var typeArg = new Argument<string>("type") { Description = "Record type (LAND, NAVM, WRLD)" };
        var formIdArg = new Argument<string>("formid") { Description = "Record FormID (hex, e.g., 0x000DA726)" };

        var subrecordOpt = new Option<string[]>("-s", "--subrecord")
        {
            Description = "Subrecord signatures to search (repeatable)",
            AllowMultipleArgumentsPerToken = true
        };
        var modeOpt = new Option<string>("-m", "--mode")
        {
            Description = "Search mode: raw, swap2, swap4, land, land-vhgt, land-atxt, land-vtxt, all",
            DefaultValueFactory = _ => "all"
        };
        var maxHitsOpt = new Option<int>("-l", "--limit")
        {
            Description = "Maximum matches per pattern",
            DefaultValueFactory = _ => 5
        };
        var locateOpt = new Option<bool>("--locate") { Description = "Map hits back to records" };

        command.Arguments.Add(pcArg);
        command.Arguments.Add(xboxArg);
        command.Arguments.Add(typeArg);
        command.Arguments.Add(formIdArg);
        command.Options.Add(subrecordOpt);
        command.Options.Add(modeOpt);
        command.Options.Add(maxHitsOpt);
        var scopeOpt = new Option<string>("--scope")
        {
            Description = "Search scope: file or record",
            DefaultValueFactory = _ => "file"
        };
        command.Options.Add(scopeOpt);
        command.Options.Add(locateOpt);

        command.SetAction(parseResult => GeomSearch(
            parseResult.GetValue(pcArg)!,
            parseResult.GetValue(xboxArg)!,
            parseResult.GetValue(typeArg)!,
            parseResult.GetValue(formIdArg)!,
            parseResult.GetValue(subrecordOpt),
            parseResult.GetValue(modeOpt)!,
            parseResult.GetValue(maxHitsOpt),
            parseResult.GetValue(scopeOpt)!,
            parseResult.GetValue(locateOpt)));

        return command;
    }

    private static int GeomSearch(
        string pcPath,
        string xboxPath,
        string recordType,
        string formIdText,
        string[]? subrecordFilters,
        string mode,
        int maxHits,
        string scope,
        bool locate)
    {
        if (!File.Exists(pcPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {pcPath}");
            return 1;
        }

        if (!File.Exists(xboxPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {xboxPath}");
            return 1;
        }

        if (!TryParseFormId(formIdText, out var formId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid FormID: {formIdText}");
            return 1;
        }

        var pcData = File.ReadAllBytes(pcPath);
        var xboxData = File.ReadAllBytes(xboxPath);

        var pcHeader = EsmParser.ParseFileHeader(pcData);
        var xboxHeader = EsmParser.ParseFileHeader(xboxData);
        if (pcHeader == null || xboxHeader == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Failed to parse ESM headers");
            return 1;
        }

        var type = recordType.ToUpperInvariant();
        var normalizedScope = scope.Trim().ToLowerInvariant();
        if (normalizedScope is not ("file" or "record"))
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Scope must be 'file' or 'record'.");
            return 1;
        }

        var (pcRecord, pcRecordData) = FindRecord(pcData, pcHeader.IsBigEndian, type, formId);
        if (pcRecord == null || pcRecordData == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] PC record {type} 0x{formId:X8} not found");
            return 1;
        }

        var subrecords = EsmHelpers.ParseSubrecords(pcRecordData, pcHeader.IsBigEndian);
        var wanted = ResolveSubrecordFilters(type, subrecordFilters);

        var candidates = subrecords
            .Where(s => wanted.Contains(s.Signature, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching subrecords found in PC record.[/]");
            return 0;
        }

        AnalyzerRecordInfo? xboxRecord = null;
        byte[]? xboxRecordData = null;
        var haystack = xboxData;
        if (normalizedScope == "record")
        {
            (xboxRecord, xboxRecordData) = FindRecord(xboxData, xboxHeader.IsBigEndian, type, formId);
            if (xboxRecord == null || xboxRecordData == null)
            {
                AnsiConsole.MarkupLine($"[red]ERROR:[/] Xbox record {type} 0x{formId:X8} not found");
                return 1;
            }

            haystack = xboxRecordData;
        }

        var searchModes = ResolveSearchModes(mode, type);
        var records = normalizedScope == "file" && locate
            ? EsmHelpers.ScanAllRecords(xboxData, xboxHeader.IsBigEndian).OrderBy(r => r.Offset).ToList()
            : [];

        AnsiConsole.MarkupLine($"[cyan]Record:[/] {type} 0x{formId:X8}");
        AnsiConsole.MarkupLine($"[cyan]Scope:[/] {(normalizedScope == "record" ? "record" : "file")}");
        AnsiConsole.MarkupLine($"[cyan]Subrecords:[/] {string.Join(", ", candidates.Select(s => s.Signature))}");
        AnsiConsole.MarkupLine($"[cyan]Modes:[/] {string.Join(", ", searchModes)}");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Subrecord")
            .AddColumn("Mode")
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn(new TableColumn("Offset").RightAligned())
            .AddColumn("Record")
            .AddColumn(new TableColumn("FormID").RightAligned());

        foreach (var sub in candidates)
        foreach (var searchMode in searchModes)
        {
            var pattern = BuildPattern(sub.Data, sub.Signature, searchMode);
            if (pattern.Length == 0) continue;

            var hits = FindAllOccurrences(haystack, pattern, maxHits);
            if (hits.Count == 0) continue;

            foreach (var hit in hits)
            {
                AnalyzerRecordInfo? match = null;
                if (normalizedScope == "file" && locate)
                    match = FindRecordAtOffset(records, (uint)hit);
                else if (normalizedScope == "record") match = xboxRecord;

                var recordLabel = match?.Signature ?? "(none)";
                var formLabel = match != null ? $"0x{match.FormId:X8}" : "-";
                var offsetLabel = normalizedScope == "record" ? $"rec+0x{hit:X}" : $"0x{hit:X8}";

                table.AddRow(
                    sub.Signature,
                    searchMode,
                    $"0x{sub.Data.Length:X}",
                    offsetLabel,
                    recordLabel,
                    formLabel);
            }
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static (AnalyzerRecordInfo? Record, byte[]? RecordData) FindRecord(byte[] data, bool bigEndian,
        string recordType, uint formId)
    {
        var records = EsmHelpers.ScanForRecordType(data, bigEndian, recordType);
        var match = records.FirstOrDefault(r => r.FormId == formId);
        if (match == null) return (null, null);

        try
        {
            var recordData = EsmHelpers.GetRecordData(data, match, bigEndian);
            return (match, recordData);
        }
        catch
        {
            return (null, null);
        }
    }

    private static HashSet<string> ResolveSubrecordFilters(string recordType, string[]? subrecordFilters)
    {
        if (subrecordFilters is { Length: > 0 })
            return new HashSet<string>(subrecordFilters.Select(s => s.ToUpperInvariant()));

        return recordType switch
        {
            "LAND" => new HashSet<string>(["VNML", "VHGT", "VCLR", "VTXT"]),
            "NAVM" => new HashSet<string>(["NVVX", "NVTR", "NVGD"]),
            "WRLD" => new HashSet<string>(["OFST"]),
            _ => new HashSet<string>()
        };
    }

    private static List<string> ResolveSearchModes(string mode, string recordType)
    {
        var normalized = mode.ToLowerInvariant();
        if (normalized == "all")
            return recordType == "LAND"
                ? ["raw", "swap2", "swap4", "land"]
                : ["raw", "swap2", "swap4"];

        return normalized switch
        {
            "raw" => ["raw"],
            "swap2" => ["swap2"],
            "swap4" => ["swap4"],
            "land" => ["land"],
            "land-vhgt" => ["land-vhgt"],
            "land-atxt" => ["land-atxt"],
            "land-vtxt" => ["land-vtxt"],
            _ => ["raw"]
        };
    }

    private static byte[] BuildPattern(byte[] data, string signature, string mode)
    {
        return mode switch
        {
            "raw" => data,
            "swap2" => SwapEvery2(data),
            "swap4" => SwapEvery4(data),
            "land" => ApplyLandTransforms(signature, data),
            "land-vhgt" => ApplyLandVhgt(data),
            "land-atxt" => ApplyLandAtxt(data),
            "land-vtxt" => ApplyLandVtxt(data),
            _ => data
        };
    }

    private static byte[] ApplyLandTransforms(string signature, byte[] data)
    {
        return signature.ToUpperInvariant() switch
        {
            "VHGT" => ApplyLandVhgt(data),
            "ATXT" => ApplyLandAtxt(data),
            "VTXT" => ApplyLandVtxt(data),
            _ => Array.Empty<byte>()
        };
    }

    private static byte[] ApplyLandVhgt(byte[] data)
    {
        if (data.Length < 4) return Array.Empty<byte>();

        var copy = (byte[])data.Clone();
        (copy[0], copy[3]) = (copy[3], copy[0]);
        (copy[1], copy[2]) = (copy[2], copy[1]);
        return copy;
    }

    private static byte[] ApplyLandAtxt(byte[] data)
    {
        if (data.Length < 8) return Array.Empty<byte>();

        var copy = (byte[])data.Clone();
        // Swap FormID (bytes 0-3)
        (copy[0], copy[3]) = (copy[3], copy[0]);
        (copy[1], copy[2]) = (copy[2], copy[1]);
        // Byte 4: Quadrant - no swap
        // Byte 5: Platform byte (Xbox=0x00, PC=0x88) - set to PC value
        copy[5] = 0x88;
        // Swap layer (bytes 6-7)
        (copy[6], copy[7]) = (copy[7], copy[6]);
        return copy;
    }

    private static byte[] ApplyLandVtxt(byte[] data)
    {
        if (data.Length < 8) return Array.Empty<byte>();

        var copy = (byte[])data.Clone();
        for (var i = 0; i + 7 < copy.Length; i += 8)
        {
            // Swap position (uint16) at bytes 0-1
            (copy[i], copy[i + 1]) = (copy[i + 1], copy[i]);
            // Swap float at bytes 4-7
            (copy[i + 4], copy[i + 7]) = (copy[i + 7], copy[i + 4]);
            (copy[i + 5], copy[i + 6]) = (copy[i + 6], copy[i + 5]);
        }

        return copy;
    }

    private static List<int> FindAllOccurrences(byte[] haystack, byte[] needle, int maxHits)
    {
        var results = new List<int>();
        if (needle.Length == 0 || haystack.Length < needle.Length || maxHits <= 0) return results;

        var span = haystack.AsSpan();
        var pattern = needle.AsSpan();
        var start = 0;

        while (start <= span.Length - pattern.Length && results.Count < maxHits)
        {
            var found = span[start..].IndexOf(pattern);
            if (found < 0) break;

            var offset = start + found;
            results.Add(offset);
            start = offset + 1;
        }

        return results;
    }

    private static byte[] SwapEvery2(byte[] data)
    {
        var copy = (byte[])data.Clone();
        for (var i = 0; i + 1 < copy.Length; i += 2) (copy[i], copy[i + 1]) = (copy[i + 1], copy[i]);
        return copy;
    }

    private static byte[] SwapEvery4(byte[] data)
    {
        var copy = (byte[])data.Clone();
        for (var i = 0; i + 3 < copy.Length; i += 4)
        {
            (copy[i], copy[i + 3]) = (copy[i + 3], copy[i]);
            (copy[i + 1], copy[i + 2]) = (copy[i + 2], copy[i + 1]);
        }

        return copy;
    }

    private static AnalyzerRecordInfo? FindRecordAtOffset(List<AnalyzerRecordInfo> records, uint offset)
    {
        foreach (var record in records)
        {
            var start = record.Offset;
            var end = record.Offset + record.TotalSize;
            if (offset >= start && offset < end) return record;
        }

        return null;
    }

    private static bool TryParseFormId(string text, out uint formId)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];

        return uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out formId);
    }
}