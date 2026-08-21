using System.Buffers.Binary;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.FileFormat;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized.World;

internal enum RuntimeCellSource
{
    EditorIdHash,
    AllFormsHash,
    HeapScan
}

internal readonly record struct RuntimeCellHit(uint FormId, uint CellVa, RuntimeCellSource Source);

internal readonly record struct RuntimeCellEnumeratorStats(
    int FromEditorIdHash,
    int FromAllFormsHash,
    int FromHeapScan,
    int UniqueTotal);

internal readonly record struct RuntimeCellEnumeration(
    IReadOnlyList<RuntimeCellHit> Cells,
    RuntimeCellEnumeratorStats Stats,
    IReadOnlyList<uint> NavMeshVas,
    IReadOnlyList<uint> NavMeshVaCandidates);

/// <summary>
///     Aggregates runtime <c>TESObjectCELL</c> instances from three evidence-backed discovery paths so
///     downstream consumers (currently NAVM discovery; later REFR / ACHR enumeration) can
///     hand off cell VAs without caring which source produced them. Dedups by FormID with
///     first-source-wins ordering (EditorIdHash &gt; AllFormsHash &gt; HeapScan)
///     so each FormID's <see cref="RuntimeCellHit.Source" /> preserves its earliest provenance.
///     PDB-derived layouts (verified on Aug_RB MemDebug PDB):
///     <list type="bullet">
///         <item>
///             <description>
///                 <c>NiTMapBase</c> (16B): vfptr(+0), m_uiHashSize(+4 uint32), m_ppkHashTable(+8 NiTMapItem**),
///                 m_kAllocator(+12).
///             </description>
///         </item>
///         <item>
///             <description>
///                 <c>NiTMapItem&lt;uint, TESForm*&gt;</c> (12B): m_pkNext(+0), m_key(+4 FormID), m_val(+8
///                 TESForm*).
///             </description>
///         </item>
///         <item>
///             <description>
///                 <c>TESForm</c> subobject prefix: cFormType(byte @ +4), iFormID(uint32 @ +12).
///                 <c>NiTMapItem.m_val</c> points at this subobject even when it is interior to the complete object.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <c>TESObjectCELL</c>: pNavMeshes(+116 NavMeshArray*) — used to validate heap-scan candidates
///                 without dereferencing.
///             </description>
///         </item>
///     </list>
///     The loaded exterior grid is owned by the separate <c>TES</c> singleton through its
///     <c>GridCellArray*</c>. It is deliberately not inferred from a <c>TESWorldSpace*</c>;
///     no reproducible singleton locator is currently available to this reader.
/// </summary>
internal sealed class RuntimeCellEnumerator
{
    private const byte CellFormType = 0x39;
    private const byte WrldFormType = 0x41;
    private const byte NavmFormType = 0x43;

    // NiTMapBase layout
    private const int NiTMapHashSizeOffset = 4;
    private const int NiTMapBucketArrayOffset = 8;
    private const int NiTMapHeaderSize = 16;

    // NiTMapItem<uint, TESForm*> layout: +0 pNext, +4 key, +8 value, size 12. The chain pointer at
    // +0 is reached through the bucket entry itself, so only key and value need named offsets.
    private const int NiTMapItemKeyOffset = 4;
    private const int NiTMapItemValueOffset = 8;
    private const int NiTMapItemSize = 12;

    // TESForm-subobject prefix: cFormType at +4, iFormID at +12. pAllForms stores TESForm*,
    // so these offsets are canonical for every map value, including interior TESForm bases in
    // multiply inherited complete objects. Complete-object PDB fields are rebased downstream.
    private const int TesFormTypeByteOffset = 4;

    private const int TesFormIdOffset = 12;

    // TESObjectCELL: pNavMeshes is a 4-byte NavMeshArray pointer (PDB UDT 0x0002C7DB) at
    // offset 116. NavMeshArray is a separate 16-byte allocation, NOT inline in TESObjectCELL.
    // See RuntimeNavMeshDiscovery.DiscoverForCellVa for the full dereference chain.
    private const int CellNavMeshPointerOffset = 116;
    private const int CellHeapScanReadWindow = CellNavMeshPointerOffset + 4;

    // Bucket walk guard
    private const int MaxBucketsHardLimit = 262144;
    private const int MaxChainHops = 1000;
    private readonly RuntimeMemoryContext _context;

    /// <summary>
    ///     Raw-byte → canonical-byte FormType remap built from
    ///     <see cref="RuntimeEditorIdEntry.OriginalFormType" /> on drift-corrected entries.
    ///     Applied to every raw FormType byte we read from heap memory (the pAllForms walk
    ///     plus heap-scan validation) so the canonical constants
    ///     <see cref="CellFormType" />, <see cref="WrldFormType" />, and
    ///     <see cref="NavmFormType" /> work uniformly across early-build drift (e.g. Nov 2009
    ///     +1 shift at 0x46) and the final layout. Empty when no drift is present, in which
    ///     case <see cref="ToCanonical" /> is identity.
    /// </summary>
    private readonly IReadOnlyDictionary<byte, byte> _driftRemap;

