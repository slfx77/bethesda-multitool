using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a <see cref="LightRecord" /> (LIGH) as PC-format subrecord bytes.
///     Emits the full record from scratch: EDID + OBND? + MODL? + MODT? + SCRI? +
///     FULL? + ICON? + DATA(32B) + FNAM? + SNAM?.
///     Override path is a no-op.
///     DATA layout (32 bytes, all PC little-endian):
///     int32  Duration(0)        — seconds (0 = infinite)
///     uint32 Radius(4)
///     uint32 Color(8)           — RGBA packed
///     uint32 Flags(12)          — can-take, flicker, off-by-default, ...
///     float  FalloffExponent(16)
///     float  Fov(20)
///     int32  Value(24)
///     float  Weight(28)
/// </summary>
public sealed class LighEncoder : IRecordEncoder
{
    public string RecordType => "LIGH";
    public Type ModelType => typeof(LightRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    /// <summary>
    ///     Encode a new LIGH record from scratch in fopdoc canonical order:
    ///     EDID, OBND, MODL, MODT, SCRI, FULL, ICON, DATA, FNAM, SNAM.
    /// </summary>
    internal static EncodedRecord EncodeNew(LightRecord ligh)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(ligh.EditorId))
        {
            warnings.Add($"New LIGH 0x{ligh.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", ligh.EditorId ?? string.Empty));

        if (ligh.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(ligh.Bounds));
        }

        if (!string.IsNullOrEmpty(ligh.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", ligh.ModelPath));
        }

        if (ligh.TextureHashData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        if (ligh.Script.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", ligh.Script.Value));
        }

        if (!string.IsNullOrEmpty(ligh.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", ligh.FullName));
        }

        if (!string.IsNullOrEmpty(ligh.IconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", ligh.IconPath));
        }

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(ligh)));

        if (ligh.Fade.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("FNAM", ligh.Fade.Value));
        }

        if (ligh.SoundFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SNAM", ligh.SoundFormId.Value));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(LightRecord ligh)
    {
        var data = new byte[32];
        SubrecordEncoder.WriteInt32(data, 0, ligh.Duration);
        SubrecordEncoder.WriteUInt32(data, 4, ligh.Radius);
        SubrecordEncoder.WriteUInt32(data, 8, ligh.Color);
        SubrecordEncoder.WriteUInt32(data, 12, ligh.Flags);
        SubrecordEncoder.WriteFloat(data, 16, ligh.FalloffExponent);
        SubrecordEncoder.WriteFloat(data, 20, ligh.Fov);
        SubrecordEncoder.WriteInt32(data, 24, ligh.Value);
        SubrecordEncoder.WriteFloat(data, 28, ligh.Weight);
        return data;
    }
}
