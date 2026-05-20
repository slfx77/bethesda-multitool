using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using FalloutXbox360Utils.Core.Formats.Esm.Terrain;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Nav;

internal sealed class LandOverrideBuilder(
    IConversionProgressSink sink,
    Func<LandVisualData?, LandVisualData?> rewriteTextureFormIds)
{
    public bool TryEncodeForCell(
        CellRecord dmpCell,
        FormIdAllocator allocator,
        PluginBuildOptions options,
        ConversionPipelineStats stats,
        out byte[] landBytes,
        uint? existingLandFormId = null,
        LandVisualData? masterVisualData = null,
        LandHeightmap? fallbackHeightmap = null)
    {
        landBytes = [];

        var heightmap = dmpCell.Heightmap;
        if (heightmap is null && dmpCell.RuntimeTerrainMesh is not null)
        {
            try
            {
                heightmap = RuntimeTerrainHeightmapEncoder.Encode(dmpCell.RuntimeTerrainMesh);
            }
            catch
            {
                heightmap = null;
            }
        }

        if (heightmap is null)
        {
            heightmap = fallbackHeightmap;
        }

        if (heightmap is null)
        {
            return false;
        }

        byte[]? runtimeVertexColors = null;
        if (dmpCell.RuntimeTerrainMesh is not null)
        {
            try
            {
                runtimeVertexColors = RuntimeTerrainColorExtractor.ExtractVclr(dmpCell.RuntimeTerrainMesh);
            }
            catch
            {
                runtimeVertexColors = null;
            }
        }

        var visualData = LandVisualData.MergeForEmission(
            dmpCell.LandVisualData,
            runtimeVertexColors,
            masterVisualData);
        visualData = rewriteTextureFormIds(visualData);

        var subs = LandEncoder.Encode(heightmap, visualData);
        if (subs is null)
        {
            return false;
        }

        if (!existingLandFormId.HasValue && options.NewRecordBaseFormId == 0u)
        {
            return false;
        }

        var landFormId = existingLandFormId ?? allocator.Allocate();
        var flags = options.CompressRecords ? 0x00040000u : 0u;
        landBytes = PluginRecordByteBuilder.BuildNewRecordBytes("LAND", landFormId, flags, subs);
        if (existingLandFormId.HasValue)
        {
            stats.OverridesEmitted++;
        }
        else
        {
            stats.NewRecordsEmitted++;
        }

        stats.IncrementEmitted("LAND");

        if (options.VerboseDecisions)
        {
            var action = existingLandFormId.HasValue ? "overrode" : "allocated";
            sink.Decision("Merging cell children",
                $"Emitted LAND 0x{landFormId:X8} ({action}) for exterior cell 0x{dmpCell.FormId:X8} " +
                $"(grid {dmpCell.GridX}, {dmpCell.GridY}).",
                "LAND", landFormId,
                existingLandFormId.HasValue ? "land.override" : "land.new-cell");
        }

        return true;
    }
}
