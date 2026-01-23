using System.Text;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using Xbox360MemoryCarver.Core.Utils;

namespace EsmAnalyzer.Commands;

public static partial class DiffCommands
{
    private static int DiffHeader(string xboxPath, string pcPath)
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

        AnsiConsole.MarkupLine("[bold cyan]ESM Header Comparison[/]");
        AnsiConsole.MarkupLine(
            $"Xbox 360: {Path.GetFileName(xboxPath)} ({(xboxBigEndian ? "Big-endian" : "Little-endian")})");
        AnsiConsole.MarkupLine(
            $"PC:       {Path.GetFileName(pcPath)} ({(pcBigEndian ? "Big-endian" : "Little-endian")})");
        AnsiConsole.WriteLine();

        // === Main Record Header (24 bytes) ===
        AnsiConsole.MarkupLine("[bold yellow]═══ TES4 Record Header (24 bytes) ═══[/]");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Offset[/]")
            .AddColumn("[bold]Field[/]")
            .AddColumn("[bold]Size[/]")
            .AddColumn("[bold]Xbox 360 (raw)[/]")
            .AddColumn("[bold]Xbox 360 (value)[/]")
            .AddColumn("[bold]PC (raw)[/]")
            .AddColumn("[bold]PC (value)[/]")
            .AddColumn("[bold]Status[/]");

        // Signature (4 bytes) - reversed on Xbox
        var xboxSig = Encoding.ASCII.GetString(xboxData, 0, 4);
        var pcSig = Encoding.ASCII.GetString(pcData, 0, 4);
        var xboxSigReversed = new string(xboxSig.Reverse().ToArray());
        table.AddRow(
            "0x00", "Signature", "4",
            FormatBytes(xboxData, 0, 4), $"'{xboxSig}' → '{xboxSigReversed}'",
            FormatBytes(pcData, 0, 4), $"'{pcSig}'",
            xboxSigReversed == pcSig ? "[green]MATCH[/]" : "[red]DIFFER[/]"
        );

