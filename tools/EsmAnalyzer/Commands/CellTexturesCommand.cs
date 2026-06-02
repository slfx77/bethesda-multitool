using System.CommandLine;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Subrecords;
using FalloutXbox360Utils.Core.Utils;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Diagnostic command: dumps raw BTXT/ATXT bytes for the LAND record whose parent
///     CELL matches the supplied FormID. Used to verify Xbox 360 BE→LE handling for
///     texture-layer parsing.
/// </summary>
internal static class CellTexturesCommand
{
    internal static Command Create()
    {
        var command = new Command("cell-textures",
            "Dump raw BTXT/ATXT bytes for the LAND record whose parent CELL matches the supplied FormID");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var cellArg = new Argument<string>("cell-formid")
        {
            Description = "Parent CELL FormID (hex, e.g., 0x000E1943)"
        };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(cellArg);
        command.SetAction(parseResult => Execute(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(cellArg)!));

        return command;
    }

    internal static int Execute(string filePath, string cellFormIdText)
    {
        var cellFormId = EsmFileLoader.ParseFormId(cellFormIdText);
        if (cellFormId == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid FormID: {cellFormIdText}");
            return 1;
        }

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null)
        {
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[cyan]File:[/] {Path.GetFileName(filePath)} ({(esm.IsBigEndian ? "Big-endian" : "Little-endian")})");
        AnsiConsole.MarkupLine($"[cyan]Target CELL:[/] 0x{cellFormId.Value:X8}");
        AnsiConsole.WriteLine();

        // Walk every GRUP header in the file; find the "Cell Temporary Children" GRUP
        // (group type 9) whose parent FormID equals the target cell. Then enumerate the
        // records inside that GRUP and pick out the LAND.
        var match = FindCellTempChildrenGrup(esm.Data, cellFormId.Value, esm.IsBigEndian);
        if (match is null)
        {
            AnsiConsole.MarkupLine(
                $"[red]No Cell Temporary Children GRUP found for CELL 0x{cellFormId.Value:X8}.[/]");
            return 1;
        }

        var land = FindLandInGrup(esm.Data, match.Value.start, match.Value.end, esm.IsBigEndian);
        if (land is null)
        {
            AnsiConsole.MarkupLine(
                $"[red]Cell GRUP exists at 0x{match.Value.start:X} but contains no LAND record.[/]");
            return 1;
        }

        DumpLand(esm.Data, land, esm.IsBigEndian, filePath, cellFormId.Value);
        return 0;
    }

