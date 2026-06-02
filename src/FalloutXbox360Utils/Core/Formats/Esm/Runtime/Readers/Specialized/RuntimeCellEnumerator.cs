using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

internal enum RuntimeCellSource
{
    EditorIdHash,
    AllFormsHash,
    WorldspaceGrid,
    HeapScan
}

internal readonly record struct RuntimeCellHit(uint FormId, uint CellVa, RuntimeCellSource Source);

internal readonly record struct RuntimeCellEnumeratorStats(
    int FromEditorIdHash,
    int FromAllFormsHash,
    int FromWorldspaceGrid,
    int FromHeapScan,
    int UniqueTotal);

internal readonly record struct RuntimeCellEnumeration(
    IReadOnlyList<RuntimeCellHit> Cells,
    RuntimeCellEnumeratorStats Stats,
    IReadOnlyList<uint> NavMeshVas,
    IReadOnlyList<uint> NavMeshVaCandidates);

/// <summary>
///     Aggregates runtime <c>TESObjectCELL</c> instances from up to four discovery paths so
///     downstream consumers (currently NAVM discovery; later REFR / ACHR enumeration) can
///     hand off cell VAs without caring which source produced them. Dedups by FormID with
///     first-source-wins ordering (EditorIdHash &gt; AllFormsHash &gt; WorldspaceGrid &gt; HeapScan)
///     so each FormID's <see cref="RuntimeCellHit.Source" /> preserves its earliest provenance.
///
///     PDB-derived layouts (verified on Aug_RB MemDebug PDB):
///     <list type="bullet">
///         <item><description><c>NiTMapBase</c> (16B): vfptr(+0), m_uiHashSize(+4 uint32), m_ppkHashTable(+8 NiTMapItem**), m_kAllocator(+12).</description></item>
///         <item><description><c>NiTMapItem&lt;uint, TESForm*&gt;</c> (12B): m_pkNext(+0), m_key(+4 FormID), m_val(+8 TESForm*).</description></item>
///         <item><description><c>TESForm</c> prefix: cFormType(+1 byte), iFormID(+12 uint32). All TESForm-derived classes share this prefix at +0 of their layout.</description></item>
///         <item><description><c>TESObjectCELL</c>: pNavMeshes(+116 BSSimpleArray, 16B inline) — used to validate heap-scan candidates without dereferencing.</description></item>
///         <item><description><c>TESWorldSpace</c> (244B): pGridCellA(+16) — pointer to a GridCellArray.</description></item>
///         <item><description><c>GridCellArray</c> (40B): 25-slot fixed array of TESObjectCELL* in [+0..+100), grid-center metadata (+36 iCurrentGridX, +40 iCurrentGridY).</description></item>
///     </list>
/// </summary>
internal sealed class RuntimeCellEnumerator
{
    private readonly RuntimeMemoryContext _context;
    private readonly MinidumpInfo _minidumpInfo;
    private readonly uint _pAllFormsVa;
    /// <summary>
    ///     Raw-byte → canonical-byte FormType remap built from
    ///     <see cref="RuntimeEditorIdEntry.OriginalFormType" /> on drift-corrected entries.
    ///     Applied to every raw FormType byte we read from heap memory (Path 1 pAllForms walk
    ///     plus Path 2/3 cell validators) so the canonical constants
    ///     <see cref="CellFormType" />, <see cref="WrldFormType" />, and
    ///     <see cref="NavmFormType" /> work uniformly across early-build drift (e.g. Nov 2009
    ///     +1 shift at 0x46) and the final layout. Empty when no drift is present, in which
    ///     case <see cref="ToCanonical" /> is identity.
    /// </summary>
    private readonly IReadOnlyDictionary<byte, byte> _driftRemap;

    public RuntimeCellEnumerator(
        RuntimeMemoryContext context,
        MinidumpInfo minidumpInfo,
        uint pAllFormsVa)
        : this(context, minidumpInfo, pAllFormsVa, driftRemap: null)
    {
    }

    public RuntimeCellEnumerator(
        RuntimeMemoryContext context,
        MinidumpInfo minidumpInfo,
        uint pAllFormsVa,
        IReadOnlyDictionary<byte, byte>? driftRemap)
    {
        _context = context;
        _minidumpInfo = minidumpInfo;
        _pAllFormsVa = pAllFormsVa;
        _driftRemap = driftRemap ?? new Dictionary<byte, byte>();
    }

