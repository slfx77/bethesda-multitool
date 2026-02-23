using FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers;
using Spectre.Console;
using System.Buffers.Binary;
using System.CommandLine;
using System.Globalization;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Schema;
using static EsmAnalyzer.Commands.OfstMathUtils;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Shared OFST extraction, bounds reading, cell grid mapping helpers reused by all OFST commands.
/// </summary>
internal static class OfstDataLoader
{
    internal const string FilePathDescription = "Path to the ESM file";
    internal const string WorldArgumentName = "world";
    internal const string WorldFormIdDescription = "WRLD FormID (hex, e.g., 0x0000003C)";
    internal const string LimitOptionShort = "-l";
    internal const string LimitOptionLong = "--limit";
    internal const string ColumnIndexLabel = "Index";
    internal const string ErrorReadBounds = "[red]ERROR:[/] Failed to read WRLD bounds";
    internal const string ErrorInvalidBounds = "[red]ERROR:[/] Invalid WRLD bounds";
    internal const float UnsetFloatThreshold = 1e20f;

    internal static bool TryParseFormId(string text, out uint formId)
    {
        var parsed = EsmFileLoader.ParseFormId(text);
        if (parsed == null)
        {
            formId = 0;
            return false;
        }

        formId = parsed.Value;
        return true;
    }

