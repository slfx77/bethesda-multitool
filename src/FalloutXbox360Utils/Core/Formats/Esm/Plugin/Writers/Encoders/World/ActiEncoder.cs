using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes an <see cref="ActivatorRecord" /> (ACTI) as PC-format subrecord bytes.
///     Emits the full record from scratch: EDID + OBND? + FULL? + MODL? + SCRI? +
///     SNAM? + RNAM? + WNAM?. ACTI has no DATA subrecord — all attributes are optional
///     individual subrecords. The override path is a no-op.
/// </summary>
public sealed class ActiEncoder : IRecordEncoder
{
    public string RecordType => "ACTI";
    public Type ModelType => typeof(ActivatorRecord);

    /// <summary>
    ///     Encode a new ACTI record from scratch in fopdoc canonical order:
    ///     EDID, OBND, FULL, MODL, SCRI, SNAM, RNAM, WNAM.
    /// </summary>
    internal static EncodedRecord EncodeNew(ActivatorRecord acti)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(acti.EditorId))
        {
            warnings.Add($"New ACTI 0x{acti.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", acti.EditorId ?? string.Empty));

        if (acti.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(acti.Bounds));
        }

        if (!string.IsNullOrEmpty(acti.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", acti.FullName));
        }

        if (!string.IsNullOrEmpty(acti.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", acti.ModelPath));
        }

        if (acti.TextureHashData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        if (acti.Script.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", acti.Script.Value));
        }

        if (acti.ActivationSoundFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SNAM", acti.ActivationSoundFormId.Value));
        }

        if (acti.RadioStationFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("RNAM", acti.RadioStationFormId.Value));
        }

        if (acti.WaterTypeFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("WNAM", acti.WaterTypeFormId.Value));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
