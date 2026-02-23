using FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers;
using Spectre.Console;
using System.CommandLine;
using System.Globalization;
using FalloutXbox360Utils.Core.Formats.Esm;
using static EsmAnalyzer.Commands.OfstDataLoader;
using static EsmAnalyzer.Commands.OfstMathUtils;

namespace EsmAnalyzer.Commands;

/// <summary>
///     OFST validate and image subcommands: ofst-validate, ofst-image.
/// </summary>
public static class OfstRebuildCommand
{
    public static Command CreateOfstValidateCommand()
    {
        var command = new Command("ofst-validate",
            "Validate WRLD OFST offsets against CELL records and grid coordinates");

        var fileArg = new Argument<string>("file") { Description = FilePathDescription };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var limitOption = new Option<int>(LimitOptionShort, LimitOptionLong)
        { Description = "Maximum mismatches to show (0 = unlimited)", DefaultValueFactory = _ => 50 };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(worldArg);
        command.Options.Add(limitOption);

        command.SetAction(parseResult => ValidateOfst(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(limitOption)));

        return command;
    }

    public static Command CreateOfstImageCommand()
    {
        var command = new Command("ofst-image",
            "Visualize WRLD OFST offset table as an image (shows zero vs non-zero entries)");

        var fileArg = new Argument<string>("file") { Description = FilePathDescription };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var outputOption = new Option<string>("-o", "--output")
        { Description = "Output PNG file path", DefaultValueFactory = _ => "ofst_map.png" };
        var scaleOption = new Option<int>("-s", "--scale")
        { Description = "Scale factor (pixels per cell)", DefaultValueFactory = _ => 4 };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(worldArg);
        command.Options.Add(outputOption);
        command.Options.Add(scaleOption);

        command.SetAction(parseResult => GenerateOfstImage(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(outputOption)!,
            parseResult.GetValue(scaleOption)));

        return command;
    }

    // ─── Validate ──────────────────────────────────────────────────────────────

    private static int ValidateOfst(string filePath, string worldFormIdText, int limit)
    {
        if (!TryParseFormId(worldFormIdText, out var worldFormId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid WRLD FormID: {worldFormIdText}");
            return 1;
        }

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null)
        {
            return 1;
        }

        if (!TryGetWorldContext(esm.Data, esm.IsBigEndian, worldFormId, out var context))
        {
            return 1;
        }

        var records = EsmRecordParser.ScanAllRecords(esm.Data, esm.IsBigEndian)
            .OrderBy(r => r.Offset)
            .ToList();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(ColumnIndexLabel)
            .AddColumn("Grid")
            .AddColumn("OFST")
            .AddColumn("Resolved")
            .AddColumn("Issue");

        var mismatches = ValidateOfstEntries(context, esm.Data, esm.IsBigEndian, records, limit, table);

        AnsiConsole.MarkupLine(
            $"[cyan]OFST validation[/] WRLD 0x{worldFormId:X8}: mismatches {mismatches:N0}");
        if (mismatches > 0)
        {
            AnsiConsole.Write(table);
        }

        return mismatches == 0 ? 0 : 1;
    }

    private static int ValidateOfstEntries(WorldContext context, byte[] data, bool bigEndian,
        List<AnalyzerRecordInfo> records, int limit, Table table)
    {
        var mismatches = 0;

        for (var index = 0; index < context.Offsets.Count; index++)
        {
            var entry = context.Offsets[index];
            if (entry == 0)
            {
                continue;
            }

            var result = ValidateOfstEntry(context, data, bigEndian, records, index, entry);
            if (result.Issue != null)
            {
                _ = table.AddRow(index.ToString(), $"{result.GridX},{result.GridY}", $"0x{entry:X8}",
                    result.ResolvedLabel, result.Issue);
                mismatches++;
            }

            if (limit > 0 && mismatches >= limit)
            {
                break;
            }
        }

        return mismatches;
    }

    private static OfstValidationResult ValidateOfstEntry(WorldContext context, byte[] data, bool bigEndian,
        List<AnalyzerRecordInfo> records, int index, uint entry)
    {
        GetGridForIndex(context, index, out var gridX, out var gridY);
        var resolvedLabel = "(none)";
        var resolvedOffset = context.WrldRecord.Offset + entry;
        var match = FindRecordAtOffset(records, resolvedOffset);

        string? issue;
        if (match == null)
        {
            issue = "Missing record";
            return new OfstValidationResult(gridX, gridY, resolvedLabel, issue);
        }

        if (match.Signature != "CELL")
        {
            resolvedLabel = $"{match.Signature} 0x{match.FormId:X8}";
            issue = "Not a CELL";
            return new OfstValidationResult(gridX, gridY, resolvedLabel, issue);
        }

        resolvedLabel = $"CELL 0x{match.FormId:X8}";
        var cellData = EsmHelpers.GetRecordData(data, match, bigEndian);
        var cellSubs = EsmRecordParser.ParseSubrecords(cellData, bigEndian);
        if (!TryGetCellGrid(cellSubs, bigEndian, out var cellX, out var cellY))
        {
            issue = "Missing XCLC";
            return new OfstValidationResult(gridX, gridY, resolvedLabel, issue);
        }

        if (cellX != gridX || cellY != gridY)
        {
            resolvedLabel = $"CELL 0x{match.FormId:X8} ({cellX},{cellY})";
            issue = "Grid mismatch";
            return new OfstValidationResult(gridX, gridY, resolvedLabel, issue);
        }

        return new OfstValidationResult(gridX, gridY, resolvedLabel, null);
    }

