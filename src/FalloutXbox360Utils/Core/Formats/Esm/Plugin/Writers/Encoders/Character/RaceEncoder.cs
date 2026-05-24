using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;

/// <summary>
///     Encodes a <see cref="RaceRecord" /> (RACE) as PC-format subrecord bytes.
///     This is the largest encoder yet — RACE carries body part hierarchies, FaceGen morph
///     coefficients (gendered), default hair/eyes, voice types, and 30+ optional fields.
///     fopdoc canonical order:
///     EDID, FULL?, DESC?, DATA(36B), ONAM?(older race), YNAM?(younger race),
///     VTCK*(voice types — pair of FormIDs), DNAM?(default hair pair), CNAM?(hair color),
///     PNAM?(face gen main clamp), UNAM?(face gen face clamp), ATTR? (not modeled),
///     NAM0 + (INDX + MODL + ICON)* + NAM1 + (INDX + MODL + ICON)* (head + body parts),
///     HNAM*(hair styles, FormID array), ENAM*(eye colors, FormID array),
///     MNAM + FGGS + FGGA + FGTS (male facegen morphs),
///     FNAM + FGGS + FGGA + FGTS (female facegen morphs), SNAM?.
///     Skill boosts (DATA bytes 0-13) pack as 7 pairs of (int8 SkillIndex + int8 Boost).
/// </summary>
public sealed class RaceEncoder : IRecordEncoder
{
    private static byte[] BuildSkillBoostBytes(RaceRecord race)
    {
        // 14 bytes: 7 pairs of (int8 SkillIndex + int8 Boost). -1 sentinel + zero for unused slots.
        var bytes = new byte[14];
        for (var i = 0; i < 7; i++)
        {
            if (i < race.SkillBoosts.Count)
            {
                var (skillIndex, boost) = race.SkillBoosts[i];
                bytes[i * 2] = (byte)(sbyte)skillIndex;
                bytes[i * 2 + 1] = (byte)boost;
            }
            else
            {
                bytes[i * 2] = 0xFF;
                bytes[i * 2 + 1] = 0;
            }
        }
        return bytes;
    }

