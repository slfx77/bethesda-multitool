using System.Text;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using Xbox360MemoryCarver.Core.Utils;

namespace EsmAnalyzer.Core;

/// <summary>
///     Utilities for working with LAND heightmap data.
/// </summary>
public static class HeightmapUtils
{
    /// <summary>
    ///     Parses VHGT heightmap data into a 2D array of heights.
    /// </summary>
    /// <param name="data">Raw VHGT subrecord data.</param>
    /// <param name="bigEndian">True if data is big-endian (Xbox 360).</param>
    /// <returns>33×33 array of height values.</returns>
    public static float[,] ParseVhgtData(byte[] data, bool bigEndian)
    {
        if (data.Length < EsmConstants.VhgtBaseHeightSize + EsmConstants.LandGridArea)
            throw new ArgumentException($"VHGT data too small: {data.Length} bytes");

        var baseHeight = bigEndian
            ? BitConverter.ToSingle([data[3], data[2], data[1], data[0]], 0)
            : BitConverter.ToSingle(data, 0);

        var heights = new float[EsmConstants.LandGridSize, EsmConstants.LandGridSize];
        var offset = baseHeight * 8f;
        var rowOffset = 0f;

        for (var i = 0; i < EsmConstants.LandGridArea; i++)
        {
            var idx = EsmConstants.VhgtBaseHeightSize + i;
            if (idx >= data.Length) continue;

            var value = (sbyte)data[idx] * 8f;
            var row = i / EsmConstants.LandGridSize;
            var col = i % EsmConstants.LandGridSize;

            if (col == 0)
            {
                rowOffset = 0;
                offset += value;
            }
            else
            {
                rowOffset += value;
            }

            heights[col, row] = offset + rowOffset;
        }

        return heights;
    }

    /// <summary>
    ///     Extracts all heightmaps for a worldspace.
    /// </summary>
    /// <param name="data">Raw ESM file data.</param>
    /// <param name="bigEndian">True if data is big-endian (Xbox 360).</param>
    /// <param name="worldspaceFormId">FormID of the target worldspace.</param>
    /// <returns>Dictionary of heightmaps keyed by cell grid coordinates, and cell editor IDs.</returns>
    public static (Dictionary<(int x, int y), float[,]> heightmaps, Dictionary<(int x, int y), string> cellNames)
        ExtractWorldspaceHeightmaps(byte[] data, bool bigEndian, uint worldspaceFormId)
    {
        var heightmaps = new Dictionary<(int x, int y), float[,]>();
        var cellNames = new Dictionary<(int x, int y), string>();

        // Find all CELL and LAND records for this worldspace
        var (cellRecords, landRecords) = FindCellsAndLandsForWorldspace(data, bigEndian, worldspaceFormId);

        // Build cell map with grid coordinates
        var cellMap = new Dictionary<(int x, int y), CellRecordInfo>();
        foreach (var cell in cellRecords)
        {
            try
            {
                var recordData = GetRecordData(data, cell, bigEndian);
                var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

                // Check for EDID (editor ID)
                var edid = EsmRecordParser.FindSubrecord(subrecords, "EDID");
                var editorId = edid != null ? EsmRecordParser.GetSubrecordString(edid) : null;

                // Check for XCLC (cell grid coordinates)
                var xclc = EsmRecordParser.FindSubrecord(subrecords, "XCLC");
                if (xclc != null && xclc.Data.Length >= 8)
                {
                    var gridX = EsmBinary.ReadInt32(xclc.Data, 0, bigEndian);
                    var gridY = EsmBinary.ReadInt32(xclc.Data, 4, bigEndian);

                    cellMap[(gridX, gridY)] = new CellRecordInfo
                    {
                        FormId = cell.FormId,
                        GridX = gridX,
                        GridY = gridY,
                        EditorId = editorId,
                        Offset = cell.Offset
                    };

                    // Store editor ID if present
                    if (!string.IsNullOrEmpty(editorId))
                        cellNames[(gridX, gridY)] = editorId;
                }
            }
            catch
            {
                // Skip cells that fail to parse
            }
        }

        // Get all cell offsets for boundary checking
        var allCellOffsets = cellRecords.Select(c => c.Offset).OrderBy(o => o).ToList();

        // Sort LANDs by offset
        var sortedLands = landRecords.OrderBy(l => l.Offset).ToList();

        // Match LANDs to cells
        foreach (var cell in cellMap.Values.OrderBy(c => c.Offset))
        {
            // Find LAND records after this cell
            var landsAfterCell = sortedLands.Where(l => l.Offset > cell.Offset).Take(5).ToList();

            foreach (var land in landsAfterCell)
            {
                // Check if this LAND belongs to a later cell
                var nextCellOffset = allCellOffsets.FirstOrDefault(o => o > cell.Offset);
                if (nextCellOffset != default && land.Offset > nextCellOffset)
                    break;

                try
                {
                    var recordData = GetRecordData(data, land, bigEndian);
                    var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

                    var vhgt = EsmRecordParser.FindSubrecord(subrecords, "VHGT");
                    if (vhgt != null && vhgt.Data.Length >= EsmConstants.VhgtBaseHeightSize + EsmConstants.LandGridArea)
                    {
                        var heights = ParseVhgtData(vhgt.Data, bigEndian);
                        if (!heightmaps.ContainsKey((cell.GridX, cell.GridY)))
                            heightmaps[(cell.GridX, cell.GridY)] = heights;
                    }

                    sortedLands.Remove(land);
                    break;
                }
                catch
                {
                    sortedLands.Remove(land);
                }
            }
        }

        return (heightmaps, cellNames);
    }

