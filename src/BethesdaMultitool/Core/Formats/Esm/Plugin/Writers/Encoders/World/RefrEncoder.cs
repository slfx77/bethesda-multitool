using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a placed-reference record (REFR) as PC-format subrecord bytes from a parsed
///     <see cref="PlacedReference" />. Both the override path and the new-record path emit
///     DATA carrying the DMP-captured X/Y/Z/RotX/RotY/RotZ — when a FormID matches the
///     master ESM, the DMP position takes precedence over vanilla's editor placement.
///     The override path emits NAME when available, XSCL, and DATA. The merge engine
///     retains XEZN / XLOC / XOWN / XLKR / XESP / XTEL / XCNT from the master ESM by
///     positional per-signature replacement.
///     The new-record path emits a complete subrecord stream (no master to merge with).
///     DATA layout: float X(0) + float Y(4) + float Z(8) + float RotX(12) + float RotY(16) + float RotZ(20).
///     DATA is emitted on overrides; vanilla NAVMs are preserved via the CellGrupBuilder
///     NAVM preservation path so the engine clamps refs to the floor at load time and
///     the captured live positions don't cause NPC sinking.
///     Map-marker REFRs (IsMapMarker=true) additionally emit XMRK + optional FNAM + optional
///     FULL (display label) + optional TNAM (marker type). On overrides the merge engine
///     overlays these onto the master record so a runtime-captured rename (MarkerName) wins.
/// </summary>
public sealed class RefrEncoder : IRecordEncoder
{
    // XLOC schema field names: Level, Key, Flags, NumTries, TimesUnlocked.
    private static readonly Dictionary<string, Func<PlacedReference, object?>> XlocExtractors = new(StringComparer.Ordinal)
    {
        ["Level"] = m => m.LockLevel ?? (byte)0,
        ["Key"] = m => m.LockKeyFormId ?? 0u,
        ["Flags"] = m => m.LockFlags ?? (byte)0,
        ["NumTries"] = m => m.LockNumTries ?? 0u,
        ["TimesUnlocked"] = m => m.LockTimesUnlocked ?? 0u,
    };

    // XRDO schema: Range + Type + StaticPercentage + PositionRef (16 bytes). A validated
    // PositionRef is patched onto the record via `with { }` before serialization.
    private static readonly Dictionary<string, Func<PlacedReference, object?>> XrdoExtractors = new(StringComparer.Ordinal)
    {
        ["Range"] = m => m.RadioData?.Radius ?? 0f,
        ["Type"] = m => m.RadioData?.RangeType ?? 0u,
        ["StaticPercentage"] = m => m.RadioData?.StaticPercentage ?? 0f,
        ["PositionRef"] = m => m.RadioData?.PositionRefFormId ?? 0u,
    };

    // XESP schema: ParentRef + Flags. The resolved FormID is patched onto the record via
    // `with { EnableParentFormId = resolved }` before serialization.
    private static readonly Dictionary<string, Func<PlacedReference, object?>> XespExtractors = new(StringComparer.Ordinal)
    {
        ["ParentRef"] = m => m.EnableParentFormId ?? 0u,
        ["Flags"] = m => m.EnableParentFlags ?? (byte)0,
    };

    // XTEL schema: DestinationDoor + PosX/Y/Z + RotX/Y/Z + Flags. Resolved door + PosRot
    // values are patched onto the record via `with { }` before serialization.
    private static readonly Dictionary<string, Func<PlacedReference, object?>> XtelExtractors = new(StringComparer.Ordinal)
    {
        ["DestinationDoor"] = m => m.DestinationDoorFormId ?? 0u,
        ["PosX"] = m => m.TeleportPosRot?.X ?? 0f,
        ["PosY"] = m => m.TeleportPosRot?.Y ?? 0f,
        ["PosZ"] = m => m.TeleportPosRot?.Z ?? 0f,
        ["RotX"] = m => m.TeleportPosRot?.RotX ?? 0f,
        ["RotY"] = m => m.TeleportPosRot?.RotY ?? 0f,
        ["RotZ"] = m => m.TeleportPosRot?.RotZ ?? 0f,
        ["Flags"] = m => m.TeleportFlags ?? (byte)0,
    };

