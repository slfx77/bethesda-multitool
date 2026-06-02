using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime.Readers;

/// <summary>
///     Synthetic in-memory tests for <see cref="RuntimeCellEnumerator" />: the four-source
///     <c>TESObjectCELL</c> discovery pipeline used by Phase 2c NAVM enumeration. Each test
///     builds a single contiguous "heap" byte[], lays out a planned set of structs (pAllForms
///     hash table, TESForms, TESWorldSpace + GridCellArray, decoy heap allocations), wraps it
///     in a <see cref="SparseMemoryAccessor" /> as a single range covering the synthetic
///     region, and asserts the enumerator's output.
/// </summary>
public sealed class RuntimeCellEnumeratorTests
{
    private const uint HeapBaseVa = 0x40000000;
    private const uint CellVtable = 0x82010000;
    private const uint DecoyVtable = 0x82020000;
    private const uint HeapNonModuleVtable = 0x40FF0000;

    private const byte CellFormType = 0x39;
    private const byte WrldFormType = 0x41;
    private const byte NavmFormType = 0x43;
    private const byte WeapFormType = 0x28;

    private const int CellStructSize = 256;
    private const int CellNavMeshArrayOffset = 116;
    private const int WrldStructSize = 256;
    private const int WrldGridCellArrayPtrOffset = 16;
    private const int GridCellArrayPointersSize = 25 * 4;

    // ============================================================================
    // Path 0: Editor-id hash filter
    // ============================================================================

    [Fact]
    public void EditorIdHash_ReturnsOnlyCellFormType()
    {
        var heap = new HeapBuilder(0x4000);
        var cellVa = heap.PlaceCell(formId: 0x0000A001);
        var notCellVa = heap.PlaceTesForm(WeapFormType, 0x0000B001);

        var enumerator = heap.BuildEnumerator(pAllFormsVa: 0);
        var entries = new[]
        {
            MakeEntry(formId: 0x0000A001, formType: CellFormType, tesFormPtr: cellVa),
            MakeEntry(formId: 0x0000B001, formType: WeapFormType, tesFormPtr: notCellVa)
        };

        var result = enumerator.Enumerate(entries, knownWrldFormIds: []);

        Assert.Equal(1, result.Stats.FromEditorIdHash);
        Assert.Equal(1, result.Stats.UniqueTotal);
        Assert.Single(result.Cells);
        Assert.Equal(0x0000A001u, result.Cells[0].FormId);
        Assert.Equal(cellVa, result.Cells[0].CellVa);
        Assert.Equal(RuntimeCellSource.EditorIdHash, result.Cells[0].Source);
    }

    // ============================================================================
    // Path 1: pAllForms hash walk
    // ============================================================================

    [Fact]
    public void AllFormsHash_FiltersByFormType_AndWalksLinkedList()
    {
        var heap = new HeapBuilder(0x4000);
        var cellAVa = heap.PlaceCell(formId: 0x10000001);
        var cellBVa = heap.PlaceCell(formId: 0x10000002);
        var weaponVa = heap.PlaceTesForm(WeapFormType, 0x10000003);

        // Two-bucket hash table; bucket 0 chains cellA -> weapon -> cellB so the walker
        // must traverse the m_pkNext links to pick up both CELL entries.
        var node3 = heap.PlaceMapItem(formId: 0x10000002, formVa: cellBVa, nextVa: 0);
        var node2 = heap.PlaceMapItem(formId: 0x10000003, formVa: weaponVa, nextVa: node3);
        var node1 = heap.PlaceMapItem(formId: 0x10000001, formVa: cellAVa, nextVa: node2);
        var hashTableVa = heap.PlaceHashTable(buckets: [node1, 0]);

        var enumerator = heap.BuildEnumerator(hashTableVa);
        var result = enumerator.Enumerate(editorIdEntries: [], knownWrldFormIds: []);

        Assert.Equal(2, result.Stats.FromAllFormsHash);
        Assert.Equal(0, result.Stats.FromEditorIdHash);
        Assert.Equal(2, result.Stats.UniqueTotal);
        var formIds = result.Cells.Select(c => c.FormId).OrderBy(x => x).ToArray();
        Assert.Equal(new uint[] { 0x10000001, 0x10000002 }, formIds);
        Assert.All(result.Cells, c => Assert.Equal(RuntimeCellSource.AllFormsHash, c.Source));
    }

