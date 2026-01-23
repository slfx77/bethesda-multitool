using System.Text.Json;
using EsmAnalyzer.Core;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Utils;

namespace EsmAnalyzer.Commands;

public static partial class ExportCommands
{
    private static int ExportLand(string filePath, string? formIdStr, bool exportAll, int limit, string outputDir)
    {
        var targetFormId = EsmFileLoader.ParseFormId(formIdStr);
        if (!string.IsNullOrWhiteSpace(formIdStr) && targetFormId == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid FormID: {formIdStr}");
            return 1;
        }

        if (targetFormId == null && !exportAll)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Specify --formid <ID> or --all");
            return 1;
        }

        // Create output directory
        Directory.CreateDirectory(outputDir);

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null) return 1;

        AnsiConsole.MarkupLine($"[blue]Exporting LAND records from:[/] {Path.GetFileName(filePath)}");
        AnsiConsole.MarkupLine(
            $"Endianness: {(esm.IsBigEndian ? "[yellow]Big-endian (Xbox 360)[/]" : "[green]Little-endian (PC)[/]")}");
        AnsiConsole.MarkupLine($"Output directory: [cyan]{outputDir}[/]");
        AnsiConsole.WriteLine();

        // Scan for LAND records using byte scan for reliable Xbox 360 detection
        List<AnalyzerRecordInfo> landRecordList = [];

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning for LAND records...",
                ctx => { landRecordList = EsmHelpers.ScanForRecordType(esm.Data, esm.IsBigEndian, "LAND"); });

        IEnumerable<AnalyzerRecordInfo> landRecords = landRecordList;

        if (targetFormId.HasValue)
            landRecords = landRecords.Where(r => r.FormId == targetFormId.Value);
        else
            landRecords = landRecords.Take(limit);

        var landList = landRecords.ToList();
        AnsiConsole.MarkupLine($"Found [cyan]{landList.Count}[/] LAND record(s) to export");
        AnsiConsole.WriteLine();

        var exported = 0;
        var failed = 0;

        AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .Start(ctx =>
            {
                var task = ctx.AddTask("Exporting LAND records", maxValue: landList.Count);

                foreach (var rec in landList)
                {
                    try
                    {
                        ExportLandRecord(esm.Data, rec, esm.IsBigEndian, outputDir);
                        exported++;
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"  [red]FAILED:[/] 0x{rec.FormId:X8} - {ex.Message}");
                        failed++;
                    }

                    task.Increment(1);
                }
            });

        AnsiConsole.WriteLine();

        // Summary table
        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Value[/]");

        summaryTable.AddRow("Exported", $"[green]{exported}[/]");
        if (failed > 0) summaryTable.AddRow("Failed", $"[red]{failed}[/]");
        summaryTable.AddRow("Output Directory", outputDir);

        AnsiConsole.Write(summaryTable);

        return failed > 0 ? 1 : 0;
    }

    private static void ExportLandRecord(byte[] data, AnalyzerRecordInfo rec, bool bigEndian, string outputDir)
    {
        var recordData = EsmHelpers.GetRecordData(data, rec, bigEndian);
        var subrecords = EsmHelpers.ParseSubrecords(recordData, bigEndian);

        // Create LAND data structure
        var landData = new LandExportData
        {
            FormId = $"0x{rec.FormId:X8}",
            Offset = $"0x{rec.Offset:X8}",
            CompressedSize = (int)rec.DataSize,
            DecompressedSize = recordData.Length,
            IsBigEndian = bigEndian,
            Subrecords = []
        };

        var formIdHex = rec.FormId.ToString("X8");

        foreach (var sub in subrecords)
        {
            landData.Subrecords.Add(new SubrecordExportInfo
            {
                Signature = sub.Signature,
                Size = sub.Data.Length
            });

            switch (sub.Signature)
            {
                case "DATA":
                    landData.DataFlags = bigEndian
                        ? BinaryUtils.ReadUInt32BE(sub.Data)
                        : BinaryUtils.ReadUInt32LE(sub.Data);
                    break;

                case "VNML":
                    ExportNormalMap(sub.Data, Path.Combine(outputDir, $"{formIdHex}_normals.png"));
                    landData.HasNormals = true;
                    break;

                case "VHGT":
                    var (heightmap, baseHeight) = ParseHeightmap(sub.Data, bigEndian);
                    ExportHeightmap(heightmap, Path.Combine(outputDir, $"{formIdHex}_heightmap.png"));
                    landData.HasHeightmap = true;
                    landData.BaseHeight = baseHeight;
                    break;

                case "VCLR":
                    ExportVertexColors(sub.Data, Path.Combine(outputDir, $"{formIdHex}_colors.png"));
                    landData.HasVertexColors = true;
                    break;

                case "ATXT":
                    var atxt = ParseTextureLayer(sub.Data, bigEndian);
                    landData.TextureLayers ??= [];
                    landData.TextureLayers.Add(atxt);
                    break;

                case "BTXT":
                    landData.BaseTexture = ParseTextureLayer(sub.Data, bigEndian);
                    break;

                case "VTXT":
                    landData.VertexTextureEntries += sub.Data.Length / 8;
                    break;
            }
        }

        // Export JSON
        var json = JsonSerializer.Serialize(landData, s_jsonOptions);
        File.WriteAllText(Path.Combine(outputDir, $"{formIdHex}_data.json"), json);
    }

    private static void ExportNormalMap(byte[] data, string path)
    {
        ExportRgbMap(data, path);
    }

    private static (float[,] heights, float baseHeight) ParseHeightmap(byte[] data, bool bigEndian, bool debug = false)
    {
        // VHGT format from UESP:
        // - float offset: base height (multiplied by 8 to get world units)
        // - 33×33 signed bytes: height gradients (multiplied by 8)
        // - 3 bytes: unused
        //
        // The algorithm accumulates row deltas, applying the first delta to the running
        // offset and subsequent deltas to a row offset to build the 33x33 grid.

        var baseHeight = bigEndian
            ? BitConverter.ToSingle([data[3], data[2], data[1], data[0]], 0)
            : BitConverter.ToSingle(data, 0);

        var heights = new float[CellGridSize, CellGridSize];

        var offset = baseHeight * 8f;
        var rowOffset = 0f;

        for (var i = 0; i < 1089; i++)
        {
            var idx = 4 + i;
            if (idx >= data.Length) continue;

            var value = (sbyte)data[idx] * 8f;
            var r = i / CellGridSize;
            var c = i % CellGridSize;

            if (c == 0)
            {
                rowOffset = 0;
                offset += value;
            }
            else
            {
                rowOffset += value;
            }

            heights[c, r] = offset + rowOffset;

            if (debug && r < 2 && c < 5)
                Console.WriteLine(
                    $"  [{c},{r}] i={i} grad={(sbyte)data[idx]} offset={offset:F1} rowOffset={rowOffset:F1} height={heights[c, r]:F1}");
        }

        return (heights, baseHeight);
    }

    private static void ExportHeightmap(float[,] heights, string path)
    {
        var minHeight = float.MaxValue;
        var maxHeight = float.MinValue;

        for (var y = 0; y < CellGridSize; y++)
            for (var x = 0; x < CellGridSize; x++)
            {
                minHeight = Math.Min(minHeight, heights[x, y]);
                maxHeight = Math.Max(maxHeight, heights[x, y]);
            }

        var pixels = new byte[CellGridSize * CellGridSize];
        var range = maxHeight - minHeight;

        for (var y = 0; y < CellGridSize; y++)
            for (var x = 0; x < CellGridSize; x++)
            {
                byte intensity;
                if (range > 0.001f)
                    intensity = (byte)((heights[x, y] - minHeight) / range * 255);
                else
                    intensity = 128;
                pixels[y * CellGridSize + x] = intensity;
            }

        PngWriter.SaveGrayscale(pixels, CellGridSize, CellGridSize, path);
    }

    private static void ExportVertexColors(byte[] data, string path)
    {
        ExportRgbMap(data, path);
    }

    private static void ExportRgbMap(byte[] data, string path)
    {
        var pixels = new byte[CellGridSize * CellGridSize * 3];

        for (var y = 0; y < CellGridSize; y++)
            for (var x = 0; x < CellGridSize; x++)
            {
                var srcIdx = (y * CellGridSize + x) * 3;
                var dstIdx = (y * CellGridSize + x) * 3;
                if (srcIdx + 2 < data.Length)
                {
                    pixels[dstIdx + 0] = data[srcIdx + 0];
                    pixels[dstIdx + 1] = data[srcIdx + 1];
                    pixels[dstIdx + 2] = data[srcIdx + 2];
                }
            }

        PngWriter.SaveRgb(pixels, CellGridSize, CellGridSize, path);
    }

    private static TextureLayerInfo ParseTextureLayer(byte[] data, bool bigEndian)
    {
        var formId = bigEndian
            ? BinaryUtils.ReadUInt32BE(data)
            : BinaryUtils.ReadUInt32LE(data);

        var quadrant = data[4];
        var unknown = data[5];

        var layer = bigEndian
            ? BinaryUtils.ReadUInt16BE(data, 6)
            : BinaryUtils.ReadUInt16LE(data, 6);

        return new TextureLayerInfo
        {
            TextureFormId = $"0x{formId:X8}",
            Quadrant = quadrant,
            QuadrantName = quadrant switch
            {
                0 => "BottomLeft",
                1 => "BottomRight",
                2 => "TopLeft",
                3 => "TopRight",
                _ => "Unknown"
            },
            Layer = layer,
            UnknownByte = unknown
        };
    }
}