    private byte ToCanonical(byte rawFormType)
        => _driftRemap.TryGetValue(rawFormType, out var canonical) ? canonical : rawFormType;

    private const byte CellFormType = 0x39;
    private const byte WrldFormType = 0x41;
    private const byte NavmFormType = 0x43;

    // NiTMapBase layout
    private const int NiTMapHashSizeOffset = 4;
    private const int NiTMapBucketArrayOffset = 8;
    private const int NiTMapHeaderSize = 16;

    // NiTMapItem<uint, TESForm*> layout
    private const int NiTMapItemNextOffset = 0;
    private const int NiTMapItemKeyOffset = 4;
    private const int NiTMapItemValueOffset = 8;
    private const int NiTMapItemSize = 12;

    // Standard TESForm prefix: cFormType at +4, iFormID at +12. Used by Path 0's
    // editor-id entries (which already carry FormType from upstream) and Path 2/3 validation.
    // The pAllForms walk in Path 1 uses TesFormHeaderProbe instead to handle multi-inheritance
    // classes — TESWorldSpace is TESFullName-first (FormType +24, FormID +32) so the standard
    // offsets miss every WRLD entry, which previously broke worldspace-grid discovery.
    private const int TesFormTypeByteOffset = 4;
    private const int TesFormIdOffset = 12;
    private const int TesFormReadWindow = 16;
    private const int TesFormProbeReadWindow = TesFormHeaderProbe.RequiredBufferSize;

    // TESObjectCELL: pNavMeshes is a 4-byte NavMeshArray pointer (PDB UDT 0x0002C7DB) at
    // offset 116. NavMeshArray is a separate 16-byte allocation, NOT inline in TESObjectCELL.
    // See RuntimeNavMeshDiscovery.DiscoverForCellVa for the full dereference chain.
    private const int CellNavMeshPointerOffset = 116;
    private const int CellHeapScanReadWindow = CellNavMeshPointerOffset + 4;

    // TESWorldSpace
    private const int WorldspaceGridCellArrayPtrOffset = 16;
    private const int WorldspaceReadWindow = 24;

    // GridCellArray
    private const int GridCellArraySlotCount = 25;
    private const int GridCellArrayPointersSize = GridCellArraySlotCount * 4;

    // Bucket walk guard
    private const int MaxBucketsHardLimit = 262144;
    private const int MaxChainHops = 1000;

    /// <summary>
    ///     Enumerate every <c>TESObjectCELL</c> discoverable in the dump and return one
    ///     <see cref="RuntimeCellHit" /> per unique FormID, tagged with the highest-priority
    ///     source that produced it.
    /// </summary>
    /// <param name="editorIdEntries">
    ///     Editor-id hash entries from <c>ScanResult.RuntimeEditorIds</c>. Path 0 filters this list
    ///     by <see cref="CellFormType" /> to surface named cells (most interiors).
    /// </param>
    /// <param name="knownWrldFormIds">
    ///     Optional FormIDs of parsed WRLD records from the ESM byte stream. Augments the
    ///     editor-id hash as a WRLD source for Path 2 — Path 2 always picks up worldspaces
    ///     via <see cref="WrldFormType" /> entries in <paramref name="editorIdEntries" /> as
    ///     well, so this can be empty (e.g., a DMP whose byte stream doesn't carry WRLD records)
    ///     without losing grid coverage. Pass
    ///     <c>scanResult.MainRecords.Where(r => r.RecordType == "WRLD").Select(r => r.FormId)</c>.
    /// </param>
    /// <param name="knownNavmFormIds">
    ///     Optional FormIDs of parsed NAVM records from the ESM byte stream. Used as a
    ///     calibration anchor for Path 4: each byte-stream NAVM whose FormID is also present
    ///     in pAllForms lets us read the build's actual raw FormType byte for NAVMs, which
    ///     matters when <c>RuntimeBuildOffsets.DetectFormTypeDrift</c> couldn't confirm the
    ///     drift (typically because the byte stream lacks DIAL/INFO cross-references). The
    ///     byte filter falls back to canonical 0x43 when no calibration anchor is present.
    /// </param>
    public RuntimeCellEnumeration Enumerate(
        IReadOnlyList<RuntimeEditorIdEntry> editorIdEntries,
        IReadOnlyCollection<uint> knownWrldFormIds,
        IReadOnlyCollection<uint>? knownNavmFormIds = null)
    {
        var hits = new Dictionary<uint, RuntimeCellHit>();
        var counts = new int[4];
        var navMeshVas = new List<uint>();
        var navMeshVaCandidates = new List<uint>();

        CollectFromEditorIdHash(hits, counts, editorIdEntries);
        var wrldVas = CollectFromAllFormsHash(hits, counts, knownWrldFormIds,
            knownNavmFormIds ?? [], navMeshVas, navMeshVaCandidates);
        AddWorldspacesFromEditorIdHash(wrldVas, editorIdEntries);
        CollectFromWorldspaceGrid(hits, counts, wrldVas);
        CollectFromHeapScan(hits, counts);

        var ordered = new List<RuntimeCellHit>(hits.Count);
        foreach (var src in (ReadOnlySpan<RuntimeCellSource>)
                 [
                     RuntimeCellSource.EditorIdHash,
                     RuntimeCellSource.AllFormsHash,
                     RuntimeCellSource.WorldspaceGrid,
                     RuntimeCellSource.HeapScan
                 ])
        {
            foreach (var hit in hits.Values)
            {
                if (hit.Source == src)
                {
                    ordered.Add(hit);
                }
            }
        }

        // Stable secondary order by FormID within each source so test output is deterministic.
        ordered.Sort((a, b) =>
        {
            var sourceCmp = ((int)a.Source).CompareTo((int)b.Source);
            return sourceCmp != 0 ? sourceCmp : a.FormId.CompareTo(b.FormId);
        });

        return new RuntimeCellEnumeration(
            ordered,
            new RuntimeCellEnumeratorStats(counts[0], counts[1], counts[2], counts[3], hits.Count),
            navMeshVas,
            navMeshVaCandidates);
    }