    [Fact]
    public void AllFormsHash_RejectsNullAndZeroFormIds()
    {
        var heap = new HeapBuilder(0x4000);
        var cellVa = heap.PlaceCell(formId: 0x20000001);

        // bucket 0 has a node with key=0 (rejected) -> node with valid CELL.
        // bucket 1 has a node with nullptr value (rejected) -> nullptr next.
        var validNode = heap.PlaceMapItem(formId: 0x20000001, formVa: cellVa, nextVa: 0);
        var nullKeyNode = heap.PlaceMapItem(formId: 0, formVa: cellVa, nextVa: validNode);
        var nullValueNode = heap.PlaceMapItem(formId: 0x20000099, formVa: 0, nextVa: 0);
        var hashTableVa = heap.PlaceHashTable(buckets: [nullKeyNode, nullValueNode]);

        var enumerator = heap.BuildEnumerator(hashTableVa);
        var result = enumerator.Enumerate(editorIdEntries: [], knownWrldFormIds: []);

        Assert.Equal(1, result.Stats.FromAllFormsHash);
        Assert.Equal(0x20000001u, result.Cells[0].FormId);
    }

    // ============================================================================
    // Path 2: TESWorldSpace.pGridCellA walk
    // ============================================================================

    [Fact]
    public void WorldspaceGrid_EmitsNonNullCellsFromGrid()
    {
        var heap = new HeapBuilder(0x6000);

        // Eight cells, placed into slots 0, 3, 7, 11, 15, 19, 22, 24 (mixed-null pattern).
        var occupiedSlots = new[] { 0, 3, 7, 11, 15, 19, 22, 24 };
        var cellVas = new uint[occupiedSlots.Length];
        for (var i = 0; i < occupiedSlots.Length; i++)
        {
            cellVas[i] = heap.PlaceCell(formId: 0x30000001 + (uint)i);
        }

        var gridSlots = new uint[25];
        for (var i = 0; i < occupiedSlots.Length; i++)
        {
            gridSlots[occupiedSlots[i]] = cellVas[i];
        }

        var gridArrayVa = heap.PlaceGridCellArray(gridSlots);
        var wrldVa = heap.PlaceWorldspace(formId: 0x000000DA, gridCellArrayVa: gridArrayVa);

        // Single hash-table entry pointing at the worldspace.
        var wrldNode = heap.PlaceMapItem(formId: 0x000000DA, formVa: wrldVa, nextVa: 0);
        var hashTableVa = heap.PlaceHashTable(buckets: [wrldNode]);

        var enumerator = heap.BuildEnumerator(hashTableVa);
        var result = enumerator.Enumerate(
            editorIdEntries: [],
            knownWrldFormIds: new HashSet<uint> { 0x000000DA });

        Assert.Equal(8, result.Stats.FromWorldspaceGrid);
        Assert.Equal(8, result.Stats.UniqueTotal);
        Assert.All(result.Cells, c => Assert.Equal(RuntimeCellSource.WorldspaceGrid, c.Source));
        var formIds = result.Cells.Select(c => c.FormId).OrderBy(x => x).ToArray();
        Assert.Equal(
            occupiedSlots.Select((_, i) => 0x30000001u + (uint)i).ToArray(),
            formIds);
    }

    [Fact]
    public void WorldspaceGrid_DetectsWrldFormTypeFromKnownFormIds()
    {
        var heap = new HeapBuilder(0x4000);

        var gridArrayVa = heap.PlaceGridCellArray(new uint[25]); // all null
        var wrldVa = heap.PlaceWorldspace(formId: 0x000000E0, gridCellArrayVa: gridArrayVa);

        // WRLD form lands in the pAllForms walk because its FormID is in knownWrldFormIds.
        var wrldNode = heap.PlaceMapItem(formId: 0x000000E0, formVa: wrldVa, nextVa: 0);
        var hashTableVa = heap.PlaceHashTable(buckets: [wrldNode]);

        var enumerator = heap.BuildEnumerator(hashTableVa);
        var result = enumerator.Enumerate(
            editorIdEntries: [],
            knownWrldFormIds: new HashSet<uint> { 0x000000E0 });

        // No cells in the grid -> 0 hits. But the WRLD was discovered (path didn't crash).
        Assert.Equal(0, result.Stats.UniqueTotal);
    }

    // ============================================================================
    // Path 3: Heap-scan vtable
    // ============================================================================

