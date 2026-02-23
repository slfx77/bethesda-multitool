using FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers;
using Spectre.Console;
using System.CommandLine;
using System.Security.Cryptography;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for analyzing the Xbox 360 TOFT streaming cache region.
/// </summary>
public static class ToftCommands
{
    public static Command CreateToftCommand()
    {
        var command = new Command("toft", "Analyze the Xbox 360 TOFT streaming cache region");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Maximum number of TOFT records to list (0 = none)",
            DefaultValueFactory = _ => 50
        };
        var typeLimitOption = new Option<int>("--type-limit")
        {
            Description = "Maximum number of record types to display (0 = unlimited)",
            DefaultValueFactory = _ => 20
        };
        var compareOption = new Option<bool>("--compare")
        {
            Description = "Compare TOFT INFO records against primary records"
        };
        var compareDetailOption = new Option<bool>("--compare-detail")
        {
            Description = "Show subrecord-level detail for a TOFT INFO record"
        };
        var compareStringsOption = new Option<bool>("--compare-strings")
        {
            Description = "Compare string subrecords across TOFT vs primary INFO"
        };
        var compareStringsLimitOption = new Option<int>("--compare-strings-limit")
        {
            Description = "Maximum number of string compare rows to display (0 = unlimited)",
            DefaultValueFactory = _ => 50
        };
        var compareFormIdOption = new Option<string?>("--compare-formid")
        {
            Description = "FormID to inspect when using --compare-detail (hex, e.g., 0x000FB23E)"
        };
        var compareLimitOption = new Option<int>("--compare-limit")
        {
            Description = "Maximum number of compare mismatches to display (0 = unlimited)",
            DefaultValueFactory = _ => 25
        };

        command.Arguments.Add(fileArg);
        command.Options.Add(limitOption);
        command.Options.Add(typeLimitOption);
        command.Options.Add(compareOption);
        command.Options.Add(compareDetailOption);
        command.Options.Add(compareStringsOption);
        command.Options.Add(compareStringsLimitOption);
        command.Options.Add(compareFormIdOption);
        command.Options.Add(compareLimitOption);

        command.SetAction(parseResult => AnalyzeToftRegion(
            parseResult.GetValue(fileArg)!,
            new ToftOptions(
                parseResult.GetValue(limitOption),
                parseResult.GetValue(typeLimitOption),
                parseResult.GetValue(compareOption),
                parseResult.GetValue(compareLimitOption),
                parseResult.GetValue(compareDetailOption),
                parseResult.GetValue(compareStringsOption),
                parseResult.GetValue(compareStringsLimitOption),
                parseResult.GetValue(compareFormIdOption))));

