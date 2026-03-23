using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     Fluent builder for complete synthetic ESM files with proper GRUP nesting.
///     Produces little-endian (PC-format) ESM files that can go through the full
///     ESM analysis pipeline (EnumerateRecordsWithGrups → BuildAllMaps → ParseAll).
/// </summary>
internal sealed class EsmTestFileBuilder
{
    private readonly List<byte[]> _topLevelChunks = [];

    /// <summary>
    ///     Build a complete ESM file byte array. Prepends a TES4 header automatically.
    /// </summary>
    public byte[] Build()
    {
        var tes4 = BuildTes4Header();
        var totalSize = tes4.Length + _topLevelChunks.Sum(c => c.Length);
        var result = new byte[totalSize];
        var offset = 0;

        Array.Copy(tes4, 0, result, 0, tes4.Length);
        offset += tes4.Length;

        foreach (var chunk in _topLevelChunks)
        {
            Array.Copy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }

        return result;
    }

    /// <summary>
    ///     Runs the full ESM analysis pipeline on the built file and returns all results.
    ///     This is the same pipeline used by PcFinalEsmPipelineCache.
    /// </summary>
    public PipelineResult BuildAndAnalyze()
    {
        var fileData = Build();
        var isBigEndian = EsmParser.IsBigEndian(fileData);
        var (parsedRecords, grupHeaders) = EsmParser.EnumerateRecordsWithGrups(fileData);

        var (cellToWorldspace, landToWorldspace, cellToRefr, topicToInfo) =
            EsmFileAnalyzer.BuildAllMaps(parsedRecords, grupHeaders);

        var scanResult = EsmDataExtractor.ConvertToScanResult(
            parsedRecords, isBigEndian, cellToWorldspace, landToWorldspace, cellToRefr, topicToInfo);

        EsmDataExtractor.ExtractRefrRecordsFromParsed(scanResult, parsedRecords, isBigEndian);

        // Build FormID correlations from pre-parsed subrecords
        var formIdMap = new Dictionary<uint, string>();
        foreach (var record in parsedRecords)
        {
            if (record.Header.FormId == 0 || formIdMap.ContainsKey(record.Header.FormId))
            {
                continue;
            }

            var editorId = record.Subrecords.FirstOrDefault(s => s.Signature == "EDID")?.DataAsString;
            if (!string.IsNullOrEmpty(editorId))
            {
                formIdMap[record.Header.FormId] = editorId;
            }
        }

        // Create MMF from synthetic data for RecordParser
        using var mmf = MemoryMappedFile.CreateNew(null, fileData.Length);
        using var accessor = mmf.CreateViewAccessor(0, fileData.Length);
        accessor.WriteArray(0, fileData, 0, fileData.Length);

        var parser = new RecordParser(scanResult, formIdMap, accessor, fileData.Length);
        var collection = parser.ParseAll();

        return new PipelineResult(parsedRecords, grupHeaders, scanResult, collection, isBigEndian, fileData);
    }

    #region Top-Level Record GRUPs

    /// <summary>
    ///     Add a Type 0 GRUP containing the given record byte arrays.
    /// </summary>
    public EsmTestFileBuilder AddTopLevelGrup(string label, params byte[][] records)
    {
        var contentSize = records.Sum(r => r.Length);
        var grupSize = 24 + contentSize;

        var grup = new byte[grupSize];
        WriteGrupHeader(grup, 0, (uint)grupSize, label, grupType: 0);

        var offset = 24;
        foreach (var record in records)
        {
            Array.Copy(record, 0, grup, offset, record.Length);
            offset += record.Length;
        }

        _topLevelChunks.Add(grup);
        return this;
    }

    /// <summary>
    ///     Add a raw chunk (pre-built GRUP) to the file.
    /// </summary>
    public EsmTestFileBuilder AddRawChunk(byte[] chunk)
    {
        _topLevelChunks.Add(chunk);
        return this;
    }

    #endregion

    #region Worldspace GRUP Builder

    /// <summary>
    ///     Build a complete worldspace Type 0 GRUP ("WRLD") with nested cell hierarchy.
    /// </summary>
    public EsmTestFileBuilder AddWorldspace(WorldspaceData worldspace)
    {
        using var ms = new MemoryStream();

        // WRLD record
        var wrldRecord = BuildRecord("WRLD", worldspace.FormId, 0,
            ("EDID", NullTermBytes(worldspace.EditorId)),
            ("FULL", NullTermBytes(worldspace.FullName ?? worldspace.EditorId)));
        ms.Write(wrldRecord);

        // Type 1 GRUP (World Children, label = WRLD FormID)
        var worldChildrenBytes = BuildWorldChildrenGrup(worldspace);
        ms.Write(worldChildrenBytes);

        var worldContent = ms.ToArray();

        // Wrap in Type 0 GRUP (label = "WRLD")
        var grup = new byte[24 + worldContent.Length];
        WriteGrupHeader(grup, 0, (uint)grup.Length, "WRLD", 0);
        Array.Copy(worldContent, 0, grup, 24, worldContent.Length);

        _topLevelChunks.Add(grup);
        return this;
    }

