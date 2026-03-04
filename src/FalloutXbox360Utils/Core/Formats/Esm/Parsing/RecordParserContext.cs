using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Shared context for record parsing, providing access to scan results,
///     memory-mapped file data, and FormID/EditorID lookup tables.
///     Extracted from RecordParser to enable handler-based composition.
/// </summary>
public sealed class RecordParserContext
{
    private readonly Dictionary<string, List<DetectedMainRecord>> _recordsByType;
    private Dictionary<uint, uint>? _refToBase;

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
        // Uses probe-based auto-detection of early vs final build struct layout
        if (accessor != null && minidumpInfo != null && fileSize > 0)
        {
            RuntimeReader = RuntimeStructReader.CreateWithAutoDetect(
                accessor, fileSize, minidumpInfo, scanResult.RuntimeRefrFormEntries);
        }

        // Build FormID lookup from main records
        RecordsByFormId = scanResult.MainRecords
            .GroupBy(r => r.FormId)
            .ToDictionary(g => g.Key, g => g.First());

        // Build record type index for O(1) GetRecordsByType lookups
        _recordsByType = scanResult.MainRecords
            .GroupBy(r => r.RecordType)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

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

        // Pre-populate FormIdToFullName from scan results so that CaptureAllFullNames()
        // can skip already-known records instead of re-reading them from the accessor.
        PrePopulateFullNames(scanResult);

