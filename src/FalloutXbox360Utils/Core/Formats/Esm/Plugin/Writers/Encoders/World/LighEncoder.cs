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
    // Schema field names track the LIGH DATA layout: Time/Radius/Color/Flags/FalloffExponent/FOV/Value/Weight.
    private static readonly Dictionary<string, Func<LightRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["Time"] = m => m.Duration,
        ["Radius"] = m => m.Radius,
        ["Color"] = m => m.Color,
        ["Flags"] = m => m.Flags,
        ["FalloffExponent"] = m => m.FalloffExponent,
        ["FOV"] = m => m.Fov,
        ["Value"] = m => m.Value,
        ["Weight"] = m => m.Weight,
    };

    public string RecordType => "LIGH";
    public Type ModelType => typeof(LightRecord);

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

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "LIGH", 32, ligh, DataExtractors));

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
}
