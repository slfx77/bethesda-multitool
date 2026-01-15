using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using TextureAnalyzer.Parsers;
using static TextureAnalyzer.Utils.BinaryHelpers;

namespace TextureAnalyzer.Commands;

/// <summary>
///     Commands for comparing Xbox 360 DDX files with PC DDS equivalents.
/// </summary>
internal static class CompareCommands
{
    /// <summary>
    ///     Create the "compare" command for comparing DDX and DDS headers.
    /// </summary>
    public static Command CreateCompareCommand()
    {
        var command = new Command("compare", "Compare a DDX file with its PC DDS equivalent");
        var ddxArg = new Argument<string>("ddx") { Description = "Xbox 360 DDX file path" };
        var ddsArg = new Argument<string>("dds") { Description = "PC DDS file path" };
        command.Arguments.Add(ddxArg);
        command.Arguments.Add(ddsArg);
        command.SetAction(parseResult => Compare(
            parseResult.GetValue(ddxArg)!,
            parseResult.GetValue(ddsArg)!));
        return command;
    }

    /// <summary>
    ///     Create the "datacompare" command for comparing actual texture data.
    /// </summary>
    public static Command CreateDataCompareCommand()
    {
        var command = new Command("datacompare", "Compare texture data bytes between DDX and DDS");
        var ddxArg = new Argument<string>("ddx") { Description = "Xbox 360 DDX file path" };
        var ddsArg = new Argument<string>("dds") { Description = "PC DDS file path" };
        var showDiffOpt = new Option<bool>("-d", "--diff") { Description = "Show first differing bytes" };
        command.Arguments.Add(ddxArg);
        command.Arguments.Add(ddsArg);
        command.Options.Add(showDiffOpt);
        command.SetAction(parseResult => DataCompare(
            parseResult.GetValue(ddxArg)!,
            parseResult.GetValue(ddsArg)!,
            parseResult.GetValue(showDiffOpt)));
        return command;
    }

    private static void Compare(string ddxPath, string ddsPath)
    {
        if (!File.Exists(ddxPath))
        {
            AnsiConsole.MarkupLine($"[red]DDX file not found:[/] {ddxPath}");
            return;
        }
        if (!File.Exists(ddsPath))
        {
            AnsiConsole.MarkupLine($"[red]DDS file not found:[/] {ddsPath}");
            return;
        }

        var ddxData = File.ReadAllBytes(ddxPath);
        var ddsData = File.ReadAllBytes(ddsPath);

        var ddx = TextureParser.ParseDdx(ddxData);
        var dds = TextureParser.ParseDds(ddsData);

        if (ddx == null)
        {
            AnsiConsole.MarkupLine("[red]Failed to parse DDX header[/]");
            return;
        }
        if (dds == null)
        {
            AnsiConsole.MarkupLine("[red]Failed to parse DDS header[/]");
            return;
        }

        AnsiConsole.MarkupLine("[bold]Header Comparison[/]");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Property");
        table.AddColumn(new TableColumn("DDX (Xbox 360)").Centered());
        table.AddColumn(new TableColumn("DDS (PC)").Centered());
        table.AddColumn("Match");

        // Magic/Format
        var formatColor = ddx.Is3XDR ? "yellow" : "green";
        table.AddRow("Format", $"[{formatColor}]{ddx.Magic}[/]", "DDS", "[dim]-[/]");

        // Dimensions
        var dimsMatch = ddx.Width == dds.Width && ddx.Height == dds.Height;
        table.AddRow(
            "Dimensions",
            $"{ddx.Width}x{ddx.Height}",
            $"{dds.Width}x{dds.Height}",
            dimsMatch ? "[green]✓[/]" : "[red]✗[/]");

        // Format/FourCC
        var formatMatch = ddx.ExpectedFourCC == dds.FourCC;
        table.AddRow(
            "Pixel Format",
            $"{ddx.FormatName} ({ddx.ExpectedFourCC})",
            dds.FourCC,
            formatMatch ? "[green]✓[/]" : "[red]✗[/]");

        // Mip count - DDX doesn't have explicit mip count, so compare with DDS
        table.AddRow(
            "Mip Count",
            "-",
            dds.MipMapCount.ToString(CultureInfo.InvariantCulture),
            "[dim]-[/]");

        // Data size
        var dataSizeMatch = ddx.DataSize == dds.DataSize;
        table.AddRow(
            "Data Size",
            $"{ddx.DataSize:N0} bytes",
            $"{dds.DataSize:N0} bytes",
            dataSizeMatch ? "[green]✓[/]" : "[red]✗[/]");

        // Expected size
        var expectedMip0 = ddx.CalculateMip0Size();
        table.AddRow(
            "Expected Mip0",
            $"{expectedMip0:N0} bytes",
            "-",
            "[dim]-[/]");

        // Tiled flag
        table.AddRow(
            "Tiled",
            ddx.Tiled ? "Yes" : "No",
            "-",
            "[dim]-[/]");

        AnsiConsole.Write(table);

        // Summary
        AnsiConsole.WriteLine();
        var allMatch = dimsMatch && formatMatch && dataSizeMatch;
        if (allMatch)
        {
            AnsiConsole.MarkupLine("[green]✓ Headers match - texture data should be compatible[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]⚠ Header differences detected[/]");
        }

        if (ddx.Is3XDR)
        {
            AnsiConsole.MarkupLine("[yellow]⚠ This is a 3XDR file - engine-specific tiling not fully supported[/]");
        }
    }

