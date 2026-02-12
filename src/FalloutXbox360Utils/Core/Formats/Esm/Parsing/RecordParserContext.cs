using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Shared context for record reconstruction, providing access to scan results,
///     memory-mapped file data, and FormID/EditorID lookup tables.
///     Extracted from RecordParser to enable handler-based composition.
/// </summary>
public sealed class RecordParserContext
{
    public EsmRecordScanResult ScanResult { get; }
    public MemoryMappedViewAccessor? Accessor { get; }
    public long FileSize { get; }
    public RuntimeStructReader? RuntimeReader { get; }
    public Dictionary<uint, DetectedMainRecord> RecordsByFormId { get; }

    /// <summary>
    ///     Mutable: handlers write to this during reconstruction (e.g., EDID subrecord enrichment).
    /// </summary>
    public Dictionary<uint, string> FormIdToEditorId { get; }

    public Dictionary<string, uint> EditorIdToFormId { get; }

    /// <summary>
    ///     Mutable: handlers write to this during reconstruction (e.g., FULL subrecord enrichment).
    /// </summary>
    public Dictionary<uint, string> FormIdToFullName { get; } = new();

    public RecordParserContext(
        EsmRecordScanResult scanResult,
        Dictionary<uint, string>? formIdCorrelations = null,
        MemoryMappedViewAccessor? accessor = null,
        long fileSize = 0,
        MinidumpInfo? minidumpInfo = null)
    {
        ScanResult = scanResult;
        Accessor = accessor;
        FileSize = fileSize;

        // Create runtime struct reader if we have both accessor and minidump info
        if (accessor != null && minidumpInfo != null && fileSize > 0)
        {
            RuntimeReader = new RuntimeStructReader(accessor, fileSize, minidumpInfo);
        }

        // Build FormID lookup from main records
        RecordsByFormId = scanResult.MainRecords
            .GroupBy(r => r.FormId)
            .ToDictionary(g => g.Key, g => g.First());

        // Build EditorID lookups from ESM EDID subrecords or pre-built correlations
        FormIdToEditorId = formIdCorrelations != null
            ? new Dictionary<uint, string>(formIdCorrelations)
            : BuildFormIdToEditorIdMap(scanResult);

        // Merge runtime EditorIDs
        foreach (var entry in scanResult.RuntimeEditorIds)
        {
            if (entry.FormId != 0 && !FormIdToEditorId.ContainsKey(entry.FormId))
            {
                FormIdToEditorId[entry.FormId] = entry.EditorId;
            }
        }

        // Inject well-known engine FormIDs
        FormIdToEditorId.TryAdd(0x00000007, "PlayerRef");
        FormIdToEditorId.TryAdd(0x00000014, "Player");

        EditorIdToFormId = FormIdToEditorId
            .GroupBy(kv => kv.Value)
            .ToDictionary(g => g.Key, g => g.First().Key);
    }

    #region Lookup Methods

    public string? GetEditorId(uint formId)
    {
        return FormIdToEditorId.GetValueOrDefault(formId);
    }

    public uint? GetFormId(string editorId)
    {
        return EditorIdToFormId.TryGetValue(editorId, out var formId) ? formId : null;
    }

    public DetectedMainRecord? GetRecord(uint formId)
    {
        return RecordsByFormId.GetValueOrDefault(formId);
    }

    public IEnumerable<DetectedMainRecord> GetRecordsByType(string recordType)
    {
        return ScanResult.MainRecords.Where(r => r.RecordType == recordType);
    }

    #endregion

    #region Name/Subrecord Lookups

    public string? FindFullNameNear(long recordOffset)
    {
        return ScanResult.FullNames
            .Where(f => Math.Abs(f.Offset - recordOffset) < 500)
            .OrderBy(f => Math.Abs(f.Offset - recordOffset))
            .FirstOrDefault()?.Text;
    }

    public string? FindFullNameInRecordBounds(DetectedMainRecord record)
    {
        var dataStart = record.Offset + 24;
        var dataEnd = dataStart + record.DataSize;

        return ScanResult.FullNames
            .Where(f => f.Offset >= dataStart && f.Offset < dataEnd)
            .OrderBy(f => f.Offset)
            .FirstOrDefault()?.Text;
    }

    public ActorBaseSubrecord? FindActorBaseNear(long recordOffset)
    {
        return ScanResult.ActorBases
            .Where(a => Math.Abs(a.Offset - recordOffset) < 500)
            .OrderBy(a => Math.Abs(a.Offset - recordOffset))
            .FirstOrDefault();
    }

