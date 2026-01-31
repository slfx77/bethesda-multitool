using System.CommandLine;
using NifAnalyzer.Models;
using NifAnalyzer.Parsers;
using Spectre.Console;
using static FalloutXbox360Utils.Core.Formats.Nif.Conversion.NifEndianUtils;

namespace NifAnalyzer.Commands;

/// <summary>
///     Commands for parsing NiNode/BSFadeNode and related blocks field-by-field.
/// </summary>
internal static class NodeCommands
{
    /// <summary>
    ///     Create the "node" command for detailed NiNode/BSFadeNode field parsing.
    /// </summary>
    public static Command CreateNodeCommand()
    {
        var command = new Command("node", "Parse NiNode/BSFadeNode fields according to nif.xml spec");
        var fileArg = new Argument<string>("file") { Description = "NIF file path" };
        var blockArg = new Argument<int>("block") { Description = "Block index" };
        command.Arguments.Add(fileArg);
        command.Arguments.Add(blockArg);
        command.SetAction(parseResult => ParseNode(parseResult.GetValue(fileArg), parseResult.GetValue(blockArg)));
        return command;
    }

    /// <summary>
    ///     Create the "nodecompare" command for comparing NiNode fields between two files.
    /// </summary>
    public static Command CreateNodeCompareCommand()
    {
        var command = new Command("nodecompare", "Compare NiNode/BSFadeNode fields between Xbox and PC files");
        var file1Arg = new Argument<string>("xbox-file") { Description = "Xbox 360 NIF file path" };
        var file2Arg = new Argument<string>("other-file") { Description = "PC/Converted NIF file path" };
        var block1Arg = new Argument<int>("xbox-block") { Description = "Block index in Xbox file" };
        var block2Arg = new Argument<int>("other-block") { Description = "Block index in other file" };
        command.Arguments.Add(file1Arg);
        command.Arguments.Add(file2Arg);
        command.Arguments.Add(block1Arg);
        command.Arguments.Add(block2Arg);
        command.SetAction(parseResult => CompareNode(
            parseResult.GetValue(file1Arg), parseResult.GetValue(file2Arg),
            parseResult.GetValue(block1Arg), parseResult.GetValue(block2Arg)));
        return command;
    }

