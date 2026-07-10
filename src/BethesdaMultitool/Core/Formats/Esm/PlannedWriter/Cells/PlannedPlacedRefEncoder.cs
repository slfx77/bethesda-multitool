using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Placed-ref (REFR/ACHR/ACRE) encoding for the planner cell writer. Ports the legacy
///     per-ref filter chain from <c>PluginBuilder</c>'s cell-children merge loop — runtime-state
///     skip, render-culling-marker drop, sparse-cell (PersistentOnly) preservation, parent-cell
///     mismatch guard, base remap-then-validate — plus the override-merge emission path
///     (<c>TryEncodeOverrideRef</c>: DMP live position merged onto master subrecords).
/// </summary>
internal static class PlannedPlacedRefEncoder
{
    private const uint CompressedFlag = 0x00040000u;

    /// <summary>
    ///     Encode one placed-ref child plan. Returns null when the ref is dropped by policy
    ///     (counted via drop reasons). For overrides, <paramref name="routeGroupType" /> is
    ///     set to the master child-GRUP type so the record lands in master's original bucket.
    /// </summary>
    public static byte[]? Encode(
        RecordPlan child,
        CellChildEncodeContext context,
        CellEncodeState state,
        ref int routeGroupType)
    {
        if (child.Model is not PlacedReference placed)
        {
            return null;
        }

        if (RuntimeStateRecordPolicy.IsRuntimeStateFormId(placed.FormId))
        {
            context.Stats?.IncrementSkipped(child.Type);
            context.Stats?.IncrementDropReason("runtime-state.skip-ref");
            return null;
        }

        if (child.Disposition == RecordDisposition.New && context.Options.DiagnosticSkipCellNewRefs)
        {
            context.Stats?.IncrementSkipped(child.Type);
            context.Stats?.IncrementDropReason("refr.diagnostic-skip-new");
            return null;
        }

        return child.Disposition switch
        {
            RecordDisposition.New => EncodeNew(child, placed, context, state, routeGroupType),
            RecordDisposition.Override => EncodeOverride(child, placed, context, state, ref routeGroupType),
            _ => null,
        };
    }

    /// <summary>Record-header flag: ref is persistent (must be set on Persistent Children GRUP members).</summary>
    private const uint PersistentFlag = 0x00000400u;

    /// <summary>Record-header flag: visible when distant (set on VWD Children GRUP members).</summary>
    private const uint VisibleWhenDistantFlag = 0x00008000u;

    /// <summary>Engine-only render-culling marker bases: MultiBound, Occlusion, Room, Portal. Collision (0x21) is kept.</summary>
    private static readonly HashSet<uint> RenderCullingEngineBases = [0x15u, 0x17u, 0x1Fu, 0x20u];

