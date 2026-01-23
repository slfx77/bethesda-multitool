using System.CommandLine;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Utils;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for NAVI/NVMI comparison.
/// </summary>
public static class NaviCommands
{
    public static Command CreateCompareNaviCommand()
    {
        var leftArg = new Argument<string>("left") { Description = "First ESM file (e.g., converted)" };
        var rightArg = new Argument<string>("right") { Description = "Second ESM file (e.g., PC reference)" };
        var limitOpt = new Option<int>("-l", "--limit")
            { Description = "Maximum mismatches to display (0 = unlimited)", DefaultValueFactory = _ => 50 };

        var command = new Command("compare-navi", "Compare NAVI NVMI entries by Navmesh FormID")
        {
            leftArg,
            rightArg,
            limitOpt
        };

        command.SetAction(parseResult => CompareNavi(
            parseResult.GetValue(leftArg)!,
            parseResult.GetValue(rightArg)!,
            parseResult.GetValue(limitOpt)));

        return command;
    }

    public static Command CreateDumpNvmiCommand()
    {
        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var formIdArg = new Argument<string>("formid") { Description = "Navmesh FormID (hex, e.g., 0x000E605D)" };
        var hexOpt = new Option<bool>("-x", "--hex") { Description = "Show raw NVMI hex data" };

        var command = new Command("dump-nvmi", "Dump parsed NVMI fields for a Navmesh FormID")
        {
            fileArg,
            formIdArg,
            hexOpt
        };

        command.SetAction(parseResult => DumpNvmi(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(formIdArg)!,
            parseResult.GetValue(hexOpt)));

        return command;
    }

    private static int CompareNavi(string leftPath, string rightPath, int limit)
    {
        var leftEsm = EsmFileLoader.Load(leftPath);
        if (leftEsm == null) return 1;

        var rightEsm = EsmFileLoader.Load(rightPath);
        if (rightEsm == null) return 1;

        var leftMap = ExtractNvmiMap(leftEsm.Data, leftEsm.IsBigEndian);
        var rightMap = ExtractNvmiMap(rightEsm.Data, rightEsm.IsBigEndian);

        var allKeys = new SortedSet<uint>(leftMap.Keys);
        allKeys.UnionWith(rightMap.Keys);

        var missingLeft = 0;
        var missingRight = 0;
        var sizeDiff = 0;
        var contentDiff = 0;
        var identical = 0;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("FormID")
            .AddColumn("Left Len")
            .AddColumn("Right Len")
            .AddColumn("Status");

        var shown = 0;
        foreach (var key in allKeys)
        {
            leftMap.TryGetValue(key, out var left);
            rightMap.TryGetValue(key, out var right);

            if (left == null)
            {
                missingLeft++;
                if (ShouldShow(limit, ref shown))
                    table.AddRow($"0x{key:X8}", "-", right!.Length.ToString(), "Missing in left");
                continue;
            }

            if (right == null)
            {
                missingRight++;
                if (ShouldShow(limit, ref shown))
                    table.AddRow($"0x{key:X8}", left.Length.ToString(), "-", "Missing in right");
                continue;
            }

            if (left.Length != right.Length)
            {
                sizeDiff++;
                if (ShouldShow(limit, ref shown))
                    table.AddRow($"0x{key:X8}", left.Length.ToString(), right.Length.ToString(), "Size differs");
                continue;
            }

            if (!left.AsSpan().SequenceEqual(right))
            {
                contentDiff++;
                if (ShouldShow(limit, ref shown))
                    table.AddRow($"0x{key:X8}", left.Length.ToString(), right.Length.ToString(), "Content differs");
                continue;
            }

            identical++;
        }

        AnsiConsole.MarkupLine("[cyan]NAVI/NVMI Comparison Summary[/]");
        AnsiConsole.MarkupLine($"Left NVMI: [green]{leftMap.Count}[/], Right NVMI: [green]{rightMap.Count}[/]");
        AnsiConsole.MarkupLine($"Identical: [green]{identical}[/]");
        AnsiConsole.MarkupLine($"Size differs: [yellow]{sizeDiff}[/]");
        AnsiConsole.MarkupLine($"Content differs: [yellow]{contentDiff}[/]");
        AnsiConsole.MarkupLine($"Missing in left: [red]{missingLeft}[/]");
        AnsiConsole.MarkupLine($"Missing in right: [red]{missingRight}[/]");
        AnsiConsole.WriteLine();

        if (shown > 0)
            AnsiConsole.Write(table);
        else
            AnsiConsole.MarkupLine("No mismatches to display.");

        return 0;
    }

