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
/// </summary>
public sealed class RefrEncoder : IRecordEncoder
{
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

        if (placed.Count.HasValue)
        {
            subs.Add(BuildXcntSubrecord(placed.Count.Value));
        }

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

    private static bool HasAnyLockState(PlacedReference placed)
    {
        return placed.LockLevel.HasValue
               || placed.LockKeyFormId.HasValue
               || placed.LockFlags.HasValue
               || placed.LockNumTries.HasValue
               || placed.LockTimesUnlocked.HasValue;
    }

    /// <summary>
    ///     XLOC — 20 bytes fixed: uint8 LockLevel @0, padding @1-3, FormID LockKey @4-7,
    ///     uint8 LockFlags @8, padding @9-11, uint32 LockNumTries @12-15,
    ///     uint32 LockTimesUnlocked @16-19.
    /// </summary>
    private static EncodedSubrecord BuildXlocSubrecord(PlacedReference placed)
    {
        var xloc = new byte[20];
        xloc[0] = placed.LockLevel ?? 0;
        // bytes 1-3 = padding (zero)
        SubrecordEncoder.WriteFormId(xloc, 4, placed.LockKeyFormId ?? 0);
        xloc[8] = placed.LockFlags ?? 0;
        // bytes 9-11 = padding (zero)
        SubrecordEncoder.WriteUInt32(xloc, 12, placed.LockNumTries ?? 0);
        SubrecordEncoder.WriteUInt32(xloc, 16, placed.LockTimesUnlocked ?? 0);
        return new EncodedSubrecord("XLOC", xloc);
    }

    /// <summary>
    ///     XESP — 8 bytes fixed: FormID Parent @0, uint8 Flags @4, padding @5-7. The Parent
    ///     FormID arrives pre-resolved (master ∪ emitted, with remap applied) so the engine
    ///     never sees a dangling enable-state-parent ref.
    /// </summary>
    private static EncodedSubrecord BuildXespSubrecord(PlacedReference placed, uint resolvedParentId)
    {
        var xesp = new byte[8];
        SubrecordEncoder.WriteFormId(xesp, 0, resolvedParentId);
        xesp[4] = placed.EnableParentFlags ?? 0;
        // bytes 5-7 = padding (zero)
        return new EncodedSubrecord("XESP", xesp);
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
    ///     XTEL — 32 bytes fixed: FormID DoorFormId @0, 6 floats PosRot @4-27,
    ///     uint8 Flags @28, padding @29-31. PosRot/Flags are populated from
    ///     <see cref="PlacedReference.TeleportPosRot" /> and
    ///     <see cref="PlacedReference.TeleportFlags" /> when available; otherwise zeroed.
    /// </summary>
    private static EncodedSubrecord BuildXtelSubrecord(PlacedReference placed, uint resolvedDoorFormId)
    {
        var xtel = new byte[32];
        SubrecordEncoder.WriteFormId(xtel, 0, resolvedDoorFormId);

        if (placed.TeleportPosRot is { } pr)
        {
            SubrecordEncoder.WriteFloat(xtel, 4, pr.X);
            SubrecordEncoder.WriteFloat(xtel, 8, pr.Y);
            SubrecordEncoder.WriteFloat(xtel, 12, pr.Z);
            SubrecordEncoder.WriteFloat(xtel, 16, pr.RotX);
            SubrecordEncoder.WriteFloat(xtel, 20, pr.RotY);
            SubrecordEncoder.WriteFloat(xtel, 24, pr.RotZ);
        }

        if (placed.TeleportFlags.HasValue)
        {
            xtel[28] = placed.TeleportFlags.Value;
        }

        // bytes 29-31 = padding (zero)
        return new EncodedSubrecord("XTEL", xtel);
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