    /// <summary>
    ///     Finds CELL and LAND records belonging to a worldspace.
    /// </summary>
    public static (List<AnalyzerRecordInfo> cells, List<AnalyzerRecordInfo> lands)
        FindCellsAndLandsForWorldspace(byte[] data, bool bigEndian, uint worldspaceFormId)
    {
        var cells = new List<AnalyzerRecordInfo>();
        var lands = new List<AnalyzerRecordInfo>();

        var header = EsmParser.ParseFileHeader(data);
        if (header == null) return (cells, lands);

        // Skip TES4 header
        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian);
        if (tes4Header == null) return (cells, lands);

        var offset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;

        // Track if we're inside the target worldspace's GRUP
        var inTargetWorldspace = false;
        var grupEndOffset = 0;

        while (offset + EsmConstants.GrupHeaderSize <= data.Length)
        {
            var headerData = data.AsSpan(offset);
            var sig = bigEndian
                ? new string([(char)headerData[3], (char)headerData[2], (char)headerData[1], (char)headerData[0]])
                : Encoding.ASCII.GetString(data, offset, 4);

            if (sig == "GRUP")
            {
                var grupSize = EsmBinary.ReadUInt32(data, offset + 4, bigEndian);
                var grupType = EsmBinary.ReadUInt32(data, offset + 12, bigEndian);
                var grupLabel = EsmBinary.ReadUInt32(data, offset + 8, bigEndian);

                // Type 1 = Worldspace children
                if (grupType == 1 && grupLabel == worldspaceFormId)
                {
                    inTargetWorldspace = true;
                    grupEndOffset = offset + (int)grupSize;
                }

                offset += EsmParser.MainRecordHeaderSize; // Enter GRUP
            }
            else
            {
                var recSize = EsmBinary.ReadUInt32(data, offset + 4, bigEndian);
                var flags = EsmBinary.ReadUInt32(data, offset + 8, bigEndian);
                var formId = EsmBinary.ReadUInt32(data, offset + 12, bigEndian);

                if (inTargetWorldspace)
                {
                    if (sig == "CELL")
                        cells.Add(new AnalyzerRecordInfo
                        {
                            Signature = sig,
                            Offset = (uint)offset,
                            DataSize = recSize,
                            TotalSize = EsmParser.MainRecordHeaderSize + recSize,
                            FormId = formId,
                            Flags = flags
                        });
                    else if (sig == "LAND")
                        lands.Add(new AnalyzerRecordInfo
                        {
                            Signature = sig,
                            Offset = (uint)offset,
                            DataSize = recSize,
                            TotalSize = EsmParser.MainRecordHeaderSize + recSize,
                            FormId = formId,
                            Flags = flags
                        });
                }

                offset += EsmParser.MainRecordHeaderSize + (int)recSize;

                // Check if we've exited the target worldspace GRUP
                if (inTargetWorldspace && offset >= grupEndOffset)
                    inTargetWorldspace = false;
            }
        }

        return (cells, lands);
    }

    /// <summary>
    ///     Gets decompressed record data.
    /// </summary>
    public static byte[] GetRecordData(byte[] data, AnalyzerRecordInfo record, bool bigEndian)
    {
        var recordDataStart = (int)record.Offset + EsmParser.MainRecordHeaderSize;
        var recordDataEnd = recordDataStart + (int)record.DataSize;

        if (recordDataEnd > data.Length)
            throw new InvalidOperationException($"Record data extends beyond file: {record.Signature} at 0x{record.Offset:X8}");

        var recordData = data.AsSpan(recordDataStart, (int)record.DataSize).ToArray();

        // Decompress if needed
        if (record.IsCompressed && recordData.Length >= 4)
        {
            var decompressedSize = EsmBinary.ReadUInt32(recordData, 0, bigEndian);
            recordData = DecompressZlib(recordData.AsSpan(4).ToArray(), (int)decompressedSize);
        }

        return recordData;
    }

    /// <summary>
    ///     Decompresses zlib-compressed data.
    /// </summary>
    private static byte[] DecompressZlib(byte[] compressedData, int expectedSize)
    {
        using var inputStream = new MemoryStream(compressedData);
        using var zlibStream = new System.IO.Compression.ZLibStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
        using var outputStream = new MemoryStream(expectedSize);

        zlibStream.CopyTo(outputStream);
        return outputStream.ToArray();
    }

    /// <summary>
    ///     Internal cell record info for heightmap extraction.
    /// </summary>
    private sealed class CellRecordInfo
    {
        public uint FormId { get; init; }
        public int GridX { get; init; }
        public int GridY { get; init; }
        public string? EditorId { get; init; }
        public uint Offset { get; init; }
    }
}