    [Fact]
    public void HeapScan_SeedsFromKnownCellVfptr_FindsAllInstances()
    {
        var heap = new HeapBuilder(0x8000);

        // Seed cell (also surfaced via Path 0 so heap-scan has a vtable to harvest).
        var seedCellVa = heap.PlaceCell(formId: 0x40000001);
        // Two additional cells with the SAME vtable as the seed.
        var extraCellVa1 = heap.PlaceCell(formId: 0x40000002);
        var extraCellVa2 = heap.PlaceCell(formId: 0x40000003);

        // Decoy 1: a struct that starts with a DIFFERENT module-range vtable.
        heap.PlaceDecoy(DecoyVtable, formTypeByte: CellFormType, formId: 0xDEADBEEF);

        var enumerator = heap.BuildEnumerator(pAllFormsVa: 0);
        var result = enumerator.Enumerate(
            editorIdEntries: [MakeEntry(0x40000001, CellFormType, seedCellVa)],
            knownWrldFormIds: []);

        Assert.Equal(1, result.Stats.FromEditorIdHash);
        Assert.Equal(2, result.Stats.FromHeapScan);
        Assert.Equal(3, result.Stats.UniqueTotal);

        var fromHeapScan = result.Cells.Where(c => c.Source == RuntimeCellSource.HeapScan).ToArray();
        Assert.Equal(2, fromHeapScan.Length);
        var heapScanFormIds = fromHeapScan.Select(c => c.FormId).OrderBy(x => x).ToArray();
        Assert.Equal(new uint[] { 0x40000002, 0x40000003 }, heapScanFormIds);
    }

    [Fact]
    public void HeapScan_RejectsNonModuleVtable()
    {
        var heap = new HeapBuilder(0x4000);

        // Seed cell with vfptr in HEAP range, not module range. Heap-scan must skip.
        var seedCellVa = heap.PlaceCell(formId: 0x50000001, vtable: HeapNonModuleVtable);
        heap.PlaceCell(formId: 0x50000002, vtable: HeapNonModuleVtable);

        var enumerator = heap.BuildEnumerator(pAllFormsVa: 0);
        var result = enumerator.Enumerate(
            editorIdEntries: [MakeEntry(0x50000001, CellFormType, seedCellVa)],
            knownWrldFormIds: []);

        Assert.Equal(1, result.Stats.FromEditorIdHash);
        Assert.Equal(0, result.Stats.FromHeapScan);
        Assert.Equal(1, result.Stats.UniqueTotal);
    }

    [Fact]
    public void HeapScan_RejectsMatchesFailingFormTypeOrFormIdValidation()
    {
        var heap = new HeapBuilder(0x4000);

        var seedCellVa = heap.PlaceCell(formId: 0x60000001);

        // Decoy that DOES start with the cell vtable but has a non-CELL form type byte.
        heap.PlaceDecoyAtVtable(CellVtable, formTypeByte: WeapFormType, formId: 0xCAFEBABE);

        // Decoy with vtable matching but formId == 0.
        heap.PlaceDecoyAtVtable(CellVtable, formTypeByte: CellFormType, formId: 0);

        var enumerator = heap.BuildEnumerator(pAllFormsVa: 0);
        var result = enumerator.Enumerate(
            editorIdEntries: [MakeEntry(0x60000001, CellFormType, seedCellVa)],
            knownWrldFormIds: []);

        Assert.Equal(1, result.Stats.FromEditorIdHash);
        Assert.Equal(0, result.Stats.FromHeapScan);
        Assert.Equal(1, result.Stats.UniqueTotal);
    }

    [Fact]
    public void HeapScan_SkipsWhenNoSeedAvailable()
    {
        var heap = new HeapBuilder(0x4000);

        // A real cell in heap, but no seed entry is provided to the enumerator so
        // Path 3 has no vtable to harvest. Must return zero rather than scanning.
        heap.PlaceCell(formId: 0x70000001);

        var enumerator = heap.BuildEnumerator(pAllFormsVa: 0);
        var result = enumerator.Enumerate(editorIdEntries: [], knownWrldFormIds: []);

        Assert.Equal(0, result.Stats.FromHeapScan);
        Assert.Equal(0, result.Stats.UniqueTotal);
    }

    // ============================================================================
    // Dedup + stats
    // ============================================================================

    [Fact]
    public void Enumerate_DedupsByFormId_FirstSourceWins()
    {
        var heap = new HeapBuilder(0x4000);

        // One cell, but it's reachable via TWO paths: editor-id hash (Path 0) AND
        // heap-scan (Path 3, harvesting vtable from the same cell). Path 0 wins.
        var cellVa = heap.PlaceCell(formId: 0x80000001);

        var enumerator = heap.BuildEnumerator(pAllFormsVa: 0);
        var result = enumerator.Enumerate(
            editorIdEntries: [MakeEntry(0x80000001, CellFormType, cellVa)],
            knownWrldFormIds: []);

        Assert.Equal(1, result.Stats.UniqueTotal);
        Assert.Equal(1, result.Stats.FromEditorIdHash);
        Assert.Equal(0, result.Stats.FromHeapScan);
        Assert.Single(result.Cells);
        Assert.Equal(RuntimeCellSource.EditorIdHash, result.Cells[0].Source);
    }

