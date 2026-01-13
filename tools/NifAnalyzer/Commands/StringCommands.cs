using System.CommandLine;
using NifAnalyzer.Models;
using NifAnalyzer.Parsers;
using Spectre.Console;

namespace NifAnalyzer.Commands;

/// <summary>
///     Commands for string table and controller sequence analysis.
/// </summary>
internal static class StringCommands
{
    public static Command CreateStringsCommand()
    {
        var command = new Command("strings", "Dump NIF string table");
        var fileArg = new Argument<string>("file") { Description = "Path to NIF file" };
        command.Arguments.Add(fileArg);
        command.SetAction(parseResult => Strings(parseResult.GetValue(fileArg)));
        return command;
    }

    public static Command CreateStringCompareCommand()
    {
        var command = new Command("stringcompare", "Compare string tables between two NIF files");
        var file1Arg = new Argument<string>("file1") { Description = "First NIF file" };
        var file2Arg = new Argument<string>("file2") { Description = "Second NIF file" };
        command.Arguments.Add(file1Arg);
        command.Arguments.Add(file2Arg);
        command.SetAction(parseResult => StringCompare(parseResult.GetValue(file1Arg), parseResult.GetValue(file2Arg)));
        return command;
    }

    public static Command CreateControllerCommand()
    {
        var command = new Command("controller", "Parse NiControllerSequence details");
        var fileArg = new Argument<string>("file") { Description = "Path to NIF file" };
        var blockArg = new Argument<int>("block") { Description = "Block index of NiControllerSequence" };
        command.Arguments.Add(fileArg);
        command.Arguments.Add(blockArg);
        command.SetAction(parseResult => Controller(parseResult.GetValue(fileArg), parseResult.GetValue(blockArg)));
        return command;
    }

    public static Command CreateDiffBlockCommand()
    {
        var command = new Command("diffblock", "Byte-by-byte comparison of a block between two files");
        var file1Arg = new Argument<string>("file1") { Description = "First NIF file" };
        var file2Arg = new Argument<string>("file2") { Description = "Second NIF file" };
        var block1Arg = new Argument<int>("block1") { Description = "Block index in first file" };
        var block2Arg = new Argument<int>("block2") { Description = "Block index in second file" };
        command.Arguments.Add(file1Arg);
        command.Arguments.Add(file2Arg);
        command.Arguments.Add(block1Arg);
        command.Arguments.Add(block2Arg);
        command.SetAction(parseResult => DiffBlock(
            parseResult.GetValue(file1Arg), parseResult.GetValue(file2Arg),
            parseResult.GetValue(block1Arg), parseResult.GetValue(block2Arg)));
        return command;
    }

    public static Command CreatePaletteCommand()
    {
        var command = new Command("palette", "Parse NiDefaultAVObjectPalette to show object→block mappings");
        var fileArg = new Argument<string>("file") { Description = "Path to NIF file" };
        var blockArg = new Argument<int>("block") { Description = "Block index of NiDefaultAVObjectPalette" };
        command.Arguments.Add(fileArg);
        command.Arguments.Add(blockArg);
        command.SetAction(parseResult => Palette(parseResult.GetValue(fileArg), parseResult.GetValue(blockArg)));
        return command;
    }

    /// <summary>
    ///     Dump all strings in the NIF string table.
    /// </summary>
    private static void Strings(string path)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        AnsiConsole.MarkupLine($"[bold]File:[/] {Path.GetFileName(path)}");
        AnsiConsole.MarkupLine($"[bold]Num Strings:[/] {nif.NumStrings}");
        AnsiConsole.MarkupLine($"[bold]Max String Length:[/] {nif.MaxStringLength}");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn(new TableColumn("Idx").RightAligned());
        table.AddColumn(new TableColumn("Len").RightAligned());
        table.AddColumn("String");

        for (var i = 0; i < nif.Strings.Count; i++)
        {
            var s = nif.Strings[i];
            table.AddRow(i.ToString(), s.Length.ToString(), $"\"{Markup.Escape(s)}\"");
        }

