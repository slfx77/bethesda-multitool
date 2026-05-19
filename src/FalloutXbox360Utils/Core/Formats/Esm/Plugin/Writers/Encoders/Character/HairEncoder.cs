using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;

/// <summary>
///     Encodes a <see cref="HairRecord" /> (HAIR) as PC-format subrecord bytes.
///     Hair styles available for NPC character generation.
///     fopdoc canonical order: EDID, FULL?, MODL?, ICON?, DATA(1B flags — bit 0 = Playable).
/// </summary>
public sealed class HairEncoder : IRecordEncoder
{
    public string RecordType => "HAIR";
    public Type ModelType => typeof(HairRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(HairRecord hair)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(hair.EditorId))
        {
            warnings.Add($"New HAIR 0x{hair.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", hair.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(hair.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", hair.FullName));
        }

        if (!string.IsNullOrEmpty(hair.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", hair.ModelPath));
        }

        if (!string.IsNullOrEmpty(hair.TexturePath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", hair.TexturePath));
        }

        subs.Add(NewRecordSubrecords.EncodeByteSubrecord("DATA", hair.Flags));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
