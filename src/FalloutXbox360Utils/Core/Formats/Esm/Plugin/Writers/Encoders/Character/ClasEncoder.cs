using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;

/// <summary>
///     Encodes a <see cref="ClassRecord" /> (CLAS) as PC-format subrecord bytes.
///     Determines NPC skill growth and tag skills.
///     fopdoc canonical order: EDID, FULL?, DESC?, ICON?, DATA(28B), ATTR?(7B SPECIAL weights).
///     DATA layout (28B): int32 TagSkill1-4 + uint32 Flags + uint32 BuysServices +
///     int8 Teaches + uint8 MaxTrainingLevel + 2 pad.
/// </summary>
public sealed class ClasEncoder : IRecordEncoder
{
    public string RecordType => "CLAS";
    public Type ModelType => typeof(ClassRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(ClassRecord clas)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(clas.EditorId))
        {
            warnings.Add($"New CLAS 0x{clas.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", clas.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(clas.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", clas.FullName));
        }

        if (!string.IsNullOrEmpty(clas.Description))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("DESC", clas.Description));
        }

        if (!string.IsNullOrEmpty(clas.Icon))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", clas.Icon));
        }

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(clas)));

        if (clas.AttributeWeights is { Length: 7 })
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("ATTR", clas.AttributeWeights));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(ClassRecord clas)
    {
        // DATA (28 bytes): four int32 tag skill slots (-1 sentinel for unused),
        // uint32 Flags, uint32 BuysServices, int8 Teaches, uint8 MaxTrainingLevel,
        // and 2 bytes of padding.
        var data = new byte[28];
        for (var i = 0; i < 4; i++)
        {
            var skill = clas.TagSkills is { Length: > 0 } && i < clas.TagSkills.Length
                ? clas.TagSkills[i]
                : -1;
            SubrecordEncoder.WriteInt32(data, i * 4, skill);
        }

        SubrecordEncoder.WriteUInt32(data, 16, clas.Flags);
        SubrecordEncoder.WriteUInt32(data, 20, clas.BarterFlags);
        data[24] = clas.TrainingSkill;
        data[25] = clas.TrainingLevel;
        // bytes 26-27 padding
        return data;
    }
}
