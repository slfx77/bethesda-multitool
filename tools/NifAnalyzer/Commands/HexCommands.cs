using System.CommandLine;
using Spectre.Console;
using static NifAnalyzer.Utils.BinaryHelpers;

namespace NifAnalyzer.Commands;

/// <summary>
///     Raw hex dump command.
/// </summary>
internal static class HexCommands
{
    /// <summary>
    ///     Create the "hex" command for raw hex dumps.
    /// </summary>
    public static Command CreateHexCommand()
    {
        var command = new Command("hex", "Dump raw hex bytes at a specific offset");
        var fileArg = new Argument<string>("file") { Description = "NIF file path" };
        var offsetArg = new Argument<long>("offset") { Description = "Byte offset to start dump" };
        var lengthArg = new Argument<int>("length")
            { Description = "Number of bytes to dump", DefaultValueFactory = _ => 256 };
        command.Arguments.Add(fileArg);
        command.Arguments.Add(offsetArg);
        command.Arguments.Add(lengthArg);
        command.SetAction(parseResult => Hex(parseResult.GetValue(fileArg), parseResult.GetValue(offsetArg),
            parseResult.GetValue(lengthArg)));
        return command;
    }

    private static void Hex(string path, long offset, int length)
    {
        var data = File.ReadAllBytes(path);

        if (offset < 0 || offset >= data.Length)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Offset 0x{offset:X} out of range (file size: {data.Length})");
            return;
        }

        length = (int)Math.Min(length, data.Length - offset);

        var infoTable = new Table().Border(TableBorder.Rounded);
        infoTable.AddColumn("Property");
        infoTable.AddColumn("Value");
        infoTable.AddRow("File", Markup.Escape(Path.GetFileName(path)));
        infoTable.AddRow("Offset", $"0x{offset:X4}");
        infoTable.AddRow("Length", $"{length} bytes");
        AnsiConsole.Write(infoTable);
        AnsiConsole.WriteLine();

        HexDump(data, (int)offset, length);
    }
}