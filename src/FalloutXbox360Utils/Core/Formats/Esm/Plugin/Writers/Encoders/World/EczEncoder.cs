using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes an <see cref="EncounterZoneRecord" /> (ECZN) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, DATA(8B: FormID owner + sbyte rank + sbyte min level + byte flags + 1B pad).
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class EczEncoder : IRecordEncoder
{
    public string RecordType => "ECZN";
    public Type ModelType => typeof(EncounterZoneRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(EncounterZoneRecord ecz)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(ecz.EditorId))
        {
            warnings.Add($"New ECZN 0x{ecz.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", ecz.EditorId ?? string.Empty));
        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(ecz)));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(EncounterZoneRecord ecz)
    {
        var data = new byte[8];
        SubrecordEncoder.WriteFormId(data, 0, ecz.OwnerFormId);
        data[4] = (byte)ecz.Rank;
        data[5] = (byte)ecz.MinimumLevel;
        data[6] = ecz.Flags;
        // data[7] = 0 (reserved/pad)
        return data;
    }
}