    // ─── Image generation ──────────────────────────────────────────────────────

    private static int GenerateOfstImage(string filePath, string worldFormIdText, string outputPath, int scale)
    {
        if (!TryParseFormId(worldFormIdText, out var worldFormId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid WRLD FormID: {worldFormIdText}");
            return 1;
        }

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null)
        {
            return 1;
        }

        if (!TryGetWorldContext(esm.Data, esm.IsBigEndian, worldFormId, out var context))
        {
            return 1;
        }

        var columns = context.Columns;
        var rows = context.Rows;
        var offsets = context.Offsets;

        AnsiConsole.MarkupLine($"[blue]Generating OFST visualization for:[/] WRLD 0x{worldFormId:X8}");
        AnsiConsole.MarkupLine($"Grid: {columns}×{rows} cells, {offsets.Count:N0} OFST entries");

        var nonZeroOffsets = offsets.Where(o => o != 0).ToList();
        var minOffset = nonZeroOffsets.Count > 0 ? nonZeroOffsets.Min() : 0u;
        var maxOffset = nonZeroOffsets.Count > 0 ? nonZeroOffsets.Max() : 1u;
        var offsetRange = maxOffset - minOffset;
        if (offsetRange == 0)
        {
            offsetRange = 1;
        }

        var nonZeroCount = nonZeroOffsets.Count;
        var zeroCount = offsets.Count - nonZeroCount;
        AnsiConsole.MarkupLine($"Non-zero entries: [green]{nonZeroCount:N0}[/]  Zero entries: [red]{zeroCount:N0}[/]");
        AnsiConsole.MarkupLine($"Offset range: 0x{minOffset:X8} - 0x{maxOffset:X8}");

        var imageWidth = columns * scale;
        var imageHeight = rows * scale;
        var pixels = new byte[imageWidth * imageHeight * 4];

        for (var i = 0; i < imageWidth * imageHeight; i++)
        {
            pixels[(i * 4) + 0] = 40;
            pixels[(i * 4) + 1] = 40;
            pixels[(i * 4) + 2] = 40;
            pixels[(i * 4) + 3] = 255;
        }

        for (var row = 0; row < rows && row * columns < offsets.Count; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                var index = (row * columns) + col;
                if (index >= offsets.Count)
                {
                    continue;
                }

                var offset = offsets[index];
                Rgba32 color;

                if (offset == 0)
                {
                    color = new Rgba32(200, 40, 40, 255);
                }
                else
                {
                    var normalized = (float)(offset - minOffset) / offsetRange;
                    color = OffsetToColor(normalized);
                }

                var pixelY = row * scale;
                var pixelX = col * scale;

                for (var sy = 0; sy < scale; sy++)
                {
                    for (var sx = 0; sx < scale; sx++)
                    {
                        var idx = (((pixelY + sy) * imageWidth) + pixelX + sx) * 4;
                        pixels[idx + 0] = color.R;
                        pixels[idx + 1] = color.G;
                        pixels[idx + 2] = color.B;
                        pixels[idx + 3] = color.A;
                    }
                }
            }
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        PngWriter.SaveRgba(pixels, imageWidth, imageHeight, outputPath);
        AnsiConsole.MarkupLine($"[green]Saved:[/] {outputPath} ({imageWidth}×{imageHeight} px)");
        AnsiConsole.MarkupLine("[grey]Legend: Red = zero/missing, Blue→Green→Yellow→White = low→high file offset[/]");

        return 0;
    }

    /// <summary>
    ///     Convert normalized offset (0-1) to color using full HSV rainbow spectrum.
    /// </summary>
    private static Rgba32 OffsetToColor(float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        var hue = t * 300f;
        var saturation = 1.0f;
        var value = 1.0f;

        var c = value * saturation;
        var x = c * (1 - Math.Abs((hue / 60f % 2) - 1));
        var m = value - c;

        float r1, g1, b1;
        if (hue < 60)
        {
            r1 = c;
            g1 = x;
            b1 = 0;
        }
        else if (hue < 120)
        {
            r1 = x;
            g1 = c;
            b1 = 0;
        }
        else if (hue < 180)
        {
            r1 = 0;
            g1 = c;
            b1 = x;
        }
        else if (hue < 240)
        {
            r1 = 0;
            g1 = x;
            b1 = c;
        }
        else if (hue < 300)
        {
            r1 = x;
            g1 = 0;
            b1 = c;
        }
        else
        {
            r1 = c;
            g1 = 0;
            b1 = x;
        }

        var r = (byte)((r1 + m) * 255);
        var g = (byte)((g1 + m) * 255);
        var b = (byte)((b1 + m) * 255);

        return new Rgba32(r, g, b, 255);
    }
}
