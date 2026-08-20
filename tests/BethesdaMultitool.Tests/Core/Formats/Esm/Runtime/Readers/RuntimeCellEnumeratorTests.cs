using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.BinaryTestWriter;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime.Readers;

/// <summary>
///     Synthetic in-memory tests for <see cref="RuntimeCellEnumerator" />: the three-source
///     <c>TESObjectCELL</c> discovery pipeline used by runtime NAVM enumeration. Each test
///     builds a single contiguous "heap" byte[], lays out a planned set of structs (pAllForms
///     hash table, TESForms, and decoy heap allocations), wraps it
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
    // ============================================================================
    // Path 0: Editor-id hash filter
    // ============================================================================

    [Fact]
    public void EditorIdHash_ReturnsOnlyCellFormType()
    {
        var heap = new HeapBuilder(0x4000);
        var cellVa = heap.PlaceCell(0x0000A001);
        var notCellVa = heap.PlaceTesForm(WeapFormType, 0x0000B001);

        var enumerator = heap.BuildEnumerator(0);
        var entries = new[]
        {
            MakeEntry(0x0000A001, CellFormType, cellVa),
            MakeEntry(0x0000B001, WeapFormType, notCellVa)
        };

        var result = enumerator.Enumerate(entries, []);

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
        var cellAVa = heap.PlaceCell(0x10000001);
        var cellBVa = heap.PlaceCell(0x10000002);
        var weaponVa = heap.PlaceTesForm(WeapFormType, 0x10000003);

        // Two-bucket hash table; bucket 0 chains cellA -> weapon -> cellB so the walker
        // must traverse the m_pkNext links to pick up both CELL entries.
        var node3 = heap.PlaceMapItem(0x10000002, cellBVa, 0);
        var node2 = heap.PlaceMapItem(0x10000003, weaponVa, node3);
        var node1 = heap.PlaceMapItem(0x10000001, cellAVa, node2);
        var hashTableVa = heap.PlaceHashTable([node1, 0]);

        var enumerator = heap.BuildEnumerator(hashTableVa);
        var result = enumerator.Enumerate([], []);

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
        var cellVa = heap.PlaceCell(0x20000001);

        // bucket 0 has a node with key=0 (rejected) -> node with valid CELL.
        // bucket 1 has a node with nullptr value (rejected) -> nullptr next.
        var validNode = heap.PlaceMapItem(0x20000001, cellVa, 0);
        var nullKeyNode = heap.PlaceMapItem(0, cellVa, validNode);
        var nullValueNode = heap.PlaceMapItem(0x20000099, 0, 0);
        var hashTableVa = heap.PlaceHashTable([nullKeyNode, nullValueNode]);

        var enumerator = heap.BuildEnumerator(hashTableVa);
        var result = enumerator.Enumerate([], []);

        Assert.Equal(1, result.Stats.FromAllFormsHash);
        Assert.Equal(0x20000001u, result.Cells[0].FormId);
    }

    [Fact]
    public void AllFormsHash_StitchesMapItemAndTesFormHeaderAcrossDiscontiguousFileOffsets()
    {
        const uint hashVa = HeapBaseVa + 0x100;
        const uint itemVa = HeapBaseVa + 0x200;
        const uint formVa = HeapBaseVa + 0x300;
        const uint formId = 0x20000002;
        var accessor = new SparseMemoryAccessor();
        var regions = new List<MinidumpMemoryRegion>();

        var hash = CreateSingleBucketHash(hashVa, itemVa);
        accessor.AddRange(16, hash);
        regions.Add(Region(hashVa, 16, hash.Length));

        var item = CreateMapItem(formId, formVa);
        accessor.AddRange(96, item[..6]);
        accessor.AddRange(192, item[6..]);
        regions.Add(Region(itemVa, 96, 6));
        regions.Add(Region(itemVa + 6, 192, 6));

        var form = CreateTesFormHeader(CellFormType, formId);
        accessor.AddRange(288, form[..8]);
        accessor.AddRange(400, form[8..]);
        regions.Add(Region(formVa, 288, 8));
        regions.Add(Region(formVa + 8, 400, 8));

        var enumerator = BuildSparseEnumerator(accessor, regions, hashVa);
        var result = enumerator.Enumerate([], []);

        var cell = Assert.Single(result.Cells);
        Assert.Equal(formId, cell.FormId);
        Assert.Equal(formVa, cell.CellVa);
        Assert.Equal(RuntimeCellSource.AllFormsHash, cell.Source);
    }

    [Fact]
    public void AllFormsHash_RejectsMapItemAcrossVaGapEvenWhenFlatBytesLookValid()
    {
        const uint hashVa = HeapBaseVa + 0x100;
        const uint itemVa = HeapBaseVa + 0x200;
        const uint formVa = HeapBaseVa + 0x300;
        const uint formId = 0x20000003;
        var accessor = new SparseMemoryAccessor();
        var regions = new List<MinidumpMemoryRegion>();

        var hash = CreateSingleBucketHash(hashVa, itemVa);
        accessor.AddRange(16, hash);
        regions.Add(Region(hashVa, 16, hash.Length));

        // The flat file bytes form a valid item, but VA +6 is absent.
        var item = CreateMapItem(formId, formVa);
        accessor.AddRange(96, item);
        regions.Add(Region(itemVa, 96, 6));
        regions.Add(Region(itemVa + 7, 102, 6));

        var form = CreateTesFormHeader(CellFormType, formId);
        accessor.AddRange(288, form);
        regions.Add(Region(formVa, 288, form.Length));

        var enumerator = BuildSparseEnumerator(accessor, regions, hashVa);
        var result = enumerator.Enumerate([], []);

        Assert.Empty(result.Cells);
    }

    [Fact]
    public void AllFormsHash_RejectsTesFormHeaderAcrossVaGapEvenWhenFlatBytesLookValid()
    {
        const uint hashVa = HeapBaseVa + 0x100;
        const uint itemVa = HeapBaseVa + 0x200;
        const uint formVa = HeapBaseVa + 0x300;
        const uint formId = 0x20000004;
        var accessor = new SparseMemoryAccessor();
        var regions = new List<MinidumpMemoryRegion>();

        var hash = CreateSingleBucketHash(hashVa, itemVa);
        accessor.AddRange(16, hash);
        regions.Add(Region(hashVa, 16, hash.Length));

        var item = CreateMapItem(formId, formVa);
        accessor.AddRange(96, item);
        regions.Add(Region(itemVa, 96, item.Length));

        // The flat file bytes form a valid TESForm header, but VA +8 is absent.
        var form = CreateTesFormHeader(CellFormType, formId);
        accessor.AddRange(288, form);
        regions.Add(Region(formVa, 288, 8));
        regions.Add(Region(formVa + 9, 296, 8));

        var enumerator = BuildSparseEnumerator(accessor, regions, hashVa);
        var result = enumerator.Enumerate([], []);

        Assert.Empty(result.Cells);
    }

    [Fact]
    public void AllFormsHash_StitchesHeaderAcrossDiscontiguousFileOffsets()
    {
        const uint hashVa = HeapBaseVa + 0x100;
        const uint itemVa = HeapBaseVa + 0x200;
        const uint formVa = HeapBaseVa + 0x300;
        const uint formId = 0x20000005;
        var accessor = new SparseMemoryAccessor();
        var regions = new List<MinidumpMemoryRegion>();

        var hash = CreateSingleBucketHash(hashVa, itemVa);
        accessor.AddRange(16, hash[..8]);
        accessor.AddRange(128, hash[8..]);
        regions.Add(Region(hashVa, 16, 8));
        regions.Add(Region(hashVa + 8, 128, 12));

        var item = CreateMapItem(formId, formVa);
        accessor.AddRange(192, item);
        regions.Add(Region(itemVa, 192, item.Length));

        var form = CreateTesFormHeader(CellFormType, formId);
        accessor.AddRange(256, form);
        regions.Add(Region(formVa, 256, form.Length));

        var result = BuildSparseEnumerator(accessor, regions, hashVa).Enumerate([], []);

        Assert.Equal(formId, Assert.Single(result.Cells).FormId);
    }

    [Fact]
    public void AllFormsHash_RejectsHeaderAcrossVaGapEvenWhenFlatBytesLookValid()
    {
        const uint hashVa = HeapBaseVa + 0x100;
        const uint itemVa = HeapBaseVa + 0x200;
        const uint formVa = HeapBaseVa + 0x300;
        const uint formId = 0x20000006;
        var accessor = new SparseMemoryAccessor();
        var regions = new List<MinidumpMemoryRegion>();

        // The complete hash is valid at flat file offset 16, but VA +8 is missing.
        var hash = CreateSingleBucketHash(hashVa, itemVa);
        accessor.AddRange(16, hash);
        regions.Add(Region(hashVa, 16, 8));
        regions.Add(Region(hashVa + 9, 25, 11));

        var item = CreateMapItem(formId, formVa);
        accessor.AddRange(96, item);
        regions.Add(Region(itemVa, 96, item.Length));

        var form = CreateTesFormHeader(CellFormType, formId);
        accessor.AddRange(192, form);
        regions.Add(Region(formVa, 192, form.Length));

        var result = BuildSparseEnumerator(accessor, regions, hashVa).Enumerate([], []);

        Assert.Empty(result.Cells);
    }

    [Fact]
    public void AllFormsHash_StitchesBucketArrayAcrossDiscontiguousFileOffsets()
    {
        const uint hashVa = HeapBaseVa + 0x100;
        const uint bucketVa = HeapBaseVa + 0x180;
        const uint itemVa = HeapBaseVa + 0x200;
        const uint formVa = HeapBaseVa + 0x300;
        const uint formId = 0x20000007;
        var accessor = new SparseMemoryAccessor();
        var regions = new List<MinidumpMemoryRegion>();

        var header = CreateHashHeader(2, bucketVa);
        accessor.AddRange(16, header);
        regions.Add(Region(hashVa, 16, header.Length));

        var buckets = new byte[8];
        WriteUInt32BE(buckets, 4, itemVa);
        accessor.AddRange(96, buckets[..4]);
        accessor.AddRange(160, buckets[4..]);
        regions.Add(Region(bucketVa, 96, 4));
        regions.Add(Region(bucketVa + 4, 160, 4));

        var item = CreateMapItem(formId, formVa);
        accessor.AddRange(224, item);
        regions.Add(Region(itemVa, 224, item.Length));

        var form = CreateTesFormHeader(CellFormType, formId);
        accessor.AddRange(288, form);
        regions.Add(Region(formVa, 288, form.Length));

        var result = BuildSparseEnumerator(accessor, regions, hashVa).Enumerate([], []);

        Assert.Equal(formId, Assert.Single(result.Cells).FormId);
    }

    [Fact]
    public void AllFormsHash_RejectsBucketArrayAcrossVaGapEvenWhenFlatBytesLookValid()
    {
        const uint hashVa = HeapBaseVa + 0x100;
        const uint bucketVa = HeapBaseVa + 0x180;
        const uint itemVa = HeapBaseVa + 0x200;
        const uint formVa = HeapBaseVa + 0x300;
        const uint formId = 0x20000008;
        var accessor = new SparseMemoryAccessor();
        var regions = new List<MinidumpMemoryRegion>();

        var header = CreateHashHeader(2, bucketVa);
        accessor.AddRange(16, header);
        regions.Add(Region(hashVa, 16, header.Length));

        // Flat bytes contain the valid second bucket, but bucket VA +4 is absent.
        var buckets = new byte[8];
        WriteUInt32BE(buckets, 4, itemVa);
        accessor.AddRange(96, buckets);
        regions.Add(Region(bucketVa, 96, 4));
        regions.Add(Region(bucketVa + 5, 101, 3));

        var item = CreateMapItem(formId, formVa);
        accessor.AddRange(160, item);
        regions.Add(Region(itemVa, 160, item.Length));

        var form = CreateTesFormHeader(CellFormType, formId);
        accessor.AddRange(224, form);
        regions.Add(Region(formVa, 224, form.Length));

        var result = BuildSparseEnumerator(accessor, regions, hashVa).Enumerate([], []);

        Assert.Empty(result.Cells);
    }

    [Fact]
    public void AllFormsHash_StopsAtSelfReferentialMapItem()
    {
        const uint hashVa = HeapBaseVa + 0x100;
        const uint itemVa = HeapBaseVa + 0x200;
        const uint formVa = HeapBaseVa + 0x300;
        const uint formId = 0x20000009;
        var sparse = new SparseMemoryAccessor();
        var regions = new List<MinidumpMemoryRegion>();

        var hash = CreateSingleBucketHash(hashVa, itemVa);
        sparse.AddRange(16, hash);
        regions.Add(Region(hashVa, 16, hash.Length));

        var item = CreateMapItem(formId, formVa, itemVa);
        sparse.AddRange(96, item);
        regions.Add(Region(itemVa, 96, item.Length));

        var form = CreateTesFormHeader(CellFormType, formId);
        sparse.AddRange(160, form);
        regions.Add(Region(formVa, 160, form.Length));

        var counting = new CountingMemoryAccessor(sparse);
        var result = BuildSparseEnumerator(counting, regions, hashVa).Enumerate([], []);

        Assert.Equal(formId, Assert.Single(result.Cells).FormId);
        Assert.True(counting.ReadCount < 20, $"Expected cycle termination, observed {counting.ReadCount} reads.");
    }

    // ============================================================================
    // Path 2: Heap-scan vtable
    // ============================================================================

    [Fact]
    public void HeapScan_SeedsFromKnownCellVfptr_FindsAllInstances()
    {
        var heap = new HeapBuilder(0x8000);

        // Seed cell (also surfaced via Path 0 so heap-scan has a vtable to harvest).
        var seedCellVa = heap.PlaceCell(0x40000001);
        // Two additional cells with the SAME vtable as the seed.
        _ = heap.PlaceCell(0x40000002);
        _ = heap.PlaceCell(0x40000003);

        // Decoy 1: a struct that starts with a DIFFERENT module-range vtable.
        heap.PlaceDecoy(DecoyVtable, CellFormType, 0xDEADBEEF);

        var enumerator = heap.BuildEnumerator(0);
        var result = enumerator.Enumerate(
            [MakeEntry(0x40000001, CellFormType, seedCellVa)],
            []);

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
        var seedCellVa = heap.PlaceCell(0x50000001, HeapNonModuleVtable);
        heap.PlaceCell(0x50000002, HeapNonModuleVtable);

        var enumerator = heap.BuildEnumerator(0);
        var result = enumerator.Enumerate(
            [MakeEntry(0x50000001, CellFormType, seedCellVa)],
            []);

        Assert.Equal(1, result.Stats.FromEditorIdHash);
        Assert.Equal(0, result.Stats.FromHeapScan);
        Assert.Equal(1, result.Stats.UniqueTotal);
    }

    [Fact]
    public void HeapScan_RejectsMatchesFailingFormTypeOrFormIdValidation()
    {
        var heap = new HeapBuilder(0x4000);

        var seedCellVa = heap.PlaceCell(0x60000001);

        // Decoy that DOES start with the cell vtable but has a non-CELL form type byte.
        heap.PlaceDecoyAtVtable(CellVtable, WeapFormType, 0xCAFEBABE);

        // Decoy with vtable matching but formId == 0.
        heap.PlaceDecoyAtVtable(CellVtable, CellFormType, 0);

        var enumerator = heap.BuildEnumerator(0);
        var result = enumerator.Enumerate(
            [MakeEntry(0x60000001, CellFormType, seedCellVa)],
            []);

        Assert.Equal(1, result.Stats.FromEditorIdHash);
        Assert.Equal(0, result.Stats.FromHeapScan);
        Assert.Equal(1, result.Stats.UniqueTotal);
    }

    [Fact]
    public void HeapScan_StitchesCandidateAcrossDiscontiguousFileOffsets()
    {
        const uint seedVa = HeapBaseVa + 0x100;
        const uint targetVa = HeapBaseVa + 0x300;
        const uint seedFormId = 0x60000010;
        const uint targetFormId = 0x60000011;
        var accessor = new SparseMemoryAccessor();
        var regions = new List<MinidumpMemoryRegion>();

        var seed = CreateCell(seedFormId);
        accessor.AddRange(16, seed);
        regions.Add(Region(seedVa, 16, seed.Length));

        var target = CreateCell(targetFormId);
        accessor.AddRange(160, target[..60]);
        accessor.AddRange(352, target[60..]);
        regions.Add(Region(targetVa, 160, 60));
        regions.Add(Region(targetVa + 60, 352, 60));

        var result = BuildSparseEnumerator(accessor, regions, 0).Enumerate(
            [MakeEntry(seedFormId, CellFormType, seedVa)],
            []);

        var heapHit = Assert.Single(result.Cells, c => c.Source == RuntimeCellSource.HeapScan);
        Assert.Equal(targetFormId, heapHit.FormId);
        Assert.Equal(targetVa, heapHit.CellVa);
    }

    [Fact]
    public void HeapScan_FindsVtableSplitAcrossVaContiguousRegions()
    {
        const uint seedVa = HeapBaseVa + 0x100;
        const uint targetVa = HeapBaseVa + 0x300;
        const uint seedFormId = 0x60000012;
        const uint targetFormId = 0x60000013;
        var accessor = new SparseMemoryAccessor();
        var regions = new List<MinidumpMemoryRegion>();

        var seed = CreateCell(seedFormId);
        accessor.AddRange(16, seed);
        regions.Add(Region(seedVa, 16, seed.Length));

        // Split inside the four-byte vfptr and store the continuation elsewhere in the file.
        // A per-region signature search cannot see this candidate; the VA overlap can.
        var target = CreateCell(targetFormId);
        accessor.AddRange(160, target[..2]);
        accessor.AddRange(352, target[2..]);
        regions.Add(Region(targetVa, 160, 2));
        regions.Add(Region(targetVa + 2, 352, target.Length - 2));

        var result = BuildSparseEnumerator(accessor, regions, 0).Enumerate(
            [MakeEntry(seedFormId, CellFormType, seedVa)],
            []);

        var heapHit = Assert.Single(result.Cells, c => c.Source == RuntimeCellSource.HeapScan);
        Assert.Equal(targetFormId, heapHit.FormId);
        Assert.Equal(targetVa, heapHit.CellVa);
    }

    [Fact]
    public void HeapScan_RejectsCandidateAcrossVaGapEvenWhenFlatBytesLookValid()
    {
        const uint seedVa = HeapBaseVa + 0x100;
        const uint targetVa = HeapBaseVa + 0x300;
        const uint seedFormId = 0x60000020;
        const uint targetFormId = 0x60000021;
        var accessor = new SparseMemoryAccessor();
        var regions = new List<MinidumpMemoryRegion>();

        var seed = CreateCell(seedFormId);
        accessor.AddRange(16, seed);
        regions.Add(Region(seedVa, 16, seed.Length));

        // All 120 bytes are physically present, but only the first half has a VA mapping.
        // The former file-offset read accepted this bait as a complete TESObjectCELL.
        var target = CreateCell(targetFormId);
        accessor.AddRange(160, target);
        regions.Add(Region(targetVa, 160, 60));

        var result = BuildSparseEnumerator(accessor, regions, 0).Enumerate(
            [MakeEntry(seedFormId, CellFormType, seedVa)],
            []);

        Assert.DoesNotContain(result.Cells, c => c.FormId == targetFormId);
        Assert.Equal(0, result.Stats.FromHeapScan);
    }

    [Fact]
    public void HeapScan_SkipsWhenNoSeedAvailable()
    {
        var heap = new HeapBuilder(0x4000);

        // A real cell in heap, but no seed entry is provided to the enumerator so
        // Path 2 has no vtable to harvest. Must return zero rather than scanning.
        heap.PlaceCell(0x70000001);

        var enumerator = heap.BuildEnumerator(0);
        var result = enumerator.Enumerate([], []);

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
        // heap-scan (Path 2, harvesting vtable from the same cell). Path 0 wins.
        var cellVa = heap.PlaceCell(0x80000001);

        var enumerator = heap.BuildEnumerator(0);
        var result = enumerator.Enumerate(
            [MakeEntry(0x80000001, CellFormType, cellVa)],
            []);

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
        var path0CellVa = heap.PlaceCell(0x91000001);

        // Path 1 cell (in pAllForms, but NOT in editor-id entries).
        var path1CellVa = heap.PlaceCell(0x91000002);

        // Path 2 cell (only reachable via heap-scan).
        var path2CellVa = heap.PlaceCell(0x91000003);

        // pAllForms contains only the Path 1 cell. The third cell is found by its vtable.
        var path1Node = heap.PlaceMapItem(0x91000002, path1CellVa, 0);
        var hashTableVa = heap.PlaceHashTable([path1Node]);

        var enumerator = heap.BuildEnumerator(hashTableVa);
        var result = enumerator.Enumerate(
            [MakeEntry(0x91000001, CellFormType, path0CellVa)],
            []);

        Assert.Equal(1, result.Stats.FromEditorIdHash);
        Assert.Equal(1, result.Stats.FromAllFormsHash);
        Assert.Equal(1, result.Stats.FromHeapScan);
        Assert.Equal(3, result.Stats.UniqueTotal);
        Assert.Equal(3, result.Cells.Count);
        Assert.Contains(result.Cells, c => c.FormId == 0x91000003 && c.CellVa == path2CellVa);
    }

    // ============================================================================
    // Direct NAVM VA collection from pAllForms
    // ============================================================================

    [Fact]
    public void AllFormsHash_CollectsNavMeshVas_ByFormTypeByte()
    {
        var heap = new HeapBuilder(0x4000);

        // Three NAVM entries in pAllForms, one CELL, one WRLD (decoy).
        var navm1Va = heap.PlaceTesForm(NavmFormType, 0xA0000001);
        var navm2Va = heap.PlaceTesForm(NavmFormType, 0xA0000002);
        var navm3Va = heap.PlaceTesForm(NavmFormType, 0xA0000003);
        var cellVa = heap.PlaceCell(0xB0000001);

        var navm3Node = heap.PlaceMapItem(0xA0000003, navm3Va, 0);
        var navm2Node = heap.PlaceMapItem(0xA0000002, navm2Va, navm3Node);
        var navm1Node = heap.PlaceMapItem(0xA0000001, navm1Va, navm2Node);
        var cellNode = heap.PlaceMapItem(0xB0000001, cellVa, navm1Node);
        var hashTableVa = heap.PlaceHashTable([cellNode]);

        var enumerator = heap.BuildEnumerator(hashTableVa);
        // Provide a byte-stream anchor (one of the NAVM FormIDs) so calibration succeeds
        // and the canonical-byte entries route to NavMeshVas. Without an anchor the new
        // speculative behavior would send them to NavMeshVaCandidates instead — that path
        // is exercised by Uncalibrated_EmitsNavMeshVaCandidatesAcrossByteWindow.
        var result = enumerator.Enumerate(
            [],
            [],
            new HashSet<uint> { 0xA0000001 });

        Assert.Equal(3, result.NavMeshVas.Count);
        Assert.Equal(
            new[] { navm1Va, navm2Va, navm3Va }.OrderBy(v => v).ToArray(),
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
        var navmVa = heap.PlaceTesForm(rawNavm, 0xD0000001);
        var cellVa = heap.PlaceCustomCell(rawCell, 0xD0000002);
        var wrldVa = heap.PlaceTesForm(rawWrld, 0x000000DA);

        var wrldNode = heap.PlaceMapItem(0x000000DA, wrldVa, 0);
        var cellNode = heap.PlaceMapItem(0xD0000002, cellVa, wrldNode);
        var navmNode = heap.PlaceMapItem(0xD0000001, navmVa, cellNode);
        var hashTableVa = heap.PlaceHashTable([navmNode]);

        var driftRemap = new Dictionary<byte, byte>
        {
            { rawNavm, NavmFormType },
            { rawCell, CellFormType },
            { rawWrld, WrldFormType }
        };

        var enumerator = heap.BuildEnumerator(hashTableVa, driftRemap);
        var result = enumerator.Enumerate([], []);

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
        var runtimeNavmVa = heap.PlaceTesForm(driftedRawNavm, 0xF0000002);
        var runtimeNavm2Va = heap.PlaceTesForm(driftedRawNavm, 0xF0000003);

        var node3 = heap.PlaceMapItem(0xF0000003, runtimeNavm2Va, 0);
        var node2 = heap.PlaceMapItem(0xF0000002, runtimeNavmVa, node3);
        var anchorNode = heap.PlaceMapItem(anchorNavmFormId, anchorNavmVa, node2);
        var hashTableVa = heap.PlaceHashTable([anchorNode]);

        // No drift remap (simulates detector failure), but knownNavmFormIds includes the anchor.
        var enumerator = heap.BuildEnumerator(hashTableVa);
        var result = enumerator.Enumerate(
            [],
            [],
            new HashSet<uint> { anchorNavmFormId });

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
        // Without ALSO an anchor or drift-confirmed NAVM byte, the canonical
        // 0x43 entries route to NavMeshVaCandidates (speculative), not NavMeshVas. This
        // test exercises that fallback path; the calibrated-canonical path is covered by
        // AllFormsHash_CollectsNavMeshVas_ByFormTypeByte (which provides an anchor).
        var heap = new HeapBuilder(0x2000);
        var navmVa = heap.PlaceTesForm(NavmFormType, 0xE0000001);
        var navmNode = heap.PlaceMapItem(0xE0000001, navmVa, 0);
        var hashTableVa = heap.PlaceHashTable([navmNode]);

        var enumerator = heap.BuildEnumerator(hashTableVa); // no drift remap
        var result = enumerator.Enumerate([], []);

        Assert.Empty(result.NavMeshVas);
        Assert.Single(result.NavMeshVaCandidates);
        Assert.Equal(navmVa, result.NavMeshVaCandidates[0]);
    }

    // ============================================================================
    // Speculative NavMeshVaCandidates for uncalibrated builds
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
        var entry0x40 = heap.PlaceTesForm(0x40, 0x10000040); // outside window
        var entry0x41 = heap.PlaceTesForm(0x41, 0x10000041); // WRLD → wrldVas
        var entry0x42 = heap.PlaceTesForm(0x42, 0x10000042); // window
        var entry0x43 = heap.PlaceTesForm(0x43, 0x10000043); // window
        var entry0x44 = heap.PlaceTesForm(0x44, 0x10000044); // window
        var entry0x45 = heap.PlaceTesForm(0x45, 0x10000045); // window
        var entry0x46 = heap.PlaceTesForm(0x46, 0x10000046); // outside window

        var node6 = heap.PlaceMapItem(0x10000046, entry0x46, 0);
        var node5 = heap.PlaceMapItem(0x10000045, entry0x45, node6);
        var node4 = heap.PlaceMapItem(0x10000044, entry0x44, node5);
        var node3 = heap.PlaceMapItem(0x10000043, entry0x43, node4);
        var node2 = heap.PlaceMapItem(0x10000042, entry0x42, node3);
        var node1 = heap.PlaceMapItem(0x10000041, entry0x41, node2);
        var node0 = heap.PlaceMapItem(0x10000040, entry0x40, node1);
        var hashTableVa = heap.PlaceHashTable([node0]);

        var enumerator = heap.BuildEnumerator(hashTableVa);
        var result = enumerator.Enumerate(
            [],
            []);

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
        var siblingVa = heap.PlaceTesForm(NavmFormType, 0x20000002);

        var sibling = heap.PlaceMapItem(0x20000002, siblingVa, 0);
        var anchor = heap.PlaceMapItem(anchorFormId, anchorVa, sibling);
        var hashTableVa = heap.PlaceHashTable([anchor]);

        var enumerator = heap.BuildEnumerator(hashTableVa);
        var result = enumerator.Enumerate(
            [],
            [],
            new HashSet<uint> { anchorFormId });

        // Both NAVM entries surface via the calibrated byte set; candidates list is empty
        // because the canonical-byte fallback is now trusted (anchor confirmed it).
        Assert.Equal(2, result.NavMeshVas.Count);
        Assert.Empty(result.NavMeshVaCandidates);
    }

    [Fact]
    public void NavMeshVas_IsEmpty_WhenNoNavMsInAllForms()
    {
        var heap = new HeapBuilder(0x2000);
        var cellVa = heap.PlaceCell(0xC0000001);
        var cellNode = heap.PlaceMapItem(0xC0000001, cellVa, 0);
        var hashTableVa = heap.PlaceHashTable([cellNode]);

        var enumerator = heap.BuildEnumerator(hashTableVa);
        var result = enumerator.Enumerate([], []);

        Assert.Empty(result.NavMeshVas);
        Assert.Equal(1, result.Stats.FromAllFormsHash);
    }

    // ============================================================================
    // Test helpers
    // ============================================================================

    private static RuntimeEditorIdEntry MakeEntry(uint formId, byte formType, uint tesFormPtr)
    {
        return new RuntimeEditorIdEntry
        {
            EditorId = $"Entry_{formId:X8}",
            FormId = formId,
            FormType = formType,
            TesFormOffset = tesFormPtr - HeapBaseVa,
            TesFormPointer = tesFormPtr
        };
    }

    private static RuntimeCellEnumerator BuildSparseEnumerator(
        IMemoryAccessor accessor,
        List<MinidumpMemoryRegion> regions,
        uint pAllFormsVa)
    {
        const int fileSize = 512;
        var minidumpInfo = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            MemoryRegions = regions
        };
        var context = new RuntimeMemoryContext(accessor, fileSize, minidumpInfo);
        return new RuntimeCellEnumerator(context, minidumpInfo, pAllFormsVa);
    }

    private static byte[] CreateSingleBucketHash(uint hashVa, uint itemVa)
    {
        var bytes = new byte[20];
        WriteUInt32BE(bytes, 4, 1);
        WriteUInt32BE(bytes, 8, hashVa + 16);
        WriteUInt32BE(bytes, 16, itemVa);
        return bytes;
    }

    private static byte[] CreateHashHeader(uint hashSize, uint bucketVa)
    {
        var bytes = new byte[16];
        WriteUInt32BE(bytes, 4, hashSize);
        WriteUInt32BE(bytes, 8, bucketVa);
        return bytes;
    }

    private static byte[] CreateMapItem(uint formId, uint formVa, uint nextVa = 0)
    {
        var bytes = new byte[12];
        WriteUInt32BE(bytes, 0, nextVa);
        WriteUInt32BE(bytes, 4, formId);
        WriteUInt32BE(bytes, 8, formVa);
        return bytes;
    }

    private static byte[] CreateTesFormHeader(byte formType, uint formId)
    {
        var bytes = new byte[16];
        WriteUInt32BE(bytes, 0, CellVtable);
        bytes[4] = formType;
        WriteUInt32BE(bytes, 12, formId);
        return bytes;
    }

    private static byte[] CreateCell(uint formId)
    {
        var bytes = new byte[120];
        WriteUInt32BE(bytes, 0, CellVtable);
        bytes[4] = CellFormType;
        WriteUInt32BE(bytes, 12, formId);
        return bytes;
    }

    private static MinidumpMemoryRegion Region(uint va, long fileOffset, int size)
    {
        return new MinidumpMemoryRegion
        {
            VirtualAddress = va,
            FileOffset = fileOffset,
            Size = size
        };
    }

    private sealed class CountingMemoryAccessor(IMemoryAccessor inner) : IMemoryAccessor
    {
        public int ReadCount { get; private set; }

        public int ReadArray(long position, byte[] array, int offset, int count)
        {
            ReadCount++;
            return inner.ReadArray(position, array, offset, count);
        }
    }

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
        {
            return PlaceCustomCell(CellFormType, formId, vtable);
        }

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

        /// <summary>
        ///     Place a TESForm-shaped decoy in the heap that does NOT match the cell vtable.
        ///     Heap-scan must not surface it because the vtable signature doesn't match.
        /// </summary>
        public void PlaceDecoy(uint vtable, byte formTypeByte, uint formId)
        {
            const int size = 256;
            var va = AllocateAligned(size);
            var offset = OffsetForVa(va);
            WriteUInt32BE(_buffer, offset + 0, vtable);
            _buffer[offset + 4] = formTypeByte;
            WriteUInt32BE(_buffer, offset + 12, formId);
        }

        /// <summary>
        ///     Place a decoy that DOES match the cell vtable but fails other validation
        ///     gates (form-type byte or zero FormID).
        /// </summary>
        public void PlaceDecoyAtVtable(uint vtable, byte formTypeByte, uint formId)
        {
            PlaceDecoy(vtable, formTypeByte, formId);
        }

        public RuntimeCellEnumerator BuildEnumerator(uint pAllFormsVa)
        {
            return BuildEnumerator(pAllFormsVa, null);
        }

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

        private static int OffsetForVa(uint va)
        {
            return unchecked((int)(va - HeapBaseVa));
        }
    }
}