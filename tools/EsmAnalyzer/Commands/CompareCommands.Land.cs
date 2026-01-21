using EsmAnalyzer.Helpers;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

public static partial class CompareCommands
{
    private const int VhgtDataLength = 1093;

    private static int CompareLand(string xboxPath, string pcPath, string? formIdStr, bool compareAll)
    {
        var targetFormId = EsmFileLoader.ParseFormId(formIdStr);
        if (!string.IsNullOrWhiteSpace(formIdStr) && targetFormId == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid FormID: {formIdStr}");
            return 1;
        }

        if (targetFormId == null && !compareAll)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Specify --formid <ID> or --all");
            return 1;
        }

        var (xbox, pc) = EsmFileLoader.LoadPair(xboxPath, pcPath);
        if (xbox == null || pc == null) return 1;

        AnsiConsole.WriteLine();

        // Scan for LAND records using byte scan for reliable Xbox 360 detection
        List<AnalyzerRecordInfo> xboxLands = [];
        List<AnalyzerRecordInfo> pcLands = [];

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning for LAND records...", ctx =>
            {
                ctx.Status("Scanning Xbox 360 file...");
                xboxLands = EsmHelpers.ScanForRecordType(xbox.Data, xbox.IsBigEndian, "LAND");

                ctx.Status("Scanning PC file...");
                pcLands = EsmHelpers.ScanForRecordType(pc.Data, pc.IsBigEndian, "LAND");
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
            var xboxRecordData = EsmHelpers.GetRecordData(xbox.Data, xboxRec, xbox.IsBigEndian);
            var pcRecordData = EsmHelpers.GetRecordData(pc.Data, pcRec, pc.IsBigEndian);

            AnsiConsole.MarkupLine(
                $"  Xbox 360: {xboxRec.DataSize} bytes (compressed) → {xboxRecordData.Length} bytes");
            AnsiConsole.MarkupLine($"  PC:       {pcRec.DataSize} bytes (compressed) → {pcRecordData.Length} bytes");

            // Parse subrecords
            var xboxSubs = EsmHelpers.ParseSubrecords(xboxRecordData, xbox.IsBigEndian);
            var pcSubs = EsmHelpers.ParseSubrecords(pcRecordData, pc.IsBigEndian);

            AnsiConsole.MarkupLine($"  Xbox 360 subrecords: {xboxSubs.Count}");
            AnsiConsole.MarkupLine($"  PC subrecords: {pcSubs.Count}");
            AnsiConsole.WriteLine();

            var subTable = BuildSubrecordComparisonTable(xboxSubs, pcSubs);
            AnsiConsole.Write(subTable);
            AnsiConsole.WriteLine();

            if (targetFormId.HasValue)
                PrintLandDiffDetails(xboxSubs, pcSubs, xbox.IsBigEndian);
        }

