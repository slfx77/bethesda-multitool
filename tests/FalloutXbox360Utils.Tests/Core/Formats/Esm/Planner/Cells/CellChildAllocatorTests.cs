using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class CellChildAllocatorTests
{
    [Fact]
    public void Allocates_FormId_For_New_Placed_Ref()
    {
        var allocator = new CellChildAllocator(new FormIdAllocator());
        var placed = new PlacedReference
        {
            FormId = 0xAA000001,
            BaseFormId = 0x000ABCDE,
            RecordType = "REFR"
        };
        var cell = new CellRecord { FormId = 0x000ABCDE, PlacedObjects = [placed] };
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x000ABCDE,
            Source = SourceKind.DmpOverride,
            DmpModel = cell
        };

        var result = allocator.AllocateAll([entry], [], new HashSet<uint>());

        Assert.Single(result.PlacedRefSourceToEmitted);
        Assert.Equal(0x01000800u, result.PlacedRefSourceToEmitted[0xAA000001]);
        Assert.Empty(result.NavmSourceToEmitted);
    }

    [Fact]
    public void Skips_Master_Resident_Placed_Refs()
    {
        var allocator = new CellChildAllocator(new FormIdAllocator());
        var placed = new PlacedReference
        {
            FormId = 0x000ABCDE, // Already master-resident.
            BaseFormId = 0x000ABCDF,
            RecordType = "REFR"
        };
        var cell = new CellRecord { FormId = 0x000ABCDF, PlacedObjects = [placed] };
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x000ABCDF,
            Source = SourceKind.DmpOverride,
            DmpModel = cell
        };

        var result = allocator.AllocateAll(
            [entry], [], new HashSet<uint> { 0x000ABCDE });

        Assert.Empty(result.PlacedRefSourceToEmitted);
    }

    [Fact]
    public void Skips_Runtime_State_FormIds()
    {
        var allocator = new CellChildAllocator(new FormIdAllocator());
        // Player ref 0x14 has high byte 0 — runtime-state.
        var playerRef = new PlacedReference { FormId = 0x14, BaseFormId = 0x7, RecordType = "REFR" };
        var cell = new CellRecord { FormId = 0x3C, PlacedObjects = [playerRef] };
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x3C,
            Source = SourceKind.DmpOverride,
            DmpModel = cell
        };

        var result = allocator.AllocateAll([entry], [], new HashSet<uint>());

        Assert.Empty(result.PlacedRefSourceToEmitted);
    }

    [Fact]
    public void Allocates_FormId_For_New_Navm()
    {
        var allocator = new CellChildAllocator(new FormIdAllocator());
        var navm = new NavMeshRecord
        {
            FormId = 0xAA000001,
            CellFormId = 0x000ABCDE,
            RawSubrecords = [new NavMeshSubrecord("DATA", [1, 2, 3, 4])]
        };

        var result = allocator.AllocateAll([], [navm], new HashSet<uint>());

        Assert.Single(result.NavmSourceToEmitted);
        Assert.Equal(0x01000800u, result.NavmSourceToEmitted[0xAA000001]);
    }

    [Fact]
    public void Dedups_Placed_Refs_Across_Multi_Snapshot_Unions()
    {
        var allocator = new CellChildAllocator(new FormIdAllocator());
        var placed1 = new PlacedReference { FormId = 0xAA000001, BaseFormId = 0x000ABCDE, RecordType = "REFR" };
        var placed2 = new PlacedReference { FormId = 0xAA000001, BaseFormId = 0x000ABCDE, RecordType = "REFR" };
        var cell = new CellRecord { FormId = 0x000ABCDE, PlacedObjects = [placed1, placed2] };
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x000ABCDE,
            Source = SourceKind.DmpOverride,
            DmpModel = cell
        };

        var result = allocator.AllocateAll([entry], [], new HashSet<uint>());

        Assert.Single(result.PlacedRefSourceToEmitted);
    }

    [Fact]
    public void Allocates_FormId_For_DmpNew_Cell_With_NonMaster_Proto_FormId()
    {
        // Proto allocated a cell at FormID 0x0010B9A5 (looks like master range due to the
        // 0x00 prefix, but FNV vanilla doesn't actually have this FormID). Emitting it
        // verbatim makes the engine treat it as a phantom master override; virtual-cell
        // autogen at the same grid coord then collides with a real master cell. The
        // allocator must re-FormID it into our ESP's range so the engine sees a clean new
        // cell owned by our plugin.
        var allocator = new CellChildAllocator(new FormIdAllocator());
        var cell = new CellRecord
        {
            FormId = 0x0010B9A5,
            WorldspaceFormId = 0x01001C4A,
            GridX = 2,
            GridY = 3
        };
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x0010B9A5,
            Source = SourceKind.DmpNew,
            DmpModel = cell
        };

        // FNV master doesn't contain 0x0010B9A5.
        var result = allocator.AllocateAll([entry], [], new HashSet<uint>());

        Assert.Single(result.CellSourceToEmitted);
        Assert.Equal(0x01000800u, result.CellSourceToEmitted[0x0010B9A5u]);
    }

    [Fact]
    public void Skips_Cell_Allocation_For_Master_FormIds()
    {
        // Master-resident cell — DmpOverride path keeps the master FormID; no allocation.
        var allocator = new CellChildAllocator(new FormIdAllocator());
        var cell = new CellRecord
        {
            FormId = 0x000ABCDE,
            WorldspaceFormId = 0x0000003C,
            GridX = 0,
            GridY = 0
        };
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x000ABCDE,
            Source = SourceKind.DmpOverride,
            DmpModel = cell
        };

        var result = allocator.AllocateAll(
            [entry], [], new HashSet<uint> { 0x000ABCDE });

        Assert.Empty(result.CellSourceToEmitted);
    }

    [Fact]
    public void Skips_Cell_Allocation_For_MasterOnly_Source_Entries()
    {
        // Catalog includes MasterOnly entries (master cells with no DMP override). These
        // must NOT be allocated — they're emitted verbatim from the master record.
        var allocator = new CellChildAllocator(new FormIdAllocator());
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x000DA727,
            Source = SourceKind.MasterOnly,
            DmpModel = null
        };

        var result = allocator.AllocateAll([entry], [], new HashSet<uint>());

        Assert.Empty(result.CellSourceToEmitted);
    }

    [Fact]
    public void Allocates_FormId_For_DmpNew_REFR_With_PhantomMaster_FormId()
    {
        // Same bug class as the cell-Pass-0 fix, applied to placed refs. A proto-allocated
        // REFR with FormID 0x0010BABC looks like a master override (master numeric range)
        // but isn't actually in master. Before the gap-#1 fix the broad
        // (formId & 0xFF000000) == 0 check skipped allocation, emitting verbatim → phantom-
        // master crash class. After the fix the narrow EngineFixedPlacedRefs allowlist only
        // skips the genuine engine-reserved singletons (0x14, 0x18).
        var allocator = new CellChildAllocator(new FormIdAllocator());
        var phantomRef = new PlacedReference
        {
            FormId = 0x0010BABCu,
            BaseFormId = 0x000ABCDE,
            RecordType = "REFR"
        };
        var cell = new CellRecord
        {
            FormId = 0x0010BABDu,
            WorldspaceFormId = 0x000DA726u, // master WastelandNV
            GridX = 0,
            GridY = 0,
            PlacedObjects = [phantomRef]
        };
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x0010BABDu,
            Source = SourceKind.DmpOverride,
            DmpModel = cell
        };

        // Master doesn't contain 0x0010BABC (it's a proto allocation) and 0x0010BABC isn't
        // in the EngineFixedPlacedRefs allowlist → must be allocated.
        var result = allocator.AllocateAll(
            [entry], [], new HashSet<uint>());

        Assert.True(
            result.PlacedRefSourceToEmitted.ContainsKey(0x0010BABCu),
            "DmpNew REFR with phantom-master FormID must be re-allocated, not emitted verbatim.");
    }

    [Fact]
    public void Skips_Engine_Reserved_Player_Ref_0x14()
    {
        // The player REFR (0x00000014) is NOT in master ESM but IS hardcoded by the engine.
        // The allocator must preserve its identity even though masterFormIds.Contains
        // returns false — that's what the EngineFixedPlacedRefs allowlist is for.
        var allocator = new CellChildAllocator(new FormIdAllocator());
        var playerRef = new PlacedReference { FormId = 0x00000014u, BaseFormId = 0x07, RecordType = "REFR" };
        var cell = new CellRecord { FormId = 0x0000003C, PlacedObjects = [playerRef] };
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x0000003C,
            Source = SourceKind.DmpOverride,
            DmpModel = cell
        };

        var result = allocator.AllocateAll([entry], [], new HashSet<uint>());

        Assert.Empty(result.PlacedRefSourceToEmitted);
    }

    [Fact]
    public void Allocates_FormId_For_DmpNew_PGRE_PlacedRef()
    {
        // PGRE (placed grenade) is on the planner-routing roadmap; the allocator whitelist
        // now includes it defensively so when routing lands the allocator already handles
        // the phantom-master shape. Pre-gap-#3 the strict ("REFR" or "ACHR" or "ACRE")
        // whitelist would have skipped PGRE allocation, leaving its FormID verbatim.
        var allocator = new CellChildAllocator(new FormIdAllocator());
        var pgre = new PlacedReference
        {
            FormId = 0x0010BABCu,
            BaseFormId = 0x000ABCDE,
            RecordType = "PGRE"
        };
        var cell = new CellRecord
        {
            FormId = 0x0010BABDu,
            WorldspaceFormId = 0x000DA726u,
            GridX = 0,
            GridY = 0,
            PlacedObjects = [pgre]
        };
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x0010BABDu,
            Source = SourceKind.DmpOverride,
            DmpModel = cell
        };

        var result = allocator.AllocateAll([entry], [], new HashSet<uint>());

        Assert.True(
            result.PlacedRefSourceToEmitted.ContainsKey(0x0010BABCu),
            "DmpNew PGRE with phantom-master FormID must be allocated like REFR/ACHR/ACRE.");
    }

    [Fact]
    public void Cell_FormId_Allocation_Precedes_PlacedRef_Allocation()
    {
        // Pass 0 (cells) allocates before Pass 1 (placed refs). Validating the order
        // matters because FormIdAllocator hands out monotonically increasing IDs — if
        // cells allocated AFTER placed refs, child refs would get the lower FormID and
        // the cell would get higher, which downstream merge code wouldn't expect.
        var allocator = new CellChildAllocator(new FormIdAllocator());
        var placed = new PlacedReference { FormId = 0xAA000001, BaseFormId = 0x000ABCDE, RecordType = "REFR" };
        var cell = new CellRecord
        {
            FormId = 0x0010B9A5,
            WorldspaceFormId = 0x01001C4A,
            GridX = 0,
            GridY = 0,
            PlacedObjects = [placed]
        };
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x0010B9A5,
            Source = SourceKind.DmpNew,
            DmpModel = cell
        };

        var result = allocator.AllocateAll([entry], [], new HashSet<uint>());

        Assert.Equal(0x01000800u, result.CellSourceToEmitted[0x0010B9A5u]);
        Assert.Equal(0x01000801u, result.PlacedRefSourceToEmitted[0xAA000001u]);
    }
}