    private static byte[]? EncodeNew(
        RecordPlan child,
        PlacedReference placed,
        CellChildEncodeContext context,
        CellEncodeState state,
        int plannedGroupType)
    {
        // Authored-cell structural markers (room/portal/occlusion bases identified by
        // master EditorID) captured as runtime placements are never renderable content.
        if (PluginBuilder.IsRuntimeStructuralMarkerPlacement(placed, context.MasterByFormId, out _))
        {
            context.Stats?.IncrementSkipped(child.Type);
            context.Stats?.IncrementDropReason("refr.runtime-structural-marker");
            return null;
        }

        // Replaced interiors: proto portal/room/occlusion placements can't be emitted
        // coherently (XPOD/XPRM bounds aren't in the runtime struct), and their engine
        // bases (0x20 etc.) aren't in the master ESM so the EDID-based structural check
        // above can't see them. Drop them; the whole culling graph dies with them.
        if (state.DropRenderCullingMarkers
            && (RenderCullingEngineBases.Contains(placed.BaseFormId)
                || (context.MasterByFormId.TryGetValue(placed.BaseFormId, out var newBaseRecord)
                    && CellStructuralReferencePreserver.IsStructuralMarkerBase(newBaseRecord))))
        {
            context.Stats?.IncrementSkipped(child.Type);
            context.Stats?.IncrementDropReason("cell.render-culling-marker-dropped");
            return null;
        }

        // PersistentOnly captures are not authoritative for temporaries. Map markers are
        // exempt: the engine classifies them persistent at run time, but proto captures do
        // not always set the flag.
        if (state.Mode == CellMergeMode.PersistentOnly && !placed.IsPersistent && !placed.IsMapMarker)
        {
            context.Stats?.IncrementSkipped(child.Type);
            context.Stats?.IncrementDropReason("cell.persistent-only-nonpersistent-ref");
            return null;
        }

        // Remap-then-validate the NAME base, mirroring the legacy TryEncodeNewRef →
        // RemapEncodedFormIds → ValidateEncodedPlacedBase chain. Runtime-dynamic bases
        // (0xFFxxxxxx engine clones) and phantom master-range bases can never resolve at
        // load; the engine tears the cell down over them, so the ref is dropped rather
        // than emitted dangling.
        var baseFormId = placed.BaseFormId;
        if (context.Plan.SourceToEmittedFormId.TryGetValue(baseFormId, out var remappedBase))
        {
            baseFormId = remappedBase;
        }

        if (!IsValidPlacedBase(child.Type, baseFormId, context, out var knownBaseRecordType))
        {
            // Leveled-spawn recovery: a runtime-dynamic (0xFF) clone base never resolves, but the
            // actor's captured ExtraLeveledCreature pointer names the concrete NPC_/CREA the engine
            // spawned it from. Re-point the base to that (validated type-compatible, resolved
            // through SourceToEmittedFormId) so the ambient leveled crowd is emitted, not dropped.
            if (context.Options.RecoverLeveledSpawnActors
                && child.Type is "ACHR" or "ACRE"
                && placed.BaseFormId >= 0xFF000000u
                && TryRecoverLeveledSpawnBase(placed, child.Type, context, out var recoveredBase))
            {
                context.Stats?.IncrementDropReason("refr.leveled-recovered");
                baseFormId = recoveredBase;
            }
            // Last-chance EditorID-stem rescue (legacy parity): a proto base absent from
            // master may still stem-match a renamed master record of the same kind
            // (SCOLParkingLotChunk03 → SCOLParkingLotChunk03b). Only unknown bases are
            // rescue-eligible; type mismatches against a known master record are not.
            else if (knownBaseRecordType is null
                && baseFormId < 0xFF000000u
                && context.Options.EnableRefrBaseEditorIdRemap
                && context.MasterIndex is not null
                && !string.IsNullOrEmpty(placed.BaseEditorId))
            {
                var rescue = ReferenceBaseRemapper.TryRescueBaseByEditorIdStem(
                    child.Type, baseFormId, placed.BaseEditorId,
                    context.MasterIndex.StemToFormIdsByType, context.DmpBaseTypes);
                if (rescue.Outcome == ReferenceBaseRemapper.StemRescueOutcome.Rescued
                    && !context.Plan.SourceToEmittedFormId.ContainsKey(rescue.WinningFormId)
                    && IsValidPlacedBase(child.Type, rescue.WinningFormId, context, out _))
                {
                    context.Stats?.IncrementDropReason("refr.editorid-remap");
                    baseFormId = rescue.WinningFormId;
                }
                else
                {
                    if (rescue.Outcome is ReferenceBaseRemapper.StemRescueOutcome.Ambiguous
                        or ReferenceBaseRemapper.StemRescueOutcome.CrossTypeAmbiguous)
                    {
                        context.Stats?.IncrementDropReason("refr.editorid-remap-ambiguous");
                    }

                    context.Stats?.IncrementSkipped(child.Type);
                    context.Stats?.IncrementDropReason("refr.dangling-base");
                    return null;
                }
            }
            else
            {
                context.Stats?.IncrementSkipped(child.Type);
                context.Stats?.IncrementDropReason(knownBaseRecordType is null
                    ? "refr.dangling-base"
                    : "refr.base-type-mismatch");
                return null;
            }
        }

        if (baseFormId != placed.BaseFormId)
        {
            placed = placed with { BaseFormId = baseFormId };
        }

        var subs = RefrEncoder.EncodeNewPlacedReference(
            placed, context.ValidFormIds, context.Plan.SourceToEmittedFormId);
        if (subs.Subrecords.Count == 0)
        {
            return null;
        }

        var flags = context.Options.CompressRecords ? CompressedFlag : 0u;

        // GRUP membership and the record-header flag must agree: a ref inside a Persistent
        // (type-8) or VWD (type-10) Children GRUP without the matching header flag is a
        // format-invariant violation — xEdit refuses to even SAVE such a file ("needs to
        // have it's Persistent flag set to be contained in GRUP Cell Persistent Children").
        flags |= plannedGroupType switch
        {
            8 => PersistentFlag,
            10 => VisibleWhenDistantFlag,
            _ => 0u,
        };

        return PluginRecordByteBuilder.BuildNewRecordBytes(
            child.Type, child.FormId, flags, subs.Subrecords);
    }

