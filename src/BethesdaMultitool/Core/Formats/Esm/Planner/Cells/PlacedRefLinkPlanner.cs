using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Settles every outgoing FormID link on a placed-ref child (NAME base, XTEL teleport
///     target, XESP enable parent, XLKR link + keyword, XOWN owner, XEZN zone, XNDP navmesh
///     door, XRDO radio anchor) into <see cref="RecordPlan.References" /> before any byte is
///     encoded. The writer looks each one up by <see cref="ResolvedRef.FieldPath" /> and
///     obeys it — it no longer validates FormIDs, consults remap tables, or re-derives door
///     validity while serializing.
///     <para>
///     The two emission paths carry deliberately different resolution policies and this
///     planner reproduces both exactly rather than unifying them:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>New</b> refs keep the encoder's old remap-then-validate rule (still visible
///             in <c>PackEncoder</c>, which owns PACK's copy): a remap only wins when the
///             remapped value is itself live, and an unresolvable link drops its subrecord.
///             There is no sub-0x800 escape.
///         </item>
///         <item>
///             <b>Override</b> refs mirror the writer's old <c>OverrideSubrecordSanitizer</c>:
///             a remap is taken unconditionally, and engine/low FormIDs (&lt; 0x800 and the
///             hardcoded engine set) count as resolvable even when absent from the plan.
///         </item>
///     </list>
///     <para>
///     Runs after <see cref="CellChildVerdictPlanner" /> (a link may point at a ref whose
///     emit verdict decides its liveness) and after <see cref="NavmDoorLinkPlanner" /> (XTEL
///     validity is a door-set membership test).
///     </para>
/// </summary>
internal static class PlacedRefLinkPlanner
{
    /// <summary>Signatures the override sanitation walk resolves; others pass through.</summary>
    private static readonly HashSet<string> OverrideLinkSignatures =
        new(StringComparer.Ordinal) { "NAME", "XTEL", "XESP", "XLKR", "XOWN", "XEZN", "XNDP" };

    private const string DanglingReason = "refr.override-subrecord-dangling";
    private const string NamePreservedReason = "refr.override-name-preserved-master";
    private const string XtelNotDoorReason = "refr.xtel-target-not-door";

    /// <summary>
    ///     New-path dangling links get their own code. The writer previously recorded these
    ///     as encoder warnings only, so folding them into the override counter would make an
    ///     unrelated population jump; this keeps the two paths separately countable.
    /// </summary>
    private const string NewDanglingReason = "refr.new-link-dangling";

    public static ImmutableDictionary<uint, CellPlan> Apply(
        ImmutableDictionary<uint, CellPlan> cells,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        IReadOnlyDictionary<uint, uint> sourceToEmitted,
        IReadOnlySet<uint> emittedFormIds,
        IReadOnlySet<uint> validDoorRefFormIds)
    {
        // Master ∪ emitted — the identical validity set the cell writer builds.
        var valid = new HashSet<uint>(emittedFormIds);
        foreach (var formId in masterByFormId.Keys)
        {
            valid.Add(formId);
        }

        var context = new LinkContext(masterByFormId, sourceToEmitted, valid, validDoorRefFormIds);
        var builder = cells.ToBuilder();
        foreach (var (cellFormId, cell) in cells)
        {
            var persistent = ResolveBucket(cell.PersistentChildren, cell, context);
            var vwd = ResolveBucket(cell.VwdChildren, cell, context);
            var temporary = ResolveBucket(cell.TemporaryChildren, cell, context);
            if (persistent is null && vwd is null && temporary is null)
            {
                continue;
            }

            builder[cellFormId] = cell with
            {
                PersistentChildren = persistent ?? cell.PersistentChildren,
                VwdChildren = vwd ?? cell.VwdChildren,
                TemporaryChildren = temporary ?? cell.TemporaryChildren,
            };
        }

        return builder.ToImmutable();
    }

    /// <summary>Returns null when no child in the bucket carries a resolvable link.</summary>
    private static ImmutableArray<RecordPlan>? ResolveBucket(
        ImmutableArray<RecordPlan> children,
        CellPlan cell,
        LinkContext context)
    {
        ImmutableArray<RecordPlan>.Builder? builder = null;
        for (var i = 0; i < children.Length; i++)
        {
            var child = children[i];
            if (child.Model is not PlacedReference placed)
            {
                continue;
            }

            var refs = child.Disposition == RecordDisposition.New
                ? BuildNewRefs(child, placed, cell, context)
                : BuildOverrideRefs(child, placed, context);
            if (refs.IsEmpty)
            {
                continue;
            }

            builder ??= children.ToBuilder();
            builder[i] = child with { References = refs };
        }

        return builder?.ToImmutable();
    }

    /// <summary>
    ///     Override path: NAME comes from the captured base, every other link from the
    ///     captured structural subrecord bytes. Paths are occurrence-indexed in the exact
    ///     order <c>RefrEncoder.EncodePlacedReference</c> emits them so the writer's walk
    ///     lines up without re-deriving anything.
    /// </summary>
    private static ImmutableArray<ResolvedRef> BuildOverrideRefs(
        RecordPlan child,
        PlacedReference placed,
        LinkContext context)
    {
        // Master's signature governs the NAME type check: a proto ACRE captured onto a
        // master ACHR FormID must not smuggle a base the emitted type cannot initialize.
        var emittedType = context.MasterByFormId.TryGetValue(child.FormId, out var master)
            ? master.Header.Signature
            : child.Type;

        var refs = ImmutableArray.CreateBuilder<ResolvedRef>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);

        if (placed.BaseFormId != 0)
        {
            var name = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(name, placed.BaseFormId);
            AppendSubrecordRefs(refs, occurrences, "NAME", name, emittedType, context);
        }

        if (placed.StructuralData is { HasAny: true } structural)
        {
            foreach (var subrecord in structural.Subrecords)
            {
                if (!OverrideLinkSignatures.Contains(subrecord.Signature)
                    || subrecord.Data.Length < 4)
                {
                    continue;
                }

                AppendSubrecordRefs(
                    refs, occurrences, subrecord.Signature, subrecord.Data, emittedType, context);
            }
        }

        return refs.ToImmutable();
    }

    /// <summary>
    ///     Resolves every FormID slot in one override subrecord. XLKR carries one or two;
    ///     the rest carry their target first. A single unresolvable slot condemns the whole
    ///     subrecord, so each slot records the shared verdict.
    /// </summary>
    private static void AppendSubrecordRefs(
        ImmutableArray<ResolvedRef>.Builder refs,
        Dictionary<string, int> occurrences,
        string signature,
        byte[] data,
        string emittedType,
        LinkContext context)
    {
        occurrences.TryGetValue(signature, out var occurrence);
        occurrences[signature] = occurrence + 1;

        var slotCount = signature == "XLKR" ? data.Length / 4 : 1;
        var slots = new (uint Original, uint Final)[slotCount];
        string? dropReason = null;

        for (var slot = 0; slot < slotCount && dropReason is null; slot++)
        {
            var target = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(slot * 4, 4));
            var final = target;
            if (context.SourceToEmitted.TryGetValue(target, out var remapped))
            {
                // Unconditional: the override path has always trusted its own remap table
                // without re-validating the destination.
                final = remapped;
            }
            else if (!context.IsResolvable(target))
            {
                dropReason = signature == "NAME" ? NamePreservedReason : DanglingReason;
                break;
            }

            if (signature == "XTEL" && !context.ValidDoorRefFormIds.Contains(final))
            {
                dropReason = XtelNotDoorReason;
                break;
            }

            if (signature == "NAME"
                && context.MasterByFormId.TryGetValue(final, out var baseRecord)
                && !ReferenceBaseRemapper.CanPlacedRecordUseBaseType(
                    emittedType, baseRecord.Header.Signature))
            {
                dropReason = NamePreservedReason;
                break;
            }

            slots[slot] = (target, final);
        }

        for (var slot = 0; slot < slotCount; slot++)
        {
            var path = FieldPath.IndexedMember(signature, occurrence, SlotName(slot));
            if (dropReason is not null)
            {
                refs.Add(new ResolvedRef
                {
                    FieldPath = path,
                    OriginalFormId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(slot * 4, 4)),
                    Action = ResolvedRefAction.DropSubrecord,
                    Reason = dropReason,
                });
                continue;
            }

            refs.Add(new ResolvedRef
            {
                FieldPath = path,
                OriginalFormId = slots[slot].Original,
                Action = ResolvedRefAction.Resolved,
                FinalFormId = slots[slot].Final,
            });
        }
    }

    /// <summary>
    ///     New path: links come from the model's typed optional fields, each emitted once, so
    ///     paths are plain subrecord names (plus two named members for the compound fields).
    ///     The XESP parent honors a verdict-supplied replacement, and XTEL additionally has to
    ///     clear the door-type gate the writer used to apply as a separate post-encode pass.
    /// </summary>
    private static ImmutableArray<ResolvedRef> BuildNewRefs(
        RecordPlan child,
        PlacedReference placed,
        CellPlan cell,
        LinkContext context)
    {
        var refs = ImmutableArray.CreateBuilder<ResolvedRef>();
        cell.RefDecisions.TryGetValue(child.FormId, out var verdict);

        AddOptional(refs, "XEZN", placed.EncounterZoneFormId, context);
        AddOptional(refs, "XOWN", placed.OwnerFormId, context);
        AddOptional(refs, FieldPath.Subrecord("XLKR"), placed.LinkedRefFormId, context);
        AddOptional(
            refs, FieldPath.Member("XLKR", "Keyword"), placed.LinkedRefKeywordFormId, context);

        // The verdict can re-point an enable parent (reparented actors); when it does, that
        // value is the one that must resolve.
        var enableParent = verdict?.NewEnableParentFormId ?? placed.EnableParentFormId;
        AddOptional(refs, "XESP", enableParent, context);

        if (placed.DestinationDoorFormId is { } door)
        {
            // A teleport can fail two ways: the target does not resolve at all, or it resolves
            // to something that is not a door. Both drop XTEL, but they are different defects.
            var resolved = context.ResolveNew(door);
            var isDoor = resolved is { } target && context.ValidDoorRefFormIds.Contains(target);
            string? reason = null;
            if (!isDoor)
            {
                reason = resolved is null ? NewDanglingReason : XtelNotDoorReason;
            }

            refs.Add(new ResolvedRef
            {
                FieldPath = FieldPath.Subrecord("XTEL"),
                OriginalFormId = door,
                Action = isDoor ? ResolvedRefAction.Resolved : ResolvedRefAction.DropSubrecord,
                FinalFormId = isDoor ? resolved : null,
                Reason = reason,
            });
        }

        // XRDO never drops its subrecord — a radio without one defaults to an anchorless
        // Radius broadcast — so a dangling anchor resolves to the retail-normal null.
        if (placed.RadioData?.PositionRefFormId is { } anchor && anchor != 0)
        {
            var resolved = context.ResolveNew(anchor);
            refs.Add(new ResolvedRef
            {
                FieldPath = FieldPath.Member("XRDO", "PositionRef"),
                OriginalFormId = anchor,
                Action = resolved is null ? ResolvedRefAction.NullRef : ResolvedRefAction.Resolved,
                FinalFormId = resolved,
                Reason = resolved is null ? "refr.xrdo-anchor-dangling" : null,
            });
        }

        return refs.ToImmutable();
    }

    private static void AddOptional(
        ImmutableArray<ResolvedRef>.Builder refs,
        string fieldPath,
        uint? source,
        LinkContext context)
    {
        if (source is not { } formId)
        {
            return;
        }

        var resolved = context.ResolveNew(formId);
        refs.Add(new ResolvedRef
        {
            FieldPath = fieldPath,
            OriginalFormId = formId,
            Action = resolved is null ? ResolvedRefAction.DropSubrecord : ResolvedRefAction.Resolved,
            FinalFormId = resolved,
            Reason = resolved is null ? NewDanglingReason : null,
        });
    }

    private static string SlotName(int slot) => slot == 0 ? "Slot0" : "Slot1";

    private sealed class LinkContext(
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        IReadOnlyDictionary<uint, uint> sourceToEmitted,
        HashSet<uint> valid,
        IReadOnlySet<uint> validDoorRefFormIds)
    {
        public IReadOnlyDictionary<uint, ParsedMainRecord> MasterByFormId { get; } = masterByFormId;
        public IReadOnlyDictionary<uint, uint> SourceToEmitted { get; } = sourceToEmitted;
        public IReadOnlySet<uint> ValidDoorRefFormIds { get; } = validDoorRefFormIds;

        /// <summary>Override-path resolvability, including the engine/low-FormID escapes.</summary>
        public bool IsResolvable(uint formId) =>
            formId == 0
            || valid.Contains(formId)
            || formId < 0x800u
            || RuntimeStateRecordPolicy.EngineFormIds.Contains(formId);

        /// <summary>
        ///     New-path resolution: remap wins only when the remapped value is live. Null
        ///     means the caller should drop (or null) its subrecord.
        /// </summary>
        public uint? ResolveNew(uint formId)
        {
            if (formId == 0)
            {
                return formId;
            }

            if (SourceToEmitted.TryGetValue(formId, out var remapped)
                && remapped != formId
                && valid.Contains(remapped))
            {
                return remapped;
            }

            return valid.Contains(formId) ? formId : null;
        }
    }
}