    // ---- Path 0: editor-id hash filter ----

    private static void CollectFromEditorIdHash(
        Dictionary<uint, RuntimeCellHit> hits,
        int[] counts,
        IReadOnlyList<RuntimeEditorIdEntry> editorIdEntries)
    {
        foreach (var entry in editorIdEntries)
        {
            if (entry.FormType != CellFormType)
            {
                continue;
            }

            if (!entry.TesFormPointer.HasValue || entry.TesFormPointer.Value == 0)
            {
                continue;
            }

            var cellVa = unchecked((uint)entry.TesFormPointer.Value);
            if (entry.FormId == 0 || entry.FormId == 0xFFFFFFFF)
            {
                continue;
            }

            if (hits.TryAdd(entry.FormId, new RuntimeCellHit(entry.FormId, cellVa, RuntimeCellSource.EditorIdHash)))
            {
                counts[(int)RuntimeCellSource.EditorIdHash]++;
            }
        }
    }

    // ---- Path 1 (with bonus WRLD discovery): walk pAllForms once ----

    /// <summary>
    ///     Single pass over the pAllForms hash table that (a) adds any FormType==CELL entry
    ///     not already present in <paramref name="hits" />, and (b) records every TESForm
    ///     pointer whose FormID matches a known WRLD FormID so Path 2 can walk the GridCellArray
    ///     of each loaded worldspace. Returns the list of worldspace VAs for Path 2's
    ///     consumption.
    /// </summary>
    private List<uint> CollectFromAllFormsHash(
        Dictionary<uint, RuntimeCellHit> hits,
        int[] counts,
        IReadOnlyCollection<uint> knownWrldFormIds,
        IReadOnlyCollection<uint> knownNavmFormIds,
        List<uint> navMeshVas,
        List<uint> navMeshVaCandidates)
    {
        var wrldVas = new List<uint>();
        if (_pAllFormsVa == 0)
        {
            return wrldVas;
        }

        var headerOffset = _context.VaToFileOffset(_pAllFormsVa);
        if (headerOffset is not long headerFile)
        {
            return wrldVas;
        }

        var header = _context.ReadBytes(headerFile, NiTMapHeaderSize);
        if (header is null)
        {
            return wrldVas;
        }

        var hashSize = BinaryUtils.ReadUInt32BE(header, NiTMapHashSizeOffset);
        var bucketArrayVa = BinaryUtils.ReadUInt32BE(header, NiTMapBucketArrayOffset);
        if (hashSize == 0 || hashSize > MaxBucketsHardLimit || !_context.IsValidPointer(bucketArrayVa))
        {
            return wrldVas;
        }

        var bucketArrayOffset = _context.VaToFileOffset(bucketArrayVa);
        if (bucketArrayOffset is not long bucketBase)
        {
            return wrldVas;
        }

        var bucketBytes = _context.ReadBytes(bucketBase, (int)(hashSize * 4));
        if (bucketBytes is null)
        {
            return wrldVas;
        }

        var wrldSet = knownWrldFormIds as HashSet<uint> ?? [..knownWrldFormIds];
        var navmSet = knownNavmFormIds as HashSet<uint> ?? [..knownNavmFormIds];
        // TesFormHeaderProbe.RequiredBufferSize is enough to probe every candidate layout
        // (FormID at +32 needs 36 bytes); the standard layout's CELL FormType byte at +4
        // also falls within this window. Using one wider buffer keeps the walk straight.
        var formBuffer = new byte[TesFormProbeReadWindow];

        // Pass 1: walk pAllForms, collect raw (rawFormType, formId, formVa) for every valid
        // entry, plus track raw bytes that map to NAVM/CELL/WRLD via byte-stream FormID
        // anchors. This calibration lets Path 4 surface runtime NAVMs in dumps where the
        // upstream drift detector (RuntimeBuildOffsets.DetectFormTypeDrift) couldn't confirm
        // the shift — e.g. xex.dmp's Dec 2009 +1 shift at 0x42 that the cross-reference
        // misses when the byte stream lacks DIAL/INFO records.
        var allEntries = new List<(byte RawByte, uint FormId, uint FormVa)>();
        var navmRawBytes = new HashSet<byte>();
        var cellRawBytes = new HashSet<byte>();
        var wrldRawBytes = new HashSet<byte>();

        for (var b = 0; b < hashSize; b++)
        {
            var itemVa = BinaryUtils.ReadUInt32BE(bucketBytes, b * 4);
            for (var hops = 0; hops < MaxChainHops && itemVa != 0 && _context.IsValidPointer(itemVa); hops++)
            {
                var itemOffset = _context.VaToFileOffset(itemVa);
                if (itemOffset is not long itemFile)
                {
                    break;
                }

                var itemBytes = _context.ReadBytes(itemFile, NiTMapItemSize);
                if (itemBytes is null)
                {
                    break;
                }

                var keyFormId = BinaryUtils.ReadUInt32BE(itemBytes, NiTMapItemKeyOffset);
                var formVa = BinaryUtils.ReadUInt32BE(itemBytes, NiTMapItemValueOffset);
                itemVa = BinaryUtils.ReadUInt32BE(itemBytes, NiTMapItemNextOffset);

                if (keyFormId == 0 || keyFormId == 0xFFFFFFFF || !_context.IsValidPointer(formVa))
                {
                    continue;
                }

                var formOffset = _context.VaToFileOffset(formVa);
                if (formOffset is not long formFile)
                {
                    continue;
                }

                if (!TryReadInto(formFile, formBuffer))
                {
                    continue;
                }

                // TesFormHeaderProbe walks three candidate layouts: standard (+4,+12),
                // TESFullName-first (+24,+32, used by MSTT and TESWorldSpace), and
                // TESProduceForm-first (+16,+24, used by FLOR). It picks the layout
                // whose iFormID matches keyFormId — the strict (+4,+12) read previously
                // used here misses every WRLD entry since TESWorldSpace puts iFormID at +32.
                if (!TesFormHeaderProbe.TryProbe(formBuffer, out var rawFormType, out var formId,
                        expectedFormId: keyFormId))
                {
                    continue;
                }

                allEntries.Add((rawFormType, formId, formVa));

                // FormID-anchored calibration: if this FormID matches a known byte-stream
                // NAVM/CELL/WRLD record, the entry's raw FormType byte IS this build's NAVM/
                // CELL/WRLD byte regardless of what canonical or drift-remapped value says.
                if (navmSet.Contains(formId))
                {
                    navmRawBytes.Add(rawFormType);
                }

                if (cellSet().Contains(formId))
                {
                    cellRawBytes.Add(rawFormType);
                }

                if (wrldSet.Contains(formId))
                {
                    wrldRawBytes.Add(rawFormType);
                }
            }
        }

        // Calibration decision: NAVM is "calibrated" when either (a) at least one byte-stream
        // FormID anchor matched (navmRawBytes is non-empty from Pass 1), or (b) the upstream
        // drift detector remapped some raw byte to canonical NAVM. Either signal gives us a
        // trustworthy NAVM byte; canonical fallback alone does NOT count, since that's the
        // exact case that produces false positives on uncalibrated builds like xex.dmp.
        var navmAnchored = navmRawBytes.Count > 0;
        var navmDriftConfirmed = false;
        foreach (var canonicalByte in _driftRemap.Values)
        {
            if (canonicalByte == NavmFormType)
            {
                navmDriftConfirmed = true;
                break;
            }
        }

        var navmCalibrated = navmAnchored || navmDriftConfirmed;

        // CELL/WRLD canonical fallback bytes are always trusted — their identification has
        // been validated across every targeted build with no observed FPs.
        cellRawBytes.Add(InverseToCanonical(CellFormType));
        cellRawBytes.Add(CellFormType);
        wrldRawBytes.Add(InverseToCanonical(WrldFormType));
        wrldRawBytes.Add(WrldFormType);

        // NAVM fallback bytes are only added to the trusted set when calibration succeeded.
        // Otherwise they'd route uncalibrated entries (likely DIAL/INFO at canonical 0x43 on
        // drifted builds) straight into NavMeshVas with no shape check.
        if (navmCalibrated)
        {
            navmRawBytes.Add(InverseToCanonical(NavmFormType));
            navmRawBytes.Add(NavmFormType);
        }

        // Build the speculative candidate window for uncalibrated builds. Bounded to
        // [canonical-2..canonical+2] per the drift memory (observed shifts ≤ +1). Exclude
        // any byte already classified as CELL or WRLD so legitimate CELL/WRLD entries never
        // leak into the NAVM candidate channel.
        var candidateWindow = new HashSet<byte>();
        if (!navmCalibrated)
        {
            for (var d = -2; d <= 2; d++)
            {
                candidateWindow.Add((byte)(NavmFormType + d));
                candidateWindow.Add((byte)(InverseToCanonical(NavmFormType) + d));
            }

            candidateWindow.ExceptWith(cellRawBytes);
            candidateWindow.ExceptWith(wrldRawBytes);
        }

        // Pass 2: classify entries using the calibrated byte sets, plus speculative routing
        // when uncalibrated.
        foreach (var (rawByte, formId, formVa) in allEntries)
        {
            if (cellRawBytes.Contains(rawByte))
            {
                if (hits.TryAdd(formId, new RuntimeCellHit(formId, formVa, RuntimeCellSource.AllFormsHash)))
                {
                    counts[(int)RuntimeCellSource.AllFormsHash]++;
                }
            }
            else if (wrldRawBytes.Contains(rawByte) || wrldSet.Contains(formId))
            {
                wrldVas.Add(formVa);
            }
            else if (navmRawBytes.Contains(rawByte) || navmSet.Contains(formId))
            {
                // Direct BSNavMesh VA from calibrated bytes. pAllForms holds BSNavMesh
                // pointers keyed by FormID; each entry is a self-describing TESForm-derived
                // BSNavMesh struct. Captured here so Path 4 in MiscGameSystemHandler can
                // project each into a synthetic NavMeshRecord without needing a cell parent.
                navMeshVas.Add(formVa);
            }
            else if (candidateWindow.Contains(rawByte))
            {
                // Speculative candidate: byte sits in the [canonical±2] window for NAVM but
                // we have no anchor confirming it. The structural validator
                // (BsNavMeshStructuralValidator in Strict mode) filters false positives
                // before RuntimeNavMeshDiscovery projects the survivors.
                navMeshVaCandidates.Add(formVa);
            }
        }

        return wrldVas;

        // Local function: the existing CollectFromEditorIdHash population isn't available
        // here, so we derive the CELL FormID anchor set from the hits dict itself (any
        // Path 0 cell whose FormID we already trust IS a CELL by construction).
        HashSet<uint> cellSet()
        {
            var set = new HashSet<uint>(hits.Count);
            foreach (var hit in hits.Values)
            {
                set.Add(hit.FormId);
            }

            return set;
        }
    }

