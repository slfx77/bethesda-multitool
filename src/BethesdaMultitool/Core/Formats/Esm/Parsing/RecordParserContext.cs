using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Localization;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

/// <summary>
///     Shared context for record parsing, providing access to scan results,
///     memory-mapped file data, and FormID/EditorID lookup tables.
///     Extracted from RecordParser to enable handler-based composition.
/// </summary>
public sealed class RecordParserContext
{
    private readonly Dictionary<string, List<DetectedMainRecord>> _recordsByType;
    private readonly bool _recoverPartialCompressed;
    private Action<DetectedMainRecord>? _recordReadObserver;
    private Dictionary<uint, uint>? _refToBase;

    /// <summary>
    ///     Creates the parser context over a scan result, optionally backed by a memory-mapped
    ///     view and a DMP for runtime struct reads and localized-string resolution.
    /// </summary>
    public RecordParserContext(
        EsmRecordScanResult scanResult,
        Dictionary<uint, string>? formIdCorrelations = null,
        MemoryMappedViewAccessor? accessor = null,
        long fileSize = 0,
        MinidumpInfo? minidumpInfo = null,
        LocalizedStringTables? localizedStrings = null)
        : this(scanResult, formIdCorrelations,
            accessor != null ? new MmfMemoryAccessor(accessor) : null,
            fileSize, minidumpInfo, localizedStrings)
    {
    }

    /// <summary>
    ///     Creates the parser context backed by an arbitrary <see cref="IMemoryAccessor" />,
    ///     wiring up the runtime struct reader when a DMP and accessor are both present.
    /// </summary>
    public RecordParserContext(
        EsmRecordScanResult scanResult,
        Dictionary<uint, string>? formIdCorrelations,
        IMemoryAccessor? accessor,
        long fileSize,
        MinidumpInfo? minidumpInfo,
        LocalizedStringTables? localizedStrings = null)
    {
        ScanResult = scanResult;
        Accessor = accessor;
        FileSize = fileSize;
        MinidumpInfo = minidumpInfo;
        LocalizedStrings = localizedStrings;

        // Partial recovery of truncated compressed records is ON by default for memory dumps (their
        // compressed payloads are often cut short by the partial capture) and never applies to clean
        // on-disk ESMs. Opt out via EnvironmentVariables.Esm.DisableDmpPartialRecovery.
        _recoverPartialCompressed = minidumpInfo != null
                                    && !EnvironmentVariables.IsEnabled(EnvironmentVariables.Esm
                                        .DisableDmpPartialRecovery);

        // Create runtime struct reader if we have both accessor and minidump info
        // Uses probe-based auto-detection of early vs final build struct layout
        if (accessor != null && minidumpInfo != null && fileSize > 0)
        {
            // FormType drift correction is now applied centrally in
            // MinidumpAnalyzer.AnalyzeAsync, so by the time this
            // context is constructed the entries already carry canonical
            // FormType bytes. ApplyDriftCorrection is idempotent — this call
            // is defense-in-depth for callers that construct a RecordParser
            // without going through MinidumpAnalyzer (e.g., a hand-built
            // scanResult from a unit test).
            RuntimeBuildOffsets.ApplyDriftCorrection(scanResult);

            var npcEntries = scanResult.RuntimeEditorIds
                .Where(entry => entry.FormType == 0x2A)
                .ToList();
            var worldEntries = scanResult.RuntimeEditorIds
                .Where(entry => entry.FormType == 0x41)
                .ToList();
            var cellEntries = scanResult.RuntimeEditorIds
                .Where(entry => entry.FormType == 0x39)
                .ToList();
            BSStringDiagnostics.Reset();
            RuntimeReader = RuntimeStructReader.CreateWithAutoDetect(
                accessor,
                fileSize,
                minidumpInfo,
                scanResult.RuntimeRefrFormEntries,
                npcEntries,
                worldEntries,
                cellEntries,
                scanResult.RuntimeEditorIds,
                scanResult.RuntimeLandFormEntries);
        }

        // Build FormID lookup from main records
        RecordsByFormId = scanResult.MainRecords
            .GroupBy(r => r.FormId)
            .ToDictionary(g => g.Key, g => g.First());

        // Build record type index for O(1) GetRecordsByType lookups. Intentionally keeps
        // multiple records per (RecordType, FormID) tuple — Xbox 360 ESM splits INFO into
        // two halves with the same FormID, and DialogueConditionParser relies on iterating
        // both halves through this index. Per-type dedup is the responsibility of each
        // record handler (see NpcRecordHandler.ParseNpcs which dedups its accessor results).
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

        // Inject well-known identities. Player is a master-backed NPC_ EDID; PlayerRef is a
        // synthetic name for the engine-created reference at 0x14, which has no master record.
        FormIdToEditorId.TryAdd(0x00000007, "Player");
        FormIdToEditorId.TryAdd(0x00000014, "PlayerRef");

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

        // Auto-detect game version from TES4/HEDR if present
        Game = scanResult.Game != BethesdaGame.Unknown
            ? scanResult.Game
            : DetectGameFromTes4();
        if (Game != BethesdaGame.Unknown)
        {
            Logger.Instance.Debug("  [Context] Detected game: {0}", Game);
        }
    }

