using Spectre.Console;
using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Cell search commands: find CELL records by name or grid coordinates.
/// </summary>
internal static class DumpCommandsCellSearch
{
    internal static int FindCells(string filePath, string pattern, int limit)
    {
        var esm = EsmFileLoader.Load(filePath);
        if (esm == null)
        {
            return 1;
        }

        var term = pattern.Trim();
        if (term.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Pattern must not be empty");
            return 1;
        }

        var matches = 0;
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("FormID")
            .AddColumn("Offset")
            .AddColumn("EDID")
            .AddColumn("FULL")
            .AddColumn("Grid");

        List<AnalyzerRecordInfo> cells = [];

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning CELL records...",
                _ => { cells = EsmRecordParser.ScanForRecordType(esm.Data, esm.IsBigEndian, "CELL"); });

        foreach (var cell in cells)
        {
            var recordData = EsmHelpers.GetRecordData(esm.Data, cell, esm.IsBigEndian);
            var subs = EsmRecordParser.ParseSubrecords(recordData, esm.IsBigEndian);

            var edid = GetStringSubrecord(subs, "EDID");
            var full = GetStringSubrecord(subs, "FULL");

            if (!ContainsIgnoreCase(edid, term) && !ContainsIgnoreCase(full, term))
            {
                continue;
            }

            var gridText = GetCellGridText(subs, esm.IsBigEndian);

            _ = table.AddRow(
                $"0x{cell.FormId:X8}",
                $"0x{cell.Offset:X8}",
                edid ?? string.Empty,
                full ?? string.Empty,
                gridText);

            matches++;
            if (limit > 0 && matches >= limit)
            {
                break;
            }
        }

        AnsiConsole.MarkupLine(
            $"Found [cyan]{matches}[/] CELL matches for '{Markup.Escape(term)}' in {Path.GetFileName(filePath)}");
        if (matches > 0)
        {
            AnsiConsole.Write(table);
        }

        return 0;
    }

    internal static int FindCellsByGrid(string filePath, int targetX, int targetY, int limit)
    {
        var esm = EsmFileLoader.Load(filePath);
        if (esm == null)
        {
            return 1;
        }

        var matches = 0;
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("FormID")
            .AddColumn("Offset")
            .AddColumn("EDID")
            .AddColumn("FULL")
            .AddColumn("Grid");

        List<AnalyzerRecordInfo> cells = [];

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning CELL records...",
                _ => { cells = EsmRecordParser.ScanForRecordType(esm.Data, esm.IsBigEndian, "CELL"); });

        foreach (var cell in cells)
        {
            var recordData = EsmHelpers.GetRecordData(esm.Data, cell, esm.IsBigEndian);
            var subs = EsmRecordParser.ParseSubrecords(recordData, esm.IsBigEndian);

            var gridText = GetCellGridText(subs, esm.IsBigEndian);
            if (string.IsNullOrEmpty(gridText))
            {
                continue;
            }

            if (!TryParseGrid(gridText, out var gridX, out var gridY))
            {
                continue;
            }

            if (gridX != targetX || gridY != targetY)
            {
                continue;
            }

            var edid = GetStringSubrecord(subs, "EDID");
            var full = GetStringSubrecord(subs, "FULL");

            _ = table.AddRow(
                $"0x{cell.FormId:X8}",
                $"0x{cell.Offset:X8}",
                edid ?? string.Empty,
                full ?? string.Empty,
                gridText);

            matches++;
            if (limit > 0 && matches >= limit)
            {
                break;
            }
        }

        AnsiConsole.MarkupLine(
            $"Found [cyan]{matches}[/] CELL records at {targetX},{targetY} in {Path.GetFileName(filePath)}");
        if (matches > 0)
        {
            AnsiConsole.Write(table);
        }

        return 0;
    }

    private static string? GetStringSubrecord(List<AnalyzerSubrecordInfo> subrecords, string signature)
    {
        var sub = subrecords.FirstOrDefault(s => s.Signature == signature);
        if (sub == null || sub.Data.Length == 0)
        {
            return null;
        }

        var text = Encoding.ASCII.GetString(sub.Data);
        var nullIndex = text.IndexOf('\0');
        return nullIndex >= 0 ? text[..nullIndex] : text;
    }

    private static string GetCellGridText(List<AnalyzerSubrecordInfo> subrecords, bool bigEndian)
    {
        var sub = subrecords.FirstOrDefault(s => s.Signature == "XCLC");
        if (sub == null || sub.Data.Length < 8)
        {
            return "";
        }

        var x = ReadInt32(sub.Data, 0, bigEndian);
        var y = ReadInt32(sub.Data, 4, bigEndian);
        return $"{x},{y}";
    }

    private static bool TryParseGrid(string gridText, out int x, out int y)
    {
        x = 0;
        y = 0;

        var parts = gridText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y);
    }

    private static int ReadInt32(byte[] data, int offset, bool bigEndian)
    {
        return offset + 4 > data.Length
            ? 0
            : bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static bool ContainsIgnoreCase(string? value, string term)
    {
        return !string.IsNullOrEmpty(value) &&
               value.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}