    private readonly MinidumpInfo _minidumpInfo;
    private readonly uint _pAllFormsVa;

    /// <summary>Creates the enumerator over the runtime all-forms table at the given virtual address.</summary>
    public RuntimeCellEnumerator(
        RuntimeMemoryContext context,
        MinidumpInfo minidumpInfo,
        uint pAllFormsVa)
        : this(context, minidumpInfo, pAllFormsVa, null)
    {
    }

    /// <summary>
    ///     Creates the enumerator with an explicit FormType drift-remap table to canonicalize
    ///     early-build form-type codes (e.g. the Nov 2009 +1 shift).
    /// </summary>
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
    {
        return _driftRemap.TryGetValue(rawFormType, out var canonical) ? canonical : rawFormType;
    }

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
    ///     Optional FormIDs of parsed WRLD records from the ESM byte stream. These anchor the
    ///     runtime WRLD FormType byte so worldspaces are excluded from speculative NAVM
    ///     candidates; this reader does not infer a loaded grid from a WRLD object. Pass
    ///     <c>scanResult.MainRecords.Where(r => r.RecordType == "WRLD").Select(r => r.FormId)</c>.
    /// </param>
    /// <param name="knownNavmFormIds">
    ///     Optional FormIDs of parsed NAVM records from the ESM byte stream. Used as a
    ///     calibration anchor for direct NAVM discovery: each byte-stream NAVM whose FormID is also present
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
        var counts = new int[3];
        var navMeshVas = new List<uint>();
        var navMeshVaCandidates = new List<uint>();

        CollectFromEditorIdHash(hits, counts, editorIdEntries);
        CollectFromAllFormsHash(hits, counts, knownWrldFormIds,
            knownNavmFormIds ?? [], navMeshVas, navMeshVaCandidates);
        CollectFromHeapScan(hits, counts);