    private static byte[] BuildWorldChildrenGrup(WorldspaceData worldspace)
    {
        using var ms = new MemoryStream();

        // Persistent cell (if any persistent refs exist)
        if (worldspace.PersistentCell != null)
        {
            var cell = worldspace.PersistentCell;
            var cellRecord = BuildCellRecord(cell.FormId, cell.EditorId, null, null);
            ms.Write(cellRecord);

            // Type 8 GRUP (Persistent Children, label = cell FormID)
            if (cell.PersistentRefs.Count > 0)
            {
                var persistentGrup = BuildRefGrup(cell.FormId, 8, cell.PersistentRefs);
                ms.Write(persistentGrup);
            }

            // Type 9 GRUP (Temporary Children, label = cell FormID)
            if (cell.TemporaryRefs.Count > 0)
            {
                var tempGrup = BuildRefGrup(cell.FormId, 9, cell.TemporaryRefs);
                ms.Write(tempGrup);
            }
        }

        // Exterior cells in Type 4/5 GRUPs
        foreach (var cell in worldspace.ExteriorCells)
        {
            var blockX = cell.GridX / 4;
            var blockY = cell.GridY / 4;
            var subX = cell.GridX / 2;
            var subY = cell.GridY / 2;

            // Build cell content (CELL record + Type 8/9 child GRUPs)
            var cellContent = BuildExteriorCellContent(cell);

            // Wrap in Type 5 (Sub-Block)
            var subBlockLabel = ComposeGridLabel(subX, subY);
            var subBlock = new byte[24 + cellContent.Length];
            WriteGrupHeaderUint(subBlock, 0, (uint)subBlock.Length, subBlockLabel, 5);
            Array.Copy(cellContent, 0, subBlock, 24, cellContent.Length);

            // Wrap in Type 4 (Block)
            var blockLabel = ComposeGridLabel(blockX, blockY);
            var block = new byte[24 + subBlock.Length];
            WriteGrupHeaderUint(block, 0, (uint)block.Length, blockLabel, 4);
            Array.Copy(subBlock, 0, block, 24, subBlock.Length);

            ms.Write(block);
        }

        // Wrap everything in Type 1 GRUP (label = worldspace FormID)
        var content = ms.ToArray();
        var worldChildren = new byte[24 + content.Length];
        WriteGrupHeaderUint(worldChildren, 0, (uint)worldChildren.Length, worldspace.FormId, 1);
        Array.Copy(content, 0, worldChildren, 24, content.Length);

        return worldChildren;
    }

    private static byte[] BuildExteriorCellContent(CellData cell)
    {
        using var ms = new MemoryStream();

        var cellRecord = BuildCellRecord(cell.FormId, cell.EditorId, cell.GridX, cell.GridY);
        ms.Write(cellRecord);

        if (cell.PersistentRefs.Count > 0)
        {
            ms.Write(BuildRefGrup(cell.FormId, 8, cell.PersistentRefs));
        }

        if (cell.TemporaryRefs.Count > 0)
        {
            ms.Write(BuildRefGrup(cell.FormId, 9, cell.TemporaryRefs));
        }

        return ms.ToArray();
    }

    private static byte[] BuildRefGrup(uint cellFormId, int grupType, List<PlacedRefData> refs)
    {
        using var ms = new MemoryStream();
        foreach (var r in refs)
        {
            ms.Write(BuildPlacedRefRecord(r));
        }

        var content = ms.ToArray();
        var grup = new byte[24 + content.Length];
        WriteGrupHeaderUint(grup, 0, (uint)grup.Length, cellFormId, grupType);
        Array.Copy(content, 0, grup, 24, content.Length);
        return grup;
    }

    #endregion

    #region Record Builders

    private static byte[] BuildTes4Header()
    {
        // HEDR subrecord: version(4 float) + numRecords(4 int) + nextObjectId(4 uint) = 12 bytes
        var hedr = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(hedr, 1.34f);
        BinaryPrimitives.WriteInt32LittleEndian(hedr.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(hedr.AsSpan(8), 0x00000800);

        return BuildRecord("TES4", 0, 0, ("HEDR", hedr));
    }

    /// <summary>Build a LE record with subrecords.</summary>
    public static byte[] BuildRecord(string sig, uint formId, uint flags,
        params (string sig, byte[] data)[] subrecords)
    {
        var dataSize = subrecords.Sum(s => 6 + s.data.Length);
        var buf = new byte[24 + dataSize];

        // Record header
        Encoding.ASCII.GetBytes(sig, buf);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), formId);