    /// <summary>
    ///     Reads record data from the accessor, decompressing if the record is compressed.
    ///     Returns null if data cannot be read or decompression fails.
    /// </summary>
    public (byte[] Data, int Size)? ReadRecordData(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > FileSize)
        {
            return null;
        }

        Accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        if (!record.IsCompressed)
        {
            return (buffer, dataSize);
        }

        if (dataSize <= 4)
        {
            return null;
        }

        var decompressed = EsmParser.DecompressRecordData(
            buffer.AsSpan(0, dataSize), record.IsBigEndian);
        return decompressed != null ? (decompressed, decompressed.Length) : null;
    }

    /// <summary>
    ///     Resolve a FormID to EditorID or display name, checking all available sources.
    /// </summary>
    public string? ResolveFormName(uint formId)
    {
        if (formId == 0)
        {
            return null;
        }

        if (FormIdToEditorId.TryGetValue(formId, out var editorId))
        {
            return editorId;
        }

        foreach (var entry in ScanResult.RuntimeEditorIds)
        {
            if (entry.FormId == formId)
            {
                return entry.DisplayName ?? entry.EditorId;
            }
        }

        return null;
    }

    /// <summary>
    ///     Pre-scan all records for FULL subrecords, capturing display names.
    /// </summary>
    public void CaptureAllFullNames()
    {
        if (Accessor == null)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            foreach (var record in ScanResult.MainRecords)
            {
                if (FormIdToFullName.ContainsKey(record.FormId))
                {
                    continue;
                }

                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    if (sub.Signature == "FULL" && sub.DataLength > 0)
                    {
                        var name = EsmStringUtils.ReadNullTermString(
                            data.AsSpan(sub.DataOffset, sub.DataLength));
                        if (!string.IsNullOrEmpty(name))
                        {
                            FormIdToFullName.TryAdd(record.FormId, name);
                        }

                        break;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Build a FormID to display name mapping from runtime hash table entries.
    /// </summary>
    public Dictionary<uint, string> BuildFormIdToDisplayNameMap()
    {
        var map = new Dictionary<uint, string>(FormIdToFullName);

        foreach (var entry in ScanResult.RuntimeEditorIds)
        {
            if (entry.FormId != 0 && !string.IsNullOrEmpty(entry.DisplayName))
            {
                map.TryAdd(entry.FormId, entry.DisplayName);
            }
        }

        return map;
    }

    #endregion

    #region Static Helpers

    public static uint ReadFormId(ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length < 4)
        {
            return 0;
        }

        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);
    }

    public static ObjectBounds ReadObjectBounds(ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length < 12)
        {
            return new ObjectBounds();
        }

        if (bigEndian)
        {
            return new ObjectBounds
            {
                X1 = BinaryPrimitives.ReadInt16BigEndian(data),
                Y1 = BinaryPrimitives.ReadInt16BigEndian(data[2..]),
                Z1 = BinaryPrimitives.ReadInt16BigEndian(data[4..]),
                X2 = BinaryPrimitives.ReadInt16BigEndian(data[6..]),
                Y2 = BinaryPrimitives.ReadInt16BigEndian(data[8..]),
                Z2 = BinaryPrimitives.ReadInt16BigEndian(data[10..])
            };
        }

        return new ObjectBounds
        {
            X1 = BinaryPrimitives.ReadInt16LittleEndian(data),
            Y1 = BinaryPrimitives.ReadInt16LittleEndian(data[2..]),
            Z1 = BinaryPrimitives.ReadInt16LittleEndian(data[4..]),
            X2 = BinaryPrimitives.ReadInt16LittleEndian(data[6..]),
            Y2 = BinaryPrimitives.ReadInt16LittleEndian(data[8..]),
            Z2 = BinaryPrimitives.ReadInt16LittleEndian(data[10..])
        };
    }

    #endregion

    #region Private

    private static Dictionary<uint, string> BuildFormIdToEditorIdMap(EsmRecordScanResult scanResult)
    {
        var map = new Dictionary<uint, string>();

        foreach (var edid in scanResult.EditorIds)
        {
            var nearestRecord = scanResult.MainRecords
                .Where(r => r.Offset < edid.Offset && edid.Offset < r.Offset + r.DataSize + 24)
                .OrderByDescending(r => r.Offset)
                .FirstOrDefault();

            if (nearestRecord != null && !map.ContainsKey(nearestRecord.FormId))
            {
                map[nearestRecord.FormId] = edid.Name;
            }
        }

        return map;
    }

    #endregion
}
