using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="KeyRecord" /> as PC-format KEYM subrecord bytes.
///     v1 emits DATA (8 bytes: int32 value + float weight). All other subrecords are retained
///     from the source ESM.
/// </summary>
public sealed class KeymEncoder : IRecordEncoder
{
    public string RecordType => "KEYM";
    public Type ModelType => typeof(KeyRecord);

    public EncodedRecord Encode(object model)
    {
        var key = (KeyRecord)model;
        return new EncodedRecord
        {
            Subrecords = [new EncodedSubrecord("DATA", BuildDataSubrecord(key))],
            Warnings = []
        };
    }

    /// <summary>
    ///     Encode a new KEYM record from scratch. fopdoc canonical order:
    ///     EDID, FULL?, MODL?, DATA. (KEYM has no OBND in the parsed model.)
    /// </summary>
    internal static EncodedRecord EncodeNew(KeyRecord key)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(key.EditorId))
        {
            warnings.Add($"New KEYM 0x{key.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", key.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(key.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", key.FullName));
        }

        if (!string.IsNullOrEmpty(key.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", key.ModelPath));
        }

        if (key.TextureHashData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        if (!string.IsNullOrEmpty(key.IconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", key.IconPath));
        }

        if (!string.IsNullOrEmpty(key.MessageIconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MICO", key.MessageIconPath));
        }

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(key)));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(KeyRecord key)
    {
        var data = new byte[8];
        SubrecordEncoder.WriteInt32(data, 0, key.Value);
        SubrecordEncoder.WriteFloat(data, 4, key.Weight);
        return data;
    }
}