    [Fact]
    public void Stats_ReflectsPerSourceCounts()
    {
        var heap = new HeapBuilder(0xC000);

        // Path 0 cell (in editor-id entries).
        var path0CellVa = heap.PlaceCell(formId: 0x91000001);

        // Path 1 cell (in pAllForms, but NOT in editor-id entries).
        var path1CellVa = heap.PlaceCell(formId: 0x91000002);

        // Path 2 cell (only reachable via worldspace grid).
        var path2CellVa = heap.PlaceCell(formId: 0x91000003);

        // Path 3 cell (only reachable via heap-scan: not in pAllForms, not in grid).
        heap.PlaceCell(formId: 0x91000004);

        // Worldspace whose grid contains path2 cell only.
        var gridSlots = new uint[25];
        gridSlots[0] = path2CellVa;
        var gridArrayVa = heap.PlaceGridCellArray(gridSlots);
        var wrldVa = heap.PlaceWorldspace(formId: 0x00FF00FF, gridCellArrayVa: gridArrayVa);

        // pAllForms: chain { path1Cell -> wrld -> end }.
        var wrldNode = heap.PlaceMapItem(formId: 0x00FF00FF, formVa: wrldVa, nextVa: 0);
        var path1Node = heap.PlaceMapItem(formId: 0x91000002, formVa: path1CellVa, nextVa: wrldNode);
        var hashTableVa = heap.PlaceHashTable(buckets: [path1Node]);

        var enumerator = heap.BuildEnumerator(hashTableVa);
        var result = enumerator.Enumerate(
            editorIdEntries: [MakeEntry(0x91000001, CellFormType, path0CellVa)],
            knownWrldFormIds: new HashSet<uint> { 0x00FF00FF });

        Assert.Equal(1, result.Stats.FromEditorIdHash);
        Assert.Equal(1, result.Stats.FromAllFormsHash);
        Assert.Equal(1, result.Stats.FromWorldspaceGrid);
        Assert.Equal(1, result.Stats.FromHeapScan);
        Assert.Equal(4, result.Stats.UniqueTotal);
        Assert.Equal(4, result.Cells.Count);
    }

    // ============================================================================
    // Path 4: direct NAVM VA collection from pAllForms
    // ============================================================================

    [Fact]
    public void AllFormsHash_CollectsNavMeshVas_ByFormTypeByte()
    {
        var heap = new HeapBuilder(0x4000);

        // Three NAVM entries in pAllForms, one CELL, one WRLD (decoy).
        var navm1Va = heap.PlaceTesForm(NavmFormType, 0xA0000001);
        var navm2Va = heap.PlaceTesForm(NavmFormType, 0xA0000002);
        var navm3Va = heap.PlaceTesForm(NavmFormType, 0xA0000003);
        var cellVa = heap.PlaceCell(formId: 0xB0000001);

        var navm3Node = heap.PlaceMapItem(formId: 0xA0000003, formVa: navm3Va, nextVa: 0);
        var navm2Node = heap.PlaceMapItem(formId: 0xA0000002, formVa: navm2Va, nextVa: navm3Node);
        var navm1Node = heap.PlaceMapItem(formId: 0xA0000001, formVa: navm1Va, nextVa: navm2Node);
        var cellNode = heap.PlaceMapItem(formId: 0xB0000001, formVa: cellVa, nextVa: navm1Node);
        var hashTableVa = heap.PlaceHashTable(buckets: [cellNode]);

        var enumerator = heap.BuildEnumerator(hashTableVa);
        // Provide a byte-stream anchor (one of the NAVM FormIDs) so calibration succeeds
        // and the canonical-byte entries route to NavMeshVas. Without an anchor the new
        // Phase 2d behaviour would send them to NavMeshVaCandidates instead — that path
        // is exercised by Uncalibrated_EmitsNavMeshVaCandidatesAcrossByteWindow.
        var result = enumerator.Enumerate(
            editorIdEntries: [],
            knownWrldFormIds: [],
            knownNavmFormIds: new HashSet<uint> { 0xA0000001 });

        Assert.Equal(3, result.NavMeshVas.Count);
        Assert.Equal(
            new uint[] { navm1Va, navm2Va, navm3Va }.OrderBy(v => v).ToArray(),
            result.NavMeshVas.OrderBy(v => v).ToArray());

        // NAVM VAs are NOT added to the cell hits collection — they're a separate channel.
        Assert.Equal(1, result.Stats.FromAllFormsHash);
        Assert.Equal(1, result.Stats.UniqueTotal);
    }

