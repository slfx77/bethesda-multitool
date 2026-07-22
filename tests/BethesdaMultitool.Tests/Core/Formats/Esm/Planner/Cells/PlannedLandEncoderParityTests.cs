using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class PlannedLandEncoderParityTests
{
    [Fact]
    public void EncodeRecord_Encodes_Override_Disposition_At_Master_FormId()
    {
        var land = new CellLandDecision
        {
            CellSourceFormId = 0x000DDF1C,
            Heightmap = new LandHeightmap
            {
                HeightOffset = 100f,
                HeightDeltas = Enumerable.Repeat((sbyte)4, 33 * 33).ToArray(),
            },
            HeightSource = CellLandHeightSource.CapturedHeightmap,
            MasterLandFormId = 0x000ABC01,
        };
        var plan = new RecordPlan
        {
            Type = "LAND",
            Disposition = RecordDisposition.Override,
            FormId = 0x000ABC01,
            Model = land,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
        };

        var bytes = PlannedLandEncoder.EncodeRecord(
            plan, new PluginBuildOptions { CompressRecords = false });

        Assert.NotNull(bytes);
        Assert.Equal("LAND", System.Text.Encoding.ASCII.GetString(bytes!, 0, 4));
        Assert.Equal(0x000ABC01u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)));
    }

    [Fact]
    public void EncodeRecord_Returns_Null_When_Legacy_Returns_Null()
    {
        // A flat heightmap — exercises the planner adapter's null/empty fallback.
        var heightmap = new LandHeightmap
        {
            HeightOffset = 0f,
            HeightDeltas = new sbyte[33 * 33] // All zeros.
        };
        var options = new PluginBuildOptions { CompressRecords = false };

        var legacy = LandEncoder.Encode(heightmap);
        var planner = PlannedLandEncoder.EncodeRecord(heightmap, null, 0x01000800, options);

        if (legacy is null || legacy.Count == 0)
        {
            Assert.Null(planner);
            return;
        }

        var legacyBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
            "LAND", 0x01000800, 0u, legacy);
        Assert.Equal(legacyBytes, planner);
    }
}