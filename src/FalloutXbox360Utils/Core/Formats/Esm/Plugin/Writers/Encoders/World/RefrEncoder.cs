using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

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
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        // NAME — base form FormID. Required for the engine to know what to spawn.
        subs.Add(EncodeFormIdSubrecord("NAME", placed.BaseFormId));

        if (placed.EncounterZoneFormId.HasValue)
        {
            var resolved = ResolveOptionalFormId(placed.EncounterZoneFormId.Value,
                validFormIds, remapTable);
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
            var xlkrSubrec = TryBuildXlkrSubrecord(placed, validFormIds, remapTable, warnings);
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
            var resolved = ResolveOptionalFormId(placed.OwnerFormId.Value,
                validFormIds, remapTable);
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
            var resolved = ResolveOptionalFormId(placed.EnableParentFormId.Value,
                validFormIds, remapTable);
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
            var resolved = ResolveOptionalFormId(placed.DestinationDoorFormId.Value,
                validFormIds, remapTable);
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
                    $"0x{placed.DestinationDoorFormId.Value:X8} dangles — subrecord skipped.");
            }
        }

        // XCNT only has stack-count semantics on REFR (e.g., caps, ammo, item containers).
        // On ACHR/ACRE the same byte slot was overloaded by Bethesda as a runtime instance
        // counter — the engine appends "(N)" to the placed actor's display name whenever
        // it sees a nonzero count. Captures from a running game inevitably have this counter
        // set (it increments per session), so emitting it back makes every templated NPC
        // show "Ulysses (20770)" etc. Strip it for actor placements; the engine restores its
        // own counter at load time.
        if (placed.Count.HasValue && placed.RecordType == "REFR")
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
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        List<string> warnings)
    {
        var resolvedRef = ResolveOptionalFormId(placed.LinkedRefFormId!.Value,
            validFormIds, remapTable);
        if (!resolvedRef.HasValue)
        {
            warnings.Add($"REFR 0x{placed.FormId:X8} XLKR linked ref " +
                $"0x{placed.LinkedRefFormId.Value:X8} dangles — subrecord skipped.");
            return null;
        }

        if (placed.LinkedRefKeywordFormId.HasValue)
        {
            var resolvedKeyword = ResolveOptionalFormId(placed.LinkedRefKeywordFormId.Value,
                validFormIds, remapTable);
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
    ///     Try remap-first-then-validity for an optional placed-ref FormID. Returns null when
    ///     the FormID is dangling with no remap; otherwise returns the resolved (possibly
    ///     remapped) FormID. Mirrors the same policy used by IDLE ANAM, IDLE CTDA params,
    ///     QUST/PERK CTDA params, and PACK PLDT/PTDT.
    /// </summary>
    private static uint? ResolveOptionalFormId(
        uint formId,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable)
    {
        if (formId == 0 || validFormIds is null)
        {
            return formId;
        }

        if (remapTable is not null
            && remapTable.TryGetValue(formId, out var remapped)
            && remapped != formId
            && validFormIds.Contains(remapped))
        {
            return remapped;
        }

        if (validFormIds.Contains(formId))
        {
            return formId;
        }

        return null;
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
