using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Pins the Phase 0 placed-ref FormID pre-allocation contract. Phase 0 must:
///     1. Assign a plugin-local FormID to every DMP placed REFR/ACHR/ACRE that's NOT in master.
///     2. Populate <c>_emittedNewFormIds</c> / <c>_emittedNewFormIdsByType</c> so Phase 3
///        encoders (notably <c>PackEncoder.EncodeNew</c>'s PLDT Union resolution) see those
///        FormIDs in the validator set.
///     3. Populate <c>_newRecordSourceToAllocated</c> so the source→allocated remap finds the
///        FormID for any subrecord that references the placed ref by its DMP-source FormID.
///     4. Skip master overrides (master REFRs reuse the master FormID — no allocation needed).
///     5. Dedup placed refs that appear in multiple cell captures (the cell-capture union runs
///        in Phase 4; Phase 0 sees the raw per-cell lists and must not double-allocate).
/// </summary>
public class PreAllocatePlacedRefFormIdsTests
{
    [Fact]
    public void PreAllocate_assigns_FormId_to_new_REFR_visible_in_emittedNewFormIds()
    {
        var builder = NewBuilder();
        var allocator = new FormIdAllocator();
        var stats = new ConversionPipelineStats();
        var dmp = new RecordCollection
        {
            Cells =
            {
                new CellRecord
                {
                    FormId = 0x000ABC00,
                    EditorId = "ProtoCell",
                    PlacedObjects = { new PlacedReference { FormId = 0x00BEEFFF, BaseFormId = 0x12, RecordType = "REFR" } }
                }
            }
        };

        builder.PreAllocateNewPlacedRefFormIds(dmp, new Dictionary<uint, ParsedMainRecord>(), allocator, stats);

        Assert.True(builder.PreAllocatedRefFormIdsForTest.TryGetValue(0x00BEEFFF, out var pre));
        Assert.Equal(0x01000800u, pre); // Default allocator base 0x800, plugin index 0x01.
        Assert.True(builder.EmittedNewFormIdsByTypeForTest.TryGetValue("REFR", out var refrSet));
        Assert.Contains(pre, refrSet!);
        Assert.True(builder.NewRecordSourceToAllocatedForTest.TryGetValue(0x00BEEFFF, out var aliased));
        Assert.Equal(pre, aliased);
    }

    [Fact]
    public void PreAllocate_skips_master_REFR_override()
    {
        var builder = NewBuilder();
        var allocator = new FormIdAllocator();
        var stats = new ConversionPipelineStats();
        const uint masterRefFormId = 0x00010203;
        var dmp = new RecordCollection
        {
            Cells =
            {
                new CellRecord
                {
                    FormId = 0x000ABC00,
                    PlacedObjects = { new PlacedReference { FormId = masterRefFormId, BaseFormId = 0x42, RecordType = "REFR" } }
                }
            }
        };
        var pcRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [masterRefFormId] = new ParsedMainRecord
            {
                Header = new MainRecordHeader { Signature = "REFR", FormId = masterRefFormId }
            }
        };

        builder.PreAllocateNewPlacedRefFormIds(dmp, pcRecords, allocator, stats);

        Assert.Empty(builder.PreAllocatedRefFormIdsForTest);
        Assert.False(allocator.HasAllocations);
    }

    [Fact]
    public void PreAllocate_dedupes_REFR_present_in_multiple_cell_captures()
    {
        var builder = NewBuilder();
        var allocator = new FormIdAllocator();
        var stats = new ConversionPipelineStats();
        const uint sourceRefId = 0x00BEEFFF;
        var dmp = new RecordCollection
        {
            Cells =
            {
                new CellRecord
                {
                    FormId = 0x000ABC00,
                    PlacedObjects = { new PlacedReference { FormId = sourceRefId, BaseFormId = 0x12, RecordType = "REFR" } }
                },
                new CellRecord
                {
                    FormId = 0x000ABC01,
                    PlacedObjects = { new PlacedReference { FormId = sourceRefId, BaseFormId = 0x12, RecordType = "REFR" } }
                }
            }
        };

        builder.PreAllocateNewPlacedRefFormIds(dmp, new Dictionary<uint, ParsedMainRecord>(), allocator, stats);

        Assert.Single(builder.PreAllocatedRefFormIdsForTest);
        Assert.Equal(0x01000801u, allocator.NextLocalId | (uint)(FormIdAllocator.PluginIndex << 24));
    }

    [Fact]
    public void PreAllocate_skips_runtime_state_FormIds_and_non_placed_record_types()
    {
        var builder = NewBuilder();
        var allocator = new FormIdAllocator();
        var stats = new ConversionPipelineStats();
        var dmp = new RecordCollection
        {
            Cells =
            {
                new CellRecord
                {
                    FormId = 0x000ABC00,
                    PlacedObjects =
                    {
                        // Player (0x07) is runtime-state per RuntimeStateRecordPolicy and must
                        // be skipped even though it does not appear in master.
                        new PlacedReference { FormId = 0x00000007, BaseFormId = 0x00000007, RecordType = "REFR" },
                        // A NAVM placed under a cell should be rejected by the record-type
                        // gate (Phase 0 only handles REFR / ACHR / ACRE).
                        new PlacedReference { FormId = 0x00BEEF11, BaseFormId = 0x0, RecordType = "NAVM" }
                    }
                }
            }
        };

        builder.PreAllocateNewPlacedRefFormIds(dmp, new Dictionary<uint, ParsedMainRecord>(), allocator, stats);

        Assert.Empty(builder.PreAllocatedRefFormIdsForTest);
    }

    private static PluginBuilder NewBuilder()
    {
        return new PluginBuilder(RecordEncoderRegistry.CreateDefault());
    }
}
