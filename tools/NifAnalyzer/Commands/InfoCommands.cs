using System.CommandLine;
using NifAnalyzer.Parsers;
using Spectre.Console;
using static NifAnalyzer.Utils.BinaryHelpers;

namespace NifAnalyzer.Commands;

/// <summary>
///     Commands for basic NIF information: info, blocks, block, compare.
/// </summary>
internal static class InfoCommands
{
    public static Command CreateInfoCommand()
    {
        var command = new Command("info", "Show NIF header information");
        var fileArg = new Argument<string>("file") { Description = "Path to NIF file" };
        command.Arguments.Add(fileArg);
        command.SetAction(parseResult => Info(parseResult.GetValue(fileArg)));
        return command;
    }

    public static Command CreateBlocksCommand()
    {
        var command = new Command("blocks", "List all blocks with types and sizes");
        var fileArg = new Argument<string>("file") { Description = "Path to NIF file" };
        command.Arguments.Add(fileArg);
        command.SetAction(parseResult => Blocks(parseResult.GetValue(fileArg)));
        return command;
    }

    public static Command CreateBlockCommand()
    {
        var command = new Command("block", "Show detailed block information");
        var fileArg = new Argument<string>("file") { Description = "Path to NIF file" };
        var indexArg = new Argument<int>("index") { Description = "Block index" };
        command.Arguments.Add(fileArg);
        command.Arguments.Add(indexArg);
        command.SetAction(parseResult => Block(parseResult.GetValue(fileArg), parseResult.GetValue(indexArg)));
        return command;
    }

    public static Command CreateCompareCommand()
    {
        var command = new Command("compare", "Compare two NIF files (blocks/types)");
        var file1Arg = new Argument<string>("file1") { Description = "First NIF file (typically Xbox 360)" };
        var file2Arg = new Argument<string>("file2") { Description = "Second NIF file (typically PC)" };
        command.Arguments.Add(file1Arg);
        command.Arguments.Add(file2Arg);
        command.SetAction(parseResult => Compare(parseResult.GetValue(file1Arg), parseResult.GetValue(file2Arg)));
        return command;
    }

    private static void Info(string path)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("File", Path.GetFileName(path));
        table.AddRow("Size", $"{data.Length:N0} bytes");
        table.AddRow("Version String", nif.VersionString);
        table.AddRow("Version", FormatVersion(nif.Version));
        table.AddRow("Endian", nif.IsBigEndian ? "[yellow]Big (Xbox 360)[/]" : "[green]Little (PC)[/]");
        table.AddRow("User Version", nif.UserVersion.ToString());
        table.AddRow("BS Version", nif.BsVersion.ToString());
        table.AddRow("Num Blocks", nif.NumBlocks.ToString());
        table.AddRow("Block Types", nif.BlockTypes.Count.ToString());
        table.AddRow("Strings", nif.NumStrings.ToString());
        table.AddRow("Block Data Offset", $"0x{nif.BlockDataOffset:X4}");

