using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
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

        var subs = RefrEncoder.EncodeNewPlacedReference(
            placed, context.ValidFormIds, context.Plan.SourceToEmittedFormId,
            context.ResolveBaseRecordType(originalBaseFormId, placed.BaseFormId));
        subs = subs with
        {
            Subrecords = PlacedRefTeleportSanitizer.Sanitize(subs.Subrecords, context),
        };
        PlannedPlacedRefEncoder.RecordEnableParentOutcome(placed, subs.Subrecords, context);
        if (subs.Subrecords.Count == 0)
        {
            return null;
        }

        routeGroupType = verdict.TargetGroupType;
        var flags = context.Options.CompressRecords ? PlannedPlacedRefEncoder.CompressedFlag : 0u;

        // GRUP membership and the record-header flag must agree (xEdit format invariant).
        flags |= verdict.TargetGroupType switch
        {
            8 => PlannedPlacedRefEncoder.PersistentFlag,
            10 => PlannedPlacedRefEncoder.VisibleWhenDistantFlag,
            _ => 0u,
        };
        if (verdict.NewInitiallyDisabled ?? placed.IsInitiallyDisabled)
        {
            flags |= PlannedPlacedRefEncoder.InitiallyDisabledFlag;
        }

        return PluginRecordByteBuilder.BuildNewRecordBytes(
            child.Type, child.FormId, flags, subs.Subrecords);
    }

    private static byte[]? EncodeOverrideEmit(
        RecordPlan child,
        PlacedReference placed,
        PlacedRefDecision verdict,
        CellChildEncodeContext context,
        CellEncodeState state,
        ref int routeGroupType)
    {
        // The verdict guarantees a master record exists; the guard is defensive.
        if (!context.MasterByFormId.TryGetValue(child.FormId, out var masterRecord))
        {
            return null;
        }

        var encoded = RefrEncoder.EncodePlacedReference(placed);
        if (encoded.Subrecords.Count == 0)
        {
            context.Stats?.IncrementSkipped(child.Type);
            return null;
        }

        encoded = encoded with
        {
            Subrecords = PlannedPlacedRefEncoder.SanitizeOverrideSubrecords(encoded.Subrecords, context)
        };

        var masterForMerge = PlannedPlacedRefEncoder.StripXemi(masterRecord);
        var merge = RecordMergeEngine.Merge(masterForMerge, encoded, SubrecordMergePolicy.Default);
        var bytes = PluginRecordByteBuilder.BuildOverrideRecordBytes(
            masterRecord, merge.SubrecordBytes, context.Options);

        // Reparented actors carry the proto's authored enable-state instead of master's
        // Initially-Disabled bit (header flags dword at offset 8).
        if (verdict.OverrideInitiallyDisabled is { } initiallyDisabled)
        {
            var headerFlags = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
            headerFlags = initiallyDisabled ? headerFlags | 0x00000800u : headerFlags & ~0x00000800u;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), headerFlags);
        }

        if (verdict.MarksMasterCovered)
        {
            state.CoveredMasterRefFormIds.Add(child.FormId);
        }

        routeGroupType = verdict.TargetGroupType;
        return bytes;
    }
}
