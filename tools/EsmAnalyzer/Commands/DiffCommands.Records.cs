using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

public static partial class DiffCommands
{
    private static int DiffRecords(string xboxPath, string pcPath, string? formIdStr, string? recordType, int limit,
        int maxBytes)
    {
        if (!File.Exists(xboxPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Xbox 360 file not found: {xboxPath}");
            return 1;
        }

        if (!File.Exists(pcPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] PC file not found: {pcPath}");
            return 1;
        }

        var xboxData = File.ReadAllBytes(xboxPath);
        var pcData = File.ReadAllBytes(pcPath);

        var xboxBigEndian = EsmParser.IsBigEndian(xboxData);
        var pcBigEndian = EsmParser.IsBigEndian(pcData);

        AnsiConsole.MarkupLine("[bold cyan]ESM Record Diff[/]");
        AnsiConsole.MarkupLine(
            $"Xbox 360: {Path.GetFileName(xboxPath)} ({(xboxBigEndian ? "Big-endian" : "Little-endian")})");
        AnsiConsole.MarkupLine(
            $"PC:       {Path.GetFileName(pcPath)} ({(pcBigEndian ? "Big-endian" : "Little-endian")})");
        AnsiConsole.WriteLine();

        // Parse specific FormID
        uint? targetFormId = null;
        if (!string.IsNullOrEmpty(formIdStr))
        {
            if (formIdStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                targetFormId = Convert.ToUInt32(formIdStr, 16);
            else
                targetFormId = uint.Parse(formIdStr);
        }

        // If we have a specific FormID, find it in both files
        if (targetFormId.HasValue)
            return DiffSpecificRecord(xboxData, pcData, xboxBigEndian, pcBigEndian, targetFormId.Value, maxBytes);

        // If we have a record type, compare records of that type
        if (!string.IsNullOrEmpty(recordType))
            return DiffRecordType(xboxData, pcData, xboxBigEndian, pcBigEndian, recordType, limit, maxBytes);

        AnsiConsole.MarkupLine("[yellow]Please specify either --formid or --type[/]");
        return 1;
    }

    private static int DiffSpecificRecord(byte[] xboxData, byte[] pcData, bool xboxBigEndian, bool pcBigEndian,
        uint formId, int maxBytes)
    {
        // Find record in Xbox file
        var xboxRecord = DiffCommandHelpers.FindRecordByFormId(xboxData, xboxBigEndian, formId);
        var pcRecord = DiffCommandHelpers.FindRecordByFormId(pcData, pcBigEndian, formId);

        if (xboxRecord == null)
        {
            AnsiConsole.MarkupLine($"[red]FormID 0x{formId:X8} not found in Xbox 360 file[/]");
            return 1;
        }

        if (pcRecord == null)
        {
            AnsiConsole.MarkupLine($"[red]FormID 0x{formId:X8} not found in PC file[/]");
            return 1;
        }

        DiffSingleRecord(xboxData, pcData, xboxBigEndian, pcBigEndian, xboxRecord, pcRecord, maxBytes);
        return 0;
    }

    private static int DiffRecordType(byte[] xboxData, byte[] pcData, bool xboxBigEndian, bool pcBigEndian,
        string recordType, int limit, int maxBytes)
    {
        var xboxRecords = EsmHelpers.ScanForRecordType(xboxData, xboxBigEndian, recordType);
        var pcRecords = EsmHelpers.ScanForRecordType(pcData, pcBigEndian, recordType);

        AnsiConsole.MarkupLine($"Found [cyan]{xboxRecords.Count}[/] {recordType} records in Xbox 360 file");
        AnsiConsole.MarkupLine($"Found [cyan]{pcRecords.Count}[/] {recordType} records in PC file");
        AnsiConsole.WriteLine();

        // Build FormID lookup for PC records
        var pcByFormId = pcRecords.ToDictionary(r => r.FormId, r => r);

        var compared = 0;
        foreach (var xboxRec in xboxRecords)
        {
            if (compared >= limit) break;

            if (pcByFormId.TryGetValue(xboxRec.FormId, out var pcRec))
            {
                DiffSingleRecord(xboxData, pcData, xboxBigEndian, pcBigEndian, xboxRec, pcRec, maxBytes);
                compared++;
            }
        }

        if (compared == 0) AnsiConsole.MarkupLine("[yellow]No matching FormIDs found between files[/]");

        return 0;
    }

    private static void DiffSingleRecord(byte[] xboxData, byte[] pcData, bool xboxBigEndian, bool pcBigEndian,
        AnalyzerRecordInfo xboxRec, AnalyzerRecordInfo pcRec, int maxBytes)
    {
        AnsiConsole.MarkupLine($"[bold yellow]═══ {xboxRec.Signature} FormID: 0x{xboxRec.FormId:X8} ═══[/]");
        AnsiConsole.WriteLine();

        // Record header comparison
        var headerTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Field[/]")
            .AddColumn("[bold]Xbox 360[/]")
            .AddColumn("[bold]PC[/]")
            .AddColumn("[bold]Status[/]");

        headerTable.AddRow("Offset", $"0x{xboxRec.Offset:X8}", $"0x{pcRec.Offset:X8}", "[grey]N/A[/]");
        headerTable.AddRow("DataSize", $"{xboxRec.DataSize:N0}", $"{pcRec.DataSize:N0}",
            xboxRec.DataSize == pcRec.DataSize ? "[green]MATCH[/]" : "[yellow]DIFFER[/]");
        headerTable.AddRow("Flags", $"0x{xboxRec.Flags:X8}", $"0x{pcRec.Flags:X8}",
            xboxRec.Flags == pcRec.Flags ? "[green]MATCH[/]" : "[yellow]DIFFER[/]");

        var xboxCompressed = (xboxRec.Flags & 0x00040000) != 0;
        var pcCompressed = (pcRec.Flags & 0x00040000) != 0;
        headerTable.AddRow("Compressed", xboxCompressed ? "Yes" : "No", pcCompressed ? "Yes" : "No",
            xboxCompressed == pcCompressed ? "[green]MATCH[/]" : "[yellow]DIFFER[/]");

        AnsiConsole.Write(headerTable);
        AnsiConsole.WriteLine();

        // Parse subrecords
        try
        {
            var xboxRecordData = EsmHelpers.GetRecordData(xboxData, xboxRec, xboxBigEndian);
            var pcRecordData = EsmHelpers.GetRecordData(pcData, pcRec, pcBigEndian);

            var xboxSubs = EsmHelpers.ParseSubrecords(xboxRecordData, xboxBigEndian);
            var pcSubs = EsmHelpers.ParseSubrecords(pcRecordData, pcBigEndian);

            // Group by signature
            var xboxBySig = xboxSubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.ToList());
            var pcBySig = pcSubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.ToList());

            var allSigs = xboxBySig.Keys.Union(pcBySig.Keys).OrderBy(s => s).ToList();

            AnsiConsole.MarkupLine("[bold]Subrecords:[/]");
            AnsiConsole.WriteLine();

            foreach (var sig in allSigs)
            {
                var xboxList = xboxBySig.GetValueOrDefault(sig, []);
                var pcList = pcBySig.GetValueOrDefault(sig, []);

                var maxCount = Math.Max(xboxList.Count, pcList.Count);

                for (var i = 0; i < maxCount; i++)
                {
                    var xsub = i < xboxList.Count ? xboxList[i] : null;
                    var psub = i < pcList.Count ? pcList[i] : null;

                    if (xsub != null && psub != null)
                        DiffSubrecord(sig, xsub, psub, xboxBigEndian, pcBigEndian, maxBytes);
                    else if (xsub != null)
                        AnsiConsole.MarkupLine($"  [red]{sig}[/]: Only in Xbox 360 ({xsub.Data.Length} bytes)");
                    else if (psub != null)
                        AnsiConsole.MarkupLine($"  [red]{sig}[/]: Only in PC ({psub.Data.Length} bytes)");
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error parsing record data: {ex.Message}[/]");
        }

        AnsiConsole.WriteLine();
    }

    private static void DiffSubrecord(string sig, AnalyzerSubrecordInfo xbox, AnalyzerSubrecordInfo pc, bool xboxBE,
        bool pcBE, int maxBytes)
    {
        var sizeMatch = xbox.Data.Length == pc.Data.Length;

        // Check if data is identical after endian conversion
        var isIdentical = xbox.Data.SequenceEqual(pc.Data);
        var isEndianSwapped = false;
        var structuredPattern = string.Empty;

        if (!isIdentical && sizeMatch)
        {
            // Check if it's just endian-swapped
            isEndianSwapped = DiffCommandHelpers.CheckEndianSwapped(xbox.Data, pc.Data);

            // If not simple endian swap, try to detect structured pattern
            if (!isEndianSwapped)
                structuredPattern = DiffCommandHelpers.AnalyzeStructuredDifference(xbox.Data, pc.Data);
        }

        string status;
        if (isIdentical)
            status = "[green]IDENTICAL[/]";
        else if (isEndianSwapped)
            status = "[cyan]ENDIAN-SWAPPED[/]";
        else if (!sizeMatch)
            status = $"[red]SIZE DIFF ({xbox.Data.Length} vs {pc.Data.Length})[/]";
        else if (!string.IsNullOrEmpty(structuredPattern))
            status = $"[cyan]STRUCTURED:[/] {structuredPattern}";
        else
            status = "[yellow]CONTENT DIFFERS[/]";

        AnsiConsole.MarkupLine($"  [bold]{sig}[/] ({xbox.Data.Length} bytes): {status}");

        // Show bytes if different and not just endian-swapped
        if (!isIdentical && !isEndianSwapped && string.IsNullOrEmpty(structuredPattern))
        {
            var showLen = Math.Min(maxBytes, Math.Max(xbox.Data.Length, pc.Data.Length));
            AnsiConsole.MarkupLine(
                $"    Xbox: {DiffCommandHelpers.FormatBytes(xbox.Data, 0, Math.Min(showLen, xbox.Data.Length))}");
            AnsiConsole.MarkupLine(
                $"    PC:   {DiffCommandHelpers.FormatBytes(pc.Data, 0, Math.Min(showLen, pc.Data.Length))}");

            // Try to interpret the data
            DiffCommandHelpers.TryInterpretDifference(sig, xbox.Data, pc.Data, xboxBE, pcBE);
        }
    }
}