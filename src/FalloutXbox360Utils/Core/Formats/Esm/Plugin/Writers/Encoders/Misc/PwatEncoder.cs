using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

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
    ///     PWAT DNAM payload (8 bytes, little-endian): uint32 WATR FormID, uint32 Flags.
    ///     Matches the schema entry at <c>SubrecordCellAndMiscSchemas (DNAM, PWAT, 8)</c>.
    /// </summary>
    internal static byte[] EncodePwatDnam(uint waterFormId, uint flags)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), waterFormId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), flags);
        return bytes;
    }
}
