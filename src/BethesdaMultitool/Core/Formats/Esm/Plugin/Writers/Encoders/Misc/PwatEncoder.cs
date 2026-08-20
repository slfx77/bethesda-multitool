using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a Placeable Water (PWAT) record. PWAT placements reference a parent WATR
///     record; missing the encoder strips the placement, so cells with proto-only PWATs
///     end up dry until the player explicitly enters them — and the engine null-derefs
///     on cell entry when a REFR points at a missing PWAT base.
///     fopdoc canonical order: EDID, OBND, MODL?, MODT?, DNAM(8B).
/// </summary>
public sealed class PwatEncoder : IRecordEncoder
{
    public string RecordType => "PWAT";

    public Type ModelType => typeof(PlaceableWaterRecord);

    internal static EncodedRecord EncodeNew(PlaceableWaterRecord pwat)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(pwat.EditorId))
        {
            warnings.Add($"New PWAT 0x{pwat.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", pwat.EditorId ?? string.Empty));

        if (pwat.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(pwat.Bounds));
        }

        if (!string.IsNullOrEmpty(pwat.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", pwat.ModelPath));
        }

        if (pwat.ModelTextureData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        subs.Add(new EncodedSubrecord("DNAM", EncodePwatDnam(pwat.WaterFormId, pwat.Flags)));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     PWAT DNAM payload (8 bytes, little-endian):
    ///     <b>
    ///         uint32 Flags first, then the WATR
    ///         FormID
    ///     </b>
    ///     — per xEdit <c>wbRecord(PWAT)</c>
    ///     (<c>wbStruct(DNAM, [wbInteger('Flags'), wbFormIDCk('Water', [WATR])])</c>) and
    ///     confirmed against all 29 retail FalloutNV.esm PWATs, where bytes 4..7 resolve to a
    ///     WATR in 29/29 and bytes 0..3 carry the reflect/refract flag word.
    ///     <para>
    ///         This is the SAME order as the runtime <c>BGSPlaceableWaterData</c> struct
    ///         (flags at +0, <c>TESWaterForm*</c> at +4), not the reverse of it. An earlier
    ///         comment here claimed the opposite and the two halves were written transposed; the
    ///         read side was inverted to match, so ESM→ESM round-trips stayed byte-neutral and hid
    ///         it. Only runtime-sourced records reached disk swapped — the engine then read our
    ///         WATR FormID as the flag word (clearing bit 28 "Depth", which drives the
    ///         depth-graded alpha ramp) and the flag word as a FormID with mod index 0x10, which
    ///         resolves to nothing.
    ///     </para>
    /// </summary>
    internal static byte[] EncodePwatDnam(uint waterFormId, uint flags)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), waterFormId);
        return bytes;
    }
}
