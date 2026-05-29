using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Nav;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Planner-side adapter for NAVM emission. The legacy
///     <see cref="NavMeshByteRewriter.Rewrite" /> patches DATA (cell FormID) and NVEX
///     (target NAVM FormIDs) in the captured subrecord stream; this adapter wraps that
///     plus the record-byte assembly. Doesn't fit
///     <see cref="IPlannedRecordEncoder{TModel}" /> because it needs three inputs
///     (the model, the target cell FormID, and the NVEX remap dictionary).
/// </summary>
public static class PlannedNavmEncoder
{
    private const uint CompressedFlag = 0x00040000u;

    /// <summary>
    ///     Build a complete NAVM record (header + patched subrecords) for emission
    ///     inside a cell's Temporary Children GRUP. The caller must supply the new cell
    ///     FormID (DATA[0..3]) and an optional NVEX rewrite dictionary that maps captured
    ///     DMP NAVM FormIDs to their planner-allocated emit FormIDs.
    /// </summary>
    public static byte[] EncodeRecord(
        NavMeshRecord navm,
        uint newCellFormId,
        uint newNavmFormId,
        IReadOnlyDictionary<uint, uint> nvexRewrites,
        PluginBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(navm);
        ArgumentNullException.ThrowIfNull(nvexRewrites);
        ArgumentNullException.ThrowIfNull(options);

        var patched = NavMeshByteRewriter.Rewrite(navm.RawSubrecords, newCellFormId, nvexRewrites);
        var flags = options.CompressRecords ? CompressedFlag : 0u;
        return PluginRecordByteBuilder.BuildNewRecordBytes("NAVM", newNavmFormId, flags, patched);
    }
}