    [Fact]
    public void DriftRemap_TranslatesRawHeapBytes_ToCanonicalFormTypes()
    {
        // Simulates an early-build dump where FormType bytes in heap memory differ from
        // canonical (e.g. Nov 2009 enum +1 shift) — the enumerator's pAllForms walk must
        // apply the drift remap to raw bytes so the canonical NAVM (0x43) / CELL (0x39) /
        // WRLD (0x41) checks still fire.
        const byte rawNavm = 0x42;
        const byte rawCell = 0x38;
        const byte rawWrld = 0x40;

        var heap = new HeapBuilder(0x4000);
        var navmVa = heap.PlaceTesForm(rawNavm, formId: 0xD0000001);
        var cellVa = heap.PlaceCustomCell(formTypeByte: rawCell, formId: 0xD0000002);
        var wrldVa = heap.PlaceCustomWorldspace(formTypeByte: rawWrld, formId: 0x000000DA,
            gridCellArrayVa: heap.PlaceGridCellArray(new uint[25]));

        var wrldNode = heap.PlaceMapItem(formId: 0x000000DA, formVa: wrldVa, nextVa: 0);
        var cellNode = heap.PlaceMapItem(formId: 0xD0000002, formVa: cellVa, nextVa: wrldNode);
        var navmNode = heap.PlaceMapItem(formId: 0xD0000001, formVa: navmVa, nextVa: cellNode);
        var hashTableVa = heap.PlaceHashTable(buckets: [navmNode]);

        var driftRemap = new Dictionary<byte, byte>
        {
            { rawNavm, NavmFormType },
            { rawCell, CellFormType },
            { rawWrld, WrldFormType }
        };

        var enumerator = heap.BuildEnumerator(hashTableVa, driftRemap);
        var result = enumerator.Enumerate(editorIdEntries: [], knownWrldFormIds: []);

        Assert.Single(result.NavMeshVas);
        Assert.Equal(navmVa, result.NavMeshVas[0]);
        Assert.Equal(1, result.Stats.FromAllFormsHash);
        Assert.Single(result.Cells);
        Assert.Equal(cellVa, result.Cells[0].CellVa);
        Assert.Equal(0xD0000002u, result.Cells[0].FormId);
    }

    [Fact]
    public void NavmCalibration_FromByteStreamFormIds_LearnsRawByteWhenDriftDetectorFailed()
    {
        // Simulates xex.dmp's failure mode: drift is conceptually present but the upstream
        // RuntimeBuildOffsets.DetectFormTypeDrift returned null (typically because the byte
        // stream lacks DIAL/INFO cross-references). The enumerator gets an EMPTY drift remap.
        // BUT the byte stream does carry NAVM record(s). Their FormID(s), supplied via
        // knownNavmFormIds, let the enumerator's pAllForms walk discover the build-specific
        // raw NAVM byte by anchoring on those FormIDs.
        const byte driftedRawNavm = 0x44;
        const uint anchorNavmFormId = 0xF0000001;

        var heap = new HeapBuilder(0x4000);
        var anchorNavmVa = heap.PlaceTesForm(driftedRawNavm, anchorNavmFormId);
        var runtimeNavmVa = heap.PlaceTesForm(driftedRawNavm, formId: 0xF0000002);
        var runtimeNavm2Va = heap.PlaceTesForm(driftedRawNavm, formId: 0xF0000003);

        var node3 = heap.PlaceMapItem(formId: 0xF0000003, formVa: runtimeNavm2Va, nextVa: 0);
        var node2 = heap.PlaceMapItem(formId: 0xF0000002, formVa: runtimeNavmVa, nextVa: node3);
        var anchorNode = heap.PlaceMapItem(formId: anchorNavmFormId, formVa: anchorNavmVa, nextVa: node2);
        var hashTableVa = heap.PlaceHashTable(buckets: [anchorNode]);

        // No drift remap (simulates detector failure), but knownNavmFormIds includes the anchor.
        var enumerator = heap.BuildEnumerator(hashTableVa);
        var result = enumerator.Enumerate(
            editorIdEntries: [],
            knownWrldFormIds: [],
            knownNavmFormIds: new HashSet<uint> { anchorNavmFormId });

        // All three NAVMs (the anchor + the 2 runtime-only) surfaced via raw byte 0x44 even
        // though the enumerator never received drift-remap or canonical-byte information.
        Assert.Equal(3, result.NavMeshVas.Count);
    }

