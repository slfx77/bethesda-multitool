using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Serializes a planner-settled placed-ref verdict (<see cref="PlacedRefDecision" />):
///     accounts its stats, honors covered-marking, and for emits produces the record bytes
///     with the plan's final base + routing. No decision logic runs here — this is the
///     end-state encoder that remains once the writer's transitional decision chain
///     (<see cref="PlannedPlacedRefEncoder" />'s New/Override paths) is retired.
/// </summary>
internal static class VerdictPlacedRefEncoder
{
    public static byte[]? Encode(
        RecordPlan child,
        PlacedReference placed,
        PlacedRefDecision verdict,
        CellChildEncodeContext context,
        CellEncodeState state,
        ref int routeGroupType)
    {
        if (verdict.Verdict == PlacedRefEmitVerdict.Drop)
        {
            if (verdict.MarksMasterCovered)
            {
                state.CoveredMasterRefFormIds.Add(child.FormId);
            }

            if (verdict.DropReason is { } reason)
            {
                context.Stats?.IncrementSkipped(child.Type);
                context.Stats?.IncrementDropReason(reason);
            }

            if (verdict.AuxStatCode is { } auxOnDrop)
            {
                context.Stats?.IncrementDropReason(auxOnDrop);
            }

            return null;
        }

        return child.Disposition == RecordDisposition.New
            ? EncodeNewEmit(child, placed, verdict, context, ref routeGroupType)
            : EncodeOverrideEmit(child, placed, verdict, context, state, ref routeGroupType);
    }

    private static byte[]? EncodeNewEmit(
        RecordPlan child,
        PlacedReference placed,
        PlacedRefDecision verdict,
        CellChildEncodeContext context,
        ref int routeGroupType)
    {
        if (verdict.AuxStatCode is { } aux)
        {
            context.Stats?.IncrementDropReason(aux);
        }

        var originalBaseFormId = placed.BaseFormId;
        if (verdict.FinalBaseFormId != placed.BaseFormId)
        {
            placed = placed with { BaseFormId = verdict.FinalBaseFormId };
        }

        if (verdict.NewEnableParentFormId is { } enableParentFormId)
        {
            placed = placed with { EnableParentFormId = enableParentFormId };
        }

        // Every optional link was settled by PlacedRefLinkPlanner; the encoder reads those
        // decisions rather than re-validating, so no post-encode sanitation pass follows.
        var subs = RefrEncoder.EncodeNewPlacedReference(
            placed, new PlanReferenceLookup(child),
            context.ResolveBaseRecordType(originalBaseFormId, placed.BaseFormId));
        AccountPlannedLinkDrops(child, context);
        PlannedPlacedRefEncoder.RecordEnableParentOutcome(placed, subs.Subrecords, context);
        if (subs.Subrecords.Count == 0)
        {
            // The plan already counted this ref as an emit (PlanCellGates), so a
            // serialization failure here silently desynchronizes the cell gates from
            // reality. Name it rather than dropping without a trace.
            context.Stats?.IncrementSkipped(child.Type);
            context.Stats?.IncrementDropReason("refr.encoder-produced-no-subrecords");
            return null;
        }

        routeGroupType = verdict.TargetGroupType;
        var flags = context.Options.CompressRecords ? PlannedPlacedRefEncoder.CompressedFlag : 0u;

        // GRUP membership and the record-header flag must agree (xEdit format invariant).
        flags |= verdict.TargetGroupType switch
        {
            8 => PlannedPlacedRefEncoder.PersistentFlag,
            10 => PlannedPlacedRefEncoder.VisibleWhenDistantFlag,
            _ => 0u
        };
        if (verdict.NewInitiallyDisabled ?? placed.IsInitiallyDisabled)
        {
            flags |= PlannedPlacedRefEncoder.InitiallyDisabledFlag;
        }

        return PluginRecordByteBuilder.BuildNewRecordBytes(
            child.Type, child.FormId, flags, subs.Subrecords);
    }

    /// <summary>
    ///     Counts the links the plan condemned on a new ref. The encoder silently honors the
    ///     decision (it has no stats sink), so the accounting happens here beside it.
    /// </summary>
    private static void AccountPlannedLinkDrops(RecordPlan child, CellChildEncodeContext context)
    {
        if (context.Stats is null)
        {
            return;
        }

        foreach (var reference in child.References)
        {
            if (reference.Action == ResolvedRefAction.DropSubrecord && reference.Reason is { } reason)
            {
                context.Stats.IncrementDropReason(reason);
            }
        }
    }

    private static byte[]? EncodeOverrideEmit(
        RecordPlan child,
        PlacedReference placed,
        PlacedRefDecision verdict,
        CellChildEncodeContext context,
        CellEncodeState state,
        ref int routeGroupType)
    {
        // The verdict guarantees a master record exists (DecideOverride drops when it
        // doesn't), so this is a planner-contract violation rather than a routine drop.
        if (!context.MasterByFormId.TryGetValue(child.FormId, out var masterRecord))
        {
            context.Stats?.IncrementSkipped(child.Type);
            context.Stats?.IncrementDropReason("refr.override-verdict-without-master");
            return null;
        }

        if (verdict.AuxStatCode is { } aux)
        {
            context.Stats?.IncrementDropReason(aux);
        }

        var encoded = RefrEncoder.EncodePlacedReference(placed);
        if (encoded.Subrecords.Count == 0)
        {
            context.Stats?.IncrementSkipped(child.Type);
            context.Stats?.IncrementDropReason("refr.encoder-produced-no-subrecords");
            return null;
        }

        encoded = encoded with
        {
            Subrecords = OverrideSubrecordSanitizer.Sanitize(encoded.Subrecords, context, child)
        };

        var masterForMerge = PlannedPlacedRefEncoder.StripXemi(masterRecord);
        var merge = RecordMergeEngine.Merge(masterForMerge, encoded, SubrecordMergePolicy.Default);
        var bytes = PluginRecordByteBuilder.BuildOverrideRecordBytes(
            masterRecord, merge.SubrecordBytes, context.Options);

        // Reparented actors carry the proto's authored enable-state instead of master's
        // Initially-Disabled bit (header flags dword at offset 8).
        if (verdict.OverrideInitiallyDisabled is { } initiallyDisabled)
        {
            var headerFlags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
            headerFlags = initiallyDisabled ? headerFlags | 0x00000800u : headerFlags & ~0x00000800u;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), headerFlags);
        }

        if (verdict.MarksMasterCovered)
        {
            state.CoveredMasterRefFormIds.Add(child.FormId);
        }

        routeGroupType = verdict.TargetGroupType;
        return bytes;
    }
}
