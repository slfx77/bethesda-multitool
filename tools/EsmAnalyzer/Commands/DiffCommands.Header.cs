using Spectre.Console;
using System.Text;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Enums;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Export;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Schema;
using FalloutXbox360Utils.Core.Utils;
using static EsmAnalyzer.Helpers.DiffHelpers;

namespace EsmAnalyzer.Commands;

public static partial class DiffCommands
{
    private static int DiffHeader(string fileAPath, string fileBPath, string labelA = "Xbox 360", string labelB = "PC")
    {
        if (!File.Exists(fileAPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] {labelA} file not found: {fileAPath}");
            return 1;
        }

        if (!File.Exists(fileBPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] {labelB} file not found: {fileBPath}");
            return 1;
        }

        var dataA = File.ReadAllBytes(fileAPath);
        var dataB = File.ReadAllBytes(fileBPath);

        var bigEndianA = EsmParser.IsBigEndian(dataA);
        var bigEndianB = EsmParser.IsBigEndian(dataB);

        AnsiConsole.MarkupLine("[bold cyan]ESM Header Comparison[/]");
        AnsiConsole.MarkupLine(
            $"{labelA}: {Path.GetFileName(fileAPath)} ({(bigEndianA ? "Big-endian" : "Little-endian")})");
        AnsiConsole.MarkupLine(
            $"{labelB}: {Path.GetFileName(fileBPath)} ({(bigEndianB ? "Big-endian" : "Little-endian")})");
        AnsiConsole.WriteLine();

        // === Main Record Header (24 bytes) ===
        AnsiConsole.MarkupLine("[bold yellow]═══ TES4 Record Header (24 bytes) ═══[/]");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Offset[/]")
            .AddColumn("[bold]Field[/]")
            .AddColumn("[bold]Size[/]")
            .AddColumn($"[bold]{labelA} (raw)[/]")
            .AddColumn($"[bold]{labelA} (value)[/]")
            .AddColumn($"[bold]{labelB} (raw)[/]")
            .AddColumn($"[bold]{labelB} (value)[/]")
            .AddColumn("[bold]Status[/]");

        // Signature (4 bytes) - reversed on Xbox
        var sigA = Encoding.ASCII.GetString(dataA, 0, 4);
        var sigB = Encoding.ASCII.GetString(dataB, 0, 4);
        var sigAReversed = bigEndianA ? new string(sigA.Reverse().ToArray()) : sigA;
        table.AddRow(
            "0x00", "Signature", "4",
            FormatBytes(dataA, 0, 4), bigEndianA ? $"'{sigA}' → '{sigAReversed}'" : $"'{sigA}'",
            FormatBytes(dataB, 0, 4), $"'{sigB}'",
            sigAReversed == sigB ? "[green]MATCH[/]" : "[red]DIFFER[/]"
        );

        // Data Size (4 bytes)
        var sizeA = bigEndianA ? BinaryUtils.ReadUInt32BE(dataA.AsSpan(4)) : BinaryUtils.ReadUInt32LE(dataA.AsSpan(4));
        var sizeB = bigEndianB ? BinaryUtils.ReadUInt32BE(dataB.AsSpan(4)) : BinaryUtils.ReadUInt32LE(dataB.AsSpan(4));
        table.AddRow(
            "0x04", "DataSize", "4",
            FormatBytes(dataA, 4, 4), sizeA.ToString("N0"),
            FormatBytes(dataB, 4, 4), sizeB.ToString("N0"),
            sizeA == sizeB ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        // Flags (4 bytes)
        var flagsA = bigEndianA ? BinaryUtils.ReadUInt32BE(dataA.AsSpan(8)) : BinaryUtils.ReadUInt32LE(dataA.AsSpan(8));
        var flagsB = bigEndianB ? BinaryUtils.ReadUInt32BE(dataB.AsSpan(8)) : BinaryUtils.ReadUInt32LE(dataB.AsSpan(8));
        table.AddRow(
            "0x08", "Flags", "4",
            FormatBytes(dataA, 8, 4), $"0x{flagsA:X8}",
            FormatBytes(dataB, 8, 4), $"0x{flagsB:X8}",
            flagsA == flagsB ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        // FormID (4 bytes)
        var formIdA = bigEndianA ? BinaryUtils.ReadUInt32BE(dataA.AsSpan(12)) : BinaryUtils.ReadUInt32LE(dataA.AsSpan(12));
        var formIdB = bigEndianB ? BinaryUtils.ReadUInt32BE(dataB.AsSpan(12)) : BinaryUtils.ReadUInt32LE(dataB.AsSpan(12));
        table.AddRow(
            "0x0C", "FormID", "4",
            FormatBytes(dataA, 12, 4), $"0x{formIdA:X8}",
            FormatBytes(dataB, 12, 4), $"0x{formIdB:X8}",
            formIdA == formIdB ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        // Revision (4 bytes)
        var revA = bigEndianA ? BinaryUtils.ReadUInt32BE(dataA.AsSpan(16)) : BinaryUtils.ReadUInt32LE(dataA.AsSpan(16));
        var revB = bigEndianB ? BinaryUtils.ReadUInt32BE(dataB.AsSpan(16)) : BinaryUtils.ReadUInt32LE(dataB.AsSpan(16));
        table.AddRow(
            "0x10", "Revision", "4",
            FormatBytes(dataA, 16, 4), $"0x{revA:X8}",
            FormatBytes(dataB, 16, 4), $"0x{revB:X8}",
            revA == revB ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        // Version (2 bytes)
        var verA = bigEndianA ? BinaryUtils.ReadUInt16BE(dataA.AsSpan(20)) : BinaryUtils.ReadUInt16LE(dataA.AsSpan(20));
        var verB = bigEndianB ? BinaryUtils.ReadUInt16BE(dataB.AsSpan(20)) : BinaryUtils.ReadUInt16LE(dataB.AsSpan(20));
        table.AddRow(
            "0x14", "Version", "2",
            FormatBytes(dataA, 20, 2), verA.ToString(),
            FormatBytes(dataB, 20, 2), verB.ToString(),
            verA == verB ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        // Unknown (2 bytes)
        var unkA = bigEndianA ? BinaryUtils.ReadUInt16BE(dataA.AsSpan(22)) : BinaryUtils.ReadUInt16LE(dataA.AsSpan(22));
        var unkB = bigEndianB ? BinaryUtils.ReadUInt16BE(dataB.AsSpan(22)) : BinaryUtils.ReadUInt16LE(dataB.AsSpan(22));
        table.AddRow(
            "0x16", "Unknown", "2",
            FormatBytes(dataA, 22, 2), unkA.ToString(),
            FormatBytes(dataB, 22, 2), unkB.ToString(),
            unkA == unkB ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        // Explain flags
        AnsiConsole.MarkupLine("[bold yellow]═══ Flag Analysis ═══[/]");
        AnsiConsole.WriteLine();

        var flagTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Bit[/]")
            .AddColumn("[bold]Mask[/]")
            .AddColumn("[bold]Name[/]")
            .AddColumn($"[bold]{labelA}[/]")
            .AddColumn($"[bold]{labelB}[/]");

        AddFlagRow(flagTable, 0, 0x00000001, "ESM (Master)", flagsA, flagsB);
        AddFlagRow(flagTable, 4, 0x00000010, "Xbox-specific?", flagsA, flagsB);
        AddFlagRow(flagTable, 7, 0x00000080, "Localized", flagsA, flagsB);
        AddFlagRow(flagTable, 18, 0x00040000, "Compressed", flagsA, flagsB);

        AnsiConsole.Write(flagTable);
        AnsiConsole.WriteLine();

        // === HEDR Subrecord ===
        AnsiConsole.MarkupLine("[bold yellow]═══ HEDR Subrecord (12 bytes data) ═══[/]");
        AnsiConsole.WriteLine();

        var hedrTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Offset[/]")
            .AddColumn("[bold]Field[/]")
            .AddColumn("[bold]Size[/]")
            .AddColumn($"[bold]{labelA} (raw)[/]")
            .AddColumn($"[bold]{labelA} (value)[/]")
            .AddColumn($"[bold]{labelB} (raw)[/]")
            .AddColumn($"[bold]{labelB} (value)[/]")
            .AddColumn("[bold]Status[/]");

        const int hedrOffset = 24; // After record header

        // Subrecord signature (4 bytes)
        var hedrSigA = Encoding.ASCII.GetString(dataA, hedrOffset, 4);
        var hedrSigB = Encoding.ASCII.GetString(dataB, hedrOffset, 4);
        var hedrSigAProcessed = bigEndianA ? new string(hedrSigA.Reverse().ToArray()) : hedrSigA;
        hedrTable.AddRow(
            "0x18", "Signature", "4",
            FormatBytes(dataA, hedrOffset, 4), bigEndianA ? $"'{hedrSigA}' → '{hedrSigAProcessed}'" : $"'{hedrSigA}'",
            FormatBytes(dataB, hedrOffset, 4), $"'{hedrSigB}'",
            hedrSigAProcessed == hedrSigB ? "[green]MATCH[/]" : "[red]DIFFER[/]"
        );

        // Subrecord size (2 bytes)
        var hedrSizeA = bigEndianA ? BinaryUtils.ReadUInt16BE(dataA.AsSpan(hedrOffset + 4)) : BinaryUtils.ReadUInt16LE(dataA.AsSpan(hedrOffset + 4));
        var hedrSizeB = bigEndianB ? BinaryUtils.ReadUInt16BE(dataB.AsSpan(hedrOffset + 4)) : BinaryUtils.ReadUInt16LE(dataB.AsSpan(hedrOffset + 4));
        hedrTable.AddRow(
            "0x1C", "Size", "2",
            FormatBytes(dataA, hedrOffset + 4, 2), hedrSizeA.ToString(),
            FormatBytes(dataB, hedrOffset + 4, 2), hedrSizeB.ToString(),
            hedrSizeA == hedrSizeB ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        // HEDR data starts at offset 30
        const int hedrDataOffset = hedrOffset + 6;

        // Version (float, 4 bytes)
        var versionFloatA = bigEndianA
            ? BitConverter.ToSingle(BitConverter.GetBytes(BinaryUtils.ReadUInt32BE(dataA.AsSpan(hedrDataOffset))), 0)
            : BitConverter.ToSingle(dataA, hedrDataOffset);
        var versionFloatB = bigEndianB
            ? BitConverter.ToSingle(BitConverter.GetBytes(BinaryUtils.ReadUInt32BE(dataB.AsSpan(hedrDataOffset))), 0)
            : BitConverter.ToSingle(dataB, hedrDataOffset);
        hedrTable.AddRow(
            "0x1E", "Version", "4",
            FormatBytes(dataA, hedrDataOffset, 4), versionFloatA.ToString("F2"),
            FormatBytes(dataB, hedrDataOffset, 4), versionFloatB.ToString("F2"),
            Math.Abs(versionFloatA - versionFloatB) < 0.01f ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        // NumRecords (int32, 4 bytes)
        var numRecA = bigEndianA ? BinaryUtils.ReadUInt32BE(dataA.AsSpan(hedrDataOffset + 4)) : BinaryUtils.ReadUInt32LE(dataA.AsSpan(hedrDataOffset + 4));
        var numRecB = bigEndianB ? BinaryUtils.ReadUInt32BE(dataB.AsSpan(hedrDataOffset + 4)) : BinaryUtils.ReadUInt32LE(dataB.AsSpan(hedrDataOffset + 4));
        hedrTable.AddRow(
            "0x22", "NumRecords", "4",
            FormatBytes(dataA, hedrDataOffset + 4, 4), numRecA.ToString("N0"),
            FormatBytes(dataB, hedrDataOffset + 4, 4), numRecB.ToString("N0"),
            numRecA == numRecB ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        // NextObjectId (uint32, 4 bytes)
        var nextIdA = bigEndianA ? BinaryUtils.ReadUInt32BE(dataA.AsSpan(hedrDataOffset + 8)) : BinaryUtils.ReadUInt32LE(dataA.AsSpan(hedrDataOffset + 8));
        var nextIdB = bigEndianB ? BinaryUtils.ReadUInt32BE(dataB.AsSpan(hedrDataOffset + 8)) : BinaryUtils.ReadUInt32LE(dataB.AsSpan(hedrDataOffset + 8));
        hedrTable.AddRow(
            "0x26", "NextObjectId", "4",
            FormatBytes(dataA, hedrDataOffset + 8, 4), $"0x{nextIdA:X8}",
            FormatBytes(dataB, hedrDataOffset + 8, 4), $"0x{nextIdB:X8}",
            nextIdA == nextIdB ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        AnsiConsole.Write(hedrTable);
        AnsiConsole.WriteLine();

        // Summary
        AnsiConsole.MarkupLine("[bold yellow]═══ Conversion Notes ═══[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("To convert Xbox 360 → PC format:");
        AnsiConsole.MarkupLine("  • [cyan]Signatures[/]: Reverse 4 bytes (e.g., '4SET' → 'TES4')");
        AnsiConsole.MarkupLine("  • [cyan]2-byte integers[/]: Swap byte order");
        AnsiConsole.MarkupLine("  • [cyan]4-byte integers[/]: Swap byte order");
        AnsiConsole.MarkupLine("  • [cyan]Floats[/]: Swap byte order (IEEE 754)");
        AnsiConsole.MarkupLine("  • [cyan]Flags[/]: May need to clear Xbox-specific bit 0x10");
        AnsiConsole.MarkupLine("  • [cyan]Strings[/]: No change needed");
        AnsiConsole.MarkupLine("  • [cyan]Byte arrays[/]: No change needed");

        return 0;
    }
}