    [Fact]
    public void DriftRemap_Absent_FallsBackToIdentity()
    {
        // No drift dictionary supplied — bytes in heap pass through unchanged. Equivalent
        // to a final-build dump where FormType bytes are already canonical.
        //
        // Phase 2d note: without ALSO an anchor or drift-confirmed NAVM byte, the canonical
        // 0x43 entries route to NavMeshVaCandidates (speculative), not NavMeshVas. This
        // test exercises that fallback path; the calibrated-canonical path is covered by
        // AllFormsHash_CollectsNavMeshVas_ByFormTypeByte (which provides an anchor).
        var heap = new HeapBuilder(0x2000);
        var navmVa = heap.PlaceTesForm(NavmFormType, formId: 0xE0000001);
        var navmNode = heap.PlaceMapItem(formId: 0xE0000001, formVa: navmVa, nextVa: 0);
        var hashTableVa = heap.PlaceHashTable(buckets: [navmNode]);

        var enumerator = heap.BuildEnumerator(hashTableVa); // no drift remap
        var result = enumerator.Enumerate(editorIdEntries: [], knownWrldFormIds: []);

        Assert.Empty(result.NavMeshVas);
        Assert.Single(result.NavMeshVaCandidates);
        Assert.Equal(navmVa, result.NavMeshVaCandidates[0]);
    }

    // ============================================================================
    // Phase 2d: speculative NavMeshVaCandidates for uncalibrated builds
    // ============================================================================

    [Fact]
    public void Uncalibrated_EmitsNavMeshVaCandidatesAcrossByteWindow()
    {
        // No byte-stream anchor (knownNavmFormIds=null) AND no drift remap → NAVM
        // calibration falls back to canonical. In that mode the enumerator must NOT
        // route entries to NavMeshVas (every match would be a guess); instead it should
        // emit a speculative candidate list across the [canonical-2..canonical+2]
        // window, excluding bytes already classified as CELL (0x39) or WRLD (0x41).
        var heap = new HeapBuilder(0x4000);
        var entry0x40 = heap.PlaceTesForm(0x40, formId: 0x10000040); // outside window
        var entry0x41 = heap.PlaceTesForm(0x41, formId: 0x10000041); // WRLD → wrldVas
        var entry0x42 = heap.PlaceTesForm(0x42, formId: 0x10000042); // window
        var entry0x43 = heap.PlaceTesForm(0x43, formId: 0x10000043); // window
        var entry0x44 = heap.PlaceTesForm(0x44, formId: 0x10000044); // window
        var entry0x45 = heap.PlaceTesForm(0x45, formId: 0x10000045); // window
        var entry0x46 = heap.PlaceTesForm(0x46, formId: 0x10000046); // outside window

        var node6 = heap.PlaceMapItem(0x10000046, entry0x46, nextVa: 0);
        var node5 = heap.PlaceMapItem(0x10000045, entry0x45, nextVa: node6);
        var node4 = heap.PlaceMapItem(0x10000044, entry0x44, nextVa: node5);
        var node3 = heap.PlaceMapItem(0x10000043, entry0x43, nextVa: node4);
        var node2 = heap.PlaceMapItem(0x10000042, entry0x42, nextVa: node3);
        var node1 = heap.PlaceMapItem(0x10000041, entry0x41, nextVa: node2);
        var node0 = heap.PlaceMapItem(0x10000040, entry0x40, nextVa: node1);
        var hashTableVa = heap.PlaceHashTable(buckets: [node0]);

        var enumerator = heap.BuildEnumerator(hashTableVa);
        var result = enumerator.Enumerate(
            editorIdEntries: [],
            knownWrldFormIds: [],
            knownNavmFormIds: null);

        // No anchor → NavMeshVas stays empty even though byte 0x43 (canonical NAVM) is present.
        Assert.Empty(result.NavMeshVas);

        // NavMeshVaCandidates contains the four window entries minus WRLD's 0x41.
        Assert.Equal(4, result.NavMeshVaCandidates.Count);
        var candidateSet = new HashSet<uint>(result.NavMeshVaCandidates);
        Assert.Contains(entry0x42, candidateSet);
        Assert.Contains(entry0x43, candidateSet);
        Assert.Contains(entry0x44, candidateSet);
        Assert.Contains(entry0x45, candidateSet);
        Assert.DoesNotContain(entry0x40, candidateSet);
        Assert.DoesNotContain(entry0x41, candidateSet);
        Assert.DoesNotContain(entry0x46, candidateSet);
    }

