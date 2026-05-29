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
        var allocator = new CellChildAllocator(new FormIdAllocator(0x800));
        var placed = new PlacedReference
        {
            FormId = 0xAA000001,
            BaseFormId = 0x000ABCDE,
            RecordType = "REFR",
        };
        var cell = new CellRecord { FormId = 0x000ABCDE, PlacedObjects = [placed] };
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x000ABCDE,
            Source = SourceKind.DmpOverride,
            DmpModel = cell,
        };

        var result = allocator.AllocateAll([entry], [], new HashSet<uint>());

        Assert.Single(result.PlacedRefSourceToEmitted);
        Assert.Equal(0x01000800u, result.PlacedRefSourceToEmitted[0xAA000001]);
        Assert.Empty(result.NavmSourceToEmitted);
    }

    [Fact]
    public void Skips_Master_Resident_Placed_Refs()
    {
        var allocator = new CellChildAllocator(new FormIdAllocator(0x800));
        var placed = new PlacedReference
        {
            FormId = 0x000ABCDE, // Already master-resident.
            BaseFormId = 0x000ABCDF,
            RecordType = "REFR",
        };
        var cell = new CellRecord { FormId = 0x000ABCDF, PlacedObjects = [placed] };
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x000ABCDF,
            Source = SourceKind.DmpOverride,
            DmpModel = cell,
        };

        var result = allocator.AllocateAll(
            [entry], [], new HashSet<uint> { 0x000ABCDE });

        Assert.Empty(result.PlacedRefSourceToEmitted);
    }

    [Fact]
    public void Skips_Runtime_State_FormIds()
    {
        var allocator = new CellChildAllocator(new FormIdAllocator(0x800));
        // Player ref 0x14 has high byte 0 — runtime-state.
        var playerRef = new PlacedReference { FormId = 0x14, BaseFormId = 0x7, RecordType = "REFR" };
        var cell = new CellRecord { FormId = 0x3C, PlacedObjects = [playerRef] };
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x3C,
            Source = SourceKind.DmpOverride,
            DmpModel = cell,
        };

        var result = allocator.AllocateAll([entry], [], new HashSet<uint>());

        Assert.Empty(result.PlacedRefSourceToEmitted);
    }

    [Fact]
    public void Allocates_FormId_For_New_Navm()
    {
        var allocator = new CellChildAllocator(new FormIdAllocator(0x800));
        var navm = new NavMeshRecord
        {
            FormId = 0xAA000001,
            CellFormId = 0x000ABCDE,
            RawSubrecords = [new NavMeshSubrecord("DATA", [1, 2, 3, 4])],
        };

        var result = allocator.AllocateAll([], [navm], new HashSet<uint>());

        Assert.Single(result.NavmSourceToEmitted);
        Assert.Equal(0x01000800u, result.NavmSourceToEmitted[0xAA000001]);
    }

    [Fact]
    public void Dedups_Placed_Refs_Across_Multi_Snapshot_Unions()
    {
        var allocator = new CellChildAllocator(new FormIdAllocator(0x800));
        var placed1 = new PlacedReference { FormId = 0xAA000001, BaseFormId = 0x000ABCDE, RecordType = "REFR" };
        var placed2 = new PlacedReference { FormId = 0xAA000001, BaseFormId = 0x000ABCDE, RecordType = "REFR" };
        var cell = new CellRecord { FormId = 0x000ABCDE, PlacedObjects = [placed1, placed2] };
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x000ABCDE,
            Source = SourceKind.DmpOverride,
            DmpModel = cell,
        };

        var result = allocator.AllocateAll([entry], [], new HashSet<uint>());

        Assert.Single(result.PlacedRefSourceToEmitted);
    }
}