    private static void DataCompare(string ddxPath, string ddsPath, bool showDiff)
    {
        if (!File.Exists(ddxPath) || !File.Exists(ddsPath))
        {
            AnsiConsole.MarkupLine("[red]File not found[/]");
            return;
        }

        var ddxData = File.ReadAllBytes(ddxPath);
        var ddsData = File.ReadAllBytes(ddsPath);

        var ddx = TextureParser.ParseDdx(ddxData);
        var dds = TextureParser.ParseDds(ddsData);

        if (ddx == null || dds == null)
        {
            AnsiConsole.MarkupLine("[red]Failed to parse headers[/]");
            return;
        }

        // Extract just the texture data
        var ddxTexData = ddxData.AsSpan(12);  // After 12-byte DDX header
        var ddsTexData = ddsData.AsSpan(128); // After 128-byte DDS header

        var minLen = Math.Min(ddxTexData.Length, ddsTexData.Length);
        var matchCount = 0;
        var firstDiffOffset = -1;

        for (var i = 0; i < minLen; i++)
        {
            if (ddxTexData[i] == ddsTexData[i])
            {
                matchCount++;
            }
            else if (firstDiffOffset < 0)
            {
                firstDiffOffset = i;
            }
        }

        var matchPercent = 100.0 * matchCount / minLen;

        AnsiConsole.MarkupLine("[bold]Data Comparison[/]");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Metric");
        table.AddColumn("Value");

        table.AddRow("DDX data size", $"{ddxTexData.Length:N0} bytes");
        table.AddRow("DDS data size", $"{ddsTexData.Length:N0} bytes");
        table.AddRow("Bytes compared", $"{minLen:N0}");
        table.AddRow("Matching bytes", $"{matchCount:N0} ({matchPercent:F2}%)");

        if (firstDiffOffset >= 0)
        {
            table.AddRow("First difference", $"Offset 0x{firstDiffOffset:X}");
        }
        else
        {
            table.AddRow("First difference", "[green]None - data identical![/]");
        }

        AnsiConsole.Write(table);

        // Show diff bytes
        if (showDiff && firstDiffOffset >= 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]First differing bytes:[/]");

            var showOffset = Math.Max(0, firstDiffOffset - 16);
            var showLength = Math.Min(64, minLen - showOffset);

            AnsiConsole.MarkupLine("[cyan]DDX data:[/]");
            HexDump(ddxData, 12 + showOffset, showLength);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[cyan]DDS data:[/]");
            HexDump(ddsData, 128 + showOffset, showLength);
        }
    }
}