    /// <summary>
    ///     Parse and display NiNode/BSFadeNode fields according to nif.xml spec.
    ///     For BS Version > 26, Flags is uint (4 bytes), not ushort (2 bytes).
    /// </summary>
    private static void ParseNode(string path, int blockIndex)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        if (blockIndex < 0 || blockIndex >= nif.NumBlocks)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Block index {blockIndex} out of range (0-{nif.NumBlocks - 1})");
            return;
        }

        var typeName = nif.GetBlockTypeName(blockIndex);
        var offset = nif.GetBlockOffset(blockIndex);
        var size = (int)nif.BlockSizes[blockIndex];
        var be = nif.IsBigEndian;

        var infoTable = new Table().Border(TableBorder.Rounded);
        infoTable.AddColumn("Property");
        infoTable.AddColumn("Value");
        infoTable.AddRow("Block", $"{blockIndex}: [cyan]{Markup.Escape(typeName)}[/]");
        infoTable.AddRow("Offset", $"0x{offset:X4}");
        infoTable.AddRow("Size", $"{size} bytes");
        infoTable.AddRow("Endian", be ? "[yellow]Big (Xbox 360)[/]" : "[green]Little (PC)[/]");
        infoTable.AddRow("BS Version", nif.BsVersion.ToString());
        AnsiConsole.Write(infoTable);
        AnsiConsole.WriteLine();

        var pos = offset;
        var end = offset + size;

        // === NiObjectNET ===
        AnsiConsole.Write(new Rule("[bold]NiObjectNET[/]").LeftJustified());

        if (pos + 4 > end)
        {
            Error("Truncated at Name");
            return;
        }

        var nameIdx = (int)ReadUInt32(data, pos, be);
        var nameStr = nameIdx >= 0 && nameIdx < nif.Strings.Count ? nif.Strings[nameIdx] : "(none)";
        PrintField("Name", pos, 4, $"{nameIdx} = \"{Markup.Escape(nameStr)}\"");
        pos += 4;

        if (pos + 4 > end)
        {
            Error("Truncated at NumExtraData");
            return;
        }

        var numExtraData = ReadUInt32(data, pos, be);
        PrintField("Num Extra Data", pos, 4, numExtraData.ToString());
        pos += 4;

        if (numExtraData > 0)
        {
            AnsiConsole.MarkupLine($"  [dim]Extra Data Refs ({numExtraData}):[/]");
            for (var i = 0; i < numExtraData && pos + 4 <= end; i++)
            {
                var refIdx = (int)ReadUInt32(data, pos, be);
                AnsiConsole.MarkupLine($"    [dim][{i}] 0x{pos:X4}: {refIdx}[/]");
                pos += 4;
            }
        }

        if (pos + 4 > end)
        {
            Error("Truncated at Controller");
            return;
        }

        var controllerRef = (int)ReadUInt32(data, pos, be);
        PrintField("Controller", pos, 4, controllerRef.ToString());
        pos += 4;

        // === NiAVObject ===
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]NiAVObject[/]").LeftJustified());

        // Flags: uint for BSVER > 26, ushort for BSVER <= 26
        var flagsIsUInt = nif.BsVersion > 26;
        if (flagsIsUInt)
        {
            if (pos + 4 > end)
            {
                Error("Truncated at Flags");
                return;
            }

            var flags = ReadUInt32(data, pos, be);
            PrintField("Flags (uint)", pos, 4, $"0x{flags:X8}");
            pos += 4;
        }
        else
        {
            if (pos + 2 > end)
            {
                Error("Truncated at Flags");
                return;
            }

            var flags = ReadUInt16(data, pos, be);
            PrintField("Flags (ushort)", pos, 2, $"0x{flags:X4}");
            pos += 2;
        }

        // Translation (Vector3 - 12 bytes)
        if (pos + 12 > end)
        {
            Error("Truncated at Translation");
            return;
        }

        var tx = ReadFloat(data.AsSpan(), pos, be);
        var ty = ReadFloat(data.AsSpan(), pos + 4, be);
        var tz = ReadFloat(data.AsSpan(), pos + 8, be);
        PrintField("Translation", pos, 12, $"({tx:F4}, {ty:F4}, {tz:F4})");
        pos += 12;

        // Rotation (Matrix33 - 36 bytes)
        if (pos + 36 > end)
        {
            Error("Truncated at Rotation");
            return;
        }

        AnsiConsole.MarkupLine($"  [dim]0x{pos:X4} [36] Rotation (Matrix33):[/]");
        for (var row = 0; row < 3; row++)
        {
            var m0 = ReadFloat(data.AsSpan(), pos + row * 12, be);
            var m1 = ReadFloat(data.AsSpan(), pos + row * 12 + 4, be);
            var m2 = ReadFloat(data.AsSpan(), pos + row * 12 + 8, be);
            AnsiConsole.MarkupLine($"           [dim][{m0,10:F4} {m1,10:F4} {m2,10:F4}][/]");
        }

        pos += 36;

        // Scale (float - 4 bytes)
        if (pos + 4 > end)
        {
            Error("Truncated at Scale");
            return;
        }

        var scale = ReadFloat(data.AsSpan(), pos, be);
        PrintField("Scale", pos, 4, $"{scale:F4}");
        pos += 4;

        // For BS <= FO3 (BS Version <= 34): Num Properties + Properties array
        var hasProperties = nif.BsVersion <= 34;
        if (hasProperties)
        {
            if (pos + 4 > end)
            {
                Error("Truncated at Num Properties");
                return;
            }

            var numProperties = ReadUInt32(data, pos, be);
            PrintField("Num Properties", pos, 4, numProperties.ToString());
            pos += 4;

            if (numProperties > 0)
            {
                AnsiConsole.MarkupLine($"  [dim]Property Refs ({numProperties}):[/]");
                for (var i = 0; i < numProperties && pos + 4 <= end; i++)
                {
                    var refIdx = (int)ReadUInt32(data, pos, be);
                    AnsiConsole.MarkupLine($"    [dim][{i}] 0x{pos:X4}: {refIdx}[/]");
                    pos += 4;
                }
            }
        }

        // Collision Object
        if (pos + 4 > end)
        {
            Error("Truncated at Collision Object");
            return;
        }

        var collisionRef = (int)ReadUInt32(data, pos, be);
        PrintField("Collision Object", pos, 4, collisionRef.ToString());
        pos += 4;

        // === NiNode ===
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]NiNode[/]").LeftJustified());

        // Children array
        if (pos + 4 > end)
        {
            Error("Truncated at Num Children");
            return;
        }

        var numChildren = ReadUInt32(data, pos, be);
        PrintField("Num Children", pos, 4, numChildren.ToString());
        pos += 4;

        if (numChildren > 0 && numChildren < 1000)
        {
            AnsiConsole.MarkupLine($"  [dim]Children Refs ({numChildren}):[/]");
            for (var i = 0; i < numChildren && pos + 4 <= end; i++)
            {
                var refIdx = (int)ReadUInt32(data, pos, be);
                AnsiConsole.MarkupLine($"    [dim][{i}] 0x{pos:X4}: {refIdx}[/]");
                pos += 4;
            }
        }

        // Effects array
        if (pos + 4 > end)
        {
            Error("Truncated at Num Effects");
            return;
        }

        var numEffects = ReadUInt32(data, pos, be);
        PrintField("Num Effects", pos, 4, numEffects.ToString());
        pos += 4;

        if (numEffects > 0 && numEffects < 1000)
        {
            AnsiConsole.MarkupLine($"  [dim]Effect Refs ({numEffects}):[/]");
            for (var i = 0; i < numEffects && pos + 4 <= end; i++)
            {
                var refIdx = (int)ReadUInt32(data, pos, be);
                AnsiConsole.MarkupLine($"    [dim][{i}] 0x{pos:X4}: {refIdx}[/]");
                pos += 4;
            }
        }

        AnsiConsole.WriteLine();
        var consumed = pos - offset;
        if (consumed == size)
        {
            AnsiConsole.MarkupLine($"[green]Bytes consumed: {consumed} / {size}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Bytes consumed: {consumed} / {size}[/]");
            AnsiConsole.MarkupLine($"[yellow]WARNING: Size mismatch! Expected {size}, consumed {consumed}[/]");
        }
    }

    /// <summary>
    ///     Compare NiNode/BSFadeNode fields between Xbox and PC/Converted files.
    /// </summary>
    private static void CompareNode(string xboxPath, string otherPath, int xboxBlock, int otherBlock)
    {
        var xboxData = File.ReadAllBytes(xboxPath);
        var otherData = File.ReadAllBytes(otherPath);

        var xbox = NifParser.Parse(xboxData);
        var other = NifParser.Parse(otherData);

        var xTypeName = xbox.GetBlockTypeName(xboxBlock);
        var oTypeName = other.GetBlockTypeName(otherBlock);

        AnsiConsole.Write(new Rule("[bold]Node Comparison[/]").LeftJustified());

        var infoTable = new Table().Border(TableBorder.Rounded);
        infoTable.AddColumn("");
        infoTable.AddColumn("File");
        infoTable.AddColumn("Block");
        infoTable.AddColumn("Offset");
        infoTable.AddRow("Xbox", Markup.Escape(Path.GetFileName(xboxPath)), $"{xboxBlock} ({Markup.Escape(xTypeName)})",
            $"0x{xbox.GetBlockOffset(xboxBlock):X4}");
        infoTable.AddRow("Other", Markup.Escape(Path.GetFileName(otherPath)),
            $"{otherBlock} ({Markup.Escape(oTypeName)})", $"0x{other.GetBlockOffset(otherBlock):X4}");
        AnsiConsole.Write(infoTable);
        AnsiConsole.WriteLine();

        // Parse both and compare key fields
        var xFields = ParseNodeFields(xboxData, xbox, xboxBlock);
        var oFields = ParseNodeFields(otherData, other, otherBlock);

        var compareTable = new Table().Border(TableBorder.Simple);
        compareTable.AddColumn("Field");
        compareTable.AddColumn("Xbox");
        compareTable.AddColumn("Other");
        compareTable.AddColumn("Match");

        foreach (var key in xFields.Keys)
        {
            var xVal = xFields.GetValueOrDefault(key, "N/A");
            var oVal = oFields.GetValueOrDefault(key, "N/A");
            var match = xVal == oVal;
            compareTable.AddRow(
                key,
                Markup.Escape(xVal),
                Markup.Escape(oVal),
                match ? "[green]✓[/]" : "[red]✗ MISMATCH[/]");
        }

        AnsiConsole.Write(compareTable);
    }

    private static Dictionary<string, string> ParseNodeFields(byte[] data, NifInfo nif, int blockIndex)
    {
        var fields = new Dictionary<string, string>();
        var offset = nif.GetBlockOffset(blockIndex);
        var size = (int)nif.BlockSizes[blockIndex];
        var be = nif.IsBigEndian;
        var pos = offset;
        var end = offset + size;

        // NiObjectNET
        if (pos + 4 <= end)
        {
            var nameIdx = (int)ReadUInt32(data, pos, be);
            var nameStr = nameIdx >= 0 && nameIdx < nif.Strings.Count ? nif.Strings[nameIdx] : "";
            fields["Name"] = $"{nameIdx} ({nameStr})";
            pos += 4;
        }

        if (pos + 4 <= end)
        {
            var numExtraData = ReadUInt32(data, pos, be);
            fields["NumExtraData"] = numExtraData.ToString();
            pos += 4;
            pos += (int)numExtraData * 4; // Skip extra data refs
        }

        if (pos + 4 <= end)
        {
            fields["Controller"] = ((int)ReadUInt32(data, pos, be)).ToString();
            pos += 4;
        }

        // NiAVObject - Flags
        var flagsIsUInt = nif.BsVersion > 26;
        if (flagsIsUInt && pos + 4 <= end)
        {
            var flags = ReadUInt32(data, pos, be);
            fields["Flags"] = $"0x{flags:X8}";
            pos += 4;
        }
        else if (!flagsIsUInt && pos + 2 <= end)
        {
            var flags = ReadUInt16(data, pos, be);
            fields["Flags"] = $"0x{flags:X4}";
            pos += 2;
        }

        // Translation
        if (pos + 12 <= end)
        {
            var tx = ReadFloat(data.AsSpan(), pos, be);
            var ty = ReadFloat(data.AsSpan(), pos + 4, be);
            var tz = ReadFloat(data.AsSpan(), pos + 8, be);
            fields["Translation"] = $"({tx:F2},{ty:F2},{tz:F2})";
            pos += 12;
        }

        // Skip Rotation (36 bytes) and Scale (4 bytes)
        pos += 40;

        // Properties (if BS <= 34)
        if (nif.BsVersion <= 34 && pos + 4 <= end)
        {
            var numProperties = ReadUInt32(data, pos, be);
            fields["NumProperties"] = numProperties.ToString();
            pos += 4;
            pos += (int)numProperties * 4;
        }

        // Collision
        if (pos + 4 <= end)
        {
            fields["Collision"] = ((int)ReadUInt32(data, pos, be)).ToString();
            pos += 4;
        }

        // NiNode
        if (pos + 4 <= end)
        {
            var numChildren = ReadUInt32(data, pos, be);
            fields["NumChildren"] = numChildren.ToString();
            pos += 4;
            pos += (int)Math.Min(numChildren, 100) * 4;
        }

        if (pos + 4 <= end) fields["NumEffects"] = ReadUInt32(data, pos, be).ToString();

        return fields;
    }

    private static void PrintField(string name, int offset, int size, string value)
    {
        AnsiConsole.MarkupLine($"  [dim]0x{offset:X4} [{size,2}][/] {name}: {value}");
    }

    private static void Error(string msg)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {msg}");
    }
}