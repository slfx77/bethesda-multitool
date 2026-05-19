using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a <see cref="CaravanCardRecord" /> (CCRD) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, FULL?, MODL?, DATA(uint32 value), SCRI?, YNAM?(pickup sound), ZNAM?(putdown sound).
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class CcrdEncoder : IRecordEncoder
{
    public string RecordType => "CCRD";
    public Type ModelType => typeof(CaravanCardRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(CaravanCardRecord ccrd)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(ccrd.EditorId))
        {
            warnings.Add($"New CCRD 0x{ccrd.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", ccrd.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(ccrd.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", ccrd.FullName));
        }

        if (!string.IsNullOrEmpty(ccrd.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", ccrd.ModelPath));
        }

        subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("DATA", ccrd.Value));

        if (ccrd.ScriptFormId != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", ccrd.ScriptFormId));
        }

        if (ccrd.PickupSoundFormId != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("YNAM", ccrd.PickupSoundFormId));
        }

        if (ccrd.PutdownSoundFormId != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ZNAM", ccrd.PutdownSoundFormId));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