        return 0;
    }

    private static void PrintLandDiffDetails(List<AnalyzerSubrecordInfo> xboxSubs, List<AnalyzerSubrecordInfo> pcSubs,
        bool xboxBigEndian)
    {
        var xboxBySig = xboxSubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.ToList());
        var pcBySig = pcSubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.ToList());

        if (xboxBySig.TryGetValue("VHGT", out var xboxVhgt) && pcBySig.TryGetValue("VHGT", out var pcVhgt))
        {
            var left = xboxBigEndian ? ApplyLandVhgt(xboxVhgt[0].Data) : xboxVhgt[0].Data;
            if (left is not null)
            {
                var diffIndex = FindFirstDiffIndexVhgt(left, pcVhgt[0].Data);
                if (diffIndex >= 0)
                    AnsiConsole.MarkupLine(
                        $"[yellow]VHGT first diff at byte {diffIndex}[/] (Xbox={left[diffIndex]:X2} PC={pcVhgt[0].Data[diffIndex]:X2})");
            }
        }

        if (xboxBySig.TryGetValue("ATXT", out var xboxAtxt) && pcBySig.TryGetValue("ATXT", out var pcAtxt))
        {
            var left = xboxBigEndian ? ApplyLandAtxt(xboxAtxt[0].Data) : xboxAtxt[0].Data;
            if (left is not null)
            {
                var diffIndex = FindFirstDiffIndex(left, pcAtxt[0].Data);
                if (diffIndex >= 0)
                    AnsiConsole.MarkupLine(
                        $"[yellow]ATXT first diff at byte {diffIndex}[/] (Xbox={left[diffIndex]:X2} PC={pcAtxt[0].Data[diffIndex]:X2})");
            }
        }
    }

    private static int FindFirstDiffIndex(byte[] left, byte[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        for (var i = 0; i < length; i++)
            if (left[i] != right[i])
                return i;
        return left.Length == right.Length ? -1 : length;
    }

    private static int FindFirstDiffIndexVhgt(byte[] left, byte[] right)
    {
        var length = Math.Min(Math.Min(left.Length, right.Length), VhgtDataLength);
        for (var i = 0; i < length; i++)
            if (left[i] != right[i])
                return i;

        if (length == VhgtDataLength) return -1;
        return left.Length == right.Length ? -1 : length;
    }

    private static bool VhgtEquals(byte[] left, byte[] right)
    {
        if (left.Length < VhgtDataLength || right.Length < VhgtDataLength)
        {
            var len = Math.Min(left.Length, right.Length);
            if (!left.AsSpan(0, len).SequenceEqual(right.AsSpan(0, len))) return false;
            return left.Length == right.Length;
        }

        return left.AsSpan(0, VhgtDataLength).SequenceEqual(right.AsSpan(0, VhgtDataLength));
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

    private static string GetSubrecordStatus(bool allIdentical, bool allLandTransformed, bool allEndianSwapped)
    {
        if (allIdentical) return "[green]IDENTICAL[/]";
        if (allLandTransformed) return "[yellow]LAND-TRANSFORMED[/]";
        if (allEndianSwapped) return "[yellow]ENDIAN-SWAPPED (4-byte)[/]";
        return "[red]DIFFERS[/]";
    }

    private static Table BuildSubrecordComparisonTable(List<AnalyzerSubrecordInfo> xboxSubs,
        List<AnalyzerSubrecordInfo> pcSubs)
    {
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

                var isVhgt = sig.Equals("VHGT", StringComparison.OrdinalIgnoreCase);
                if (isVhgt)
                {
                    if (!VhgtEquals(xboxSub.Data, pcSub.Data)) allIdentical = false;
                }
                else if (!xboxSub.Data.AsSpan().SequenceEqual(pcSub.Data))
                {
                    allIdentical = false;
                }

                var landTransformed = ApplyLandTransform(sig, xboxSub.Data);
                if (landTransformed == null ||
                    (isVhgt ? !VhgtEquals(landTransformed, pcSub.Data) : !landTransformed.AsSpan().SequenceEqual(pcSub.Data)))
                    allLandTransformed = false;

                if (!IsEndianSwapped4(xboxSub.Data, pcSub.Data))
                    allEndianSwapped = false;
            }

            var status = GetSubrecordStatus(allIdentical, allLandTransformed, allEndianSwapped);
            subTable.AddRow(
                sig,
                $"{xboxList.Count}×",
                $"{totalXboxSize:N0}",
                status
            );
        }

        return subTable;
    }

    private static bool IsEndianSwapped4(byte[] xboxData, byte[] pcData)
    {
        if (xboxData.Length != pcData.Length || xboxData.Length % 4 != 0) return false;

        var swapped = new byte[xboxData.Length];
        for (var j = 0; j < xboxData.Length; j += 4)
        {
            swapped[j] = xboxData[j + 3];
            swapped[j + 1] = xboxData[j + 2];
            swapped[j + 2] = xboxData[j + 1];
            swapped[j + 3] = xboxData[j];
        }

        return swapped.AsSpan().SequenceEqual(pcData);
    }
}