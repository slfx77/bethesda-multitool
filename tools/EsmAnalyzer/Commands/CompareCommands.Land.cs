using System.Globalization;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

public static partial class CompareCommands
{
    private static int CompareLand(string xboxPath, string pcPath, string? formIdStr, bool compareAll)
    {
        uint? targetFormId = null;

        if (!string.IsNullOrEmpty(formIdStr))
            targetFormId = formIdStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt32(formIdStr, 16)
                : uint.Parse(formIdStr, CultureInfo.InvariantCulture);

        if (targetFormId == null && !compareAll)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Specify --formid <ID> or --all");
            return 1;
        }

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

        var xboxHeader = EsmParser.ParseFileHeader(xboxData);
        var pcHeader = EsmParser.ParseFileHeader(pcData);

        if (xboxHeader == null || pcHeader == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Failed to parse ESM headers");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"Xbox 360: {Path.GetFileName(xboxPath)} ({(xboxHeader.IsBigEndian ? "Big-endian" : "Little-endian")})");
        AnsiConsole.MarkupLine(
            $"PC: {Path.GetFileName(pcPath)} ({(pcHeader.IsBigEndian ? "Big-endian" : "Little-endian")})");
        AnsiConsole.WriteLine();

        // Scan for LAND records using byte scan for reliable Xbox 360 detection
        List<AnalyzerRecordInfo> xboxLands = [];
        List<AnalyzerRecordInfo> pcLands = [];

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning for LAND records...", ctx =>
            {
                ctx.Status("Scanning Xbox 360 file...");
                xboxLands = EsmHelpers.ScanForRecordType(xboxData, xboxHeader.IsBigEndian, "LAND");

                ctx.Status("Scanning PC file...");
                pcLands = EsmHelpers.ScanForRecordType(pcData, pcHeader.IsBigEndian, "LAND");
            });

        AnsiConsole.MarkupLine($"Xbox 360 LAND records: [cyan]{xboxLands.Count:N0}[/]");
        AnsiConsole.MarkupLine($"PC LAND records: [cyan]{pcLands.Count:N0}[/]");
        AnsiConsole.WriteLine();

        // Build FormID -> record lookup for PC
        var pcLandsByFormId = pcLands.ToDictionary(r => r.FormId);

        IEnumerable<AnalyzerRecordInfo> landsToCompare;
        if (targetFormId.HasValue)
            landsToCompare = xboxLands.Where(r => r.FormId == targetFormId.Value);
        else
            // Sample 10 LAND records for --all
            landsToCompare = xboxLands.Take(10);

