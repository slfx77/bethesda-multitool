using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="HeadPartRecord" /> (HDPT) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, FULL?, MODL?, MODT?, MODS?, DATA(1B flags), HNAM*(extra part FormIDs).
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class HdptEncoder : IRecordEncoder
{
    public string RecordType => "HDPT";
    public Type ModelType => typeof(HeadPartRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(HeadPartRecord hdpt)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(hdpt.EditorId))
        {
            warnings.Add($"New HDPT 0x{hdpt.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", hdpt.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(hdpt.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", hdpt.FullName));
        }

        if (!string.IsNullOrEmpty(hdpt.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", hdpt.ModelPath));
        }

        if (hdpt.TextureHashData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        subs.Add(NewRecordSubrecords.EncodeByteSubrecord("DATA", hdpt.Flags));

        foreach (var extraFormId in hdpt.ExtraParts)
        {
            if (extraFormId == 0)
            {
                continue;
            }
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("HNAM", extraFormId));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
