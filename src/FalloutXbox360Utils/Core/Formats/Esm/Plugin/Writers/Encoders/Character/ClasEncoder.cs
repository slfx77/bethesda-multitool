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
    private static int TagSkillAt(ClassRecord clas, int index)
    {
        return clas.TagSkills is { Length: > 0 } && index < clas.TagSkills.Length
            ? clas.TagSkills[index]
            : -1;
    }

    private static readonly Dictionary<string, Func<ClassRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["TagSkill1"] = m => TagSkillAt(m, 0),
        ["TagSkill2"] = m => TagSkillAt(m, 1),
        ["TagSkill3"] = m => TagSkillAt(m, 2),
        ["TagSkill4"] = m => TagSkillAt(m, 3),
        ["Flags"] = m => m.Flags,
        ["BuysServices"] = m => m.BarterFlags,
        ["Teaches"] = m => (sbyte)m.TrainingSkill,
        ["MaxTrainingLevel"] = m => m.TrainingLevel,
    };

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

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "CLAS", 28, clas, DataExtractors));

        if (clas.AttributeWeights is { Length: 7 })
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("ATTR", clas.AttributeWeights));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
