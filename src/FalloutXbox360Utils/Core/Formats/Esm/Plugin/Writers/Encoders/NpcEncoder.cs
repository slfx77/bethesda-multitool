using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes an <see cref="NpcRecord" /> as PC-format NPC_ subrecord bytes.
///     v1 emits ACBS (24 bytes — actor base stats) only. The many other NPC_ subrecords
///     (RNAM race, CNAM class, SCRI script, INAM death item, VTCK voice type, faction lists,
///     SPECIAL stats, skills, AI data, FaceGen morphs, head parts) are retained from the
///     source ESM. ACBS is the most frequently mutated runtime block and the only one the
///     parsed <see cref="ActorBaseSubrecord" /> covers byte-for-byte.
///     ACBS layout: uint32 Flags(0) + uint16 Fatigue(4) + uint16 BarterGold(6) +
///                  int16 Level(8) + uint16 CalcMin(10) + uint16 CalcMax(12) +
///                  uint16 SpeedMult(14) + float KarmaAlignment(16) +
///                  int16 DispositionBase(20) + uint16 TemplateFlags(22).
/// </summary>
public sealed class NpcEncoder : IRecordEncoder
{
    public string RecordType => "NPC_";
    public Type ModelType => typeof(NpcRecord);

    public EncodedRecord Encode(object model)
    {
        var npc = (NpcRecord)model;
        if (npc.Stats is null)
        {
            return new EncodedRecord
            {
                Subrecords = [],
                Warnings = [$"NPC 0x{npc.FormId:X8} has no parsed ACBS — record retains ESM verbatim."]
            };
        }

        return new EncodedRecord
        {
            Subrecords = [new EncodedSubrecord("ACBS", BuildAcbsSubrecord(npc.Stats))],
            Warnings = []
        };
    }