    [Fact]
    public void Calibrated_EmitsEmptyNavMeshVaCandidates()
    {
        // Anchor present (knownNavmFormIds contains a real NAVM FormID whose entry sits
        // in pAllForms at byte 0x43). NavMeshVas gets that entry; NavMeshVaCandidates
        // stays empty because the trusted byte already routes every NAVM-shaped entry.
        var heap = new HeapBuilder(0x4000);
        const uint anchorFormId = 0x20000001;
        var anchorVa = heap.PlaceTesForm(NavmFormType, anchorFormId);
        var siblingVa = heap.PlaceTesForm(NavmFormType, formId: 0x20000002);

        var sibling = heap.PlaceMapItem(0x20000002, siblingVa, nextVa: 0);
        var anchor = heap.PlaceMapItem(anchorFormId, anchorVa, nextVa: sibling);
        var hashTableVa = heap.PlaceHashTable(buckets: [anchor]);

        var enumerator = heap.BuildEnumerator(hashTableVa);
        var result = enumerator.Enumerate(
            editorIdEntries: [],
            knownWrldFormIds: [],
            knownNavmFormIds: new HashSet<uint> { anchorFormId });

        // Both NAVM entries surface via the calibrated byte set; candidates list is empty
        // because the canonical-byte fallback is now trusted (anchor confirmed it).
        Assert.Equal(2, result.NavMeshVas.Count);
        Assert.Empty(result.NavMeshVaCandidates);
    }

    [Fact]
    public void NavMeshVas_IsEmpty_WhenNoNavMsInAllForms()
    {
        var heap = new HeapBuilder(0x2000);
        var cellVa = heap.PlaceCell(formId: 0xC0000001);
        var cellNode = heap.PlaceMapItem(formId: 0xC0000001, formVa: cellVa, nextVa: 0);
        var hashTableVa = heap.PlaceHashTable(buckets: [cellNode]);

        var enumerator = heap.BuildEnumerator(hashTableVa);
        var result = enumerator.Enumerate(editorIdEntries: [], knownWrldFormIds: []);

        Assert.Empty(result.NavMeshVas);
        Assert.Equal(1, result.Stats.FromAllFormsHash);
    }

    // ============================================================================
    // Test helpers
    // ============================================================================

    private static RuntimeEditorIdEntry MakeEntry(uint formId, byte formType, uint tesFormPtr)
        => new()
        {
            EditorId = $"Entry_{formId:X8}",
            FormId = formId,
            FormType = formType,
            TesFormOffset = tesFormPtr - HeapBaseVa,
            TesFormPointer = tesFormPtr
        };

    /// <summary>
    ///     Single contiguous "heap" buffer with bump-allocator placement. Every struct
    ///     placed by <c>PlaceXxx</c> lives at file-offset = (returned VA - HeapBaseVa)
    ///     within one big captured range; <see cref="SparseMemoryAccessor" /> reads
    ///     across the whole region without the gap-returning-zero issue.
    /// </summary>
    private sealed class HeapBuilder
    {
        private readonly byte[] _buffer;
        private int _cursor;

        public HeapBuilder(int sizeBytes)
        {
            _buffer = new byte[sizeBytes];
            // Start the bump allocator at +0x100 so VA 0x40000000 is reserved (the test
            // never expects a struct to land there).
            _cursor = 0x100;
        }

        public uint PlaceCell(uint formId, uint vtable = CellVtable)
            => PlaceCustomCell(formTypeByte: CellFormType, formId, vtable);

        public uint PlaceCustomCell(byte formTypeByte, uint formId, uint vtable = CellVtable)
        {
            var va = AllocateAligned(CellStructSize);
            var offset = OffsetForVa(va);
            WriteUInt32BE(_buffer, offset + 0, vtable);
            _buffer[offset + 4] = formTypeByte;
            WriteUInt32BE(_buffer, offset + 12, formId);
            // pNavMeshes pointer at +116 left zero — heap-scan validator accepts that.
            return va;
        }

        public uint PlaceTesForm(byte formType, uint formId)
        {
            const int size = 24;
            var va = AllocateAligned(size);
            var offset = OffsetForVa(va);
            WriteUInt32BE(_buffer, offset + 0, CellVtable);
            _buffer[offset + 4] = formType;
            WriteUInt32BE(_buffer, offset + 12, formId);
            return va;
        }

        public uint PlaceMapItem(uint formId, uint formVa, uint nextVa)
        {
            const int size = 12;
            var va = AllocateAligned(size);
            var offset = OffsetForVa(va);
            WriteUInt32BE(_buffer, offset + 0, nextVa);
            WriteUInt32BE(_buffer, offset + 4, formId);
            WriteUInt32BE(_buffer, offset + 8, formVa);
            return va;
        }

