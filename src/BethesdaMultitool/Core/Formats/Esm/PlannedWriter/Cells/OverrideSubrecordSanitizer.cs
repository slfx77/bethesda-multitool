using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Applies the plan's per-link decisions to an override ref's encoded subrecords: writes
///     each resolved FormID into its slot and omits any subrecord the plan condemned.
///     <para>
///     No decision is made here. <c>PlacedRefLinkPlanner</c> settled remapping, dangling
///     targets, XTEL door validity and the NAME-vs-master-type rule at plan time; this walk
///     only has to line its occurrence indices up with the planner's — which it does because
///     both traverse NAME-then-structural in emission order, and both skip sub-4-byte
///     subrecords without consuming an index.
///     </para>
///     <para>
///     Dropping the individual subrecord (rather than the whole override, as the retired
///     legacy path did) is what lets the merge fall back to master's own value — the point of
///     the NAME rule, whose stat code is named for that outcome.
///     </para>
/// </summary>
internal static class OverrideSubrecordSanitizer
{
    private static readonly HashSet<string> LinkSignatures =
        new(StringComparer.Ordinal) { "NAME", "XTEL", "XESP", "XLKR", "XOWN", "XEZN", "XNDP" };

    internal static List<EncodedSubrecord> Sanitize(
        IReadOnlyList<EncodedSubrecord> subrecords,
        CellChildEncodeContext context,
        RecordPlan child)
    {
        var lookup = new PlanReferenceLookup(child);
        var result = new List<EncodedSubrecord>(subrecords.Count);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var sub in subrecords)
        {
            if (!LinkSignatures.Contains(sub.Signature) || sub.Bytes.Length < 4)
            {
                result.Add(sub);
                continue;
            }

            occurrences.TryGetValue(sub.Signature, out var occurrence);
            occurrences[sub.Signature] = occurrence + 1;

            var slotCount = sub.Signature == "XLKR" ? sub.Bytes.Length / 4 : 1;
            var bytes = sub.Bytes;
            string? dropReason = null;
            uint? sourceXespParent = null;

            for (var slot = 0; slot < slotCount; slot++)
            {
                var path = FieldPath.IndexedMember(
                    sub.Signature, occurrence, slot == 0 ? "Slot0" : "Slot1");
                if (!lookup.TryGet(path, out var resolved))
                {
                    // The planner walks the same stream, so a missing path means writer and
                    // planner disagree about the record's shape — surface it, don't guess.
                    throw new KeyNotFoundException(
                        $"No planned link decision for {sub.Signature} slot {slot} on " +
                        $"0x{child.FormId:X8} ({path}). Planner and writer disagree on the " +
                        "override subrecord stream.");
                }

                if (slot == 0 && sub.Signature == "XESP")
                {
                    sourceXespParent = resolved.OriginalFormId;
                }

                if (resolved.Action == ResolvedRefAction.DropSubrecord)
                {
                    dropReason = resolved.Reason;
                    break;
                }

                if (resolved.FinalFormId is not { } final
                    || final == BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(slot * 4, 4)))
                {
                    continue;
                }

                if (ReferenceEquals(bytes, sub.Bytes))
                {
                    bytes = sub.Bytes.ToArray();
                }

                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(slot * 4, 4), final);
            }

            if (sourceXespParent is { } xespParent)
            {
                PlannedPlacedRefEncoder.RecordEnableParentOutcome(
                    xespParent, dropReason is null, context);
            }

            if (dropReason is null)
            {
                result.Add(ReferenceEquals(bytes, sub.Bytes) ? sub : sub with { Bytes = bytes });
                continue;
            }

            context.Stats?.IncrementDropReason(dropReason);
        }

        return result;
    }
}