    /// <summary>
    ///     Encode a new NPC_ record from scratch. Includes EDID, FULL?, ACBS, RNAM (race),
    ///     plus optional FormID / faction / spell / inventory / package / AI subrecords.
    ///     v5 defers DATA + DNAM (NPC_-specific byte layouts not verified) and FaceGen morphs.
    /// </summary>
    internal static EncodedRecord EncodeNew(NpcRecord npc)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(npc.EditorId))
        {
            warnings.Add($"New NPC 0x{npc.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", npc.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(npc.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", npc.FullName));
        }

        if (npc.Stats is null)
        {
            warnings.Add($"New NPC 0x{npc.FormId:X8} has no ACBS — emitting zero-filled actor base stats.");
            subs.Add(new EncodedSubrecord("ACBS", new byte[24]));
        }
        else
        {
            subs.Add(new EncodedSubrecord("ACBS", BuildAcbsSubrecord(npc.Stats)));
        }

        // SNAM faction memberships — 8 bytes each: FormID + uint8 rank + 3 padding.
        foreach (var membership in npc.Factions)
        {
            var snam = new byte[8];
            SubrecordEncoder.WriteFormId(snam, 0, membership.FactionFormId);
            snam[4] = (byte)membership.Rank;
            // bytes 5-7 padding (zero)
            subs.Add(new EncodedSubrecord("SNAM", snam));
        }

        if (npc.DeathItem.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("INAM", npc.DeathItem.Value));
        }

        if (npc.VoiceType.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("VTCK", npc.VoiceType.Value));
        }

        if (npc.Template.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("TPLT", npc.Template.Value));
        }

        if (npc.Race.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("RNAM", npc.Race.Value));
        }
        else
        {
            warnings.Add($"New NPC 0x{npc.FormId:X8} has no race — racial traits will use engine default.");
        }

        // SPLO — spell/ability list. One subrecord per spell.
        foreach (var spellId in npc.Spells)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SPLO", spellId));
        }

        if (npc.Script.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", npc.Script.Value));
        }

        // CNTO inventory entries — 8 bytes each: FormID + int32 count.
        foreach (var item in npc.Inventory)
        {
            var cnto = new byte[8];
            SubrecordEncoder.WriteFormId(cnto, 0, item.ItemFormId);
            SubrecordEncoder.WriteInt32(cnto, 4, item.Count);
            subs.Add(new EncodedSubrecord("CNTO", cnto));
        }

        // AIDT — 20 bytes: AI behavior data.
        if (npc.AiData is not null)
        {
            var aidt = new byte[20];
            aidt[0] = npc.AiData.Aggression;
            aidt[1] = npc.AiData.Confidence;
            aidt[2] = npc.AiData.EnergyLevel;
            aidt[3] = npc.AiData.Responsibility;
            aidt[4] = npc.AiData.Mood;
            // bytes 5-7 padding
            SubrecordEncoder.WriteUInt32(aidt, 8, npc.AiData.Flags);
            // bytes 12-13 = TrainingSkill/TrainingLevel (not in model; zero)
            aidt[14] = npc.AiData.Assistance;
            // bytes 15-19 = AggroRadiusBehavior + AggroRadius (not in model; zero)
            subs.Add(new EncodedSubrecord("AIDT", aidt));
        }

        // PKID — AI packages. One subrecord per package FormID.
        foreach (var pkgId in npc.Packages)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("PKID", pkgId));
        }

        if (npc.Class.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("CNAM", npc.Class.Value));
        }

        if (npc.HairFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("HNAM", npc.HairFormId.Value));
        }

        if (npc.HairLength.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("LNAM", npc.HairLength.Value));
        }

        if (npc.EyesFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ENAM", npc.EyesFormId.Value));
        }

        if (npc.HairColor.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("HCLR", npc.HairColor.Value));
        }

        if (npc.CombatStyleFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ZNAM", npc.CombatStyleFormId.Value));
        }

        // NPC_ DATA — 11 bytes per FNV schema: int32 BaseHealth + 7 SPECIAL bytes
        // (Strength, Perception, Endurance, Charisma, Intelligence, Agility, Luck).
        // BaseHealth not in model — leave as zero (engine computes from Endurance + level).
        if (npc.SpecialStats is { Length: 7 } special)
        {
            var data = new byte[11];
            // bytes 0-3 = BaseHealth (zero)
            Array.Copy(special, 0, data, 4, 7);
            subs.Add(new EncodedSubrecord("DATA", data));
        }

        // NPC_ DNAM — 28 bytes: 14 base skill values + 14 mod offsets.
        // Model carries Skills (base values); mod offsets default to zero.
        if (npc.Skills is { Length: 14 } skills)
        {
            var dnam = new byte[28];
            Array.Copy(skills, 0, dnam, 0, 14);
            // bytes 14-27 = mod offsets (zero)
            subs.Add(new EncodedSubrecord("DNAM", dnam));
        }

        // FaceGen morphs — float arrays of fixed sizes per schema:
        //   FGGS=200 bytes (50 floats), FGGA=120 bytes (30 floats), FGTS=200 bytes (50 floats).
        if (npc.FaceGenGeometrySymmetric is { Length: 50 } fggs)
        {
            subs.Add(BuildFloatArraySubrecord("FGGS", fggs));
        }

        if (npc.FaceGenGeometryAsymmetric is { Length: 30 } fgga)
        {
            subs.Add(BuildFloatArraySubrecord("FGGA", fgga));
        }

        if (npc.FaceGenTextureSymmetric is { Length: 50 } fgts)
        {
            subs.Add(BuildFloatArraySubrecord("FGTS", fgts));
        }

        if (npc.HeadPartFormIds is { Count: > 0 })
        {
            warnings.Add(
                $"New NPC 0x{npc.FormId:X8} has {npc.HeadPartFormIds.Count} head part(s) — head-part subrecord structure not verified, deferred.");
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
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

    private static byte[] BuildAcbsSubrecord(ActorBaseSubrecord s)
    {
        var acbs = new byte[24];
        SubrecordEncoder.WriteUInt32(acbs, 0, s.Flags);
        SubrecordEncoder.WriteUInt16(acbs, 4, s.FatigueBase);
        SubrecordEncoder.WriteUInt16(acbs, 6, s.BarterGold);
        SubrecordEncoder.WriteInt16(acbs, 8, s.Level);
        SubrecordEncoder.WriteUInt16(acbs, 10, s.CalcMin);
        SubrecordEncoder.WriteUInt16(acbs, 12, s.CalcMax);
        SubrecordEncoder.WriteUInt16(acbs, 14, s.SpeedMultiplier);
        SubrecordEncoder.WriteFloat(acbs, 16, s.KarmaAlignment);
        SubrecordEncoder.WriteInt16(acbs, 20, s.DispositionBase);
        SubrecordEncoder.WriteUInt16(acbs, 22, s.TemplateFlags);
        return acbs;
    }
}