    private static Dictionary<uint, byte[]> ExtractNvmiMap(byte[] fileData, bool bigEndian)
    {
        var map = new Dictionary<uint, byte[]>();

        var naviRecords = EsmHelpers.ScanForRecordType(fileData, bigEndian, "NAVI");
        foreach (var navi in naviRecords)
        {
            var recordData = EsmHelpers.GetRecordData(fileData, navi, bigEndian);
            var subrecords = EsmHelpers.ParseSubrecords(recordData, bigEndian);

            foreach (var sub in subrecords)
            {
                if (sub.Signature != "NVMI" || sub.Data.Length < 8)
                    continue;

                var navmeshId = bigEndian
                    ? BinaryUtils.ReadUInt32BE(sub.Data.AsSpan(4))
                    : BinaryUtils.ReadUInt32LE(sub.Data.AsSpan(4));

                if (!map.ContainsKey(navmeshId)) map.Add(navmeshId, sub.Data);
            }
        }

        return map;
    }

    private static bool ShouldShow(int limit, ref int shown)
    {
        if (limit == 0 || shown < limit)
        {
            shown++;
            return true;
        }

        return false;
    }

    private static int DumpNvmi(string filePath, string formIdRaw, bool showHex)
    {
        var esm = EsmFileLoader.Load(filePath);
        if (esm == null) return 1;

        var formId = EsmFileLoader.ParseFormId(formIdRaw);
        if (!formId.HasValue)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Invalid FormID '{formIdRaw}'");
            return 1;
        }

        var map = ExtractNvmiMap(esm.Data, esm.IsBigEndian);
        if (!map.TryGetValue(formId.Value, out var data))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] NVMI for FormID 0x{formId.Value:X8} not found.");
            return 1;
        }

        AnsiConsole.MarkupLine($"[cyan]NVMI[/] FormID 0x{formId.Value:X8} ({Path.GetFileName(filePath)})");

        if (showHex)
        {
            EsmDisplayHelpers.RenderHexDump(data, 0);
            AnsiConsole.WriteLine();
        }

        if (data.Length < 32)
        {
            AnsiConsole.MarkupLine($"[red]Invalid NVMI length:[/] {data.Length}");
            return 1;
        }

        var flags = ReadUInt32(data, 0, esm.IsBigEndian);
        var navmesh = ReadUInt32(data, 4, esm.IsBigEndian);
        var location = ReadUInt32(data, 8, esm.IsBigEndian);
        var gridY = ReadInt16(data, 12, esm.IsBigEndian);
        var gridX = ReadInt16(data, 14, esm.IsBigEndian);
        var approxX = ReadFloat(data, 16, esm.IsBigEndian);
        var approxY = ReadFloat(data, 20, esm.IsBigEndian);
        var approxZ = ReadFloat(data, 24, esm.IsBigEndian);
        var preferred = ReadFloat(data, data.Length - 4, esm.IsBigEndian);

        var isIsland = (flags & 0x20) != 0;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Field")
            .AddColumn("Value");

        table.AddRow("Flags", $"0x{flags:X8}");
        table.AddRow("Navmesh", $"0x{navmesh:X8}");
        table.AddRow("Location", $"0x{location:X8}");
        table.AddRow("Grid", $"X={gridX}, Y={gridY}");
        table.AddRow("Approx", $"({approxX:F3}, {approxY:F3}, {approxZ:F3})");
        table.AddRow("Preferred %", $"{preferred:F3}");
        table.AddRow("Is Island", isIsland ? "Yes" : "No");

        if (isIsland && data.Length > 32)
        {
            var offset = 28;
            var minX = ReadFloat(data, offset, esm.IsBigEndian);
            offset += 4;
            var minY = ReadFloat(data, offset, esm.IsBigEndian);
            offset += 4;
            var minZ = ReadFloat(data, offset, esm.IsBigEndian);
            offset += 4;
            var maxX = ReadFloat(data, offset, esm.IsBigEndian);
            offset += 4;
            var maxY = ReadFloat(data, offset, esm.IsBigEndian);
            offset += 4;
            var maxZ = ReadFloat(data, offset, esm.IsBigEndian);
            offset += 4;
            var vertexCount = ReadUInt16(data, offset, esm.IsBigEndian);
            offset += 2;
            var triangleCount = ReadUInt16(data, offset, esm.IsBigEndian);

            table.AddRow("Bounds Min", $"({minX:F3}, {minY:F3}, {minZ:F3})");
            table.AddRow("Bounds Max", $"({maxX:F3}, {maxY:F3}, {maxZ:F3})");
            table.AddRow("Vertex Count", vertexCount.ToString());
            table.AddRow("Triangle Count", triangleCount.ToString());
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static ushort ReadUInt16(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian ? BinaryUtils.ReadUInt16BE(data, offset) : BinaryUtils.ReadUInt16LE(data, offset);
    }

    private static uint ReadUInt32(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian ? BinaryUtils.ReadUInt32BE(data, offset) : BinaryUtils.ReadUInt32LE(data, offset);
    }

    private static short ReadInt16(byte[] data, int offset, bool bigEndian)
    {
        return unchecked((short)ReadUInt16(data, offset, bigEndian));
    }

    private static float ReadFloat(byte[] data, int offset, bool bigEndian)
    {
        var raw = ReadUInt32(data, offset, bigEndian);
        return BitConverter.Int32BitsToSingle(unchecked((int)raw));
    }
}