    public string RecordType => "REFR";
    public Type ModelType => typeof(PlacedReference);

    /// <summary>Produces override subrecords for an existing REFR (a placed-object reference) from its runtime-mutable fields.</summary>
    public EncodedRecord Encode(object model)
    {
        var refr = (PlacedReference)model;
        return EncodePlacedReference(refr);
    }

    /// <summary>
    ///     Shared encoding logic for REFR/ACHR/ACRE override records. Emits NAME when the
    ///     DMP captured a base form, XSCL even when the value is the default, and DATA
    ///     carrying the DMP-captured transform. XSCL must be explicit so the merge engine
    ///     can clear a non-default master scale back to runtime's 1.0.
    /// </summary>
    internal static EncodedRecord EncodePlacedReference(PlacedReference placed)
    {
        var subs = new List<EncodedSubrecord>(3);

        if (placed.BaseFormId != 0)
        {
            subs.Add(EncodeFormIdSubrecord("NAME", placed.BaseFormId));
        }

        AppendStructuralSubrecords(subs, placed);

        // Map-marker subrecords (override path): omit FNAM so master's visibility flags
        // survive Pass 1 of RecordMergeEngine unchanged. XMRK signals "this is a map marker"
        // so the engine keeps the master record classified correctly; FULL/TNAM are emitted
        // only when the runtime captured a value, in which case they overlay master's bytes.
        AppendMapMarkerSubrecords(subs, placed, isNewRecord: false);

        subs.Add(new EncodedSubrecord("XSCL", BuildXsclSubrecord(placed.Scale)));
        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(placed)));

        return new EncodedRecord
        {
            Subrecords = subs,
            Warnings = []
        };
    }

    /// <summary>
    ///     Encoding logic for a new (non-override) placed-ref record. Emits a complete
    ///     subrecord stream in fopdoc-canonical order: NAME, XEZN, XLKR, XLOC, XOWN, XESP,
    ///     XTEL, XCNT, XSCL, DATA.
    /// </summary>
    /// <remarks>
    ///     <para>Emits XLOC (lock state), XESP (enable parent), XLKR (linked ref), and XTEL
    ///     (door teleport — emitted with FormID + zero PosRot/Flags because the model only
    ///     carries the destination FormID). XCNT is 4 bytes per the parser's
    ///     <c>Simple4Byte</c> schema.</para>
    ///     <para>Optional FormID-bearing subrecords (XEZN, XLKR keyword + ref, XOWN,
    ///     XESP, XTEL door) are validated against master ∪ emitted. If a dangling FormID can't
    ///     be remapped through the alias table the subrecord is SKIPPED (not emitted with a
    ///     dangling value — engine logs "Unable to find linked reference / enable state
    ///     parent" warnings when it sees one, and removes the data anyway). Skipping at emit
    ///     time avoids the cosmetic noise + keeps the record's data-size header consistent
    ///     with what the engine actually keeps after load.</para>
    /// </remarks>
    internal static EncodedRecord EncodeNewPlacedReference(
        PlacedReference placed,
        PlanReferenceLookup? links = null,
        string? baseRecordType = null)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        // NAME — base form FormID. Required for the engine to know what to spawn.
        subs.Add(EncodeFormIdSubrecord("NAME", placed.BaseFormId));

        // XRDO goes immediately after NAME: that is where all 19 radio references in retail
        // FalloutNV.esm carry it (13 as EDID NAME XRDO DATA, 6 as NAME XRDO DATA).
        if (placed.RadioData is not null)
        {
            subs.Add(BuildXrdoSubrecord(placed, links, warnings));
        }

        if (placed.EncounterZoneFormId.HasValue)
        {
            var resolved = Resolve(links, "XEZN", placed.EncounterZoneFormId.Value);
            if (resolved.HasValue)
            {
                subs.Add(EncodeFormIdSubrecord("XEZN", resolved.Value));
            }
            else
            {
                warnings.Add($"REFR 0x{placed.FormId:X8} XEZN encounter zone " +
                    $"0x{placed.EncounterZoneFormId.Value:X8} dangles — subrecord skipped.");
            }
        }

        if (placed.LinkedRefFormId.HasValue)
        {
            var xlkrSubrec = TryBuildXlkrSubrecord(placed, links, warnings);
            if (xlkrSubrec is not null)
            {
                subs.Add(xlkrSubrec);
            }
        }

        if (HasAnyLockState(placed))
        {
            subs.Add(BuildXlocSubrecord(placed));
        }

        if (placed.OwnerFormId.HasValue)
        {
            var resolved = Resolve(links, "XOWN", placed.OwnerFormId.Value);
            if (resolved.HasValue)
            {
                subs.Add(EncodeFormIdSubrecord("XOWN", resolved.Value));
            }
            else
            {
                warnings.Add($"REFR 0x{placed.FormId:X8} XOWN owner " +
                    $"0x{placed.OwnerFormId.Value:X8} dangles — subrecord skipped.");
            }
        }

        if (placed.EnableParentFormId.HasValue)
        {
            var resolved = Resolve(links, "XESP", placed.EnableParentFormId.Value);
            if (resolved.HasValue)
            {
                subs.Add(BuildXespSubrecord(placed, resolved.Value));
            }
            else
            {
                warnings.Add($"REFR 0x{placed.FormId:X8} XESP enable parent " +
                    $"0x{placed.EnableParentFormId.Value:X8} dangles — subrecord skipped.");
            }
        }

        if (placed.DestinationDoorFormId.HasValue)
        {
            // The plan's XTEL decision already carries the door-type gate, so a resolved
            // value here is a proven-live door and needs no second opinion post-encode.
            var resolved = Resolve(links, "XTEL", placed.DestinationDoorFormId.Value);
            if (resolved.HasValue)
            {
                subs.Add(BuildXtelSubrecord(placed, resolved.Value));
                if (placed.TeleportPosRot is null)
                {
                    warnings.Add(
                        $"REFR 0x{placed.FormId:X8} XTEL teleport position not available — emitted with zero PosRot.");
                }
            }
            else
            {
                warnings.Add($"REFR 0x{placed.FormId:X8} XTEL destination door " +
                    $"0x{placed.DestinationDoorFormId.Value:X8} is not a live door — subrecord skipped.");
            }
        }

        // XCNT is a stack count and is ONLY meaningful when the base is a carriable
        // inventory item (caps, ammo, a loose weapon pile). Bethesda overloads the same
        // ExtraCount slot at run time as a per-session instance counter on every OTHER
        // reference kind — placed actors AND world objects like containers/furniture. A
        // running-game capture therefore carries a large counter (it increments per
        // session), so emitting it back makes the engine append "(N)" to the hover name:
        // "Ulysses (20770)", "Trash Can (21022)". Gate on the BASE record type, not the
        // placed record type — an item base keeps its real count; anything else (CONT,
        // FURN, ACTI, DOOR, STAT, …) or an unknown/proto base drops it, and the engine
        // restores its own counter at load.
        if (placed.Count.HasValue && placed.RecordType == "REFR"
            && BaseTypeAllowsStackCount(baseRecordType))
        {
            subs.Add(BuildXcntSubrecord(placed.Count.Value));
        }

        // Map-marker subrecords (new-record path). Inserted between XCNT and XSCL so the
        // stream ends with the standard transform pair (XSCL, DATA) regardless of marker
        // status. Emits FNAM with a sensible default (0x03 = Visible | CanTravel per
        // docs/PDB_Runtime_Structures.md:715) so brand-new markers actually appear on the
        // Pip-Boy map.
        AppendMapMarkerSubrecords(subs, placed, isNewRecord: true);
        AppendStructuralSubrecords(subs, placed);

        if (Math.Abs(placed.Scale - 1.0f) > float.Epsilon)
        {
            subs.Add(new EncodedSubrecord("XSCL", BuildXsclSubrecord(placed.Scale)));
        }

        // DATA last — fopdoc convention.
        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(placed)));

        return new EncodedRecord
        {
            Subrecords = subs,
            Warnings = warnings
        };
    }

    private static void AppendStructuralSubrecords(List<EncodedSubrecord> subs, PlacedReference placed)
    {
        if (placed.StructuralData is not { HasAny: true })
        {
            return;
        }

        foreach (var subrecord in placed.StructuralData.Subrecords)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord(subrecord.Signature, subrecord.Data));
        }
    }

    private static bool HasAnyLockState(PlacedReference placed)
    {
        return placed.LockLevel.HasValue
               || placed.LockKeyFormId.HasValue
               || placed.LockFlags.HasValue
               || placed.LockNumTries.HasValue
               || placed.LockTimesUnlocked.HasValue;
    }

    /// <summary>
    ///     XLOC — 20 bytes fixed per the XLOC schema: Level + Padding(3) + Key + Flags +
    ///     Padding(3) + NumTries + TimesUnlocked.
    /// </summary>
    private static EncodedSubrecord BuildXlocSubrecord(PlacedReference placed)
    {
        return SchemaModelSerializer.SerializeSubrecord("XLOC", "", 20, placed, XlocExtractors);
    }

    /// <summary>
    ///     XRDO — 16 bytes: Range, Type, StaticPercentage, PositionRef.
    ///     <para>
    ///     Unlike the other FormID-bearing subrecords the whole subrecord is never dropped: XRDO
    ///     is what tells the engine how a radio broadcasts, and a radio reference without one gets
    ///     defaulted to Type 0 (Radius) with a NULL anchor — the exact state that makes the engine
    ///     log "Radio station exterior position ref … is not placed in an exterior". A dangling
    ///     Position Reference is zeroed instead, which is the retail-normal value (17 of 19 retail
    ///     radios have no anchor at all).
    ///     </para>
    /// </summary>
    private static EncodedSubrecord BuildXrdoSubrecord(
        PlacedReference placed,
        PlanReferenceLookup? links,
        List<string> warnings)
    {
        var radio = placed.RadioData!;
        var positionRef = radio.PositionRefFormId is { } captured && captured != 0
            ? Resolve(links, FieldPath.Member("XRDO", "PositionRef"), captured)
            : null;

        if (radio.PositionRefFormId is { } original && original != 0 && positionRef is null)
        {
            warnings.Add($"REFR 0x{placed.FormId:X8} XRDO position reference " +
                $"0x{original:X8} dangles — emitting NULL.");
        }

        // Only Radius broadcasts need an exterior anchor at all. Whether this reference's own cell
        // is exterior is not knowable here — PlacedReference carries no cell context — so the
        // unanchored-radius case is flagged without asserting it is wrong.
        if (radio.RequiresExteriorAnchor && positionRef is null)
        {
            warnings.Add($"REFR 0x{placed.FormId:X8} broadcasts by radius (XRDO type 0) with no " +
                "position reference — the engine anchors it to the reference itself and will log " +
                "if that lands in an interior.");
        }

        var mutated = placed with { RadioData = radio with { PositionRefFormId = positionRef ?? 0u } };
        return SchemaModelSerializer.SerializeSubrecord("XRDO", "", 16, mutated, XrdoExtractors);
    }

    /// <summary>
    ///     XESP — 8 bytes fixed per the XESP schema: ParentRef + Flags + Padding(3). The
    ///     resolved Parent FormID is patched onto the record via `with { }` so the static
    ///     extractor map sees it.
    /// </summary>
    private static EncodedSubrecord BuildXespSubrecord(PlacedReference placed, uint resolvedParentId)
    {
        var mutated = placed with { EnableParentFormId = resolvedParentId };
        return SchemaModelSerializer.SerializeSubrecord("XESP", "", 8, mutated, XespExtractors);
    }

    /// <summary>
    ///     XLKR — 4 bytes (just LinkedRef) or 8 bytes (Keyword + LinkedRef). Both fields are
    ///     individually validated; the subrecord is dropped only when the linked-ref FormID
    ///     itself dangles. When only the keyword is dangling we degrade to the 4-byte form
    ///     (engine treats no-keyword XLKR as a generic-keyword link).
    /// </summary>
    private static EncodedSubrecord? TryBuildXlkrSubrecord(
        PlacedReference placed,
        PlanReferenceLookup? links,
        List<string> warnings)
    {
        var resolvedRef = Resolve(links, "XLKR", placed.LinkedRefFormId!.Value);
        if (!resolvedRef.HasValue)
        {
            warnings.Add($"REFR 0x{placed.FormId:X8} XLKR linked ref " +
                $"0x{placed.LinkedRefFormId.Value:X8} dangles — subrecord skipped.");
            return null;
        }

        if (placed.LinkedRefKeywordFormId.HasValue)
        {
            var resolvedKeyword = Resolve(
                links, FieldPath.Member("XLKR", "Keyword"), placed.LinkedRefKeywordFormId.Value);
            if (resolvedKeyword.HasValue)
            {
                var xlkr8 = new byte[8];
                SubrecordEncoder.WriteFormId(xlkr8, 0, resolvedKeyword.Value);
                SubrecordEncoder.WriteFormId(xlkr8, 4, resolvedRef.Value);
                return new EncodedSubrecord("XLKR", xlkr8);
            }

            warnings.Add($"REFR 0x{placed.FormId:X8} XLKR keyword " +
                $"0x{placed.LinkedRefKeywordFormId.Value:X8} dangles — degraded to 4-byte XLKR " +
                "(linked ref only).");
        }

        var xlkr4 = new byte[4];
        SubrecordEncoder.WriteFormId(xlkr4, 0, resolvedRef.Value);
        return new EncodedSubrecord("XLKR", xlkr4);
    }

    /// <summary>
    ///     Reads the plan's decision for one optional placed-ref link. Returns the FormID to
    ///     emit, or null when the plan condemned the subrecord.
    ///     <para>
    ///     A null <paramref name="links" /> means no plan was supplied — the captured value is
    ///     emitted verbatim. Only shape-level callers (encoder unit tests) do that; every
    ///     production path routes through <c>PlacedRefLinkPlanner</c>, which owns the
    ///     remap-then-validate policy this method used to implement inline.
    ///     </para>
    /// </summary>
    private static uint? Resolve(PlanReferenceLookup? links, string fieldPath, uint captured)
    {
        if (links is null || captured == 0)
        {
            return captured;
        }

        if (!links.TryGet(fieldPath, out var resolved))
        {
            throw new KeyNotFoundException(
                $"No planned link decision for {fieldPath} on a new placed reference. " +
                "PlacedRefLinkPlanner and RefrEncoder disagree on the subrecord stream.");
        }

        // Null covers both "drop the subrecord" and "keep it but null the field": XRDO is the
        // only site that keeps its subrecord, and it already coalesces null to 0 (and warns).
        return resolved.Action == ResolvedRefAction.Resolved
            ? resolved.FinalFormId ?? captured
            : null;
    }

    /// <summary>
    ///     XTEL — 32 bytes fixed per the XTEL schema: DestinationDoor + 6 PosRot floats +
    ///     Flags + Padding(3). The resolved door FormID is patched onto the record via
    ///     `with { }` so the static extractor map sees it.
    /// </summary>
    private static EncodedSubrecord BuildXtelSubrecord(PlacedReference placed, uint resolvedDoorFormId)
    {
        var mutated = placed with { DestinationDoorFormId = resolvedDoorFormId };
        return SchemaModelSerializer.SerializeSubrecord("XTEL", "", 32, mutated, XtelExtractors);
    }

    /// <summary>
    ///     XCNT — 4 bytes per parser's Simple4Byte schema: int16 Count @0, padding @2-3.
    ///     Anything shorter is silently rejected by the parser's <c>DataLength &gt;= 4</c> guard.
    /// </summary>
    /// <summary>
    ///     Carriable inventory-item base types whose placed refs may legitimately carry an
    ///     XCNT stack count (a loose pile of caps/ammo, a stack of chips). Every other base —
    ///     containers, furniture, activators, doors, statics, actors — never has a real stack
    ///     count; a captured count there is the runtime session counter (hover "(N)" bug).
    ///     A null/unknown base type is treated as non-item: the reported bug hit master
    ///     containers (base resolves), and suppressing a rare unresolved proto item-pile
    ///     count is strictly safer than re-introducing the counter.
    /// </summary>
    private static readonly HashSet<string> StackCountableBaseTypes = new(StringComparer.Ordinal)
    {
        "WEAP", "ARMO", "ARMA", "AMMO", "MISC", "ALCH", "BOOK",
        "KEYM", "NOTE", "IMOD", "CMNY", "CCRD", "CHIP",
    };

    private static bool BaseTypeAllowsStackCount(string? baseRecordType) =>
        baseRecordType is not null && StackCountableBaseTypes.Contains(baseRecordType);

    private static EncodedSubrecord BuildXcntSubrecord(short count)
    {
        var xcnt = new byte[4];
        SubrecordEncoder.WriteInt16(xcnt, 0, count);
        // bytes 2-3 = padding (zero)
        return new EncodedSubrecord("XCNT", xcnt);
    }

    /// <summary>
    ///     Emits the XMRK / FNAM? / FULL? / TNAM? subrecord cluster for a map-marker REFR.
    ///     <para>XMRK is the 0-byte presence flag; the engine ignores TNAM/FULL/FNAM without it.</para>
    ///     <para>FNAM is the 1-byte visibility flag set (bit 0=Visible, bit 1=CanTravel,
    ///     bit 2=Hidden per the runtime BGSPrimitiveMarker layout). Emitted only on the
    ///     new-record path with a 0x03 default (Visible + CanTravel) so brand-new markers
    ///     appear on the Pip-Boy map. On overrides we leave FNAM alone so the master's
    ///     authored value passes through RecordMergeEngine Pass 1 unchanged.</para>
    ///     <para>FULL is the latin1 display label ("Goodsprings"). When emitted on an
    ///     override path it overlays master's FULL byte-for-byte at master's position —
    ///     that's the rename path.</para>
    ///     <para>TNAM is 2 bytes: byte 0 = marker type (cast from MapMarkerType, 0=None
    ///     through 14=Vault), byte 1 = 0 padding.</para>
    /// </summary>
    private static void AppendMapMarkerSubrecords(
        List<EncodedSubrecord> subs,
        PlacedReference placed,
        bool isNewRecord)
    {
        if (!placed.IsMapMarker)
        {
            return;
        }

        // XMRK — 0-byte presence flag. Always emit for map markers; the rest of the
        // cluster is meaningless without it.
        subs.Add(new EncodedSubrecord("XMRK", []));

        if (isNewRecord)
        {
            // FNAM — visibility flags. 0x03 = Visible + CanTravel (standard shipping value).
            subs.Add(new EncodedSubrecord("FNAM", [0x03]));
        }

        if (!string.IsNullOrEmpty(placed.MarkerName))
        {
            subs.Add(EncodeStringSubrecord("FULL", placed.MarkerName));
        }

        if (placed.MarkerType.HasValue)
        {
            var tnam = new byte[2];
            tnam[0] = (byte)placed.MarkerType.Value;
            // byte 1 = 0 padding
            subs.Add(new EncodedSubrecord("TNAM", tnam));
        }
    }

    private static EncodedSubrecord EncodeStringSubrecord(string signature, string value)
    {
        var byteCount = Encoding.Latin1.GetByteCount(value);
        var buffer = new byte[byteCount + 1];
        Encoding.Latin1.GetBytes(value, buffer);
        // Final byte already 0 (null terminator).
        return new EncodedSubrecord(signature, buffer);
    }

    private static byte[] BuildDataSubrecord(PlacedReference placed)
    {
        var data = new byte[24];
        SubrecordEncoder.WriteFloat(data, 0, placed.X);
        SubrecordEncoder.WriteFloat(data, 4, placed.Y);
        SubrecordEncoder.WriteFloat(data, 8, placed.Z);
        SubrecordEncoder.WriteFloat(data, 12, placed.RotX);
        SubrecordEncoder.WriteFloat(data, 16, placed.RotY);
        SubrecordEncoder.WriteFloat(data, 20, placed.RotZ);
        return data;
    }

    private static byte[] BuildXsclSubrecord(float scale)
    {
        var xscl = new byte[4];
        SubrecordEncoder.WriteFloat(xscl, 0, scale);
        return xscl;
    }

    private static EncodedSubrecord EncodeFormIdSubrecord(string signature, uint formId)
    {
        var bytes = new byte[4];
        SubrecordEncoder.WriteFormId(bytes, 0, formId);
        return new EncodedSubrecord(signature, bytes);
    }
}
