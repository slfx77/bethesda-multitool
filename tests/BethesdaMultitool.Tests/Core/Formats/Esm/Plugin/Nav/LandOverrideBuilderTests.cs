using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.Nav;

/// <summary>
///     Diagnostics-ratchet tests for <see cref="LandOverrideBuilder" />: the previously
///     silent failure paths (terrain-mesh encode exception, encoder decline, missing
///     allocator base) must record a drop reason and a sink warning while keeping the
///     return value and control flow exactly as before.
/// </summary>
public sealed class LandOverrideBuilderTests
{
    private const uint CellFormId = 0x000DDF3Bu;

    [Fact]
    public void TerrainMeshEncodeThrow_RecordsReason_AndStillReturnsFalse()
    {
        var sink = new RecordingSink();
        var stats = new ConversionPipelineStats();
        var builder = new LandOverrideBuilder(sink, static v => v);
        // An empty vertex buffer cannot reconstruct a terrain grid, so
        // RuntimeTerrainHeightmapEncoder.Encode throws InvalidOperationException.
        var cell = new CellRecord
        {
            FormId = CellFormId,
            RuntimeTerrainMesh = new RuntimeTerrainMesh { Vertices = [] }
        };

        var result = builder.TryEncodeForCell(
            cell, new FormIdAllocator(), new PluginBuildOptions(), stats, out var landBytes);

        Assert.False(result);
        Assert.Empty(landBytes);
        Assert.Equal(1, stats.DropReasonCounts["land.terrain-mesh-encode-failed"]);
        var warning = Assert.Single(sink.Events);
        Assert.Equal(ConversionEventSeverity.Warning, warning.Severity);
        Assert.Equal("land.terrain-mesh-encode-failed", warning.Code);
        Assert.Equal(CellFormId, warning.FormId);
        Assert.Equal(nameof(InvalidOperationException), warning.Metadata!["exceptionType"]);
    }

    [Fact]
    public void EncoderDecline_RecordsReason_AndStillReturnsFalse()
    {
        var sink = new RecordingSink();
        var stats = new ConversionPipelineStats();
        var builder = new LandOverrideBuilder(sink, static v => v);
        // LandEncoder.Encode declines any heightmap whose delta buffer is not exactly
        // the canonical 33x33 vertex count.
        var cell = new CellRecord
        {
            FormId = CellFormId,
            Heightmap = new LandHeightmap { HeightDeltas = new sbyte[8] }
        };

        var result = builder.TryEncodeForCell(
            cell, new FormIdAllocator(), new PluginBuildOptions(), stats, out var landBytes);

        Assert.False(result);
        Assert.Empty(landBytes);
        Assert.Equal(1, stats.DropReasonCounts["land.encoder-declined"]);
        var warning = Assert.Single(sink.Events);
        Assert.Equal("land.encoder-declined", warning.Code);
        Assert.Equal(CellFormId, warning.FormId);
    }

    [Fact]
    public void NoAllocatorBase_RecordsReason_AndStillReturnsFalse()
    {
        var sink = new RecordingSink();
        var stats = new ConversionPipelineStats();
        var builder = new LandOverrideBuilder(sink, static v => v);
        var cell = new CellRecord
        {
            FormId = CellFormId,
            Heightmap = new LandHeightmap { HeightDeltas = new sbyte[33 * 33] }
        };

        var result = builder.TryEncodeForCell(
            cell, new FormIdAllocator(),
            new PluginBuildOptions { NewRecordBaseFormId = 0u }, stats, out var landBytes);

        Assert.False(result);
        Assert.Empty(landBytes);
        Assert.Equal(1, stats.DropReasonCounts["land.no-allocator-base"]);
        var warning = Assert.Single(sink.Events);
        Assert.Equal("land.no-allocator-base", warning.Code);
        Assert.Equal(0, stats.NewRecordsEmitted);
    }

    [Fact]
    public void SuccessfulEncode_RecordsNoDropReasons()
    {
        var sink = new RecordingSink();
        var stats = new ConversionPipelineStats();
        var builder = new LandOverrideBuilder(sink, static v => v);
        var cell = new CellRecord
        {
            FormId = CellFormId,
            Heightmap = new LandHeightmap { HeightDeltas = new sbyte[33 * 33] }
        };

        var result = builder.TryEncodeForCell(
            cell, new FormIdAllocator(), new PluginBuildOptions(), stats, out var landBytes);

        Assert.True(result);
        Assert.NotEmpty(landBytes);
        Assert.Empty(stats.DropReasonCounts);
        Assert.Equal(1, stats.NewRecordsEmitted);
    }

    private sealed class RecordingSink : IConversionProgressSink
    {
        public List<ConversionProgressEvent> Events { get; } = [];

        public void OnPhaseStart(string phase, int? totalItems)
        {
        }

        public void OnEvent(ConversionProgressEvent evt)
        {
            Events.Add(evt);
        }

        public void OnPhaseEnd(string phase, ConversionPipelineStats partialStats)
        {
        }

        public void OnComplete(ConversionPipelineStats stats)
        {
        }
    }
}