        AnsiConsole.Write(table);
    }

    /// <summary>
    ///     Compare string tables between two NIF files.
    /// </summary>
    private static void StringCompare(string path1, string path2)
    {
        var data1 = File.ReadAllBytes(path1);
        var data2 = File.ReadAllBytes(path2);
        var nif1 = NifParser.Parse(data1);
        var nif2 = NifParser.Parse(data2);

        AnsiConsole.Write(new Rule("[bold blue]String Table Comparison[/]").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]File 1:[/] {Path.GetFileName(path1)} ([cyan]{nif1.NumStrings}[/] strings)");
        AnsiConsole.MarkupLine($"[bold]File 2:[/] {Path.GetFileName(path2)} ([cyan]{nif2.NumStrings}[/] strings)");
        AnsiConsole.WriteLine();

        var maxStrings = Math.Max(nif1.Strings.Count, nif2.Strings.Count);

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn(new TableColumn("Idx").RightAligned());
        table.AddColumn("File 1");
        table.AddColumn("File 2");
        table.AddColumn("Match");

        for (var i = 0; i < maxStrings; i++)
        {
            var s1 = i < nif1.Strings.Count ? nif1.Strings[i] : "<missing>";
            var s2 = i < nif2.Strings.Count ? nif2.Strings[i] : "<missing>";
            var match = s1 == s2 ? "[green]OK[/]" : "[red]DIFF[/]";
            var s1Display = s1.Length > 28 ? s1[..25] + "..." : s1;
            var s2Display = s2.Length > 28 ? s2[..25] + "..." : s2;
            table.AddRow(i.ToString(), Markup.Escape(s1Display), Markup.Escape(s2Display), match);
        }

        AnsiConsole.Write(table);
    }

    /// <summary>
    ///     Parse and display NiControllerSequence details.
    /// </summary>
    private static void Controller(string path, int blockIndex)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        if (blockIndex < 0 || blockIndex >= nif.NumBlocks)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Block index {blockIndex} out of range (0-{nif.NumBlocks - 1})");
            return;
        }

        var typeName = nif.GetBlockTypeName(blockIndex);
        if (typeName != "NiControllerSequence")
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Block {blockIndex} is {typeName}, not NiControllerSequence");
            return;
        }

        var offset = nif.GetBlockOffset(blockIndex);
        var size = (int)nif.BlockSizes[blockIndex];
        var blockData = data.AsSpan(offset, size);

        var infoTable = new Table().Border(TableBorder.Rounded);
        infoTable.AddColumn("Property");
        infoTable.AddColumn("Value");
        infoTable.AddRow("File", Path.GetFileName(path));
        infoTable.AddRow("Block", $"{blockIndex}: [cyan]{typeName}[/]");
        infoTable.AddRow("Offset", $"0x{offset:X4}");
        infoTable.AddRow("Size", $"{size} bytes");
        infoTable.AddRow("Endian", nif.IsBigEndian ? "[yellow]Big (Xbox 360)[/]" : "[green]Little (PC)[/]");
        AnsiConsole.Write(infoTable);
        AnsiConsole.WriteLine();

        // Parse NiControllerSequence structure
        var pos = 0;

        // Name (string index)
        var nameIdx = ReadInt32(blockData, ref pos, nif.IsBigEndian);
        var name = GetStringOrIndex(nif, nameIdx);
        AnsiConsole.MarkupLine($"[bold]Name:[/] {Markup.Escape(name)} (idx={nameIdx})");

        // Num Controlled Blocks
        var numControlledBlocks = ReadInt32(blockData, ref pos, nif.IsBigEndian);
        AnsiConsole.MarkupLine($"[bold]Num Controlled Blocks:[/] {numControlledBlocks}");

        // Array Size
        var arrayGrowBy = ReadInt32(blockData, ref pos, nif.IsBigEndian);
        AnsiConsole.MarkupLine($"[bold]Array Grow By:[/] {arrayGrowBy}");
        AnsiConsole.WriteLine();

        AnsiConsole.Write(new Rule("[bold]Controlled Blocks[/]").LeftJustified());

        var ctrlTable = new Table().Border(TableBorder.Simple);
        ctrlTable.AddColumn(new TableColumn("#").RightAligned());
        ctrlTable.AddColumn("Interp");
        ctrlTable.AddColumn("Ctrl");
        ctrlTable.AddColumn("Pri");
        ctrlTable.AddColumn("NodeName");
        ctrlTable.AddColumn("PropType");
        ctrlTable.AddColumn("CtrlType");
        ctrlTable.AddColumn("CtrlID");
        ctrlTable.AddColumn("InterpID");

        // Each ControlledBlock entry (for BS Version 34)
        for (var i = 0; i < numControlledBlocks; i++)
        {
            var interpolator = ReadInt32(blockData, ref pos, nif.IsBigEndian);
            var controller = ReadInt32(blockData, ref pos, nif.IsBigEndian);
            var priority = blockData[pos++];

            var nodeNameIdx = ReadInt32(blockData, ref pos, nif.IsBigEndian);
            var propTypeIdx = ReadInt32(blockData, ref pos, nif.IsBigEndian);
            var ctrlTypeIdx = ReadInt32(blockData, ref pos, nif.IsBigEndian);
            var ctrlIdIdx = ReadInt32(blockData, ref pos, nif.IsBigEndian);
            var interpIdIdx = ReadInt32(blockData, ref pos, nif.IsBigEndian);

            var nodeName = GetStringOrIndex(nif, nodeNameIdx);
            var propType = GetStringOrIndex(nif, propTypeIdx);
            var ctrlType = GetStringOrIndex(nif, ctrlTypeIdx);
            var ctrlId = GetStringOrIndex(nif, ctrlIdIdx);
            var interpId = GetStringOrIndex(nif, interpIdIdx);

            var interpStr = interpolator == -1 ? "None" : $"Ref[[{interpolator}]]";
            var ctrlStr = controller == -1 ? "None" : $"Ref[[{controller}]]";

            ctrlTable.AddRow(
                i.ToString(), interpStr, ctrlStr, priority.ToString(),
                Markup.Escape(nodeName), Markup.Escape(propType), Markup.Escape(ctrlType),
                Markup.Escape(ctrlId), Markup.Escape(interpId));
        }

        AnsiConsole.Write(ctrlTable);
        AnsiConsole.WriteLine();

        // Rest of NiControllerSequence
        var weight = ReadFloat(blockData, ref pos, nif.IsBigEndian);
        var textKeysIdx = ReadInt32(blockData, ref pos, nif.IsBigEndian);
        var cycleType = ReadInt32(blockData, ref pos, nif.IsBigEndian);
        var frequency = ReadFloat(blockData, ref pos, nif.IsBigEndian);
        var startTime = ReadFloat(blockData, ref pos, nif.IsBigEndian);
        var stopTime = ReadFloat(blockData, ref pos, nif.IsBigEndian);
        var managerIdx = ReadInt32(blockData, ref pos, nif.IsBigEndian);
        var accumRootNameIdx = ReadInt32(blockData, ref pos, nif.IsBigEndian);

        var seqTable = new Table().Border(TableBorder.Rounded);
        seqTable.AddColumn("Property");
        seqTable.AddColumn("Value");
        seqTable.AddRow("Weight", weight.ToString("F4"));
        seqTable.AddRow("Text Keys Ref", textKeysIdx == -1 ? "None" : $"Block[[{textKeysIdx}]]");
        seqTable.AddRow("Cycle Type", cycleType.ToString());
        seqTable.AddRow("Frequency", frequency.ToString("F4"));
        seqTable.AddRow("Start Time", startTime.ToString("F4"));
        seqTable.AddRow("Stop Time", stopTime.ToString("F4"));
        seqTable.AddRow("Manager Ptr", managerIdx == -1 ? "None" : $"Block[[{managerIdx}]]");
        seqTable.AddRow("Accum Root Name",
            $"{Markup.Escape(GetStringOrIndex(nif, accumRootNameIdx))} (idx={accumRootNameIdx})");
        AnsiConsole.Write(seqTable);
    }

    /// <summary>
    ///     Byte-by-byte comparison of a specific block between two files.
    /// </summary>
    private static void DiffBlock(string path1, string path2, int block1, int block2)
    {
        var data1 = File.ReadAllBytes(path1);
        var data2 = File.ReadAllBytes(path2);
        var nif1 = NifParser.Parse(data1);
        var nif2 = NifParser.Parse(data2);

        if (block1 < 0 || block1 >= nif1.NumBlocks)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Block {block1} out of range for file 1 (0-{nif1.NumBlocks - 1})");
            return;
        }

        if (block2 < 0 || block2 >= nif2.NumBlocks)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Block {block2} out of range for file 2 (0-{nif2.NumBlocks - 1})");
            return;
        }

        var offset1 = nif1.GetBlockOffset(block1);
        var offset2 = nif2.GetBlockOffset(block2);
        var size1 = (int)nif1.BlockSizes[block1];
        var size2 = (int)nif2.BlockSizes[block2];
        var type1 = nif1.GetBlockTypeName(block1);
        var type2 = nif2.GetBlockTypeName(block2);

        AnsiConsole.Write(new Rule("[bold]Block Diff[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var infoTable = new Table().Border(TableBorder.Rounded);
        infoTable.AddColumn("");
        infoTable.AddColumn("File");
        infoTable.AddColumn("Block");
        infoTable.AddColumn("Type");
        infoTable.AddColumn("Offset");
        infoTable.AddColumn("Size");
        infoTable.AddRow("File 1", Markup.Escape(Path.GetFileName(path1)), block1.ToString(),
            $"[cyan]{Markup.Escape(type1)}[/]", $"0x{offset1:X4}", $"{size1} bytes");
        infoTable.AddRow("File 2", Markup.Escape(Path.GetFileName(path2)), block2.ToString(),
            $"[cyan]{Markup.Escape(type2)}[/]", $"0x{offset2:X4}", $"{size2} bytes");
        AnsiConsole.Write(infoTable);
        AnsiConsole.WriteLine();

        if (size1 != size2)
        {
            AnsiConsole.MarkupLine($"[yellow]WARNING:[/] Block sizes differ ({size1} vs {size2})");
            AnsiConsole.WriteLine();
        }

        var minSize = Math.Min(size1, size2);
        var diffCount = 0;
        var firstDiffs = new List<(int Offset, byte B1, byte B2)>();

        for (var i = 0; i < minSize; i++)
        {
            var b1 = data1[offset1 + i];
            var b2 = data2[offset2 + i];
            if (b1 != b2)
            {
                diffCount++;
                if (firstDiffs.Count < 20) firstDiffs.Add((i, b1, b2));
            }
        }

        if (diffCount == 0 && size1 == size2)
        {
            AnsiConsole.MarkupLine("[bold green]Blocks are IDENTICAL[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[bold]Total differences:[/] [red]{diffCount}[/] bytes");
        AnsiConsole.WriteLine();

        var diffTable = new Table().Border(TableBorder.Simple);
        diffTable.AddColumn(new TableColumn("Offset").RightAligned());
        diffTable.AddColumn(new TableColumn("File1").Centered());
        diffTable.AddColumn(new TableColumn("File2").Centered());
        diffTable.AddColumn("Context");

        foreach (var (off, b1, b2) in firstDiffs)
        {
            // Try to determine context (what field this might be)
            var context = "";
            if (off % 4 == 0)
                // Could be a 4-byte value - show as int
                if (off + 4 <= minSize)
                {
                    var val1 = BitConverter.ToInt32(data1, offset1 + off);
                    var val2 = BitConverter.ToInt32(data2, offset2 + off);
                    context = $"(int: {val1} vs {val2})";
                }

            diffTable.AddRow($"0x{off:X4}", $"[red]0x{b1:X2}[/]", $"[green]0x{b2:X2}[/]", context);
        }

    }

    /// <summary>
    ///     Parse and display NiDefaultAVObjectPalette contents.
    /// </summary>
    private static void Palette(string path, int blockIndex)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        if (blockIndex < 0 || blockIndex >= nif.NumBlocks)
        {
            AnsiConsole.MarkupLine($"[red]Error: Block index {blockIndex} out of range (0-{nif.NumBlocks - 1})[/]");
            return;
        }

        var typeName = nif.GetBlockTypeName(blockIndex);
        if (typeName != "NiDefaultAVObjectPalette")
        {
            AnsiConsole.MarkupLine($"[red]Error: Block {blockIndex} is {typeName}, not NiDefaultAVObjectPalette[/]");
            return;
        }

        var blockOffset = nif.GetBlockOffset(blockIndex);
        var blockData = data.AsSpan(blockOffset);
        var be = nif.IsBigEndian;

        // Scene (Ptr to NiAVObject) - 4 bytes
        var pos = 0;
        var sceneRef = ReadInt32(blockData, ref pos, be);

        // Num Objs - 4 bytes
        var numObjs = ReadInt32(blockData, ref pos, be);

        AnsiConsole.MarkupLine($"[bold]File:[/] {Path.GetFileName(path)}");
        AnsiConsole.MarkupLine($"[bold]Block:[/] {blockIndex} (NiDefaultAVObjectPalette)");
        AnsiConsole.MarkupLine($"[bold]Endian:[/] {(be ? "Big (Xbox 360)" : "Little (PC)")}");
        AnsiConsole.MarkupLine($"[bold]Scene Ref:[/] {(sceneRef == -1 ? "null" : sceneRef.ToString())}");
        AnsiConsole.MarkupLine($"[bold]Num Objects:[/] {numObjs}");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("Idx").RightAligned());
        table.AddColumn("Name");
        table.AddColumn(new TableColumn("Block Ref").RightAligned());
        table.AddColumn("Block Type");

        // Parse AVObject array - each entry is: SizedString (uint length + chars) + Ptr (int)
        for (var i = 0; i < numObjs; i++)
        {
            // Read SizedString: uint length + chars
            var strLen = ReadInt32(blockData, ref pos, be);
            var name = System.Text.Encoding.ASCII.GetString(data, blockOffset + pos, strLen);
            pos += strLen;

            // Read Ptr (block reference)
            var blockRef = ReadInt32(blockData, ref pos, be);

            var blockType = blockRef >= 0 && blockRef < nif.NumBlocks ? nif.GetBlockTypeName(blockRef) : (blockRef == -1 ? "null" : "INVALID");
            var refStr = blockRef == -1 ? "[dim]null[/]" : (blockRef < nif.NumBlocks ? $"[green]{blockRef}[/]" : $"[red]{blockRef} (INVALID!)[/]");

            table.AddRow(i.ToString(), Markup.Escape(name), refStr, blockType);
        }

        AnsiConsole.Write(table);
    }

    private static string GetStringOrIndex(NifInfo nif, int idx)
    {
        if (idx == -1)
            return "<none>";
        if (idx >= 0 && idx < nif.Strings.Count)
            return nif.Strings[idx];
        return $"<invalid:{idx}>";
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int pos, bool bigEndian)
    {
        var value = bigEndian
            ? (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]
            : data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24);
        pos += 4;
        return value;
    }

    private static float ReadFloat(ReadOnlySpan<byte> data, ref int pos, bool bigEndian)
    {
        Span<byte> temp = stackalloc byte[4];
        if (bigEndian)
        {
            temp[0] = data[pos + 3];
            temp[1] = data[pos + 2];
            temp[2] = data[pos + 1];
            temp[3] = data[pos];
        }
        else
        {
            temp[0] = data[pos];
            temp[1] = data[pos + 1];
            temp[2] = data[pos + 2];
            temp[3] = data[pos + 3];
        }

        pos += 4;
        return BitConverter.ToSingle(temp);
    }
}