    /// <summary>
    ///     Inverse of <see cref="ToCanonical" />: given a canonical byte, return the raw
    ///     byte that maps to it (so a single lookup tells Pass 2 whether a heap byte
    ///     represents the canonical type). Identity when the canonical byte isn't a remap
    ///     target.
    /// </summary>
    private byte InverseToCanonical(byte canonical)
    {
        foreach (var (raw, can) in _driftRemap)
        {
            if (can == canonical)
            {
                return raw;
            }
        }

        return canonical;
    }

    private bool TryReadInto(long fileOffset, byte[] buffer)
    {
        if (fileOffset + buffer.Length > _context.FileSize)
        {
            return false;
        }

        try
        {
            _context.Accessor.ReadArray(fileOffset, buffer, 0, buffer.Length);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     The pAllForms walk is the canonical worldspace source, but proto DMPs sometimes
    ///     ship without WRLD MainRecords in their in-DMP ESM byte stream (the parsed
    ///     <c>scanResult.MainRecords</c> list is empty for WRLD), which strands
    ///     <see cref="CollectFromAllFormsHash" />'s cross-reference set. Drift-corrected
    ///     editor-id entries already carry <see cref="WrldFormType" />=0x41 reliably, so this
    ///     pass guarantees Path 2 always has a worldspace to walk when one exists in the dump.
    ///     Deduplication by VA prevents double-walking when both sources yield the same WRLD.
    /// </summary>
    private static void AddWorldspacesFromEditorIdHash(
        List<uint> wrldVas,
        IReadOnlyList<RuntimeEditorIdEntry> editorIdEntries)
    {
        if (editorIdEntries.Count == 0)
        {
            return;
        }

        var existing = wrldVas.ToHashSet();
        foreach (var entry in editorIdEntries)
        {
            if (entry.FormType != WrldFormType || entry.TesFormPointer is not { } ptr || ptr == 0)
            {
                continue;
            }

            var wrldVa = unchecked((uint)ptr);
            if (existing.Add(wrldVa))
            {
                wrldVas.Add(wrldVa);
            }
        }
    }

    // ---- Path 2: walk each loaded TESWorldSpace's GridCellArray ----

    private void CollectFromWorldspaceGrid(
        Dictionary<uint, RuntimeCellHit> hits,
        int[] counts,
        List<uint> wrldVas)
    {
        if (wrldVas.Count == 0)
        {
            return;
        }

        var cellFormBuffer = new byte[TesFormReadWindow];

        foreach (var wrldVa in wrldVas)
        {
            var wrldOffset = _context.VaToFileOffset(wrldVa);
            if (wrldOffset is not long wrldFile)
            {
                continue;
            }

            var wrldBytes = _context.ReadBytes(wrldFile, WorldspaceReadWindow);
            if (wrldBytes is null)
            {
                continue;
            }

            var gridCellArrayVa = BinaryUtils.ReadUInt32BE(wrldBytes, WorldspaceGridCellArrayPtrOffset);
            if (!_context.IsValidPointer(gridCellArrayVa))
            {
                continue;
            }

            var gridOffset = _context.VaToFileOffset(gridCellArrayVa);
            if (gridOffset is not long gridFile)
            {
                continue;
            }

            var slotBytes = _context.ReadBytes(gridFile, GridCellArrayPointersSize);
            if (slotBytes is null)
            {
                continue;
            }

            for (var i = 0; i < GridCellArraySlotCount; i++)
            {
                var cellVa = BinaryUtils.ReadUInt32BE(slotBytes, i * 4);
                if (!_context.IsValidPointer(cellVa))
                {
                    continue;
                }

                var cellOffset = _context.VaToFileOffset(cellVa);
                if (cellOffset is not long cellFile)
                {
                    continue;
                }

                if (!TryReadInto(cellFile, cellFormBuffer))
                {
                    continue;
                }

                if (ToCanonical(cellFormBuffer[TesFormTypeByteOffset]) != CellFormType)
                {
                    continue;
                }

                var formId = BinaryUtils.ReadUInt32BE(cellFormBuffer, TesFormIdOffset);
                if (formId == 0 || formId == 0xFFFFFFFF)
                {
                    continue;
                }

                if (hits.TryAdd(formId, new RuntimeCellHit(formId, cellVa, RuntimeCellSource.WorldspaceGrid)))
                {
                    counts[(int)RuntimeCellSource.WorldspaceGrid]++;
                }
                else
                {
                    // FormID already in hits via Path 0 or Path 1, but Path 2's grid-slot VA
                    // is the engine's canonical TESObjectCELL pointer. If the existing VA
                    // differs (e.g., editor-id hash stored a TESForm sub-object pointer), we
                    // upgrade the entry's CellVa to the grid VA so downstream
                    // DiscoverNavMeshesForCellVa reads the correct cell base. Source stays
                    // first-source-wins for provenance.
                    var existing = hits[formId];
                    if (existing.CellVa != cellVa)
                    {
                        hits[formId] = existing with { CellVa = cellVa };
                    }
                }
            }
        }
    }

    // ---- Path 3: heap-scan for TESObjectCELL vtable ----

    private void CollectFromHeapScan(Dictionary<uint, RuntimeCellHit> hits, int[] counts)
    {
        if (hits.Count == 0)
        {
            // No seed available — can't extract a vtable VA empirically. Skip rather than
            // fabricate a guess.
            Logger.Instance.Debug(
                "  [RuntimeCellEnumerator] Heap-scan skipped: no seed cell available from earlier paths.");
            return;
        }

        var seedVtable = TryHarvestVtableFromHits(hits);
        if (seedVtable is not uint vtableVa)
        {
            Logger.Instance.Debug(
                "  [RuntimeCellEnumerator] Heap-scan skipped: no seed cell produced a module-range vfptr.");
            return;
        }

        var matcher = new SignatureMatcher();
        var vtablePattern = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(vtablePattern, vtableVa);
        matcher.AddPattern("CELL_VTABLE", vtablePattern);
        matcher.Build();

        var validateBuffer = new byte[CellHeapScanReadWindow];

        foreach (var region in _minidumpInfo.MemoryRegions)
        {
            var regionVa = unchecked((uint)region.VirtualAddress);
            if (regionVa < Xbox360MemoryUtils.HeapBase || regionVa >= Xbox360MemoryUtils.HeapEnd)
            {
                continue;
            }

            if (region.Size <= 0 || region.FileOffset + region.Size > _context.FileSize)
            {
                continue;
            }

            var regionBytes = _context.ReadBytes(region.FileOffset, checked((int)region.Size));
            if (regionBytes is null)
            {
                continue;
            }

            var matches = matcher.Search(regionBytes, region.FileOffset);
            foreach (var (_, _, position) in matches)
            {
                // position is the file offset of the vtable match (cellVa + 0 in struct terms).
                var fileOffsetInRegion = position - region.FileOffset;
                if ((fileOffsetInRegion & 3) != 0)
                {
                    continue;
                }

                var cellVa = unchecked(regionVa + (uint)fileOffsetInRegion);
                if (!TryReadInto(position, validateBuffer))
                {
                    continue;
                }

                if (ToCanonical(validateBuffer[TesFormTypeByteOffset]) != CellFormType)
                {
                    continue;
                }

                var formId = BinaryUtils.ReadUInt32BE(validateBuffer, TesFormIdOffset);
                if (formId == 0 || formId == 0xFFFFFFFF)
                {
                    continue;
                }

                if (!LooksLikePlausibleNavMeshArrayPointer(validateBuffer))
                {
                    continue;
                }

                if (hits.TryAdd(formId, new RuntimeCellHit(formId, cellVa, RuntimeCellSource.HeapScan)))
                {
                    counts[(int)RuntimeCellSource.HeapScan]++;
                }
            }
        }
    }

    private uint? TryHarvestVtableFromHits(Dictionary<uint, RuntimeCellHit> hits)
    {
        var vfptrBuffer = new byte[4];
        foreach (var hit in hits.Values)
        {
            var offset = _context.VaToFileOffset(hit.CellVa);
            if (offset is not long file)
            {
                continue;
            }

            if (!TryReadInto(file, vfptrBuffer))
            {
                continue;
            }

            var vfptr = BinaryUtils.ReadUInt32BE(vfptrBuffer, 0);
            if (Xbox360MemoryUtils.IsModulePointer(vfptr))
            {
                return vfptr;
            }
        }

        return null;
    }

    /// <summary>
    ///     Heap-scan validator for the <c>pNavMeshes</c> pointer at <c>TESObjectCELL+0x74</c>:
    ///     either zero (cell has no NavMeshArray allocated) or a valid runtime pointer to a
    ///     NavMeshArray. Anything else means we matched on an unrelated allocation that happens
    ///     to start with the cell vtable but isn't actually a TESObjectCELL.
    /// </summary>
    private bool LooksLikePlausibleNavMeshArrayPointer(byte[] buffer)
    {
        var pNavMeshes = BinaryUtils.ReadUInt32BE(buffer, CellNavMeshPointerOffset);
        return pNavMeshes == 0 || _context.IsValidPointer(pNavMeshes);
    }
}