    /// <summary>
    ///     Base-object bounds index (FormID → OBND), set by <c>RecordParser</c> before cell parsing so
    ///     <c>CellLinkageHandler.ToPlacedReference</c> constructs each <c>PlacedReference</c> already
    ///     enriched — the post-parse enrichment sweep used to <c>with</c>-clone essentially every
    ///     placed ref (5.1M clones on Fallout 76). Null until the base-object parses have run.
    /// </summary>
    internal Dictionary<uint, ObjectBounds>? PlacedObjectBoundsIndex { get; set; }

    /// <summary>Base-object model-path index (FormID → MODL); see <see cref="PlacedObjectBoundsIndex" />.</summary>
    internal Dictionary<uint, string>? PlacedObjectModelIndex { get; set; }

    public EsmRecordScanResult ScanResult { get; }
    public IMemoryAccessor? Accessor { get; }
    public long FileSize { get; }
    public RuntimeStructReader? RuntimeReader { get; }
    public MinidumpInfo? MinidumpInfo { get; }
    public Dictionary<uint, DetectedMainRecord> RecordsByFormId { get; }

    /// <summary>Detected game/engine, auto-detected from the scan result or TES4/HEDR.</summary>
    public BethesdaGame Game { get; }

    /// <summary>
    ///     FormIDs of compressed records whose zlib payload was truncated in a memory dump and only
    ///     partially recovered (complete leading subrecords salvaged, the cut-off tail dropped). Empty for
    ///     clean ESMs and for records that decompressed fully. Lets callers report/flag preserved-partial
    ///     provenance.
    /// </summary>
    public HashSet<uint> PartiallyRecoveredFormIds { get; } = [];

    /// <summary>
    ///     FormIDs whose record body could not be read as one VA-contiguous run out of a memory dump:
    ///     the bytes following the record's start belong to a different virtual address range, so only
    ///     the resident prefix was handed to the parser. A flat file read would have spliced foreign
    ///     bytes into the middle of the record and produced plausible-looking garbage instead.
    ///     Empty for clean ESMs, and expected to be empty for well-formed dumps — a non-zero count is
    ///     the measurement that says how much this mattered.
    /// </summary>
    public HashSet<uint> NonContiguousRecordFormIds { get; } = [];

    /// <summary>
    ///     External string tables for a localized plugin (Skyrim/FO4/Starfield with the TES4 0x80
    ///     flag). Null for non-localized plugins, in which case <see cref="ReadLString" /> reads
    ///     inline zstrings exactly as before.
    /// </summary>
    public LocalizedStringTables? LocalizedStrings { get; }

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

