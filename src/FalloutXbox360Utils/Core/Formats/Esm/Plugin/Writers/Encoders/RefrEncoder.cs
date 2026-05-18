using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

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
///     History: v1-v21 dropped DATA on overrides because the DMP captures live runtime
///     state and we suspected mid-walk/mid-fall positions caused NPC sinking. v22 reinstates
///     DATA on overrides — the sinking root cause was traced to dropped vanilla NAVMs
///     (addressed in v21 via the CellGrupBuilder NAVM preservation path), not transient
///     captured positions.
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
    ///     v4 closes the v3 deferred-subrecord gaps: XLOC (lock state), XESP (enable parent),
    ///     XLKR (linked ref), and XTEL (door teleport — emitted with FormID + zero PosRot/Flags
    ///     because the model only carries the destination FormID). v4 also fixes the v3 XCNT
    ///     bug (was 2 bytes, now 4 per the parser's <c>Simple4Byte</c> schema).
    /// </remarks>
    internal static EncodedRecord EncodeNewPlacedReference(PlacedReference placed)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        // NAME — base form FormID. Required for the engine to know what to spawn.
        subs.Add(EncodeFormIdSubrecord("NAME", placed.BaseFormId));

        if (placed.EncounterZoneFormId.HasValue)
        {
            subs.Add(EncodeFormIdSubrecord("XEZN", placed.EncounterZoneFormId.Value));
        }

        if (placed.LinkedRefFormId.HasValue)
        {
            subs.Add(BuildXlkrSubrecord(placed));
        }

        if (HasAnyLockState(placed))
        {
            subs.Add(BuildXlocSubrecord(placed));
        }

        if (placed.OwnerFormId.HasValue)
        {
            subs.Add(EncodeFormIdSubrecord("XOWN", placed.OwnerFormId.Value));
        }

        if (placed.EnableParentFormId.HasValue)
        {
            subs.Add(BuildXespSubrecord(placed));
        }

        if (placed.DestinationDoorFormId.HasValue)
        {
            subs.Add(BuildXtelSubrecord(placed));
            if (placed.TeleportPosRot is null)
            {
                warnings.Add(
                    $"REFR 0x{placed.FormId:X8} XTEL teleport position not available — emitted with zero PosRot.");
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
    ///     XESP — 8 bytes fixed: FormID Parent @0, uint8 Flags @4, padding @5-7.
    /// </summary>
    private static EncodedSubrecord BuildXespSubrecord(PlacedReference placed)
    {
        var xesp = new byte[8];
        SubrecordEncoder.WriteFormId(xesp, 0, placed.EnableParentFormId!.Value);
        xesp[4] = placed.EnableParentFlags ?? 0;
        // bytes 5-7 = padding (zero)
        return new EncodedSubrecord("XESP", xesp);
    }

    /// <summary>
    ///     XLKR — 4 bytes (just LinkedRef) or 8 bytes (Keyword + LinkedRef) depending on
    ///     whether the model carries a keyword FormID.
    /// </summary>
    private static EncodedSubrecord BuildXlkrSubrecord(PlacedReference placed)
    {
        if (placed.LinkedRefKeywordFormId.HasValue)
        {
            var xlkr8 = new byte[8];
            SubrecordEncoder.WriteFormId(xlkr8, 0, placed.LinkedRefKeywordFormId.Value);
            SubrecordEncoder.WriteFormId(xlkr8, 4, placed.LinkedRefFormId!.Value);
            return new EncodedSubrecord("XLKR", xlkr8);
        }

        var xlkr4 = new byte[4];
        SubrecordEncoder.WriteFormId(xlkr4, 0, placed.LinkedRefFormId!.Value);
        return new EncodedSubrecord("XLKR", xlkr4);
    }

    /// <summary>
    ///     XTEL — 32 bytes fixed: FormID DoorFormId @0, 6 floats PosRot @4-27,
    ///     uint8 Flags @28, padding @29-31. PosRot/Flags are populated from
    ///     <see cref="PlacedReference.TeleportPosRot" /> and
    ///     <see cref="PlacedReference.TeleportFlags" /> when available; otherwise zeroed.
    /// </summary>
    private static EncodedSubrecord BuildXtelSubrecord(PlacedReference placed)
    {
        var xtel = new byte[32];
        SubrecordEncoder.WriteFormId(xtel, 0, placed.DestinationDoorFormId!.Value);

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
    ///     v3 mistakenly emitted only 2 bytes, which the parser's <c>DataLength &gt;= 4</c>
    ///     guard would silently reject.
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