        // Merge runtime display names into FormIdToFullName so they're available during parsing.
        // These come from BSStringT reads on the TESFullName field during EditorID extraction.
        foreach (var entry in scanResult.RuntimeEditorIds)
        {
            if (entry.FormId != 0 && !string.IsNullOrEmpty(entry.DisplayName))
            {
                FormIdToFullName.TryAdd(entry.FormId, entry.DisplayName);
            }
        }
    }

    public EsmRecordScanResult ScanResult { get; }
    public MemoryMappedViewAccessor? Accessor { get; }
    public long FileSize { get; }
    public RuntimeStructReader? RuntimeReader { get; }
    public Dictionary<uint, DetectedMainRecord> RecordsByFormId { get; }

    /// <summary>
    ///     Mutable: handlers write to this during parsing (e.g., EDID subrecord enrichment).
    /// </summary>
    public Dictionary<uint, string> FormIdToEditorId { get; }

    public Dictionary<string, uint> EditorIdToFormId { get; }

    /// <summary>
    ///     Mutable: handlers write to this during parsing (e.g., FULL subrecord enrichment).
    /// </summary>
    public Dictionary<uint, string> FormIdToFullName { get; } = new();

    /// <summary>
    ///     Runtime worldspace cell maps from walking TESWorldSpace pCellMap hash tables.
    ///     Keyed by worldspace FormID. Set during RecordParser enrichment phase.
    /// </summary>
    public Dictionary<uint, RuntimeWorldspaceData>? RuntimeWorldspaceCellMaps { get; set; }

    /// <summary>
    ///     Pre-built Ref→Base mapping from ScanResult.RefrRecords.
    ///     Cached for reuse by both ScriptRecordHandler (during parsing) and CreateResolver().
    /// </summary>
    public Dictionary<uint, uint> RefToBase => _refToBase ??= BuildRefToBase();

    #region Runtime Merge

    /// <summary>
    ///     Merges runtime-only records into an existing list, deduplicating by FormID.
    ///     Eliminates the repeated runtime merge pattern across all handler methods.
    /// </summary>
    public void MergeRuntimeRecords<T>(
        List<T> records,
        byte formType,
        Func<T, uint> formIdSelector,
        Func<RuntimeStructReader, RuntimeEditorIdEntry, T?> factory,
        string typeName) where T : class
    {
        if (RuntimeReader == null)
        {
            return;
        }

        var esmFormIds = new HashSet<uint>(records.Count);
        foreach (var record in records)
        {
            esmFormIds.Add(formIdSelector(record));
        }

        var runtimeCount = 0;
        foreach (var entry in ScanResult.RuntimeEditorIds)
        {
            if (entry.FormType != formType || esmFormIds.Contains(entry.FormId))
            {
                continue;
            }

            var item = factory(RuntimeReader, entry);
            if (item != null)
            {
                records.Add(item);
                runtimeCount++;
            }
        }

        if (runtimeCount > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Added {runtimeCount} {typeName} from runtime struct reading " +
                $"(total: {records.Count}, ESM: {esmFormIds.Count})");
        }
    }

    /// <summary>
    ///     Merges runtime-only records that have no specialized reader into the GenericRecords list
    ///     using PDB-derived struct layouts. Skips FormTypes with hand-written readers and FormIDs
    ///     already present in any ESM-parsed collection.
    /// </summary>
    public void MergeRuntimeGenericRecords(
        List<GenericEsmRecord> genericRecords,
        HashSet<uint> allEsmFormIds)
    {
        if (RuntimeReader == null)
        {
            return;
        }

        var runtimeCount = 0;
        foreach (var entry in ScanResult.RuntimeEditorIds)
        {
            // Skip types that have specialized readers
            if (PdbStructLayouts.HasSpecializedReader(entry.FormType))
            {
                continue;
            }

            // Skip if already parsed from ESM or already in generic list
            if (allEsmFormIds.Contains(entry.FormId))
            {
                continue;
            }

            var record = RuntimeReader.ReadGenericRecord(entry);
            if (record != null)
            {
                genericRecords.Add(record);
                allEsmFormIds.Add(entry.FormId); // Prevent duplicates
                runtimeCount++;
            }
        }

        if (runtimeCount > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Added {runtimeCount} generic records from PDB-based runtime reading " +
                $"(total generic: {genericRecords.Count})");
        }
    }

    #endregion

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
        return _recordsByType.TryGetValue(recordType, out var list) ? list : [];
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
            Logger.Instance.Debug("  [ReadRecordData] NULL: {0} 0x{1:X8} at offset 0x{2:X} — exceeds file size ({3}+{4} > {5})",
                record.RecordType, record.FormId, record.Offset, dataStart, dataSize, FileSize);
            return null;
        }

        Accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        if (!record.IsCompressed)
        {
            return (buffer, dataSize);
        }

        if (dataSize <= 4)
        {
            Logger.Instance.Debug("  [ReadRecordData] NULL: {0} 0x{1:X8} compressed but dataSize={2}",
                record.RecordType, record.FormId, dataSize);
            return null;
        }

        var decompressed = EsmParser.DecompressRecordData(
            buffer.AsSpan(0, dataSize), record.IsBigEndian);
        if (decompressed == null)
        {
            Logger.Instance.Debug("  [ReadRecordData] NULL: {0} 0x{1:X8} decompression failed (flags=0x{2:X8}, dataSize={3})",
                record.RecordType, record.FormId, record.Flags, dataSize);
        }

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

        return FormIdToFullName.GetValueOrDefault(formId);
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

    /// <summary>Creates a FormIdResolver from the current context's dictionaries.</summary>
    public FormIdResolver CreateResolver()
    {
        return new FormIdResolver(FormIdToEditorId, BuildFormIdToDisplayNameMap(), RefToBase);
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

    private void PrePopulateFullNames(EsmRecordScanResult scanResult)
    {
        if (scanResult.FullNames.Count == 0)
        {
            return;
        }

        // FullNames.Offset == parent record offset (set by ConvertToScanResult).
        // Build offset→FormId lookup from RecordsByFormId is expensive; instead use
        // the sorted MainRecords list since offsets are unique per record.
        var recordByOffset = new Dictionary<long, uint>(scanResult.MainRecords.Count);
        foreach (var record in scanResult.MainRecords)
        {
            recordByOffset.TryAdd(record.Offset, record.FormId);
        }

        foreach (var fullName in scanResult.FullNames)
        {
            if (string.IsNullOrEmpty(fullName.Text))
            {
                continue;
            }

            if (recordByOffset.TryGetValue(fullName.Offset, out var formId) && formId != 0)
            {
                FormIdToFullName.TryAdd(formId, fullName.Text);
            }
        }
    }

    private Dictionary<uint, uint> BuildRefToBase()
    {
        var map = new Dictionary<uint, uint>();
        foreach (var refr in ScanResult.RefrRecords)
        {
            if (refr.Header.FormId != 0 && refr.BaseFormId != 0)
            {
                map.TryAdd(refr.Header.FormId, refr.BaseFormId);
            }
        }

        return map;
    }

    private static Dictionary<uint, string> BuildFormIdToEditorIdMap(EsmRecordScanResult scanResult)
    {
        var map = new Dictionary<uint, string>();
        var records = scanResult.MainRecords;

        if (records.Count == 0)
        {
            return map;
        }

        // Build sorted offset array for O(log N) binary search per EditorID.
        // MainRecords are typically in file order already, but sort to guarantee.
        var sortedRecords = records.OrderBy(r => r.Offset).ToList();
        var offsets = new long[sortedRecords.Count];
        for (var i = 0; i < sortedRecords.Count; i++)
        {
            offsets[i] = sortedRecords[i].Offset;
        }

        foreach (var edid in scanResult.EditorIds)
        {
            // Binary search for the record whose Offset is <= edid.Offset
            var idx = Array.BinarySearch(offsets, edid.Offset);
            if (idx < 0)
            {
                idx = ~idx - 1; // Bitwise complement gives insertion point; -1 for the record before
            }

            if (idx < 0)
            {
                continue;
            }

            var candidate = sortedRecords[idx];
            // Verify EDID falls within this record's data region
            if (edid.Offset < candidate.Offset + candidate.DataSize + 24)
            {
                map.TryAdd(candidate.FormId, edid.Name);
            }
        }

        return map;
    }

    #endregion
}