    /// <summary>
    ///     Read a localizable string subrecord. For a localized plugin the subrecord holds a 4-byte
    ///     string ID resolved against the matching external table; for a non-localized plugin the bytes are
    ///     inline Windows-1252 null-terminated text. On a localized plugin a MISSED lookup returns empty (not
    ///     the index bytes rendered as text) so callers fall back to the EditorID rather than showing mojibake
    ///     — some IDs are genuinely absent from the table (e.g. internal dialogue topics whose player-prompt
    ///     string was never authored).
    /// </summary>
    public string ReadLString(ReadOnlySpan<byte> subData, LStringKind kind)
    {
        if (LocalizedStrings is { } tables)
        {
            if (subData.Length < 4)
            {
                return string.Empty;
            }

            var stringId = BinaryPrimitives.ReadUInt32LittleEndian(subData);
            return tables.Resolve(stringId, kind) ?? string.Empty;
        }

        return EsmStringUtils.ReadNullTermString(subData);
    }

    /// <summary>Read a FULL display-name subrecord (resolves via the .STRINGS table if localized).</summary>
    public string ReadFullName(ReadOnlySpan<byte> subData)
    {
        return ReadLString(subData, LStringKind.Strings);
    }

    /// <summary>Read a DESC description subrecord (resolves via the .DLSTRINGS table if localized).</summary>
    public string ReadDescription(ReadOnlySpan<byte> subData)
    {
        return ReadLString(subData, LStringKind.DlStrings);
    }

    /// <summary>Read a dialogue response text subrecord (resolves via the .ILSTRINGS table if localized).</summary>
    public string ReadDialogueText(ReadOnlySpan<byte> subData)
    {
        return ReadLString(subData, LStringKind.IlStrings);
    }

    /// <summary>
    ///     Read a dialogue prompt subrecord (INFO RNAM). xEdit models this as <c>wbLStringKC</c> like FULL, so
    ///     it resolves via the .STRINGS table (not .ILSTRINGS, which holds the longer NAM1 response text).
    /// </summary>
    public string ReadPromptText(ReadOnlySpan<byte> subData)
    {
        return ReadLString(subData, LStringKind.Strings);
    }

    #region Runtime Merge

    /// <summary>
    ///     Merges runtime records into an existing list: runtime-only FormIDs are added, and a
    ///     FormID the ESM already supplied has its <b>missing</b> members filled in from the runtime
    ///     capture.
    ///     <para>
    ///         This used to <c>continue</c> before the factory ever ran, so for any FormID the ESM
    ///         had, the live heap object was never even read. The two sources fail in different
    ///         places — a struck ESM copy can be missing an EditorID, a model path or a reference
    ///         the runtime object still holds — so discarding one unread threw away the only
    ///         evidence that could repair the other. ESM precedence is unchanged:
    ///         <see cref="RecordModelUnion.Fill" /> never overwrites a value the ESM copy actually
    ///         had, and never touches a non-nullable scalar, where zero cannot be told from unset.
    ///     </para>
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