    /// <summary>
    ///     Override emission: DMP runtime-mutable fields (position/rotation, lock state,
    ///     teleport data) merged onto the master record's subrecords. Master header and
    ///     FormID are kept. Ports legacy <c>TryEncodeOverrideRef</c>.
    /// </summary>
    private static byte[]? EncodeOverride(
        RecordPlan child,
        PlacedReference placed,
        CellChildEncodeContext context,
        CellEncodeState state,
        ref int routeGroupType)
    {
        if (context.MasterIndex is null
            || !context.MasterByFormId.TryGetValue(child.FormId, out var masterRecord))
        {
            return null; // No master record to merge onto — nothing safe to emit.
        }

        // Master TEMPORARY actors must never be re-emitted by an ESM-flagged plugin —
        // not as overrides, carries, or tombstones. Under ESM semantics the actor's form
        // pre-exists before its cell attaches, so the attach-time temp stream skips it
        // (TESObjectCELL child loader: LookupFormByID != 0 => skip, decompile-verified),
        // InitItem never runs, the XLKR linked-ref slot keeps its raw FormID, and the
        // actor's AI package dereferences it mid-attach (the Gomorrah01 patrol AV at
        // FalloutNV+0x8D6F3A, reproduced across v91-v94). Marking the ref covered makes
        // carry-forward and tombstone synthesis leave it alone, so the master's own copy
        // streams vanilla-style at attach. Persistent actor overrides stay — that is
        // normal shipped-DLC practice and loads through the startup path.
        if (child.Type is "ACHR" or "ACRE"
            && context.MasterIndex.ChildLocations.TryGetValue(child.FormId, out var actorLocation)
            && actorLocation.GroupType == 9)
        {
            state.CoveredMasterRefFormIds.Add(child.FormId);
            context.Stats?.IncrementSkipped(child.Type);
            context.Stats?.IncrementDropReason("actor.temp-override-suppressed-esm");
            return null;
        }

        // Replaced interiors: don't re-emit captured render-culling markers (their final-
        // game bounds mis-cull the proto layout), and don't mark them covered — under the
        // ownership rule omission removes master's copy too, disabling portal culling.
        if (state.DropRenderCullingMarkers
            && CellStructuralReferencePreserver.IsRenderCullingMarker(masterRecord, context.MasterByFormId))
        {
            context.Stats?.IncrementSkipped(child.Type);
            context.Stats?.IncrementDropReason("cell.render-culling-marker-dropped");
            return null;
        }

        // Sparse (PersistentOnly) captures are not authoritative for existing master refs;
        // the master copy is carried verbatim by the preservation pass instead. Map markers
        // whose name/type/position genuinely differ are the one exception.
        if (state.Mode == CellMergeMode.PersistentOnly
            && !(placed.IsMapMarker && PluginBuilder.MapMarkerDiffersFromMaster(placed, masterRecord)))
        {
            context.Stats?.IncrementSkipped(child.Type);
            context.Stats?.IncrementDropReason("refr.sparse-cell-master-preserved");
            return null;
        }

        // Master is the source of truth for the parent-cell relationship: the runtime
        // occasionally attaches persistent refs to the player's grid cell rather than the
        // master parent, and emitting under the wrong parent produces duplicate parent
        // assignments that crash the renderer.
        if (context.MasterIndex.RefToCell.TryGetValue(child.FormId, out var masterParentCell)
            && masterParentCell != 0
            && masterParentCell != state.CellFormId
            && !placed.IsMapMarker)
        {
            context.Stats?.IncrementSkipped(child.Type);
            context.Stats?.IncrementDropReason("refr.parent-cell-mismatch");
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
            Subrecords = SanitizeOverrideSubrecords(encoded.Subrecords, context)
        };

        // XEMI (emittance link) is eager-resolved when the plugin is ESM-flagged and was a
        // known fault source in the ESM-era builds; the carry-forward path strips it via
        // ReconstructRecordBytes, so the merge path must too or overrides re-import it.
        var masterForMerge = masterRecord.Subrecords.Any(s => s.Signature == "XEMI")
            ? masterRecord with
            {
                Subrecords = masterRecord.Subrecords.Where(s => s.Signature != "XEMI").ToList()
            }
            : masterRecord;

        var merge = RecordMergeEngine.Merge(masterForMerge, encoded, SubrecordMergePolicy.Default);
        var bytes = PluginRecordByteBuilder.BuildOverrideRecordBytes(
            masterRecord, merge.SubrecordBytes, context.Options);

        state.CoveredMasterRefFormIds.Add(child.FormId);
        if (context.MasterIndex.ChildLocations.TryGetValue(child.FormId, out var location)
            && location.GroupType is 8 or 9 or 10)
        {
            routeGroupType = location.GroupType;
        }

        // GRUP membership must agree with the record's persistent flag — xEdit refuses to
        // save a persistent-flagged record in a Temporary Children GRUP ("can not have
        // it's Persistent flag set..."). The header flags come from the master clone, so
        // when the flag says persistent, the flag wins over any bucket lookup miss
        // (observed: master's NelsonBarracks01 g8 pair routed to g9 by a ChildLocations
        // edge case).
        if (bytes.Length >= 12
            && (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)) & PersistentFlag) != 0)
        {
            routeGroupType = 8;
        }

        return bytes;
    }

    /// <summary>
    ///     Remap-then-validate the FormID-bearing override subrecords (XTEL target, XESP
    ///     parent, XLKR links, XOWN owner, XEZN zone) against master ∪ emitted. Dangling
    ///     targets drop the subrecord — the legacy <c>RemapEncodedFormIds</c> behavior; the
    ///     engine would log "Unable to find linked reference" and strip the data anyway.
    /// </summary>
    private static List<EncodedSubrecord> SanitizeOverrideSubrecords(
        IReadOnlyList<EncodedSubrecord> subrecords,
        CellChildEncodeContext context)
    {
        var result = new List<EncodedSubrecord>(subrecords.Count);
        foreach (var sub in subrecords)
        {
            if (sub.Signature is not ("NAME" or "XTEL" or "XESP" or "XLKR" or "XOWN" or "XEZN" or "XNDP")
                || sub.Bytes.Length < 4)
            {
                result.Add(sub);
                continue;
            }

            // XLKR can carry one or two FormIDs; the other signatures carry the target
            // first. Remap each through the plan and require every one to resolve.
            var uintCount = sub.Signature == "XLKR" ? sub.Bytes.Length / 4 : 1;
            var bytes = sub.Bytes;
            var allResolvable = true;
            for (var u = 0; u < uintCount; u++)
            {
                var target = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(u * 4, 4));
                if (context.Plan.SourceToEmittedFormId.TryGetValue(target, out var remapped))
                {
                    if (ReferenceEquals(bytes, sub.Bytes))
                    {
                        bytes = sub.Bytes.ToArray();
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(u * 4, 4), remapped);
                    continue;
                }

                if (!IsResolvableFormId(target, context))
                {
                    allResolvable = false;
                    break;
                }
            }

            if (allResolvable)
            {
                result.Add(ReferenceEquals(bytes, sub.Bytes) ? sub : sub with { Bytes = bytes });
                continue;
            }

            // NAME needs special care: a master ACHR captured live can point its base at
            // the 0xFF runtime leveled-actor clone. Dropping the subrecord (instead of the
            // whole override, as legacy did) lets the merge keep master's own NAME.
            context.Stats?.IncrementDropReason(sub.Signature == "NAME"
                ? "refr.override-name-preserved-master"
                : "refr.override-subrecord-dangling");
        }

        return result;
    }

    private static bool IsResolvableFormId(uint formId, CellChildEncodeContext context)
    {
        return formId == 0
               || context.ValidFormIds.Contains(formId)
               || formId < 0x800u
               || RuntimeStateRecordPolicy.EngineFormIds.Contains(formId);
    }

    /// <summary>
    ///     Runtime-dynamic (0xFF) leveled-spawn recovery: the actor's captured
    ///     <see cref="PlacedReference.LeveledCreatureOriginalBaseFormId" /> (preferred), else
    ///     <see cref="PlacedReference.LeveledCreatureTemplateFormId" />, names the concrete base
    ///     the engine spawned it from. Accept the first candidate that resolves (through
    ///     <c>SourceToEmittedFormId</c>) to a type-compatible NPC_/CREA base.
    /// </summary>
    private static bool TryRecoverLeveledSpawnBase(
        PlacedReference placed,
        string placedRecordType,
        CellChildEncodeContext context,
        out uint recoveredFormId)
    {
        return TryLeveledCandidate(placed.LeveledCreatureOriginalBaseFormId, placedRecordType, context, out recoveredFormId)
               || TryLeveledCandidate(placed.LeveledCreatureTemplateFormId, placedRecordType, context, out recoveredFormId);
    }

    private static bool TryLeveledCandidate(
        uint? candidate,
        string placedRecordType,
        CellChildEncodeContext context,
        out uint recoveredFormId)
    {
        recoveredFormId = 0;
        if (candidate is not { } source || source == 0 || source >= 0xFF000000u)
        {
            return false;
        }

        var resolved = context.Plan.SourceToEmittedFormId.TryGetValue(source, out var emitted) ? emitted : source;

        // IsValidPlacedBase enforces CanPlacedRecordUseBaseType for master bases, so a master
        // LVLN/LVLC candidate is rejected here and we fall through to the other pointer.
        if (!IsValidPlacedBase(placedRecordType, resolved, context, out var knownBaseRecordType))
        {
            return false;
        }

        // Emitted (captured-proto) bases pass IsValidPlacedBase without a type check — verify the
        // captured base type is actor-compatible so we never re-point an actor at a non-actor base.
        if (knownBaseRecordType is null
            && !(context.DmpBaseTypes is { } dmpTypes
                 && dmpTypes.TryGetValue(source, out var dmpType)
                 && ReferenceBaseRemapper.CanPlacedRecordUseBaseType(placedRecordType, dmpType)))
        {
            return false;
        }

        recoveredFormId = resolved;
        return true;
    }

    /// <summary>
    ///     A placed ref's base must resolve at load: a master record of a compatible base
    ///     type, a FormID this plugin emits, or an engine-hardcoded form (reserved range
    ///     below 0x800 plus the runtime-state singletons). Everything else — 0xFF-range
    ///     runtime clones, proto FormIDs the final master cut — dangles.
    /// </summary>
    private static bool IsValidPlacedBase(
        string placedRecordType,
        uint baseFormId,
        CellChildEncodeContext context,
        out string? knownBaseRecordType)
    {
        knownBaseRecordType = null;

        if (baseFormId is 0 or 0xFFFFFFFFu)
        {
            return true; // Null/sentinel bases pass through, same as legacy validation.
        }

        if (context.MasterByFormId.TryGetValue(baseFormId, out var masterRecord))
        {
            knownBaseRecordType = masterRecord.Header.Signature;
            return ReferenceBaseRemapper.CanPlacedRecordUseBaseType(
                placedRecordType, masterRecord.Header.Signature);
        }

        if (context.Plan.EmittedFormIds.Contains(baseFormId))
        {
            return true;
        }

        return baseFormId < 0x800u
               || RuntimeStateRecordPolicy.EngineFormIds.Contains(baseFormId);
    }
}