        var offset = 24;
        foreach (var (subSig, data) in subrecords)
        {
            Encoding.ASCII.GetBytes(subSig, buf.AsSpan(offset));
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(offset + 4), (ushort)data.Length);
            Array.Copy(data, 0, buf, offset + 6, data.Length);
            offset += 6 + data.Length;
        }

        return buf;
    }

    private static byte[] BuildCellRecord(uint formId, string? editorId, int? gridX, int? gridY)
    {
        var subs = new List<(string, byte[])>();

        if (editorId != null)
        {
            subs.Add(("EDID", NullTermBytes(editorId)));
        }

        // DATA subrecord (1 byte flags — 0 = no flags)
        subs.Add(("DATA", [0]));

        // XCLC subrecord (grid coordinates, 12 bytes: X int32, Y int32, flags uint32)
        if (gridX.HasValue && gridY.HasValue)
        {
            var xclc = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(xclc, gridX.Value);
            BinaryPrimitives.WriteInt32LittleEndian(xclc.AsSpan(4), gridY.Value);
            subs.Add(("XCLC", xclc));
        }

        return BuildRecord("CELL", formId, 0, subs.ToArray());
    }

    private static byte[] BuildPlacedRefRecord(PlacedRefData r)
    {
        var subs = new List<(string, byte[])>();

        if (r.EditorId != null)
        {
            subs.Add(("EDID", NullTermBytes(r.EditorId)));
        }

        // NAME subrecord (base object FormID, 4 bytes)
        var nameData = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(nameData, r.BaseFormId);
        subs.Add(("NAME", nameData));

        // XESP subrecord (Enable Parent, 8 bytes)
        if (r.EnableParentFormId.HasValue)
        {
            var xesp = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(xesp, r.EnableParentFormId.Value);
            xesp[4] = r.EnableParentFlags;
            subs.Add(("XESP", xesp));
        }

        // DATA subrecord (position: 6 floats = 24 bytes)
        var posData = new byte[24];
        BinaryPrimitives.WriteSingleLittleEndian(posData, r.X);
        BinaryPrimitives.WriteSingleLittleEndian(posData.AsSpan(4), r.Y);
        BinaryPrimitives.WriteSingleLittleEndian(posData.AsSpan(8), r.Z);
        subs.Add(("DATA", posData));

        return BuildRecord(r.RecordType, r.FormId, r.Flags, subs.ToArray());
    }

    #endregion

    #region Utility

    private static byte[] NullTermBytes(string s)
    {
        var bytes = new byte[s.Length + 1];
        Encoding.ASCII.GetBytes(s, bytes);
        return bytes;
    }

    private static void WriteGrupHeader(byte[] buf, int offset, uint grupSize, string label, int grupType)
    {
        Encoding.ASCII.GetBytes("GRUP", buf.AsSpan(offset));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 4), grupSize);
        Encoding.ASCII.GetBytes(label, buf.AsSpan(offset + 8));
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset + 12), grupType);
    }

    private static void WriteGrupHeaderUint(byte[] buf, int offset, uint grupSize, uint label, int grupType)
    {
        Encoding.ASCII.GetBytes("GRUP", buf.AsSpan(offset));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 4), grupSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 8), label);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(offset + 12), grupType);
    }

    private static uint ComposeGridLabel(int x, int y)
    {
        unchecked
        {
            return (ushort)y | ((uint)(ushort)x << 16);
        }
    }

    #endregion

    #region Data Models

    internal sealed class WorldspaceData
    {
        public required uint FormId { get; init; }
        public required string EditorId { get; init; }
        public string? FullName { get; init; }
        public CellData? PersistentCell { get; init; }
        public List<CellData> ExteriorCells { get; init; } = [];
    }

    internal sealed class CellData
    {
        public required uint FormId { get; init; }
        public string? EditorId { get; init; }
        public int GridX { get; init; }
        public int GridY { get; init; }
        public List<PlacedRefData> PersistentRefs { get; init; } = [];
        public List<PlacedRefData> TemporaryRefs { get; init; } = [];
    }

    internal sealed class PlacedRefData
    {
        public required string RecordType { get; init; } // "REFR", "ACHR", "ACRE"
        public required uint FormId { get; init; }
        public required uint BaseFormId { get; init; }
        public uint Flags { get; init; }
        public string? EditorId { get; init; }
        public uint? EnableParentFormId { get; init; }
        public byte EnableParentFlags { get; init; }
        public float X { get; init; }
        public float Y { get; init; }
        public float Z { get; init; }
    }

    internal sealed record PipelineResult(
        List<ParsedMainRecord> ParsedRecords,
        List<GrupHeaderInfo> GrupHeaders,
        EsmRecordScanResult ScanResult,
        RecordCollection Collection,
        bool IsBigEndian,
        byte[] FileData);

    #endregion
}
