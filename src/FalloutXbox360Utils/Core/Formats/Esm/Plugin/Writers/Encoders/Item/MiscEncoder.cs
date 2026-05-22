using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

/// <summary>
///     Encodes a <see cref="MiscItemRecord" /> as PC-format MISC subrecord bytes.
///     Emits DATA (8 bytes: int32 value + float weight). All other subrecords (EDID, FULL,
///     MODL, MODT, OBND, ICON, MICO, SCRI, YNAM, ZNAM) are retained from the source ESM.
/// </summary>
public sealed class MiscEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<MiscItemRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["Value"] = m => m.Value,
        ["Weight"] = m => m.Weight,
    };

    public string RecordType => "MISC";
    public Type ModelType => typeof(MiscItemRecord);

    public EncodedRecord Encode(object model)
    {
        var misc = (MiscItemRecord)model;
        return new EncodedRecord
        {
            Subrecords = [SchemaModelSerializer.SerializeSubrecord("DATA", "MISC", 8, misc, DataExtractors)],
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

        if (misc.TextureHashData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        if (!string.IsNullOrEmpty(misc.IconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", misc.IconPath));
        }

        if (!string.IsNullOrEmpty(misc.MessageIconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MICO", misc.MessageIconPath));
        }

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "MISC", 8, misc, DataExtractors));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
