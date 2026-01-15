using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using TextureAnalyzer.Parsers;
using static TextureAnalyzer.Utils.BinaryHelpers;

namespace TextureAnalyzer.Commands;

/// <summary>
///     Commands for displaying texture file information.
/// </summary>
internal static class InfoCommands
{
    /// <summary>
    ///     Create the "info" command for displaying texture info.
    /// </summary>
    public static Command CreateInfoCommand()
    {
        var command = new Command("info", "Display detailed information about a DDX or DDS file");
        var fileArg = new Argument<string>("file") { Description = "Texture file path (.ddx or .dds)" };
        command.Arguments.Add(fileArg);
        command.SetAction(parseResult => Info(parseResult.GetValue(fileArg)!));
        return command;
    }

    private static void Info(string path)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {path}");
            return;
        }

        var data = File.ReadAllBytes(path);
        var magic = TextureParser.GetMagic(data);

        if (magic is "3XDO" or "3XDR")
        {
            ShowDdxInfo(path, data);
        }
        else if (magic == "DDS ")
        {
            ShowDdsInfo(path, data);
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Unknown file format:[/] {magic ?? "(too small)"}");
        }
    }

    private static void ShowDdxInfo(string path, byte[] data)
    {
        var ddx = TextureParser.ParseDdx(data);
        if (ddx == null)
        {
            AnsiConsole.MarkupLine("[red]Failed to parse DDX header[/]");
            return;
        }

        var formatColor = ddx.Is3XDR ? "yellow" : "green";
        AnsiConsole.MarkupLine($"[bold {formatColor}]Xbox 360 DDX ({ddx.Magic})[/]");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddColumn("Notes");

        table.AddRow("File", Markup.Escape(Path.GetFileName(path)), $"{data.Length:N0} bytes");
        table.AddRow("Magic", ddx.Magic, ddx.Is3XDR ? "[yellow]Engine-specific tiling[/]" : "[green]Morton tiling[/]");
        table.AddRow("Version", ddx.Version.ToString(CultureInfo.InvariantCulture), "");
        table.AddRow("Priorities", $"L={ddx.PriorityL} C={ddx.PriorityC} H={ddx.PriorityH}", "Load priority levels");
        table.AddRow("Dimensions", $"{ddx.Width} x {ddx.Height}", $"{ddx.Width * ddx.Height:N0} pixels");
        table.AddRow("DataFormat", $"0x{ddx.DataFormat:X2}", Markup.Escape("DWORD[3] bits 0-7"));
        table.AddRow("ActualFormat", $"0x{ddx.ActualFormat:X2}", Markup.Escape(ddx.FormatName));
        table.AddRow("FourCC", Markup.Escape(ddx.ExpectedFourCC), "Expected DDS format");
        table.AddRow("Tiled", ddx.Tiled ? "Yes" : "No", "");
        table.AddRow("Block Size", $"{ddx.BlockSize} bytes", "");
        table.AddRow("Expected Mip0", $"{ddx.CalculateMip0Size():N0} bytes", "");
        table.AddRow("Data Size", $"{ddx.DataSize:N0} bytes", "After 0x44-byte header");

        AnsiConsole.Write(table);

        // Show Format dwords
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Format DWORDs:[/]");
        var formatTable = new Table().Border(TableBorder.Rounded);
        formatTable.AddColumn("DWORD");
        formatTable.AddColumn("Offset");
        formatTable.AddColumn("Value");
        formatTable.AddColumn("Notes");
        formatTable.AddRow("0", "0x18", $"0x{ddx.Dword0:X8}", Markup.Escape($"Tiled={(ddx.Dword0 >> 19) & 1}"));
        formatTable.AddRow("3", "0x24", $"0x{ddx.Dword3:X8}", Markup.Escape($"DataFormat=0x{ddx.DataFormat:X2}"));
        formatTable.AddRow("4", "0x28", $"0x{ddx.Dword4:X8}", Markup.Escape($"ActualFormat=0x{(ddx.Dword4 >> 24) & 0xFF:X2}"));
        formatTable.AddRow("5", "0x2C", $"0x{ddx.Dword5:X8}", Markup.Escape($"Dims: {ddx.Width}x{ddx.Height}"));
        AnsiConsole.Write(formatTable);

        // Show header hex dump
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Header bytes (0x00-0x43):[/]");
        HexDump(data, 0, Math.Min(0x44, data.Length));
    }

    private static void ShowDdsInfo(string path, byte[] data)
    {
        var dds = TextureParser.ParseDds(data);
        if (dds == null)
        {
            AnsiConsole.MarkupLine("[red]Failed to parse DDS header[/]");
            return;
        }

        AnsiConsole.MarkupLine("[bold cyan]PC DDS File[/]");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddColumn("Notes");

        table.AddRow("File", Markup.Escape(Path.GetFileName(path)), $"{data.Length:N0} bytes");
        table.AddRow("Header Size", dds.HeaderSize.ToString(), "Should be 124");
        table.AddRow("Dimensions", $"{dds.Width} x {dds.Height}", $"{dds.Width * dds.Height:N0} pixels");
        table.AddRow("FourCC", dds.FourCC, "");
        table.AddRow("Mip Count", dds.MipMapCount.ToString(), "");
        table.AddRow("Pitch/Size", $"{dds.PitchOrLinearSize:N0}", "");
        table.AddRow("Data Size", $"{dds.DataSize:N0} bytes", "After 128-byte header");

        AnsiConsole.Write(table);
    }
}