    private static readonly Dictionary<string, Func<RaceRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["SkillBoosts"] = BuildSkillBoostBytes,
        ["MaleHeight"] = m => m.MaleHeight,
        ["FemaleHeight"] = m => m.FemaleHeight,
        ["MaleWeight"] = m => m.MaleWeight,
        ["FemaleWeight"] = m => m.FemaleWeight,
        ["Flags"] = m => m.DataFlags,
    };

    private static readonly Dictionary<string, Func<RaceRecord, object?>> VtckExtractors = new(StringComparer.Ordinal)
    {
        ["Male Voice Type"] = m => m.MaleVoiceFormId ?? 0u,
        ["Female Voice Type"] = m => m.FemaleVoiceFormId ?? 0u,
    };

    public string RecordType => "RACE";
    public Type ModelType => typeof(RaceRecord);

    internal static EncodedRecord EncodeNew(RaceRecord race)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(race.EditorId))
        {
            warnings.Add($"New RACE 0x{race.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", race.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(race.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", race.FullName));
        }

        if (!string.IsNullOrEmpty(race.Description))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("DESC", race.Description));
        }

        // Racial abilities (SPLO subrecords) typically come before DATA per xEdit ordering.
        foreach (var abilityFormId in race.AbilityFormIds)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SPLO", abilityFormId));
        }

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "RACE", 36, race, DataExtractors));

        if (race.OlderRaceFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ONAM", race.OlderRaceFormId.Value));
        }

        if (race.YoungerRaceFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("YNAM", race.YoungerRaceFormId.Value));
        }

        if (race.MaleVoiceFormId.HasValue || race.FemaleVoiceFormId.HasValue)
        {
            // VTCK is an 8-byte pair: male voice FormID + female voice FormID.
            subs.Add(SchemaModelSerializer.SerializeSubrecord("VTCK", "RACE", 8, race, VtckExtractors));
        }

        if (race.DefaultHairMaleFormId.HasValue || race.DefaultHairFemaleFormId.HasValue)
        {
            // DNAM is the gendered default-hair pair (8 bytes: male + female FormID).
            var dnam = new byte[8];
            SubrecordEncoder.WriteFormId(dnam, 0, race.DefaultHairMaleFormId ?? 0u);
            SubrecordEncoder.WriteFormId(dnam, 4, race.DefaultHairFemaleFormId ?? 0u);
            subs.Add(new EncodedSubrecord("DNAM", dnam));
        }

        if (race.DefaultHairColor.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeByteSubrecord("CNAM", race.DefaultHairColor.Value));
        }

        if (race.FaceGenMainClamp is < 0f or > 0f)
        {
            subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("PNAM", race.FaceGenMainClamp));
        }

        if (race.FaceGenFaceClamp is < 0f or > 0f)
        {
            subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("UNAM", race.FaceGenFaceClamp));
        }

        // Head parts — NAM0 opens a body-part block, then INDX (uint32 index) + MODL + ICON
        // for each entry. Indices 0=Head, 1=Eyes, 2=Mouth, 3=LowerTeeth, 4=UpperTeeth, 5=Tongue.
        var headParts = CollectHeadParts(race);
        if (headParts.Count > 0)
        {
            subs.Add(new EncodedSubrecord("NAM0", []));
            foreach (var part in headParts)
            {
                EmitBodyPart(subs, part.Index, part.ModelPath, part.IconPath);
            }
        }

        // Body parts — NAM1 + (INDX + MODL + ICON)* with indices 0=UpperBody, 1=LeftHand,
        // 2=RightHand. (Female variants are written separately under FNAM — see below.)
        var bodyParts = CollectBodyParts(race);
        if (bodyParts.Count > 0)
        {
            subs.Add(new EncodedSubrecord("NAM1", []));
            foreach (var part in bodyParts)
            {
                EmitBodyPart(subs, part.Index, part.ModelPath, part.IconPath);
            }
        }

        foreach (var hairFormId in race.HairStyleFormIds)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("HNAM", hairFormId));
        }

        foreach (var eyeFormId in race.EyeColorFormIds)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ENAM", eyeFormId));
        }

        // FaceGen base morphs come in two sections: MNAM (male) and FNAM (female), each
        // followed by FGGS (50 floats / 200B), FGGA (30 floats / 120B), FGTS (50 floats / 200B).
        if (HasMaleFaceGen(race))
        {
            subs.Add(new EncodedSubrecord("MNAM", []));
            EmitFaceGenMorphs(subs,
                race.MaleFaceGenGeometrySymmetric,
                race.MaleFaceGenGeometryAsymmetric,
                race.MaleFaceGenTextureSymmetric);
        }

        if (HasFemaleFaceGen(race))
        {
            subs.Add(new EncodedSubrecord("FNAM", []));
            EmitFaceGenMorphs(subs,
                race.FemaleFaceGenGeometrySymmetric,
                race.FemaleFaceGenGeometryAsymmetric,
                race.FemaleFaceGenTextureSymmetric);
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static List<(uint Index, string? ModelPath, string? IconPath)> CollectHeadParts(RaceRecord race)
    {
        var parts = new List<(uint, string?, string?)>();
        AddIfPresent(parts, 0, race.MaleHeadModelPath, race.MaleHeadTexturePath);
        AddIfPresent(parts, 2, race.MaleMouthModelPath, null);
        AddIfPresent(parts, 3, race.MaleLowerTeethModelPath, null);
        AddIfPresent(parts, 4, race.MaleUpperTeethModelPath, null);
        AddIfPresent(parts, 5, race.MaleTongueModelPath, null);
        return parts;
    }

    private static List<(uint Index, string? ModelPath, string? IconPath)> CollectBodyParts(RaceRecord race)
    {
        var parts = new List<(uint, string?, string?)>();
        AddIfPresent(parts, 0, race.MaleUpperBodyPath, race.MaleBodyTexturePath);
        AddIfPresent(parts, 1, race.MaleLeftHandPath, null);
        AddIfPresent(parts, 2, race.MaleRightHandPath, null);
        return parts;
    }

    private static void AddIfPresent(
        List<(uint Index, string? ModelPath, string? IconPath)> parts,
        uint index,
        string? modelPath,
        string? iconPath)
    {
        if (!string.IsNullOrEmpty(modelPath) || !string.IsNullOrEmpty(iconPath))
        {
            parts.Add((index, modelPath, iconPath));
        }
    }

    private static void EmitBodyPart(
        List<EncodedSubrecord> subs,
        uint index,
        string? modelPath,
        string? iconPath)
    {
        subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("INDX", index));
        if (!string.IsNullOrEmpty(modelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", modelPath));
        }

        if (!string.IsNullOrEmpty(iconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", iconPath));
        }
    }

    private static bool HasMaleFaceGen(RaceRecord race)
    {
        return race.MaleFaceGenGeometrySymmetric is not null
               || race.MaleFaceGenGeometryAsymmetric is not null
               || race.MaleFaceGenTextureSymmetric is not null;
    }

    private static bool HasFemaleFaceGen(RaceRecord race)
    {
        return race.FemaleFaceGenGeometrySymmetric is not null
               || race.FemaleFaceGenGeometryAsymmetric is not null
               || race.FemaleFaceGenTextureSymmetric is not null;
    }

    private static void EmitFaceGenMorphs(
        List<EncodedSubrecord> subs,
        float[]? fggs,
        float[]? fgga,
        float[]? fgts)
    {
        if (fggs is { Length: > 0 })
        {
            subs.Add(BuildFloatArraySubrecord("FGGS", fggs));
        }

        if (fgga is { Length: > 0 })
        {
            subs.Add(BuildFloatArraySubrecord("FGGA", fgga));
        }

        if (fgts is { Length: > 0 })
        {
            subs.Add(BuildFloatArraySubrecord("FGTS", fgts));
        }
    }

    private static EncodedSubrecord BuildFloatArraySubrecord(string signature, float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            SubrecordEncoder.WriteFloat(bytes, i * 4, values[i]);
        }

        return new EncodedSubrecord(signature, bytes);
    }
}