        AnsiConsole.Write(table);
    }

    private static void Blocks(string path)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        AnsiConsole.MarkupLine($"[bold]File:[/] {Path.GetFileName(path)} ([cyan]{nif.NumBlocks}[/] blocks)");
        AnsiConsole.MarkupLine(
            $"[bold]Endian:[/] {(nif.IsBigEndian ? "[yellow]Big (Xbox 360)[/]" : "[green]Little (PC)[/]")}");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn(new TableColumn("Idx").RightAligned());
        table.AddColumn("Offset");
        table.AddColumn(new TableColumn("Size").RightAligned());
        table.AddColumn("Type");

        var offset = nif.BlockDataOffset;
        for (var i = 0; i < nif.NumBlocks; i++)
        {
            var typeIdx = nif.BlockTypeIndices[i];
            var typeName = nif.BlockTypes[typeIdx];
            var size = nif.BlockSizes[i];

            table.AddRow(i.ToString(), $"0x{offset:X4}", size.ToString(), typeName);
            offset += (int)size;
        }

        AnsiConsole.Write(table);
    }

    private static void Block(string path, int blockIndex)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        if (blockIndex < 0 || blockIndex >= nif.NumBlocks)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Block index {blockIndex} out of range (0-{nif.NumBlocks - 1})");
            return;
        }

        var offset = nif.GetBlockOffset(blockIndex);
        var typeName = nif.GetBlockTypeName(blockIndex);
        var size = (int)nif.BlockSizes[blockIndex];

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Block", blockIndex.ToString());
        table.AddRow("Type", $"[cyan]{typeName}[/]");
        table.AddRow("Offset", $"0x{offset:X4}");
        table.AddRow("Size", $"{size} bytes");
        table.AddRow("Endian", nif.IsBigEndian ? "[yellow]Big (Xbox 360)[/]" : "[green]Little (PC)[/]");
        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]First bytes:[/]");
        HexDump(data, offset, Math.Min(128, size));
    }

    private static void Compare(string xboxPath, string pcPath)
    {
        var xboxData = File.ReadAllBytes(xboxPath);
        var pcData = File.ReadAllBytes(pcPath);

        var xbox = NifParser.Parse(xboxData);
        var pc = NifParser.Parse(pcData);

        AnsiConsole.Write(new Rule("[bold blue]NIF Comparison[/]").LeftJustified());
        AnsiConsole.WriteLine();

        // Header comparison table
        var headerTable = new Table().Border(TableBorder.Rounded);
        headerTable.AddColumn("Property");
        headerTable.AddColumn(new TableColumn("File 1").Centered());
        headerTable.AddColumn(new TableColumn("File 2").Centered());

        headerTable.AddRow("File", Path.GetFileName(xboxPath), Path.GetFileName(pcPath));
        headerTable.AddRow("File Size", $"{xboxData.Length:N0}", $"{pcData.Length:N0}");
        headerTable.AddRow("Endian",
            xbox.IsBigEndian ? "[yellow]Big[/]" : "[green]Little[/]",
            pc.IsBigEndian ? "[yellow]Big[/]" : "[green]Little[/]");
        headerTable.AddRow("Version", FormatVersion(xbox.Version), FormatVersion(pc.Version));
        headerTable.AddRow("User Version", xbox.UserVersion.ToString(), pc.UserVersion.ToString());
        headerTable.AddRow("BS Version", xbox.BsVersion.ToString(), pc.BsVersion.ToString());
        headerTable.AddRow("Num Blocks", xbox.NumBlocks.ToString(), pc.NumBlocks.ToString());
        headerTable.AddRow("Num Block Types", xbox.BlockTypes.Count.ToString(), pc.BlockTypes.Count.ToString());
        headerTable.AddRow("Block Data Offset", $"0x{xbox.BlockDataOffset:X4}", $"0x{pc.BlockDataOffset:X4}");

        AnsiConsole.Write(headerTable);
        AnsiConsole.WriteLine();

        // Block type comparison
        AnsiConsole.Write(new Rule("[bold blue]Block Type Comparison[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var allTypes = xbox.BlockTypes.Union(pc.BlockTypes).OrderBy(t => t).ToList();
        var xboxTypeCounts = xbox.BlockTypeIndices.GroupBy(i => xbox.BlockTypes[i])
            .ToDictionary(g => g.Key, g => g.Count());
        var pcTypeCounts = pc.BlockTypeIndices.GroupBy(i => pc.BlockTypes[i]).ToDictionary(g => g.Key, g => g.Count());

        var typeTable = new Table().Border(TableBorder.Simple);
        typeTable.AddColumn("Block Type");
        typeTable.AddColumn(new TableColumn("File 1").RightAligned());
        typeTable.AddColumn(new TableColumn("File 2").RightAligned());
        typeTable.AddColumn("Diff");

        foreach (var type in allTypes)
        {
            var xc = xboxTypeCounts.GetValueOrDefault(type, 0);
            var pcc = pcTypeCounts.GetValueOrDefault(type, 0);
            var diff = xc != pcc ? "[red]<--[/]" : "";
            typeTable.AddRow(type, xc.ToString(), pcc.ToString(), diff);
        }

        AnsiConsole.Write(typeTable);
        AnsiConsole.WriteLine();

        // Block-by-block comparison
        AnsiConsole.Write(new Rule("[bold blue]Block-by-Block Comparison[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var maxBlocks = Math.Max(xbox.NumBlocks, pc.NumBlocks);

        var blockTable = new Table().Border(TableBorder.Simple);
        blockTable.AddColumn(new TableColumn("Idx").RightAligned());
        blockTable.AddColumn("File 1 Type");
        blockTable.AddColumn(new TableColumn("Size").RightAligned());
        blockTable.AddColumn("File 2 Type");
        blockTable.AddColumn(new TableColumn("Size").RightAligned());
        blockTable.AddColumn("Diff");

        for (var i = 0; i < maxBlocks; i++)
        {
            var xboxType = i < xbox.NumBlocks ? xbox.BlockTypes[xbox.BlockTypeIndices[i]] : "-";
            var pcType = i < pc.NumBlocks ? pc.BlockTypes[pc.BlockTypeIndices[i]] : "-";
            var xboxSize = i < xbox.NumBlocks ? xbox.BlockSizes[i].ToString() : "-";
            var pcSize = i < pc.NumBlocks ? pc.BlockSizes[i].ToString() : "-";
            var diff = xboxType != pcType ? "[red]<--[/]" : "";

            blockTable.AddRow(i.ToString(), xboxType, xboxSize, pcType, pcSize, diff);
        }

        AnsiConsole.Write(blockTable);
    }
}