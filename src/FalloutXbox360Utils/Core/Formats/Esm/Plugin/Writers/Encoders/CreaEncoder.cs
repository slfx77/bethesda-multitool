using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="CreatureRecord" /> (CREA) as PC-format subrecord bytes.
///     Non-human actors. Closely parallels NPC_ but with the creature-specific DATA layout.
///     fopdoc canonical order: EDID, FULL?, MODL?, ACBS(24B), SNAM*(faction memberships, 8B each),
///         INAM?(death item), SCRI?, AIDT?(20B), PKID*, SPLO*, DATA(17B), CSDC?.
///     DATA layout (17B): uint8 CreatureType + uint8 CombatSkill + uint8 MagicSkill +
///         uint8 StealthSkill + int32 Health + int16 AttackDamage + 7 bytes unused.
///     FaceGen / NIFZ / NIFT / KFFZ / KFNM etc. NOT modeled yet — deferred.
/// </summary>
public sealed class CreaEncoder : IRecordEncoder
{
    public string RecordType => "CREA";
    public Type ModelType => typeof(CreatureRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(CreatureRecord crea)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(crea.EditorId))
        {
            warnings.Add($"New CREA 0x{crea.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", crea.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(crea.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", crea.FullName));
        }

        if (!string.IsNullOrEmpty(crea.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", crea.ModelPath));
        }

        // ACBS — actor base stats. Mirrors NpcEncoder's pattern. Zero-fills if model lacks ACBS.
        var acbs = new byte[24];
        if (crea.Stats is { } stats)
        {
            SubrecordEncoder.WriteUInt32(acbs, 0, stats.Flags);
            SubrecordEncoder.WriteUInt16(acbs, 4, stats.FatigueBase);
            SubrecordEncoder.WriteUInt16(acbs, 6, stats.BarterGold);
            SubrecordEncoder.WriteInt16(acbs, 8, stats.Level);
            SubrecordEncoder.WriteUInt16(acbs, 10, stats.CalcMin);
            SubrecordEncoder.WriteUInt16(acbs, 12, stats.CalcMax);
            SubrecordEncoder.WriteUInt16(acbs, 14, stats.SpeedMultiplier);
            SubrecordEncoder.WriteFloat(acbs, 16, stats.KarmaAlignment);
            SubrecordEncoder.WriteInt16(acbs, 20, stats.DispositionBase);
            SubrecordEncoder.WriteUInt16(acbs, 22, stats.TemplateFlags);
        }
        else
        {
            warnings.Add($"New CREA 0x{crea.FormId:X8} has no ACBS — emitting zero-filled actor base stats.");
        }

        subs.Add(new EncodedSubrecord("ACBS", acbs));

        // SNAM faction memberships — 8 bytes each (FormID + uint8 rank + 3 padding).
        foreach (var faction in crea.Factions)
        {
            var snam = new byte[8];
            SubrecordEncoder.WriteFormId(snam, 0, faction.FactionFormId);
            snam[4] = (byte)faction.Rank;
            subs.Add(new EncodedSubrecord("SNAM", snam));
        }

        if (crea.DeathItem.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("INAM", crea.DeathItem.Value));
        }

        if (crea.Script.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", crea.Script.Value));
        }

        if (crea.AiData is not null)
        {
            var aidt = new byte[20];
            aidt[0] = crea.AiData.Aggression;
            aidt[1] = crea.AiData.Confidence;
            aidt[2] = crea.AiData.EnergyLevel;
            aidt[3] = crea.AiData.Responsibility;
            aidt[4] = crea.AiData.Mood;
            SubrecordEncoder.WriteUInt32(aidt, 8, crea.AiData.Flags);
            aidt[14] = crea.AiData.Assistance;
            subs.Add(new EncodedSubrecord("AIDT", aidt));
        }

        foreach (var pkgId in crea.Packages)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("PKID", pkgId));
        }

        foreach (var spellId in crea.Spells)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SPLO", spellId));
        }

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(crea)));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(CreatureRecord crea)
    {
        var data = new byte[17];
        data[0] = crea.CreatureType;
        data[1] = crea.CombatSkill;
        data[2] = crea.MagicSkill;
        data[3] = crea.StealthSkill;
        // bytes 4-7: int32 Health — not modeled, zero (engine computes from Endurance + level).
        SubrecordEncoder.WriteInt16(data, 8, crea.AttackDamage);
        // bytes 10-16: 7 unused/reserved bytes — leave zero.
        return data;
    }
}