        foreach (var xboxRec in landsToCompare)
        {
            var rule = new Rule($"LAND FormID: [cyan]0x{xboxRec.FormId:X8}[/]");
            rule.LeftJustified();
            AnsiConsole.Write(rule);

            if (!pcLandsByFormId.TryGetValue(xboxRec.FormId, out var pcRec))
            {
                AnsiConsole.MarkupLine("[red]NOT FOUND in PC file![/]");
                continue;
            }

            // Get record data
            var xboxRecordData = EsmHelpers.GetRecordData(xboxData, xboxRec, xboxHeader.IsBigEndian);
            var pcRecordData = EsmHelpers.GetRecordData(pcData, pcRec, pcHeader.IsBigEndian);

            AnsiConsole.MarkupLine(
                $"  Xbox 360: {xboxRec.DataSize} bytes (compressed) → {xboxRecordData.Length} bytes");
            AnsiConsole.MarkupLine($"  PC:       {pcRec.DataSize} bytes (compressed) → {pcRecordData.Length} bytes");

            // Parse subrecords
            var xboxSubs = EsmHelpers.ParseSubrecords(xboxRecordData, xboxHeader.IsBigEndian);
            var pcSubs = EsmHelpers.ParseSubrecords(pcRecordData, pcHeader.IsBigEndian);

            AnsiConsole.MarkupLine($"  Xbox 360 subrecords: {xboxSubs.Count}");
            AnsiConsole.MarkupLine($"  PC subrecords: {pcSubs.Count}");
            AnsiConsole.WriteLine();

            // Compare each subrecord
            var pcSubsBySig = pcSubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.ToList());
            var xboxSubsBySig = xboxSubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.ToList());

            var subTable = new Table()
                .Border(TableBorder.Simple)
                .AddColumn("[bold]Subrecord[/]")
                .AddColumn("[bold]Count[/]")
                .AddColumn("[bold]Size[/]")
                .AddColumn("[bold]Status[/]");

            foreach (var sig in xboxSubsBySig.Keys.Union(pcSubsBySig.Keys).OrderBy(s => s))
            {
                var xboxList = xboxSubsBySig.GetValueOrDefault(sig, []);
                var pcList = pcSubsBySig.GetValueOrDefault(sig, []);

                if (xboxList.Count != pcList.Count)
                {
                    subTable.AddRow(
                        sig,
                        $"Xbox={xboxList.Count}, PC={pcList.Count}",
                        "-",
                        "[red]COUNT MISMATCH[/]"
                    );
                    continue;
                }

                var totalXboxSize = xboxList.Sum(s => s.Data.Length);
                var allIdentical = true;
                var allEndianSwapped = true;
                var allLandTransformed = true;

                for (var i = 0; i < xboxList.Count; i++)
                {
                    var xboxSub = xboxList[i];
                    var pcSub = pcList[i];

                    if (!xboxSub.Data.AsSpan().SequenceEqual(pcSub.Data)) allIdentical = false;

                    var landTransformed = ApplyLandTransform(sig, xboxSub.Data);
                    if (landTransformed == null)
                        allLandTransformed = false;
                    else if (!landTransformed.AsSpan().SequenceEqual(pcSub.Data)) allLandTransformed = false;

                    // Check if 4-byte endian swapped
                    if (xboxSub.Data.Length == pcSub.Data.Length && xboxSub.Data.Length % 4 == 0)
                    {
                        var swapped = new byte[xboxSub.Data.Length];
                        for (var j = 0; j < xboxSub.Data.Length; j += 4)
                        {
                            swapped[j] = xboxSub.Data[j + 3];
                            swapped[j + 1] = xboxSub.Data[j + 2];
                            swapped[j + 2] = xboxSub.Data[j + 1];
                            swapped[j + 3] = xboxSub.Data[j];
                        }

                        if (!swapped.AsSpan().SequenceEqual(pcSub.Data)) allEndianSwapped = false;
                    }
                    else
                    {
                        allEndianSwapped = false;
                    }
                }

                var status = allIdentical
                    ? "[green]IDENTICAL[/]"
                    : allLandTransformed
                        ? "[yellow]LAND-TRANSFORMED[/]"
                        : allEndianSwapped
                            ? "[yellow]ENDIAN-SWAPPED (4-byte)[/]"
                            : "[red]DIFFERS[/]";

                subTable.AddRow(
                    sig,
                    $"{xboxList.Count}×",
                    $"{totalXboxSize:N0}",
                    status
                );
            }

            AnsiConsole.Write(subTable);
            AnsiConsole.WriteLine();
        }

        return 0;
    }

    private static byte[]? ApplyLandTransform(string signature, byte[] data)
    {
        return signature.ToUpperInvariant() switch
        {
            "VHGT" => ApplyLandVhgt(data),
            "ATXT" => ApplyLandAtxt(data),
            "VTXT" => ApplyLandVtxt(data),
            _ => null
        };
    }

    private static byte[]? ApplyLandVhgt(byte[] data)
    {
        if (data.Length < 4) return null;

        var copy = (byte[])data.Clone();
        (copy[0], copy[3]) = (copy[3], copy[0]);
        (copy[1], copy[2]) = (copy[2], copy[1]);
        return copy;
    }

    private static byte[]? ApplyLandAtxt(byte[] data)
    {
        if (data.Length < 8) return null;

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

    private static byte[]? ApplyLandVtxt(byte[] data)
    {
        if (data.Length < 8) return null;

        var copy = (byte[])data.Clone();
        for (var i = 0; i + 7 < copy.Length; i += 8)
        {
            (copy[i], copy[i + 1]) = (copy[i + 1], copy[i]);
            (copy[i + 4], copy[i + 7]) = (copy[i + 7], copy[i + 4]);
            (copy[i + 5], copy[i + 6]) = (copy[i + 6], copy[i + 5]);
        }

        return copy;
    }
}