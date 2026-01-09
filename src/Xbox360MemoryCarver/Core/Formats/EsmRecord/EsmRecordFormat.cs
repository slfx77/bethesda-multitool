using System.Buffers;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.EsmRecord;

/// <summary>
///     ESM record fragment format module.
///     Scans memory dumps for GMST, EDID, SCTX, and SCRO records.
///     This format doesn't participate in normal carving (ShowInFilterUI = false)
///     but provides dump analysis capabilities.
/// </summary>
public sealed class EsmRecordFormat : FileFormatBase, IDumpScanner
{
    public override string FormatId => "esmrecord";
    public override string DisplayName => "ESM Records";
    public override string Extension => ".esm";
    public override FileCategory Category => FileCategory.Plugin;
    public override string OutputFolder => "esm_records";
    public override int MinSize => 8;
    public override int MaxSize => 64 * 1024;
    public override bool ShowInFilterUI => false;

    public override IReadOnlyList<FormatSignature> Signatures { get; } = [];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        return null;
    }

    #region IDumpScanner

    public object ScanDump(MemoryMappedViewAccessor accessor, long fileSize)
    {
        return ScanForRecordsMemoryMapped(accessor, fileSize);
    }

    public static EsmRecordScanResult ScanForRecords(byte[] data)
    {
        var result = new EsmRecordScanResult();
        var seenEdids = new HashSet<string>();
        var seenFormIds = new HashSet<uint>();

        for (var i = 0; i <= data.Length - 8; i++)
            if (MatchesSignature(data, i, "EDID"u8))
                TryAddEdidRecord(data, i, data.Length, result.EditorIds, seenEdids);
            else if (MatchesSignature(data, i, "GMST"u8))
                TryAddGmstRecord(data, i, data.Length, result.GameSettings);
            else if (MatchesSignature(data, i, "SCTX"u8))
                TryAddSctxRecord(data, i, data.Length, result.ScriptSources);
            else if (MatchesSignature(data, i, "SCRO"u8))
                TryAddScroRecord(data, i, data.Length, result.FormIdReferences, seenFormIds);

        return result;
    }

    /// <summary>
    ///     Scan an entire memory dump for ESM records using memory-mapped access.
    ///     Processes in chunks to avoid loading the entire file into memory.
    /// </summary>
    public static EsmRecordScanResult ScanForRecordsMemoryMapped(MemoryMappedViewAccessor accessor, long fileSize)
    {
        const int chunkSize = 16 * 1024 * 1024; // 16MB chunks
        const int overlapSize = 1024; // Overlap to handle records at chunk boundaries

        var result = new EsmRecordScanResult();
        var seenEdids = new HashSet<string>();
        var seenFormIds = new HashSet<uint>();
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize + overlapSize);

        try
        {
            long offset = 0;
            while (offset < fileSize)
            {
                var toRead = (int)Math.Min(chunkSize + overlapSize, fileSize - offset);
                accessor.ReadArray(offset, buffer, 0, toRead);

                // Determine the search limit for this chunk
                // Only search up to chunkSize unless this is the last chunk
                var searchLimit = offset + chunkSize >= fileSize ? toRead - 8 : chunkSize;

                // Scan this chunk
                for (var i = 0; i <= searchLimit; i++)
                    if (MatchesSignature(buffer, i, "EDID"u8))
                        TryAddEdidRecordWithOffset(buffer, i, toRead, offset, result.EditorIds, seenEdids);
                    else if (MatchesSignature(buffer, i, "GMST"u8))
                        TryAddGmstRecordWithOffset(buffer, i, toRead, offset, result.GameSettings);
                    else if (MatchesSignature(buffer, i, "SCTX"u8))
                        TryAddSctxRecordWithOffset(buffer, i, toRead, offset, result.ScriptSources);
                    else if (MatchesSignature(buffer, i, "SCRO"u8))
                        TryAddScroRecordWithOffset(buffer, i, toRead, offset, result.FormIdReferences, seenFormIds);

                offset += chunkSize;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return result;
    }

    public static Dictionary<uint, string> CorrelateFormIdsToNames(byte[] data,
        EsmRecordScanResult? existingScan = null)
    {
        var scan = existingScan ?? ScanForRecords(data);
        var correlations = new Dictionary<uint, string>();

        foreach (var edid in scan.EditorIds)
        {
            var formId = FindRecordFormId(data, (int)edid.Offset);
            if (formId != 0 && !correlations.ContainsKey(formId)) correlations[formId] = edid.Name;
        }

        return correlations;
    }

    /// <summary>
    ///     Correlate FormIDs to names using memory-mapped access.
    /// </summary>
    public static Dictionary<uint, string> CorrelateFormIdsToNamesMemoryMapped(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        EsmRecordScanResult existingScan)
    {
        var correlations = new Dictionary<uint, string>();
        var buffer = ArrayPool<byte>.Shared.Rent(256); // Small buffer for searching backward

        try
        {
            foreach (var edid in existingScan.EditorIds)
            {
                var edidOffset = edid.Offset;
                var searchStart = Math.Max(0, edidOffset - 200);
                var toRead = (int)Math.Min(256, edidOffset - searchStart + 50);

                if (toRead <= 0) continue;

                accessor.ReadArray(searchStart, buffer, 0, toRead);

                var localEdidOffset = (int)(edidOffset - searchStart);
                var formId = FindRecordFormIdInBuffer(buffer, localEdidOffset, toRead);

                if (formId != 0 && !correlations.ContainsKey(formId)) correlations[formId] = edid.Name;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return correlations;
    }

    /// <summary>
    ///     Export ESM records to files. Delegates to EsmRecordExporter.
    /// </summary>
    public static Task ExportRecordsAsync(
        EsmRecordScanResult records,
        Dictionary<uint, string> formIdMap,
        string outputDir,
        bool verbose = false)
    {
        return EsmRecordExporter.ExportRecordsAsync(records, formIdMap, outputDir, verbose);
    }

    #endregion

    #region Private Implementation

    private static bool MatchesSignature(byte[] data, int i, ReadOnlySpan<byte> sig)
    {
        return data[i] == sig[0] && data[i + 1] == sig[1] && data[i + 2] == sig[2] && data[i + 3] == sig[3];
    }

    private static void TryAddEdidRecord(byte[] data, int i, int dataLength, List<EdidRecord> records,
        HashSet<string> seen)
    {
        if (i + 6 > dataLength) return;
        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len == 0 || len >= 256 || i + 6 + len > dataLength) return;

        var name = ReadNullTermString(data, i + 6, len);
        if (IsValidEditorId(name) && seen.Add(name)) records.Add(new EdidRecord(name, i));
    }

    private static void TryAddEdidRecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<EdidRecord> records, HashSet<string> seen)
    {
        if (i + 6 > dataLength) return;
        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len == 0 || len >= 256 || i + 6 + len > dataLength) return;

        var name = ReadNullTermString(data, i + 6, len);
        if (IsValidEditorId(name) && seen.Add(name)) records.Add(new EdidRecord(name, baseOffset + i));
    }

    private static void TryAddGmstRecord(byte[] data, int i, int dataLength, List<GmstRecord> records)
    {
        if (i + 6 > dataLength) return;
        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len == 0 || len >= 512 || i + 6 + len > dataLength) return;

        var name = ReadNullTermString(data, i + 6, len);
        if (IsValidSettingName(name)) records.Add(new GmstRecord(name, i, len));
    }

    private static void TryAddGmstRecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<GmstRecord> records)
    {
        if (i + 6 > dataLength) return;
        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len == 0 || len >= 512 || i + 6 + len > dataLength) return;

        var name = ReadNullTermString(data, i + 6, len);
        if (IsValidSettingName(name)) records.Add(new GmstRecord(name, baseOffset + i, len));
    }

    private static void TryAddSctxRecord(byte[] data, int i, int dataLength, List<SctxRecord> records)
    {
        if (i + 6 > dataLength) return;
        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len <= 10 || len >= 65535 || i + 6 + len > dataLength) return;

        var text = Encoding.ASCII.GetString(data, i + 6, len).TrimEnd('\0');
        if (text.Length > 5 && ContainsScriptKeywords(text)) records.Add(new SctxRecord(text, i, len));
    }

    private static void TryAddSctxRecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<SctxRecord> records)
    {
        if (i + 6 > dataLength) return;
        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len <= 10 || len >= 65535 || i + 6 + len > dataLength) return;

        var text = Encoding.ASCII.GetString(data, i + 6, len).TrimEnd('\0');
        if (text.Length > 5 && ContainsScriptKeywords(text)) records.Add(new SctxRecord(text, baseOffset + i, len));
    }

    private static void TryAddScroRecord(byte[] data, int i, int dataLength, List<ScroRecord> records,
        HashSet<uint> seen)
    {
        if (i + 10 > dataLength) return;
        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 4) return;

        var formId = BinaryUtils.ReadUInt32LE(data, i + 6);
        if (formId == 0 || formId == 0xFFFFFFFF || formId >> 24 > 0x0F) return;

        if (seen.Add(formId)) records.Add(new ScroRecord(formId, i));
    }

    private static void TryAddScroRecordWithOffset(byte[] data, int i, int dataLength, long baseOffset,
        List<ScroRecord> records, HashSet<uint> seen)
    {
        if (i + 10 > dataLength) return;
        var len = BinaryUtils.ReadUInt16LE(data, i + 4);
        if (len != 4) return;

        var formId = BinaryUtils.ReadUInt32LE(data, i + 6);
        if (formId == 0 || formId == 0xFFFFFFFF || formId >> 24 > 0x0F) return;

        if (seen.Add(formId)) records.Add(new ScroRecord(formId, baseOffset + i));
    }

    private static uint FindRecordFormId(byte[] data, int edidOffset)
    {
        var searchStart = Math.Max(0, edidOffset - 200);
        for (var checkOffset = edidOffset - 4; checkOffset >= searchStart; checkOffset--)
        {
            var formId = TryExtractFormIdFromRecordHeader(data, checkOffset, edidOffset, data.Length);
            if (formId != 0) return formId;
        }

        return 0;
    }

    private static uint FindRecordFormIdInBuffer(byte[] data, int edidLocalOffset, int dataLength)
    {
        var searchStart = Math.Max(0, edidLocalOffset - 200);
        for (var checkOffset = edidLocalOffset - 4; checkOffset >= searchStart; checkOffset--)
        {
            var formId = TryExtractFormIdFromRecordHeader(data, checkOffset, edidLocalOffset, dataLength);
            if (formId != 0) return formId;
        }

        return 0;
    }

    private static uint TryExtractFormIdFromRecordHeader(byte[] data, int checkOffset, int edidOffset, int dataLength)
    {
        if (checkOffset + 24 >= dataLength) return 0;
        if (!IsRecordTypeMarker(data, checkOffset)) return 0;

        var formId = BinaryUtils.ReadUInt32LE(data, checkOffset + 12);
        if (formId == 0 || formId == 0xFFFFFFFF || formId >> 24 > 0x0F) return 0;

        var size = BinaryUtils.ReadUInt32LE(data, checkOffset + 4);
        if (size > 0 && size < 10_000_000 && edidOffset < checkOffset + 24 + size) return formId;

        return 0;
    }

    private static bool IsRecordTypeMarker(byte[] data, int offset)
    {
        for (var b = 0; b < 4; b++)
            if (!char.IsAsciiLetterOrDigit((char)data[offset + b]) && data[offset + b] != '_')
                return false;

        return true;
    }

    private static string ReadNullTermString(byte[] data, int offset, int maxLen)
    {
        var end = offset;
        while (end < offset + maxLen && end < data.Length && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data, offset, end - offset);
    }

    private static bool IsValidEditorId(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 2 || name.Length > 200) return false;
        if (!char.IsLetter(name[0])) return false;

        var validChars = name.Count(c => char.IsLetterOrDigit(c) || c == '_');
        return validChars >= name.Length * 0.9;
    }

    private static bool IsValidSettingName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 2) return false;

        var firstChar = char.ToLower(name[0], CultureInfo.InvariantCulture);
        return firstChar is 'f' or 'i' or 's' or 'b' && name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static bool ContainsScriptKeywords(string text)
    {
        return text.Contains("Enable", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Disable", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("MoveTo", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("SetStage", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("GetStage", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("if ", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("endif", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("REF", StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