        return command;
    }

    private static int AnalyzeToftRegion(string filePath, ToftOptions options)
    {
        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null)
        {
            return 1;
        }

        var data = esm.Data;
        var bigEndian = esm.IsBigEndian;

        var toftRecord = EsmRecordParser.ScanForRecordType(data, bigEndian, "TOFT")
            .OrderBy(r => r.Offset)
            .FirstOrDefault();

        if (toftRecord == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] TOFT record not found");
            return 1;
        }

        var preToftRecords = EsmRecordParser.ScanAllRecords(data, bigEndian)
            .Where(r => r.Offset < toftRecord.Offset)
            .ToList();

        var preToftData = BuildPreToftData(preToftRecords, data, options.ComparePrimary);
        var scanResult = ScanToftEntries(data, bigEndian, toftRecord, preToftData.ByType);

        ToftDisplayRenderer.PrintToftSummary(toftRecord.Offset, scanResult.EndOffset, scanResult.ToftBytes,
            scanResult.Entries.Count);
        ToftDisplayRenderer.WriteTypeTable(scanResult.TypeCounts, scanResult.TypeWithPrimary, options.TypeLimit);

        if (options.ComparePrimary)
        {
            ToftComparisonHelper.WriteCompareResults(new ToftComparisonHelper.ToftCompareContext(scanResult.Entries,
                data, preToftData.InfoHashes, preToftData.InfoByFormId, bigEndian, options.CompareLimit,
                options.CompareDetail, options.CompareFormIdText));
        }

        if (options.CompareStrings)
        {
            ToftComparisonHelper.WriteStringCompare(new ToftComparisonHelper.ToftStringCompareContext(
                scanResult.Entries, data, preToftData.InfoByFormId, bigEndian, options.CompareStringsLimit));
        }

        if (options.Limit <= 0)
        {
            return 0;
        }

        ToftDisplayRenderer.WriteEntryTable(scanResult.Entries, options.Limit);

        return 0;
    }

    private static PreToftData BuildPreToftData(List<AnalyzerRecordInfo> preToftRecords, byte[] data,
        bool includeHashes)
    {
        var infoHashes = new Dictionary<uint, (int Size, byte[] Hash)>();
        var infoByFormId = preToftRecords
            .Where(r => r.Signature == "INFO")
            .GroupBy(r => r.FormId)
            .ToDictionary(g => g.Key, g => g.First());
        var byType = preToftRecords
            .GroupBy(r => r.Signature)
            .ToDictionary(g => g.Key, g => g.Select(r => r.FormId).ToHashSet());

        if (!includeHashes)
        {
            return new PreToftData(infoHashes, infoByFormId, byType);
        }

        foreach (var record in preToftRecords.Where(r => r.Signature == "INFO"))
        {
            var size = (int)record.TotalSize;
            if (size <= 0 || record.Offset + size > data.Length)
            {
                continue;
            }

            var hash = SHA256.HashData(data.AsSpan((int)record.Offset, size));
            infoHashes[record.FormId] = (size, hash);
        }

        return new PreToftData(infoHashes, infoByFormId, byType);
    }

    private static ToftScanResult ScanToftEntries(byte[] data, bool bigEndian, AnalyzerRecordInfo toftRecord,
        Dictionary<string, HashSet<uint>> preToftByType)
    {
        var typeCounts = new Dictionary<string, int>();
        var typeWithPrimary = new Dictionary<string, int>();
        var entries = new List<ToftEntry>();

        var offset = (int)toftRecord.Offset;
        var endOffset = offset;

        while (offset + EsmParser.MainRecordHeaderSize <= data.Length)
        {
            var grupHeader = EsmParser.ParseGroupHeader(data.AsSpan(offset), bigEndian);
            if (grupHeader != null)
            {
                endOffset = offset;
                break;
            }

            var recordHeader = EsmParser.ParseRecordHeader(data.AsSpan(offset), bigEndian);
            if (recordHeader == null)
            {
                break;
            }

            var recordEnd = offset + EsmParser.MainRecordHeaderSize + (int)recordHeader.DataSize;
            if (recordEnd > data.Length)
            {
                break;
            }

            var signature = recordHeader.Signature;
            var hasPrimary = preToftByType.TryGetValue(signature, out var formIds) &&
                             formIds.Contains(recordHeader.FormId);

            entries.Add(new ToftEntry(offset, signature, recordHeader.FormId, recordHeader.DataSize, hasPrimary));

            typeCounts[signature] = typeCounts.TryGetValue(signature, out var count) ? count + 1 : 1;
            if (hasPrimary)
            {
                typeWithPrimary[signature] = typeWithPrimary.TryGetValue(signature, out var primaryCount)
                    ? primaryCount + 1
                    : 1;
            }

            offset = recordEnd;
            endOffset = offset;
        }

        var toftBytes = endOffset - (int)toftRecord.Offset;
        return new ToftScanResult(entries, typeCounts, typeWithPrimary, endOffset, toftBytes);
    }

    internal sealed record ToftEntry(int Offset, string Signature, uint FormId, uint DataSize, bool HasPrimary)
    {
        public int TotalSize => (int)DataSize + EsmParser.MainRecordHeaderSize;
    }

    private sealed record PreToftData(
        Dictionary<uint, (int Size, byte[] Hash)> InfoHashes,
        Dictionary<uint, AnalyzerRecordInfo> InfoByFormId,
        Dictionary<string, HashSet<uint>> ByType);

    private sealed record ToftScanResult(
        List<ToftEntry> Entries,
        Dictionary<string, int> TypeCounts,
        Dictionary<string, int> TypeWithPrimary,
        int EndOffset,
        int ToftBytes);

    private sealed record ToftOptions(
        int Limit,
        int TypeLimit,
        bool ComparePrimary,
        int CompareLimit,
        bool CompareDetail,
        bool CompareStrings,
        int CompareStringsLimit,
        string? CompareFormIdText);
}
