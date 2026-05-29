using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     The planner-side equivalent of legacy <see cref="CellGrupBuilder.BuildCellSection" />.
///     Walks <see cref="EmitPlan.CellsByFormId" />, encodes each cell's children via the
///     planned encoders, and delegates the GRUP framing to the legacy
///     <see cref="CellGrupBuilder" />. Reusing the legacy framing means the GRUP nesting /
///     labels match byte-for-byte by construction.
/// </summary>
public sealed class PlanCellSectionBuilder
{
    private const uint CompressedFlag = 0x00040000u;

    public byte[]? BuildCellSection(
        EmitPlan plan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        PluginBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(masterByFormId);
        ArgumentNullException.ThrowIfNull(options);

        var bundles = ConvertCellsToBundles(plan, options);
        if (bundles.Count == 0)
        {
            return null;
        }

        return CellGrupBuilder.BuildCellSection(
            bundles, masterByFormId, newWorldspacesByDmpFormId: null);
    }

    /// <summary>
    ///     Convert each <see cref="CellPlan" /> entry to a bundle the legacy framing engine
    ///     consumes. Encodes the child records (REFR / ACHR / ACRE / NAVM) to bytes via the
    ///     planned encoders so the output matches what legacy would produce for the same
    ///     inputs.
    /// </summary>
    private static IReadOnlyList<CellOverrideBundle> ConvertCellsToBundles(
        EmitPlan plan, PluginBuildOptions options)
    {
        var bundles = new List<CellOverrideBundle>(plan.CellsByFormId.Count);
        var nvexRewrites = plan.SourceToEmittedFormId;

        foreach (var (cellFormId, cellPlan) in plan.CellsByFormId)
        {
            if (cellPlan.CellRecordPlan.Disposition == RecordDisposition.Skip)
            {
                continue;
            }

            var cellRecordBytes = EncodeCellAnchor(cellPlan, options);
            if (cellRecordBytes is null)
            {
                continue; // Skip cells we can't anchor (no master + no DMP model).
            }

            bundles.Add(new CellOverrideBundle
            {
                CellFormId = cellFormId,
                Context = cellPlan.Context,
                CellRecordBytes = cellRecordBytes,
                PersistentChildRecords = EncodeChildren(cellPlan.PersistentChildren, cellFormId, nvexRewrites, options),
                VwdChildRecords = EncodeChildren(cellPlan.VwdChildren, cellFormId, nvexRewrites, options),
                TemporaryChildRecords = EncodeChildren(cellPlan.TemporaryChildren, cellFormId, nvexRewrites, options),
            });
        }

        return bundles;
    }

    /// <summary>
    ///     Produce the CELL record bytes the bundle hands to legacy GRUP framing. For
    ///     KeepMaster / Override cells the master byte slice is reused verbatim; for
    ///     <see cref="RecordDisposition.New" /> cells the CELL is fresh-encoded through
    ///     <see cref="CellEncoder" /> + <see cref="PluginRecordByteBuilder.BuildNewRecordBytes" />.
    ///     Returns null when neither path is available (e.g. New disposition with no model).
    /// </summary>
    private static byte[]? EncodeCellAnchor(CellPlan cellPlan, PluginBuildOptions options)
    {
        if (cellPlan.CellRecordPlan.Master is { } master)
        {
            return CellGrupBuilder.ReconstructRecordBytes(master);
        }

        if (cellPlan.CellRecordPlan.Model is not CellRecord cellModel)
        {
            return null;
        }

        var encoded = new CellEncoder().Encode(cellModel);
        if (encoded.Subrecords.Count == 0)
        {
            return null;
        }

        var flags = options.CompressRecords ? CompressedFlag : 0u;
        return PluginRecordByteBuilder.BuildNewRecordBytes(
            "CELL", cellPlan.CellFormId, flags, encoded.Subrecords);
    }

    private static IReadOnlyList<byte[]> EncodeChildren(
        IReadOnlyList<RecordPlan> children,
        uint cellFormId,
        IReadOnlyDictionary<uint, uint> nvexRewrites,
        PluginBuildOptions options)
    {
        if (children.Count == 0)
        {
            return [];
        }

        var bytes = new List<byte[]>(children.Count);
        foreach (var child in children)
        {
            var encoded = EncodeChild(child, cellFormId, nvexRewrites, options);
            if (encoded is not null)
            {
                bytes.Add(encoded);
            }
        }

        return bytes;
    }

    private static byte[]? EncodeChild(
        RecordPlan child,
        uint cellFormId,
        IReadOnlyDictionary<uint, uint> nvexRewrites,
        PluginBuildOptions options)
    {
        switch (child.Type)
        {
            case "REFR" or "ACHR" or "ACRE":
                return EncodePlacedRef(child, options);
            case "NAVM":
                return EncodeNavm(child, cellFormId, nvexRewrites, options);
            default:
                return null;
        }
    }

    private static byte[]? EncodePlacedRef(RecordPlan child, PluginBuildOptions options)
    {
        if (child.Model is not PlacedReference placed)
        {
            return null;
        }

        var subs = child.Disposition switch
        {
            RecordDisposition.New => RefrEncoder.EncodeNewPlacedReference(placed, null, null),
            RecordDisposition.Override => RefrEncoder.EncodePlacedReference(placed),
            _ => null,
        };

        if (subs is null || subs.Subrecords.Count == 0)
        {
            return null;
        }

        var flags = options.CompressRecords ? CompressedFlag : 0u;

        if (child.Disposition == RecordDisposition.New)
        {
            return PluginRecordByteBuilder.BuildNewRecordBytes(
                child.Type, child.FormId, flags, subs.Subrecords);
        }

        // Override path: needs the master record for header reuse. Children currently
        // don't carry Master refs for placed overrides; defer override emission to legacy.
        return null;
    }

    private static byte[]? EncodeNavm(
        RecordPlan child,
        uint cellFormId,
        IReadOnlyDictionary<uint, uint> nvexRewrites,
        PluginBuildOptions options)
    {
        if (child.Model is not NavMeshRecord navm)
        {
            return null;
        }

        if (child.Disposition != RecordDisposition.New)
        {
            return null; // KeepMaster NAVMs deferred — legacy preserves them verbatim.
        }

        return PlannedNavmEncoder.EncodeRecord(navm, cellFormId, child.FormId, nvexRewrites, options);
    }
}