        // Data Size (4 bytes)
        var xboxSize = BinaryUtils.ReadUInt32BE(xboxData.AsSpan(4));
        var pcSize = BinaryUtils.ReadUInt32LE(pcData.AsSpan(4));
        table.AddRow(
            "0x04", "DataSize", "4",
            FormatBytes(xboxData, 4, 4), xboxSize.ToString("N0"),
            FormatBytes(pcData, 4, 4), pcSize.ToString("N0"),
            xboxSize == pcSize ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        // Flags (4 bytes)
        var xboxFlags = BinaryUtils.ReadUInt32BE(xboxData.AsSpan(8));
        var pcFlags = BinaryUtils.ReadUInt32LE(pcData.AsSpan(8));
        table.AddRow(
            "0x08", "Flags", "4",
            FormatBytes(xboxData, 8, 4), $"0x{xboxFlags:X8}",
            FormatBytes(pcData, 8, 4), $"0x{pcFlags:X8}",
            xboxFlags == pcFlags ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        // FormID (4 bytes)
        var xboxFormId = BinaryUtils.ReadUInt32BE(xboxData.AsSpan(12));
        var pcFormId = BinaryUtils.ReadUInt32LE(pcData.AsSpan(12));
        table.AddRow(
            "0x0C", "FormID", "4",
            FormatBytes(xboxData, 12, 4), $"0x{xboxFormId:X8}",
            FormatBytes(pcData, 12, 4), $"0x{pcFormId:X8}",
            xboxFormId == pcFormId ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        // Revision (4 bytes)
        var xboxRev = BinaryUtils.ReadUInt32BE(xboxData.AsSpan(16));
        var pcRev = BinaryUtils.ReadUInt32LE(pcData.AsSpan(16));
        table.AddRow(
            "0x10", "Revision", "4",
            FormatBytes(xboxData, 16, 4), $"0x{xboxRev:X8}",
            FormatBytes(pcData, 16, 4), $"0x{pcRev:X8}",
            xboxRev == pcRev ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        // Version (2 bytes)
        var xboxVer = BinaryUtils.ReadUInt16BE(xboxData.AsSpan(20));
        var pcVer = BinaryUtils.ReadUInt16LE(pcData.AsSpan(20));
        table.AddRow(
            "0x14", "Version", "2",
            FormatBytes(xboxData, 20, 2), xboxVer.ToString(),
            FormatBytes(pcData, 20, 2), pcVer.ToString(),
            xboxVer == pcVer ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        // Unknown (2 bytes)
        var xboxUnk = BinaryUtils.ReadUInt16BE(xboxData.AsSpan(22));
        var pcUnk = BinaryUtils.ReadUInt16LE(pcData.AsSpan(22));
        table.AddRow(
            "0x16", "Unknown", "2",
            FormatBytes(xboxData, 22, 2), xboxUnk.ToString(),
            FormatBytes(pcData, 22, 2), pcUnk.ToString(),
            xboxUnk == pcUnk ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
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
            .AddColumn("[bold]Xbox 360[/]")
            .AddColumn("[bold]PC[/]");

        AddFlagRow(flagTable, 0, 0x00000001, "ESM (Master)", xboxFlags, pcFlags);
        AddFlagRow(flagTable, 4, 0x00000010, "Xbox-specific?", xboxFlags, pcFlags);
        AddFlagRow(flagTable, 7, 0x00000080, "Localized", xboxFlags, pcFlags);
        AddFlagRow(flagTable, 18, 0x00040000, "Compressed", xboxFlags, pcFlags);

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
            .AddColumn("[bold]Xbox 360 (raw)[/]")
            .AddColumn("[bold]Xbox 360 (value)[/]")
            .AddColumn("[bold]PC (raw)[/]")
            .AddColumn("[bold]PC (value)[/]")
            .AddColumn("[bold]Status[/]");

        const int hedrOffset = 24; // After record header

        // Subrecord signature (4 bytes)
        var xboxHedrSig = Encoding.ASCII.GetString(xboxData, hedrOffset, 4);
        var pcHedrSig = Encoding.ASCII.GetString(pcData, hedrOffset, 4);
        var xboxHedrSigRev = new string(xboxHedrSig.Reverse().ToArray());
        hedrTable.AddRow(
            "0x18", "Signature", "4",
            FormatBytes(xboxData, hedrOffset, 4), $"'{xboxHedrSig}' → '{xboxHedrSigRev}'",
            FormatBytes(pcData, hedrOffset, 4), $"'{pcHedrSig}'",
            xboxHedrSigRev == pcHedrSig ? "[green]MATCH[/]" : "[red]DIFFER[/]"
        );

        // Subrecord size (2 bytes)
        var xboxHedrSize = BinaryUtils.ReadUInt16BE(xboxData.AsSpan(hedrOffset + 4));
        var pcHedrSize = BinaryUtils.ReadUInt16LE(pcData.AsSpan(hedrOffset + 4));
        hedrTable.AddRow(
            "0x1C", "Size", "2",
            FormatBytes(xboxData, hedrOffset + 4, 2), xboxHedrSize.ToString(),
            FormatBytes(pcData, hedrOffset + 4, 2), pcHedrSize.ToString(),
            xboxHedrSize == pcHedrSize ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        // HEDR data starts at offset 30
        const int hedrDataOffset = hedrOffset + 6;

        // Version (float, 4 bytes)
        var xboxVersion =
            BitConverter.ToSingle(BitConverter.GetBytes(BinaryUtils.ReadUInt32BE(xboxData.AsSpan(hedrDataOffset))), 0);
        var pcVersion = BitConverter.ToSingle(pcData, hedrDataOffset);
        hedrTable.AddRow(
            "0x1E", "Version", "4",
            FormatBytes(xboxData, hedrDataOffset, 4), xboxVersion.ToString("F2"),
            FormatBytes(pcData, hedrDataOffset, 4), pcVersion.ToString("F2"),
            Math.Abs(xboxVersion - pcVersion) < 0.01f ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        // NumRecords (int32, 4 bytes)
        var xboxNumRec = BinaryUtils.ReadUInt32BE(xboxData.AsSpan(hedrDataOffset + 4));
        var pcNumRec = BinaryUtils.ReadUInt32LE(pcData.AsSpan(hedrDataOffset + 4));
        hedrTable.AddRow(
            "0x22", "NumRecords", "4",
            FormatBytes(xboxData, hedrDataOffset + 4, 4), xboxNumRec.ToString("N0"),
            FormatBytes(pcData, hedrDataOffset + 4, 4), pcNumRec.ToString("N0"),
            xboxNumRec == pcNumRec ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
        );

        // NextObjectId (uint32, 4 bytes)
        var xboxNextId = BinaryUtils.ReadUInt32BE(xboxData.AsSpan(hedrDataOffset + 8));
        var pcNextId = BinaryUtils.ReadUInt32LE(pcData.AsSpan(hedrDataOffset + 8));
        hedrTable.AddRow(
            "0x26", "NextObjectId", "4",
            FormatBytes(xboxData, hedrDataOffset + 8, 4), $"0x{xboxNextId:X8}",
            FormatBytes(pcData, hedrDataOffset + 8, 4), $"0x{pcNextId:X8}",
            xboxNextId == pcNextId ? "[green]MATCH[/]" : "[yellow]DIFFER[/]"
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