        var indexByFormId = new Dictionary<uint, int>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            indexByFormId.TryAdd(formIdSelector(records[i]), i);
        }

        var runtimeCount = 0;
        var repairedCount = 0;
        foreach (var entry in ScanResult.RuntimeEditorIds)
        {
            if (entry.FormType != formType)
            {
                continue;
            }

            var item = factory(RuntimeReader, entry);
            if (item == null)
            {
                continue;
            }

            if (indexByFormId.TryGetValue(entry.FormId, out var existingIndex))
            {
                if (RecordModelUnion.Fill(records[existingIndex], item) is T filled &&
                    !ReferenceEquals(filled, records[existingIndex]))
                {
                    records[existingIndex] = filled;
                    repairedCount++;
                }

                continue;
            }

            records.Add(item);
            indexByFormId[entry.FormId] = records.Count - 1;
            runtimeCount++;
        }

        if (runtimeCount > 0 || repairedCount > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Added {runtimeCount} and repaired {repairedCount} {typeName} from " +
                $"runtime struct reading (total: {records.Count})");
        }
    }

    /// <summary>
    ///     Merges runtime records into an existing list, overlaying ESM-backed records in place
    ///     while preserving ESM precedence and adding runtime-only records when no semantic
    ///     record exists yet.
    /// </summary>
    public void MergeRuntimeOverlayRecords<T>(
        List<T> records,
        IReadOnlyCollection<byte> formTypes,
        Func<T, uint> formIdSelector,
        Func<RuntimeStructReader, RuntimeEditorIdEntry, T?> factory,
        Func<T, T, T> merger,
        string typeName) where T : class
    {
        if (RuntimeReader == null || formTypes.Count == 0)
        {
            return;
        }

        var recordIndexByFormId = new Dictionary<uint, int>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            recordIndexByFormId.TryAdd(formIdSelector(records[i]), i);
        }

        var mergedCount = 0;
        var addedCount = 0;
        foreach (var entry in ScanResult.RuntimeEditorIds)
        {
            if (!formTypes.Contains(entry.FormType))
            {
                continue;
            }

            var runtimeRecord = factory(RuntimeReader, entry);
            if (runtimeRecord == null)
            {
                continue;
            }

            if (recordIndexByFormId.TryGetValue(entry.FormId, out var existingIndex))
            {
                records[existingIndex] = merger(records[existingIndex], runtimeRecord);
                mergedCount++;
            }
            else
            {
                records.Add(runtimeRecord);
                recordIndexByFormId[entry.FormId] = records.Count - 1;
                addedCount++;
            }
        }

        if (mergedCount > 0 || addedCount > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Runtime overlay merged {mergedCount} and added {addedCount} {typeName} " +
                $"(total: {records.Count})");
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

    /// <summary>Looks up the editor ID for a FormID, or null if unknown.</summary>
    public string? GetEditorId(uint formId)
    {
        return FormIdToEditorId.GetValueOrDefault(formId);
    }

    /// <summary>Looks up the FormID for an editor ID, or null if unknown.</summary>
    public uint? GetFormId(string editorId)
    {
        return EditorIdToFormId.TryGetValue(editorId, out var formId) ? formId : null;
    }

    /// <summary>Gets the detected main record for a FormID, or null if not present.</summary>
    public DetectedMainRecord? GetRecord(uint formId)
    {
        return RecordsByFormId.GetValueOrDefault(formId);
    }

    /// <summary>Enumerates all detected main records of the given 4-character type.</summary>
    public IEnumerable<DetectedMainRecord> GetRecordsByType(string recordType)
    {
        return _recordsByType.TryGetValue(recordType, out var list) ? list : [];
    }

    /// <summary>Returns the detected main records of the given type as a read-only list.</summary>
    public IReadOnlyList<DetectedMainRecord> GetRecordListByType(string recordType)
    {
        return _recordsByType.TryGetValue(recordType, out var list) ? list : Array.Empty<DetectedMainRecord>();
    }

    #endregion

    #region Name/Subrecord Lookups

    /// <summary>Finds the FULL (display) name nearest the given record offset (within ~500 bytes).</summary>
    public string? FindFullNameNear(long recordOffset)
    {
        return ScanResult.FullNames
            .Where(f => Math.Abs(f.Offset - recordOffset) < 500)
            .OrderBy(f => Math.Abs(f.Offset - recordOffset))
            .FirstOrDefault()?.Text;
    }

    /// <summary>Finds the first FULL (display) name that falls within the record's data bounds.</summary>
    public string? FindFullNameInRecordBounds(DetectedMainRecord record)
    {
        var dataStart = record.Offset + record.HeaderSize;
        var dataEnd = dataStart + record.DataSize;

        return ScanResult.FullNames
            .Where(f => f.Offset >= dataStart && f.Offset < dataEnd)
            .OrderBy(f => f.Offset)
            .FirstOrDefault()?.Text;
    }

    /// <summary>Finds the scanned actor-base subrecord nearest the given record offset (within ~500 bytes).</summary>
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
        var dataStart = record.Offset + record.HeaderSize;
        var dataSize = (int)record.DataSize;

        if (dataStart + dataSize > FileSize)
        {
            Logger.Instance.Debug(
                "  [ReadRecordData] NULL: {0} 0x{1:X8} at offset 0x{2:X} — exceeds file size ({3}+{4} > {5})",
                record.RecordType, record.FormId, record.Offset, dataStart, dataSize, FileSize);
            return null;
        }

        // Full semantic parsing installs this only after CaptureAllFullNames. Centralizing the
        // callback here covers every typed handler while allowing repeated handler reads of one
        // descriptor to be deduplicated by the progress reporter.
        _recordReadObserver?.Invoke(record);

        // The previous Math.Min(record.DataSize, buffer.Length) silently truncated any record
        // larger than the caller's rented buffer — most visibly LAND quadrants whose later
        // subrecords sat past the cut, but the same shape affects any handler with a small
        // shared buffer (NPC, CREA, PACK, BOOK, ITEM, …). Allocate an exact-size array when
        // the caller's buffer is too small so oversized records still parse correctly.
        var useBuffer = dataSize > buffer.Length ? new byte[dataSize] : buffer;
        var readSize = ReadRecordBytes(dataStart, dataSize, useBuffer);
        if (readSize <= 0)
        {
            Logger.Instance.Debug(
                "  [ReadRecordData] NULL: {0} 0x{1:X8} at offset 0x{2:X} — no bytes readable",
                record.RecordType, record.FormId, record.Offset);
            return null;
        }

        if (readSize < dataSize)
        {
            // The record body is not one VA-contiguous run in this dump, or the accessor came up
            // short. Either way the bytes past `readSize` belong to some other address range, so
            // the prefix is everything that is genuinely this record. Recorded rather than silently
            // accepted: a flat read here used to splice foreign bytes into the middle of the record.
            NonContiguousRecordFormIds.Add(record.FormId);
            Logger.Instance.Debug(
                "  [ReadRecordData] PREFIX: {0} 0x{1:X8} at offset 0x{2:X} — {3} of {4} bytes are VA-contiguous",
                record.RecordType, record.FormId, record.Offset, readSize, dataSize);
        }

        if (!record.IsCompressed)
        {
            return (useBuffer, readSize);
        }

        dataSize = readSize;
        if (dataSize <= 4)
        {
            Logger.Instance.Debug("  [ReadRecordData] NULL: {0} 0x{1:X8} compressed but dataSize={2}",
                record.RecordType, record.FormId, dataSize);
            return null;
        }

        var decompressed = EsmParser.DecompressRecordData(
            useBuffer.AsSpan(0, dataSize), record.IsBigEndian);
        if (decompressed != null)
        {
            return (decompressed, decompressed.Length);
        }

        // Strict decompress failed. For memory dumps (opt-out via Esm.DisableDmpPartialRecovery), salvage
        // the complete leading subrecords of a truncated compressed payload instead of dropping the whole
        // record — the iterator stops cleanly at the buffer's end, so the cut-off tail is never emitted.
        if (_recoverPartialCompressed)
        {
            var (partial, isComplete) = EsmParser.DecompressRecordDataPartial(
                useBuffer.AsSpan(0, dataSize), record.IsBigEndian);
            if (partial.Length > 0)
            {
                if (!isComplete)
                {
                    PartiallyRecoveredFormIds.Add(record.FormId);
                    Logger.Instance.Debug(
                        "  [ReadRecordData] PARTIAL: {0} 0x{1:X8} recovered {2} bytes from truncated compressed payload",
                        record.RecordType, record.FormId, partial.Length);
                }

                return (partial, partial.Length);
            }
        }

        Logger.Instance.Debug(
            "  [ReadRecordData] NULL: {0} 0x{1:X8} decompression failed (flags=0x{2:X8}, dataSize={3})",
            record.RecordType, record.FormId, record.Flags, dataSize);
        return null;
    }

    /// <summary>
    ///     Fill <paramref name="target" /> with the record body at <paramref name="fileOffset" />,
    ///     returning how many leading bytes are genuinely part of that record.
    ///     <para>
    ///         For a clean ESM this is a flat read and the answer is always the full size. For a
    ///         memory dump it is not: successive memory regions are laid out back-to-back in the
    ///         file whether or not their virtual addresses are adjacent, so a flat read that runs
    ///         past a region boundary quietly splices in bytes from an unrelated address range —
    ///         which then decode as plausible-looking subrecords. Translating to a VA and walking
    ///         the region list gives the true extent, and re-deriving each region's file offset
    ///         also stitches the reverse case: regions contiguous in VA but not in the file.
    ///     </para>
    ///     <para>
    ///         Returns the resident <b>prefix</b> length rather than failing outright, matching the
    ///         truncated-compressed salvage below: the leading subrecords are real and worth keeping.
    ///     </para>
    /// </summary>
    private int ReadRecordBytes(long fileOffset, int size, byte[] target)
    {
        if (MinidumpInfo is not { IsValid: true, MemoryRegions.Count: > 0 })
        {
            return Accessor!.ReadArray(fileOffset, target, 0, size);
        }

        var startVa = MinidumpInfo.FileOffsetToVirtualAddress(fileOffset);
        if (startVa == null)
        {
            // Outside any captured region (dump header, module image, stream tables). A flat read
            // is the only meaningful interpretation and there is no VA continuity to violate.
            return Accessor!.ReadArray(fileOffset, target, 0, size);
        }

        var endVa = startVa.Value + size;
        var resident = 0;
        foreach (var region in MinidumpInfo.GetRegionsInRange(startVa.Value, endVa))
        {
            var overlapStart = Math.Max(region.VirtualAddress, startVa.Value);
            if (overlapStart - startVa.Value > resident)
            {
                break; // VA gap — everything past it belongs to a different allocation.
            }

            var overlapEnd = Math.Min(region.VirtualAddress + region.Size, endVa);
            var bufferOffset = (int)(overlapStart - startVa.Value);
            var overlapSize = (int)(overlapEnd - overlapStart);
            var regionFileOffset = region.FileOffset + (overlapStart - region.VirtualAddress);

            var copied = Accessor!.ReadArray(regionFileOffset, target, bufferOffset, overlapSize);
            resident = bufferOffset + copied;
            if (copied < overlapSize)
            {
                break; // Region is declared but not backed by file bytes.
            }
        }

        return resident;
    }

    /// <summary>
    ///     Observes successful-in-bounds record-read attempts until the returned scope is disposed.
    ///     Parsing is synchronous, so a single scoped observer is sufficient and avoids adding a
    ///     callback parameter to every domain handler.
    /// </summary>
    internal IDisposable ObserveRecordReads(Action<DetectedMainRecord> observer)
    {
        if (_recordReadObserver != null)
        {
            throw new InvalidOperationException("A record-read observer is already active.");
        }

        _recordReadObserver = observer;
        return new RecordReadObserverScope(this, observer);
    }

    private sealed class RecordReadObserverScope(
        RecordParserContext context,
        Action<DetectedMainRecord> observer) : IDisposable
    {
        private RecordParserContext? _context = context;

        public void Dispose()
        {
            var activeContext = Interlocked.Exchange(ref _context, null);
            if (activeContext != null && activeContext._recordReadObserver == observer)
            {
                activeContext._recordReadObserver = null;
            }
        }
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

        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            foreach (var record in ScanResult.MainRecords)
            {
                if (FormIdToFullName.ContainsKey(record.FormId))
                {
                    continue;
                }

                // For compressed records larger than the buffer, rent a bigger one
                // (compressed records need the full data to decompress)
                byte[]? largeBuffer = null;
                var useBuffer = buffer;
                if (record.IsCompressed && record.DataSize > buffer.Length)
                {
                    largeBuffer = ArrayPool<byte>.Shared.Rent((int)record.DataSize);
                    useBuffer = largeBuffer;
                }

                try
                {
                    var recordData = ReadRecordData(record, useBuffer);
                    if (recordData == null)
                    {
                        continue;
                    }

                    var (data, dataSize) = recordData.Value;

                    foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                    {
                        if (sub.Signature == "FULL" && sub.DataLength > 0)
                        {
                            // Table-aware: on a localized plugin (Skyrim/FO4/FO76) a FULL holds a 4-byte
                            // .STRINGS index, not inline text. ReadFullName resolves it; reading raw bytes
                            // here would seed FormIdToFullName with mojibake that then wins the TryAdd race
                            // against the schema parser's resolved name. No-op on non-localized plugins.
                            var name = ReadFullName(data.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(name))
                            {
                                FormIdToFullName.TryAdd(record.FormId, name);
                            }

                            break;
                        }
                    }
                }
                finally
                {
                    if (largeBuffer != null)
                    {
                        ArrayPool<byte>.Shared.Return(largeBuffer);
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

    /// <summary>Reads a 4-byte FormID from the start of the span in the given byte order.</summary>
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

    /// <summary>Reads an OBND object-bounds block (six int16 min/max coordinates) from the span.</summary>
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
        // On a localized plugin (Skyrim/FO4/FO76) the scanner captured each FULL as raw bytes — but a FULL is
        // a 4-byte .STRINGS index there, so scanResult.FullNames hold mojibake (e.g. "1\xAD"), not real names.
        // Seeding them would win the first-write-wins TryAdd race against the schema parser's table-resolved
        // name, so skip the pre-pop entirely and let the table-aware parse populate FormIdToFullName.
        if (LocalizedStrings != null || scanResult.FullNames.Count == 0)
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
            if (edid.Offset < candidate.Offset + candidate.DataSize + candidate.HeaderSize)
            {
                map.TryAdd(candidate.FormId, edid.Name);
            }
        }

        return map;
    }

    /// <summary>
    ///     Detect game version by finding TES4 record in scan results and reading the HEDR
    ///     version float. Returns Unknown if TES4/HEDR is not found or not readable.
    /// </summary>
    private BethesdaGame DetectGameFromTes4()
    {
        if (Accessor == null)
        {
            return BethesdaGame.Unknown;
        }

        // Find TES4 main record (always the first record in an ESM file)
        DetectedMainRecord? tes4 = null;
        foreach (var record in ScanResult.MainRecords)
        {
            if (record.RecordType == "TES4")
            {
                tes4 = record;
                break;
            }
        }

        if (tes4 == null)
        {
            return BethesdaGame.Unknown;
        }

        // Classify the framing structurally from the file header's leading bytes. This is
        // authoritative for Oblivion (and Morrowind) — its 20-byte header puts HEDR at a fixed offset,
        // unlike the 24-byte TES4 family whose HEDR version floats overlap. Reading the HEDR version
        // alone mis-resolved Oblivion (1.0) into the Fallout/Starfield range; PluginFormat.Detect keys
        // on the structural HEDR offset instead (same probe GameDetector uses, minus name refinement).
        var len = (int)Math.Min(256, FileSize - tes4.Offset);
        if (len < 30)
        {
            return BethesdaGame.Unknown;
        }

        var buffer = new byte[len];
        try
        {
            // A short read leaves the tail zeroed, which PluginFormat.Detect would happily
            // classify. The header probe only needs the first 30 bytes, so require at least that.
            if (Accessor.ReadArray(tes4.Offset, buffer, 0, len) < 30)
            {
                return BethesdaGame.Unknown;
            }
        }
        catch
        {
            return BethesdaGame.Unknown;
        }

        return PluginFormat.Detect(buffer).Game;
    }

    #endregion
}