    private static (long start, long end)? FindCellTempChildrenGrup(byte[] data, uint targetCell, bool bigEndian)
    {
        // Xbox 360 stores signature bytes in reversed order (since the record header reads as
        // BE uint32). "GRUP" PC bytes are 47 52 55 50; Xbox bytes are 50 55 52 47.
        var g0 = bigEndian ? (byte)'P' : (byte)'G';
        var g1 = bigEndian ? (byte)'U' : (byte)'R';
        var g2 = bigEndian ? (byte)'R' : (byte)'U';
        var g3 = bigEndian ? (byte)'G' : (byte)'P';

        long off = 0;
        var grupsSeen = 0;
        var grupsWithCellLabel = 0;
        while (off + 24 <= data.LongLength)
        {
            if (data[off] == g0 && data[off + 1] == g1 && data[off + 2] == g2 && data[off + 3] == g3)
            {
                grupsSeen++;
                var size = bigEndian
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)off + 4))
                    : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)off + 4));
                var label = bigEndian
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)off + 8))
                    : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)off + 8));
                var groupType = bigEndian
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)off + 12))
                    : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)off + 12));

                if (label == targetCell)
                {
                    grupsWithCellLabel++;
                    AnsiConsole.MarkupLine(
                        $"[yellow]Found GRUP at 0x{off:X} type={groupType} label=0x{label:X8} size={size}[/]");
                }

                if (groupType == 9 && label == targetCell)
                {
                    AnsiConsole.MarkupLine($"[grey]Scanned {grupsSeen} GRUPs; {grupsWithCellLabel} matching label.[/]");
                    return (off, off + size);
                }

                // Top-level GRUPs (type 0 etc.) contain nested GRUPs. We don't need to skip;
                // we just keep walking byte-by-record. The trick: a GRUP's `size` includes
                // its 24-byte header and ALL contained records/GRUPs. So advancing by `size`
                // walks past the whole tree. But we must not over-advance for type 0 since
                // we want to recurse into it. For our purposes, the safest pattern is to
                // ALWAYS step into GRUPs (advance by 24, not by size), so we visit every
                // nested GRUP header in turn.
                off += 24;
                continue;
            }

            // Otherwise this must be a record. Skip the whole record (24-byte header + DataSize).
            var dataSize = bigEndian
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)off + 4))
                : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)off + 4));
            off += 24 + dataSize;
        }

        AnsiConsole.MarkupLine($"[grey]Scanned {grupsSeen} GRUPs end-to-end; {grupsWithCellLabel} matching label.[/]");
        return null;
    }

    private static AnalyzerRecordInfo? FindLandInGrup(byte[] data, long grupStart, long grupEnd, bool bigEndian)
    {
        AnalyzerRecordInfo? first = null;
        var off = grupStart + 24;
        while (off + 24 <= grupEnd && off + 24 <= data.LongLength)
        {
            var sig = bigEndian
                ? new string([(char)data[off + 3], (char)data[off + 2], (char)data[off + 1], (char)data[off]])
                : System.Text.Encoding.ASCII.GetString(data, (int)off, 4);
            var dataSize = bigEndian
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)off + 4))
                : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)off + 4));
            if (sig == "LAND")
            {
                var flags = bigEndian
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)off + 8))
                    : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)off + 8));
                var formId = bigEndian
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)off + 12))
                    : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)off + 12));
                AnsiConsole.MarkupLine(
                    $"[grey]  LAND in GRUP: FormID=0x{formId:X8} at 0x{off:X} payload={dataSize}[/]");
                var info = new AnalyzerRecordInfo
                {
                    Signature = "LAND",
                    FormId = formId,
                    Flags = flags,
                    DataSize = dataSize,
                    Offset = (uint)off,
                    TotalSize = 24 + dataSize
                };
                first ??= info;
            }
            off += 24 + dataSize;
        }
        return first;
    }

    private static void DumpLand(byte[] data, AnalyzerRecordInfo land, bool bigEndian,
        string filePath, uint cellFormId)
    {
        var recordData = EsmHelpers.GetRecordData(data, land, bigEndian);
        AnsiConsole.MarkupLine(
            $"[green]Found LAND 0x{land.FormId:X8}[/] at file offset 0x{land.Offset:X} ({recordData.Length} byte payload)");
        AnsiConsole.WriteLine();

        var btxtCount = 0;
        var atxtCount = 0;
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Sig")
            .AddColumn("Offset")
            .AddColumn("Raw bytes (8)")
            .AddColumn("Decoded TexFormId")
            .AddColumn("Decoded Quad")
            .AddColumn("Decoded PlatformFlag")
            .AddColumn("Decoded Layer");

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(recordData, recordData.Length, bigEndian))
        {
            if (sub.Signature != "BTXT" && sub.Signature != "ATXT") continue;
            if (sub.Signature == "BTXT") btxtCount++; else atxtCount++;

            var span = recordData.AsSpan(sub.DataOffset, Math.Min(sub.DataLength, 8));
            var hex = string.Join(' ', span.ToArray().Select(b => b.ToString("X2")));

            var formId = bigEndian
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(span)
                : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span);
            var quad = span[4];
            var pflag = span[5];
            var layer = bigEndian
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(span[6..])
                : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);

            _ = table.AddRow(
                sub.Signature,
                $"0x{sub.DataOffset:X}",
                hex,
                $"0x{formId:X8}",
                quad.ToString(),
                $"0x{pflag:X2}",
                layer.ToString());
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[cyan]Total BTXT:[/] {btxtCount}   [cyan]Total ATXT:[/] {atxtCount}");
        AnsiConsole.MarkupLine("[grey]Quadrant convention: 0=SW, 1=SE, 2=NW, 3=NE[/]");
        AnsiConsole.WriteLine();

        // Cross-check: run the production LandSubrecordParser.Parse and see what makes it to
        // LandVisualData.TextureLayers. Any divergence between raw subrecord count and parsed
        // layers count is a parser bug.
        var parseResult = LandSubrecordParser.Parse(recordData, recordData.Length, bigEndian);
        var parsedLayers = parseResult.VisualData?.TextureLayers ?? [];
        var parsedBtxtByQuad = new int[4];
        var parsedAtxtByQuad = new int[4];
        foreach (var l in parsedLayers)
        {
            if (l.Quadrant >= 4) continue;
            if (l.Kind == LandTextureLayerKind.Base) parsedBtxtByQuad[l.Quadrant]++;
            else parsedAtxtByQuad[l.Quadrant]++;
        }

        AnsiConsole.MarkupLine($"[cyan]LandSubrecordParser.Parse result:[/]");
        AnsiConsole.MarkupLine($"  Parsed BTXT total: {parsedLayers.Count(l => l.Kind == LandTextureLayerKind.Base)}");
        AnsiConsole.MarkupLine($"  Parsed ATXT total: {parsedLayers.Count(l => l.Kind == LandTextureLayerKind.Alpha)}");
        for (var q = 0; q < 4; q++)
        {
            var name = q switch { 0 => "SW", 1 => "SE", 2 => "NW", 3 => "NE", _ => "?" };
            AnsiConsole.MarkupLine($"  Quadrant {q} ({name}): {parsedBtxtByQuad[q]} BTXT, {parsedAtxtByQuad[q]} ATXT");
        }

        DumpAppLoadedLayers(filePath, cellFormId).GetAwaiter().GetResult();
    }

    private static async Task DumpAppLoadedLayers(string filePath, uint cellFormId)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[cyan]Reloading via UnifiedAnalyzer (full app pipeline)...[/]");
        using var result = await UnifiedAnalyzer.AnalyzeAsync(filePath, null, default);
        var cell = result.Records.Cells.FirstOrDefault(c => c.FormId == cellFormId);
        if (cell is null)
        {
            AnsiConsole.MarkupLine($"[red]Cell 0x{cellFormId:X8} not found in RecordCollection.[/]");
            return;
        }

        var layers = cell.LandVisualData?.TextureLayers;
        AnsiConsole.MarkupLine(
            $"[cyan]cell.LandVisualData:[/] {(cell.LandVisualData is null ? "null" : "set")}");
        AnsiConsole.MarkupLine(
            $"[cyan]cell.LandVisualData.TextureLayers count:[/] {layers?.Count ?? -1}");

        // NavMesh check — find any NAVM records pointing at this cell.
        var navMeshes = result.Records.NavMeshes
            .Where(nm => nm.CellFormId == cellFormId)
            .ToList();
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]NAVMs for cell:[/] {navMeshes.Count}");
        var totalNav = result.Records.NavMeshes.Count;
        var withCell = result.Records.NavMeshes.Count(nm => nm.CellFormId != 0);
        var uniqueCellsWithNav = result.Records.NavMeshes
            .Where(nm => nm.CellFormId != 0)
            .Select(nm => nm.CellFormId)
            .Distinct()
            .Count();
        var extCellsWithGrid = result.Records.Cells
            .Count(c => c.GridX.HasValue && c.GridY.HasValue && !c.IsInterior);
        var exteriorCellsWithNav = result.Records.NavMeshes
            .Where(nm => nm.CellFormId != 0)
            .Select(nm => nm.CellFormId)
            .Distinct()
            .Count(formId =>
            {
                var cell = result.Records.Cells.FirstOrDefault(c => c.FormId == formId);
                return cell is { IsInterior: false, GridX: not null, GridY: not null };
            });
        AnsiConsole.MarkupLine(
            $"[cyan]Total NAVMs in collection:[/] {totalNav} (of which {withCell} have non-zero CellFormId)");
        AnsiConsole.MarkupLine(
            $"[cyan]Unique cells with NAVMs:[/] {uniqueCellsWithNav}");
        AnsiConsole.MarkupLine(
            $"[cyan]Of exterior grid cells ({extCellsWithGrid}), NAVM-covered:[/] {exteriorCellsWithNav}");
        foreach (var nm in navMeshes)
        {
            var hasNvvx = nm.RawSubrecords.Any(s => s.Signature == "NVVX");
            var hasNvtr = nm.RawSubrecords.Any(s => s.Signature == "NVTR");
            var nvvxBytes = nm.RawSubrecords.FirstOrDefault(s => s.Signature == "NVVX").Bytes?.Length ?? 0;
            var nvtrBytes = nm.RawSubrecords.FirstOrDefault(s => s.Signature == "NVTR").Bytes?.Length ?? 0;
            AnsiConsole.MarkupLine(
                $"  NAVM 0x{nm.FormId:X8} VertexCount={nm.VertexCount} TriangleCount={nm.TriangleCount} " +
                $"NVVX={(hasNvvx ? $"{nvvxBytes}B" : "MISSING")} NVTR={(hasNvtr ? $"{nvtrBytes}B" : "MISSING")} " +
                $"subrecords={nm.RawSubrecords.Count}");

            // Print first 3 vertices' world coords so we can tell whether they're cell-local
            // (small magnitudes) or worldspace (large magnitudes around GridX*4096).
            var nvvx = nm.RawSubrecords.FirstOrDefault(s => s.Signature == "NVVX").Bytes;
            if (nvvx is { Length: >= 36 })
            {
                for (var v = 0; v < 3; v++)
                {
                    var x = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(v * 12, 4));
                    var y = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(v * 12 + 4, 4));
                    var z = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(v * 12 + 8, 4));
                    AnsiConsole.WriteLine($"    Vert {v} = ({x:F1}, {y:F1}, {z:F1})");
                }
            }
        }

        if (layers is null || layers.Count == 0) return;

        var btxtByQuad = new int[4];
        var atxtByQuad = new int[4];
        foreach (var l in layers)
        {
            if (l.Quadrant >= 4) continue;
            if (l.Kind == LandTextureLayerKind.Base) btxtByQuad[l.Quadrant]++;
            else atxtByQuad[l.Quadrant]++;
        }
        for (var q = 0; q < 4; q++)
        {
            var name = q switch { 0 => "SW", 1 => "SE", 2 => "NW", 3 => "NE", _ => "?" };
            AnsiConsole.MarkupLine($"  Quadrant {q} ({name}): {btxtByQuad[q]} BTXT, {atxtByQuad[q]} ATXT");
        }

        // Texture resolution chain: LTEX → TXST → DiffuseTexture path.
        // Also try loading from BSAs adjacent to the ESM.
        var bsaPaths = FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.BsaDiscovery.Discover(filePath).TexturesBsaPaths;
        var sources = bsaPaths.Length > 0
            ? FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures.NifTextureArchiveSourceFactory.Create(bsaPaths)
            : new List<FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures.INifTextureSource>();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]Texture BSAs discovered:[/] {bsaPaths.Length}");
        foreach (var p in bsaPaths) AnsiConsole.WriteLine($"  {p}");
        AnsiConsole.MarkupLine("[cyan]LTEX resolution check (for each unique LTEX FormID used by this cell):[/]");
        var uniqueLtexIds = layers
            .Select(l => l.TextureFormId)
            .Where(id => id != 0)
            .Distinct()
            .ToList();
        foreach (var ltexId in uniqueLtexIds)
        {
            var ltex = result.Records.LandTextures.FirstOrDefault(l => l.FormId == ltexId);
            if (ltex is null)
            {
                AnsiConsole.WriteLine($"  LTEX 0x{ltexId:X8}: NOT FOUND in collection");
                continue;
            }
            var txstId = ltex.TextureSetFormId;
            if (!txstId.HasValue)
            {
                AnsiConsole.WriteLine($"  LTEX 0x{ltexId:X8} (EditorID={ltex.EditorId ?? "?"}): TXST FormID is null");
                continue;
            }
            var txst = result.Records.TextureSets.FirstOrDefault(t => t.FormId == txstId.Value);
            if (txst is null)
            {
                AnsiConsole.WriteLine(
                    $"  LTEX 0x{ltexId:X8} (EditorID={ltex.EditorId ?? "?"}) -> TXST 0x{txstId.Value:X8}: TXST NOT FOUND");
                continue;
            }
            // Try loading the texture via the same pipeline the rendered layer uses.
            var rawPath = txst.DiffuseTexture ?? "";
            var normPath = FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures.NifTexturePathUtility.Normalize(rawPath);
            var tex = FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures.NifTextureLoader.TryLoadFromSources(normPath, sources);
            if (tex is null && normPath.EndsWith(".dds", StringComparison.Ordinal))
            {
                var ddxPath = string.Concat(normPath.AsSpan(0, normPath.Length - 4), ".ddx");
                tex = FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures.NifTextureLoader.TryLoadFromSources(ddxPath, sources);
            }
            var loadStatus = tex is null
                ? "LOAD FAILED"
                : $"loaded ({tex.MipLevels.Count} mips, base={tex.MipLevels[0].Width}x{tex.MipLevels[0].Height})";
            AnsiConsole.WriteLine(
                $"  LTEX 0x{ltexId:X8} (EditorID={ltex.EditorId ?? "?"}) -> {rawPath}: {loadStatus}");
        }

        foreach (var s in sources) s.Dispose();
    }
}