        var ordered = new List<RuntimeCellHit>(hits.Count);
        foreach (var src in (ReadOnlySpan<RuntimeCellSource>)
                 [
                     RuntimeCellSource.EditorIdHash,
                     RuntimeCellSource.AllFormsHash,
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
            new RuntimeCellEnumeratorStats(counts[0], counts[1], counts[2], hits.Count),
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

    // ---- Path 1: walk pAllForms once ----

    /// <summary>
    ///     Single pass over the pAllForms hash table that (a) adds any FormType==CELL entry
    ///     not already present in <paramref name="hits" />, (b) calibrates the build's raw
    ///     CELL/WRLD/NAVM FormType bytes from known FormIDs, and (c) returns trusted or
    ///     speculative NAVM object VAs through the dedicated output lists.
    /// </summary>
    private void CollectFromAllFormsHash(
        Dictionary<uint, RuntimeCellHit> hits,
        int[] counts,
        IReadOnlyCollection<uint> knownWrldFormIds,
        IReadOnlyCollection<uint> knownNavmFormIds,
        List<uint> navMeshVas,
        List<uint> navMeshVaCandidates)
    {
        if (_pAllFormsVa == 0)
        {
            return;
        }

        var header = _context.ReadBytesAtVa(
            Xbox360MemoryUtils.VaToLong(_pAllFormsVa), NiTMapHeaderSize);
        if (header is null)
        {
            return;
        }

        var hashSize = BinaryUtils.ReadUInt32BE(header, NiTMapHashSizeOffset);
        var bucketArrayVa = BinaryUtils.ReadUInt32BE(header, NiTMapBucketArrayOffset);
        if (hashSize == 0 || hashSize > MaxBucketsHardLimit || !_context.IsValidPointer(bucketArrayVa))
        {
            return;
        }

        var bucketBytes = _context.ReadBytesAtVa(
            Xbox360MemoryUtils.VaToLong(bucketArrayVa), checked((int)(hashSize * 4)));
        if (bucketBytes is null)
        {
            return;
        }

        var wrldSet = knownWrldFormIds as HashSet<uint> ?? [..knownWrldFormIds];
        var navmSet = knownNavmFormIds as HashSet<uint> ?? [..knownNavmFormIds];
        // Pass 1: walk pAllForms, collect raw (rawFormType, formId, formVa) for every valid
        // entry, plus track raw bytes that map to NAVM/CELL/WRLD via byte-stream FormID
        // anchors. This calibration lets direct runtime-NAVM discovery work in dumps where the
        // upstream drift detector (RuntimeBuildOffsets.DetectFormTypeDrift) couldn't confirm
        // the shift — e.g. xex.dmp's Dec 2009 +1 shift at 0x42 that the cross-reference
        // misses when the byte stream lacks DIAL/INFO records.
        var allEntries = new List<(byte RawByte, uint FormId, uint FormVa)>();
        var navmRawBytes = new HashSet<byte>();
        var cellRawBytes = new HashSet<byte>();
        var wrldRawBytes = new HashSet<byte>();
        var knownCellFormIds = hits.Keys.ToHashSet();

        for (var b = 0; b < hashSize; b++)
        {
            var itemVa = BinaryUtils.ReadUInt32BE(bucketBytes, b * 4);
            var seenItemVas = new HashSet<uint>();
            for (var hops = 0; hops < MaxChainHops && itemVa != 0 && _context.IsValidPointer(itemVa); hops++)
            {
                if (!seenItemVas.Add(itemVa))
                {
                    break;
                }

                // VA-based reading stitches adjacent captured regions even when their file
                // offsets are discontiguous, and refuses to bridge a missing VA byte.
                var itemBytes = _context.ReadBytesAtVa(
                    Xbox360MemoryUtils.VaToLong(itemVa), NiTMapItemSize);
                if (itemBytes is null)
                {
                    break;
                }

                var keyFormId = BinaryUtils.ReadUInt32BE(itemBytes, NiTMapItemKeyOffset);
                var formVa = BinaryUtils.ReadUInt32BE(itemBytes, NiTMapItemValueOffset);
                itemVa = BinaryUtils.ReadUInt32BE(itemBytes);

                if (keyFormId == 0 || keyFormId == 0xFFFFFFFF || !_context.IsValidPointer(formVa))
                {
                    continue;
                }

                var formBytes = _context.ReadBytesAtVa(
                    Xbox360MemoryUtils.VaToLong(formVa), TesFormHeaderProbe.RequiredBufferSize);
                if (formBytes is null)
                {
                    continue;
                }

                // The map value is already TESForm*, so its identity is always +4/+12.
                // Requiring keyFormId prevents a damaged header from being accepted.
                if (!TesFormHeaderProbe.TryProbe(formBytes, out var rawFormType, out var formId,
                        keyFormId))
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

                if (knownCellFormIds.Contains(formId))
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
                // WRLD is a calibration/exclusion category only. The loaded exterior grid
                // belongs to the TES singleton, not to TESWorldSpace at a fixed offset.
            }
            else if (navmRawBytes.Contains(rawByte) || navmSet.Contains(formId))
            {
                // Direct BSNavMesh VA from calibrated bytes. pAllForms holds BSNavMesh
                // pointers keyed by FormID; each entry is a self-describing TESForm-derived
                // BSNavMesh struct. Captured here so NavMeshHandler can
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

    // ---- Path 2: heap-scan for TESObjectCELL vtable ----

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

        foreach (var region in _minidumpInfo.MemoryRegions)
        {
            var regionVa = unchecked((uint)region.VirtualAddress);
            if (regionVa < Xbox360MemoryUtils.HeapBase || regionVa >= Xbox360MemoryUtils.HeapEnd)
            {
                continue;
            }

            if (region.Size <= 0 || region.Size > int.MaxValue)
            {
                continue;
            }

            // Scan three bytes past a captured region when the following VAs are present so a
            // four-byte vfptr split at the boundary remains discoverable. Only matches whose
            // first byte belongs to this region are accepted, avoiding duplicates in the next
            // region's scan. ReadBytesAtVa handles noncontiguous file storage and VA gaps.
            var regionSize = checked((int)region.Size);
            var scanSize = regionSize;
            if (regionSize <= int.MaxValue - (sizeof(uint) - 1) &&
                _minidumpInfo.IsVaRangeCaptured(
                    Xbox360MemoryUtils.VaToLong(regionVa), regionSize + (sizeof(uint) - 1)))
            {
                scanSize += sizeof(uint) - 1;
            }

            var regionBytes = _context.ReadBytesAtVa(
                Xbox360MemoryUtils.VaToLong(regionVa), scanSize);
            if (regionBytes is null)
            {
                continue;
            }

            var matches = matcher.Search(regionBytes);
            foreach (var (_, _, position) in matches)
            {
                // position is the byte offset of the vtable match in this VA-based scan.
                if (position < 0 || position >= regionSize)
                {
                    continue;
                }

                var cellVa = unchecked(regionVa + (uint)position);
                if ((cellVa & 3) != 0)
                {
                    continue;
                }

                var validateBuffer = _context.ReadBytesAtVa(
                    Xbox360MemoryUtils.VaToLong(cellVa), CellHeapScanReadWindow);
                if (validateBuffer is null)
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
        foreach (var hit in hits.Values)
        {
            var vfptrBuffer = _context.ReadBytesAtVa(
                Xbox360MemoryUtils.VaToLong(hit.CellVa), sizeof(uint));
            if (vfptrBuffer is null)
            {
                continue;
            }

            var vfptr = BinaryUtils.ReadUInt32BE(vfptrBuffer);
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
