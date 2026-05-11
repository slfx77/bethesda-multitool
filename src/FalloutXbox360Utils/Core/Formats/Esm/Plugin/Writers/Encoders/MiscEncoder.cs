using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="MiscItemRecord" /> as PC-format MISC subrecord bytes.
///     v1 emits DATA (8 bytes: int32 value + float weight). All other subrecords (EDID, FULL,
///     MODL, MODT, OBND, ICON, MICO, SCRI, YNAM, ZNAM) are retained from the source ESM.
/// </summary>
public sealed class MiscEncoder : IRecordEncoder
{
    public string RecordType => "MISC";
    public Type ModelType => typeof(MiscItemRecord);

    public EncodedRecord Encode(object model)
    {
        var misc = (MiscItemRecord)model;
        return new EncodedRecord
        {
            Subrecords = [new EncodedSubrecord("DATA", BuildDataSubrecord(misc))],
            Warnings = []
        };
    }

    /// <summary>
    ///     Encode a new MISC record from scratch. fopdoc canonical order:
    ///     EDID, OBND?, FULL?, MODL?, DATA. MODT/ICON/MICO/SCRI/YNAM/ZNAM are deferred.
    /// </summary>
    internal static EncodedRecord EncodeNew(MiscItemRecord misc)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(misc.EditorId))
        {
            warnings.Add($"New MISC 0x{misc.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", misc.EditorId ?? string.Empty));

        if (misc.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(misc.Bounds));
        }

        if (!string.IsNullOrEmpty(misc.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", misc.FullName));
        }

        if (!string.IsNullOrEmpty(misc.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", misc.ModelPath));
        }
        else
        {
            warnings.Add($"New MISC 0x{misc.FormId:X8} has no model path — record will not render in-game.");
        }

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(misc)));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(MiscItemRecord misc)
    {
        var data = new byte[8];
        SubrecordEncoder.WriteInt32(data, 0, misc.Value);
        SubrecordEncoder.WriteFloat(data, 4, misc.Weight);
        return data;
    }
}