    internal static (AnalyzerRecordInfo? Record, byte[]? RecordData) FindWorldspaceRecord(byte[] data, bool bigEndian,
        uint formId)
    {
        var records = EsmRecordParser.ScanForRecordType(data, bigEndian, "WRLD");
        var match = records.FirstOrDefault(r => r.FormId == formId);
        if (match == null)
        {
            return (null, null);
        }

        try
        {
            var recordData = EsmHelpers.GetRecordData(data, match, bigEndian);
            return (match, recordData);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]WARN:[/] Failed to read WRLD record 0x{formId:X8}: {ex.Message}");
            return (null, null);
        }
    }

    internal static byte[]? GetOfstData(byte[] recordData, bool bigEndian)
    {
        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);
        var ofst = subrecords.FirstOrDefault(s => s.Signature == "OFST");
        return ofst?.Data;
    }

    internal static List<uint> ParseOffsets(byte[] ofstData, bool bigEndian)
    {
        var offsets = new List<uint>(ofstData.Length / 4);
        var count = ofstData.Length / 4;

        for (var i = 0; i < count; i++)
        {
            var value = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(ofstData.AsSpan(i * 4, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(ofstData.AsSpan(i * 4, 4));
            offsets.Add(value);
        }

        return offsets;
    }

    internal static bool TryGetWorldBounds(List<AnalyzerSubrecordInfo> subrecords, bool bigEndian,
        out int minX, out int maxX, out int minY, out int maxY)
    {
        minX = 0;
        maxX = 0;
        minY = 0;
        maxY = 0;

        var nam0 = subrecords.FirstOrDefault(s => s.Signature == "NAM0");
        var nam9 = subrecords.FirstOrDefault(s => s.Signature == "NAM9");
        if (nam0 == null || nam9 == null || nam0.Data.Length < 8 || nam9.Data.Length < 8)
        {
            return false;
        }

        var minXf = ReadFloat(nam0.Data, 0, bigEndian);
        var minYf = ReadFloat(nam0.Data, 4, bigEndian);
        var maxXf = ReadFloat(nam9.Data, 0, bigEndian);
        var maxYf = ReadFloat(nam9.Data, 4, bigEndian);

        if (IsUnsetFloat(minXf))
        {
            minXf = 0;
        }

        if (IsUnsetFloat(minYf))
        {
            minYf = 0;
        }

        if (IsUnsetFloat(maxXf))
        {
            maxXf = 0;
        }

        if (IsUnsetFloat(maxYf))
        {
            maxYf = 0;
        }

        const float cellScale = 4096f;
        minX = (int)Math.Round(minXf / cellScale);
        minY = (int)Math.Round(minYf / cellScale);
        maxX = (int)Math.Round(maxXf / cellScale);
        maxY = (int)Math.Round(maxYf / cellScale);

        return true;
    }

    internal static bool TryGetCellGrid(List<AnalyzerSubrecordInfo> subrecords, bool bigEndian,
        out int gridX, out int gridY)
    {
        gridX = 0;
        gridY = 0;

        var xclc = subrecords.FirstOrDefault(s => s.Signature == "XCLC");
        if (xclc == null || xclc.Data.Length < 8)
        {
            return false;
        }

        gridX = bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(xclc.Data.AsSpan(0, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(0, 4));
        gridY = bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(xclc.Data.AsSpan(4, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(4, 4));

        return true;
    }

    internal static float ReadFloat(byte[] data, int offset, bool bigEndian)
    {
        if (offset + 4 > data.Length)
        {
            return 0;
        }

        var value = bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        return BitConverter.Int32BitsToSingle((int)value);
    }

    internal static bool IsUnsetFloat(float value)
    {
        return float.IsNaN(value) || value <= -UnsetFloatThreshold || value >= UnsetFloatThreshold;
    }

    internal static bool TryGetWorldContext(byte[] data, bool bigEndian, uint worldFormId,
        out WorldContext context)
    {
        context = default!;

        var (wrldRecord, wrldData) = FindWorldspaceRecord(data, bigEndian, worldFormId);
        if (wrldRecord == null || wrldData == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] WRLD record not found for FormID 0x{worldFormId:X8}");
            return false;
        }

        var ofst = GetOfstData(wrldData, bigEndian);
        if (ofst == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] OFST subrecord not found for WRLD 0x{worldFormId:X8}");
            return false;
        }

        var subs = EsmRecordParser.ParseSubrecords(wrldData, bigEndian);
        if (!TryGetWorldBounds(subs, bigEndian, out var minX, out var maxX, out var minY, out var maxY))
        {
            AnsiConsole.MarkupLine(ErrorReadBounds);
            return false;
        }

        var columns = maxX - minX + 1;
        var rows = maxY - minY + 1;
        if (columns <= 0 || rows <= 0)
        {
            AnsiConsole.MarkupLine(ErrorInvalidBounds);
            return false;
        }

        var offsets = ParseOffsets(ofst, bigEndian);
        var boundsText = $"X[{minX},{maxX}] Y[{minY},{maxY}] ({columns}x{rows})";

        context = new WorldContext(wrldRecord, wrldData, offsets, minX, minY, maxX, maxY, columns, rows, boundsText);
        return true;
    }

    internal static bool TryResolveCellGrid(byte[] data, bool bigEndian, uint cellFormId, out int gridX, out int gridY)
    {
        gridX = 0;
        gridY = 0;

        var cellRecord = EsmRecordParser.ScanForRecordType(data, bigEndian, "CELL")
            .FirstOrDefault(r => r.FormId == cellFormId);
        if (cellRecord == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] CELL record not found for FormID 0x{cellFormId:X8}");
            return false;
        }

        var cellData = EsmHelpers.GetRecordData(data, cellRecord, bigEndian);
        var cellSubs = EsmRecordParser.ParseSubrecords(cellData, bigEndian);
        if (!TryGetCellGrid(cellSubs, bigEndian, out gridX, out gridY))
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] CELL has no XCLC grid data");
            return false;
        }

        return true;
    }

    internal static bool TryGetOfstIndex(WorldContext context, int gridX, int gridY, out int index)
    {
        index = -1;

        var col = gridX - context.MinX;
        var row = gridY - context.MinY;
        if (col < 0 || col >= context.Columns || row < 0 || row >= context.Rows)
        {
            var message =
                $"Cell grid {gridX},{gridY} outside WRLD bounds X[{context.MinX},{context.MaxX}] Y[{context.MinY},{context.MaxY}]";
            AnsiConsole.MarkupLine($"[red]ERROR:[/] {Markup.Escape(message)}");
            return false;
        }

        index = (row * context.Columns) + col;
        var ofstCount = context.Offsets.Count;
        if (index < 0 || index >= ofstCount)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] OFST index {index} out of range (0-{ofstCount - 1})");
            return false;
        }

        return true;
    }

    internal static void GetGridForIndex(WorldContext context, int index, out int gridX, out int gridY)
    {
        var row = index / context.Columns;
        var col = index % context.Columns;
        gridX = col + context.MinX;
        gridY = row + context.MinY;
    }

    internal static bool TryLoadWorldRecord(string filePath, string formIdText, out EsmFileLoadResult esm,
        out AnalyzerRecordInfo record, out byte[] recordData, out uint formId)
    {
        esm = null!;
        record = null!;
        recordData = Array.Empty<byte>();
        if (!TryParseFormId(formIdText, out formId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid WRLD FormID: {formIdText}");
            return false;
        }

        var loaded = EsmFileLoader.Load(filePath, false);
        if (loaded == null)
        {
            return false;
        }

        var (wrldRecord, wrldData) = FindWorldspaceRecord(loaded.Data, loaded.IsBigEndian, formId);
        if (wrldRecord == null || wrldData == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] WRLD record not found for FormID 0x{formId:X8}");
            return false;
        }

        esm = loaded;
        record = wrldRecord;
        recordData = wrldData;
        return true;
    }

    internal static bool TryGetWorldEntries(string filePath, string worldFormIdText, out WorldEntries world)
    {
        world = default!;

        if (!TryParseFormId(worldFormIdText, out var worldFormId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid WRLD FormID: {worldFormIdText}");
            return false;
        }

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null)
        {
            return false;
        }

        if (!TryGetWorldContext(esm.Data, esm.IsBigEndian, worldFormId, out var context))
        {
            return false;
        }

        var entries = BuildOfstEntries(context, esm.Data, esm.IsBigEndian);
        var ordered = entries.OrderBy(e => e.RecordOffset).ToList();

        world = new WorldEntries(worldFormId, context, ordered, esm.Data, esm.IsBigEndian);
        return true;
    }

    internal static bool TryGetTileGrid(WorldContext context, int tileSize, int tileX, int tileY, out int tilesX,
        out int tilesY)
    {
        tilesX = 0;
        tilesY = 0;

        if (tileSize <= 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Tile size must be > 0");
            return false;
        }

        if (tileX < 0 || tileY < 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] --tile-x and --tile-y are required");
            return false;
        }

        tilesX = (context.Columns + tileSize - 1) / tileSize;
        tilesY = (context.Rows + tileSize - 1) / tileSize;
        if (tileX < 0 || tileX >= tilesX || tileY < 0 || tileY >= tilesY)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Tile out of range. Tiles are {tilesX}x{tilesY}.");
            return false;
        }

        return true;
    }

    internal static bool TryGetTileSize(WorldContext context, int tileSize, out int tilesX, out int tilesY)
    {
        tilesX = 0;
        tilesY = 0;

        if (tileSize <= 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Tile size must be > 0");
            return false;
        }

        tilesX = (context.Columns + tileSize - 1) / tileSize;
        tilesY = (context.Rows + tileSize - 1) / tileSize;
        return true;
    }

    internal static List<OfstLayoutEntry> BuildOfstEntries(WorldContext context, byte[] data, bool bigEndian)
    {
        var records = EsmRecordParser.ScanAllRecords(data, bigEndian)
            .OrderBy(r => r.Offset)
            .ToList();

        var entries = new List<OfstLayoutEntry>(context.Offsets.Count);
        for (var index = 0; index < context.Offsets.Count; index++)
        {
            var entry = context.Offsets[index];
            if (entry == 0)
            {
                continue;
            }

            var row = index / context.Columns;
            var col = index % context.Columns;
            var gridX = col + context.MinX;
            var gridY = row + context.MinY;

            var resolvedOffset = context.WrldRecord.Offset + entry;
            var match = FindRecordAtOffset(records, resolvedOffset);

            if (match == null || match.Signature != "CELL")
            {
                continue;
            }

            var morton = Morton2D((uint)col, (uint)row);

            entries.Add(new OfstLayoutEntry(index, row, col, gridX, gridY, entry, resolvedOffset, match.FormId,
                morton, match.Offset));
        }

        return entries;
    }

    internal static AnalyzerRecordInfo? FindRecordAtOffset(List<AnalyzerRecordInfo> records, uint offset)
    {
        foreach (var record in records)
        {
            var start = record.Offset;
            var end = record.Offset + record.TotalSize;
            if (offset >= start && offset < end)
            {
                return record;
            }
        }

        return null;
    }

    internal static int ResolveBaseOffset(byte[] data, bool bigEndian, uint wrldOffset, uint wrldFormId, string baseMode)
    {
        return baseMode.ToLowerInvariant() switch
        {
            "file" => 0,
            "wrld" => (int)wrldOffset,
            "grup" => FindTopLevelGroupOffset(data, bigEndian, "WRLD"),
            "world" => FindWorldChildrenGroupOffset(data, bigEndian, wrldFormId),
            _ => -1
        };
    }

    private static int FindWorldChildrenGroupOffset(byte[] data, bool bigEndian, uint worldFormId)
    {
        return FindTopLevelGroupOffset(data, bigEndian, header =>
        {
            var labelValue = BinaryPrimitives.ReadUInt32LittleEndian(header.Label);
            return header.GroupType == 1 && labelValue == worldFormId;
        });
    }

    private static int FindTopLevelGroupOffset(byte[] data, bool bigEndian, string label)
    {
        return FindTopLevelGroupOffset(data, bigEndian, header =>
            header.GroupType == 0 && Encoding.ASCII.GetString(header.Label) == label);
    }

    private static int FindTopLevelGroupOffset(byte[] data, bool bigEndian, Func<GroupHeader, bool> matches)
    {
        var offset = 0;
        while (offset + EsmParser.MainRecordHeaderSize <= data.Length)
        {
            var header = EsmParser.ParseGroupHeader(data.AsSpan(offset), bigEndian);
            if (header != null)
            {
                if (matches(header))
                {
                    return offset;
                }

                if (!TryGetGroupEnd(header, offset, data.Length, out var groupEnd))
                {
                    return -1;
                }

                offset = groupEnd;
                continue;
            }

            if (!TryAdvanceRecord(data, bigEndian, offset, out var recordEnd))
            {
                return -1;
            }

            offset = recordEnd;
        }

        return -1;
    }

    private static bool TryGetGroupEnd(GroupHeader header, int offset, int dataLength, out int groupEnd)
    {
        groupEnd = offset + (int)header.GroupSize;
        return groupEnd > offset && groupEnd <= dataLength;
    }

    private static bool TryAdvanceRecord(byte[] data, bool bigEndian, int offset, out int recordEnd)
    {
        recordEnd = -1;
        var recordHeader = EsmParser.ParseRecordHeader(data.AsSpan(offset), bigEndian);
        if (recordHeader == null)
        {
            return false;
        }

        recordEnd = offset + EsmParser.MainRecordHeaderSize + (int)recordHeader.DataSize;
        return recordEnd > offset && recordEnd <= data.Length;
    }

}
