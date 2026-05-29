using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Planner-side adapter for LAND record emission. Doesn't fit the single-model
///     <see cref="IPlannedRecordEncoder{TModel}" /> contract because the legacy
///     <see cref="LandEncoder.Encode" /> takes two inputs (heightmap + visual data),
///     so this lives outside the encoder registry. <c>PlanCellSectionBuilder</c>
///     invokes it directly per LAND record.
/// </summary>
public static class PlannedLandEncoder
{
    /// <summary>Compressed-record flag bit; matches legacy <c>PluginRecordByteBuilder</c>.</summary>
    private const uint CompressedFlag = 0x00040000u;

    /// <summary>
    ///     Build a complete LAND record (header + subrecords) for emission inside a cell's
    ///     Temporary Children GRUP. Returns null when the heightmap produces no subrecord
    ///     payload (parse error or all-flat terrain that doesn't warrant emission); the
    ///     caller treats that as "skip LAND for this cell."
    /// </summary>
    public static byte[]? EncodeRecord(
        LandHeightmap heightmap,
        LandVisualData? visualData,
        uint landFormId,
        PluginBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(heightmap);
        ArgumentNullException.ThrowIfNull(options);

        var subs = LandEncoder.Encode(heightmap, visualData);
        if (subs is null || subs.Count == 0)
        {
            return null;
        }

        var flags = options.CompressRecords ? CompressedFlag : 0u;
        return PluginRecordByteBuilder.BuildNewRecordBytes("LAND", landFormId, flags, subs);
    }
}