        /// <summary>
        ///     Lay out an NiTMapBase header followed by the bucket array. Returns the
        ///     VA of the header (which is what callers pass as pAllFormsVa). Bucket
        ///     count = bucketHeads.Length.
        /// </summary>
        public uint PlaceHashTable(uint[] buckets)
        {
            const int headerSize = 16;
            var bucketArraySize = buckets.Length * 4;
            var totalSize = headerSize + bucketArraySize;
            var va = AllocateAligned(totalSize);
            var offset = OffsetForVa(va);
            WriteUInt32BE(_buffer, offset + 0, 0); // vfptr (unused by walker)
            WriteUInt32BE(_buffer, offset + 4, (uint)buckets.Length);
            WriteUInt32BE(_buffer, offset + 8, va + headerSize); // bucket array VA = right after header
            WriteUInt32BE(_buffer, offset + 12, 0); // allocator
            for (var i = 0; i < buckets.Length; i++)
            {
                WriteUInt32BE(_buffer, offset + headerSize + i * 4, buckets[i]);
            }

            return va;
        }

        public uint PlaceGridCellArray(uint[] cellPtrs)
        {
            if (cellPtrs.Length != 25)
            {
                throw new ArgumentException("GridCellArray expects exactly 25 slots", nameof(cellPtrs));
            }

            var va = AllocateAligned(GridCellArrayPointersSize);
            var offset = OffsetForVa(va);
            for (var i = 0; i < 25; i++)
            {
                WriteUInt32BE(_buffer, offset + i * 4, cellPtrs[i]);
            }

            return va;
        }

        public uint PlaceWorldspace(uint formId, uint gridCellArrayVa)
            => PlaceCustomWorldspace(formTypeByte: WrldFormType, formId, gridCellArrayVa);

        public uint PlaceCustomWorldspace(byte formTypeByte, uint formId, uint gridCellArrayVa)
        {
            var va = AllocateAligned(WrldStructSize);
            var offset = OffsetForVa(va);
            WriteUInt32BE(_buffer, offset + 0, CellVtable);
            _buffer[offset + 4] = formTypeByte;
            WriteUInt32BE(_buffer, offset + 12, formId);
            WriteUInt32BE(_buffer, offset + WrldGridCellArrayPtrOffset, gridCellArrayVa);
            return va;
        }

        /// <summary>
        ///     Place a TESForm-shaped decoy in the heap that does NOT match the cell vtable.
        ///     Heap-scan must not surface it because the vtable signature doesn't match.
        /// </summary>
        public uint PlaceDecoy(uint vtable, byte formTypeByte, uint formId)
        {
            const int size = 256;
            var va = AllocateAligned(size);
            var offset = OffsetForVa(va);
            WriteUInt32BE(_buffer, offset + 0, vtable);
            _buffer[offset + 4] = formTypeByte;
            WriteUInt32BE(_buffer, offset + 12, formId);
            return va;
        }

        /// <summary>
        ///     Place a decoy that DOES match the cell vtable but fails other validation
        ///     gates (form-type byte or zero FormID).
        /// </summary>
        public uint PlaceDecoyAtVtable(uint vtable, byte formTypeByte, uint formId)
            => PlaceDecoy(vtable, formTypeByte, formId);

        public RuntimeCellEnumerator BuildEnumerator(uint pAllFormsVa)
            => BuildEnumerator(pAllFormsVa, driftRemap: null);

        public RuntimeCellEnumerator BuildEnumerator(
            uint pAllFormsVa,
            IReadOnlyDictionary<byte, byte>? driftRemap)
        {
            var accessor = new SparseMemoryAccessor();
            accessor.AddRange(0, _buffer);

            var minidumpInfo = new MinidumpInfo
            {
                IsValid = true,
                ProcessorArchitecture = 0x03, // PowerPC
                MemoryRegions =
                [
                    new MinidumpMemoryRegion
                    {
                        VirtualAddress = HeapBaseVa,
                        FileOffset = 0,
                        Size = _buffer.Length
                    }
                ]
            };
            var context = new RuntimeMemoryContext(accessor, _buffer.Length, minidumpInfo);
            return new RuntimeCellEnumerator(context, minidumpInfo, pAllFormsVa, driftRemap);
        }

        private uint AllocateAligned(int size)
        {
            var alignedCursor = (_cursor + 3) & ~3;
            var va = HeapBaseVa + (uint)alignedCursor;
            _cursor = alignedCursor + size;
            if (_cursor > _buffer.Length)
            {
                throw new InvalidOperationException(
                    $"Heap exhausted: tried to allocate {size}B at +0x{alignedCursor:X4} (limit 0x{_buffer.Length:X4}).");
            }

            return va;
        }

        private static int OffsetForVa(uint va) => unchecked((int)(va - HeapBaseVa));
    }
}
