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
///     int16 Level(8) + uint16 CalcMin(10) + uint16 CalcMax(12) +
///     uint16 SpeedMult(14) + float KarmaAlignment(16) +
///     int16 DispositionBase(20) + uint16 TemplateFlags(22).
/// </summary>
public sealed class NpcEncoder : IRecordEncoder
{
    public string RecordType => "NPC_";
    public Type ModelType => typeof(NpcRecord);

    public EncodedRecord Encode(object model)
    {
        var npc = (NpcRecord)model;
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (npc.Stats is not null)
        {
            // v22: force AutoCalcStats on override ACBS too. The override path replaces the
            // master's ACBS Flags wholesale, which would clear master's AutoCalcStats bit if
            // the captured runtime Flags doesn't include it (we routinely see captured
            // Flags = 0x01 / Biped-only). Without AutoCalc the engine reads manual stats
            // from captured CalcMin/Max + Level, which for prototype runtime values often
            // produces 0 HP and the NPC spawns dead. Forcing AutoCalc makes the engine
            // recompute HP from Class + Level + SPECIAL so the NPC stays alive.
            subs.Add(new EncodedSubrecord("ACBS", BuildAcbsSubrecord(npc.Stats, true)));
        }
        else
        {
            warnings.Add($"NPC 0x{npc.FormId:X8} has no parsed ACBS — ACBS retained from ESM.");
        }

        // Override-mode FaceGen + appearance subrecords. RecordMergeEngine picks DMP bytes
        // when both ESM and DMP have the same signature (subject to SubrecordMergePolicy),
        // so emitting these here lets the prototype's captured FaceGen data override the
        // vanilla NPC's face. Without this, only ACBS would override — vanilla's FGGS/
        // FGGA/FGTS/HCLR/ENAM would persist and the NPC would look like the released NV
        // version even when the DMP captured prototype-era appearance.
        AppendAppearanceSubrecords(npc, subs);

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Emit the appearance subrecords (hair / eyes / hair color / FaceGen morphs) that
    ///     a runtime NPC capture carries. Shared between override <see cref="Encode" /> and
    ///     new-record <see cref="EncodeNew" />.
    /// </summary>
    private static void AppendAppearanceSubrecords(NpcRecord npc, List<EncodedSubrecord> subs)
    {
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

        // v22: GECK and in-game tooltips show the FormID as the NPC's name when the EDID
        // is empty, which leaves the user without a searchable handle in the Object Window.
        // Synthesize one from the captured FullName (preferred) or fall back to a stable
        // FormID-suffixed prefix so the NPC is at least listable and sortable.
        var editorId = !string.IsNullOrEmpty(npc.EditorId)
            ? npc.EditorId
            : SynthesizeEditorId(npc);
        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", editorId));
        if (string.IsNullOrEmpty(npc.EditorId))
        {
            warnings.Add(
                $"New NPC 0x{npc.FormId:X8} had no EditorId in the DMP — synthesized '{editorId}'.");
        }

        if (!string.IsNullOrEmpty(npc.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", npc.FullName));
        }

        if (npc.Stats is null)
        {
            warnings.Add($"New NPC 0x{npc.FormId:X8} has no ACBS — emitting default actor base stats.");
            subs.Add(new EncodedSubrecord("ACBS", BuildDefaultAcbsSubrecord()));
        }
        else
        {
            // v22: force AutoCalcStats (bit 0x10) on new NPCs so the engine derives HP / AP
            // from Level + Class + SPECIAL instead of trusting the captured runtime Flags.
            // Without AutoCalc, prototype NPCs (e.g. Ulysses) spawn dead because the
            // captured Flags is just 0x01 (Biped only) and the manual stats path computes
            // 0 health when class derived-attributes don't match the captured level.
            subs.Add(new EncodedSubrecord("ACBS", BuildAcbsSubrecord(npc.Stats, true)));
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

        // CNTO inventory entries — 8 bytes each: FormID + int32 count. Each CNTO may be
        // followed by an optional COED (12 bytes) carrying ownership/condition data.
        foreach (var item in npc.Inventory)
        {
            var cnto = new byte[8];
            SubrecordEncoder.WriteFormId(cnto, 0, item.ItemFormId);
            SubrecordEncoder.WriteInt32(cnto, 4, item.Count);
            subs.Add(new EncodedSubrecord("CNTO", cnto));
            if (ContEncoder.HasOwnership(item))
            {
                subs.Add(new EncodedSubrecord("COED", ContEncoder.BuildCoedSubrecord(item)));
            }
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

        // Appearance subrecords (HNAM/LNAM/ENAM/HCLR/FGGS/FGGA/FGTS) emit via the shared
        // helper so override and new paths stay in lockstep.
        AppendAppearanceSubrecords(npc, subs);

        if (npc.CombatStyleFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ZNAM", npc.CombatStyleFormId.Value));
        }

        // NPC_ DATA — 11 bytes per FNV schema: int32 BaseHealth + 7 SPECIAL bytes
        // (Strength, Perception, Endurance, Charisma, Intelligence, Agility, Luck).
        // BaseHealth resolves from: model-captured (on-disk DATA or runtime iHealth) →
        // synthesized from SPECIAL Endurance + Stats Level via the CsvActorWriter formula.
        // AutoCalcStats alone (v22) was insufficient; emitting a non-zero value gives the
        // engine a usable fallback when AutoCalc doesn't fire for new NPCs.
        if (npc.SpecialStats is { Length: 7 } special)
        {
            var data = new byte[11];
            SubrecordEncoder.WriteInt32(data, 0, ResolveBaseHealth(npc, special));
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

        // (FGGS/FGGA/FGTS now emitted by AppendAppearanceSubrecords above so override and
        // new paths share the same FaceGen emit logic.)

        if (npc.HeadPartFormIds is { Count: > 0 })
        {
            warnings.Add(
                $"New NPC 0x{npc.FormId:X8} has {npc.HeadPartFormIds.Count} head part(s) — head-part subrecord structure not verified, deferred.");
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Resolve the BaseHealth int32 to emit in the NPC_ DATA subrecord (bytes 0-3).
    ///     Priority: model-captured value (on-disk DATA or runtime iHealth at PDB +196) →
    ///     synthesized from SPECIAL Endurance + Stats Level using the same formula as
    ///     CsvActorWriter (Endurance × 5 + 50 + Level × 10).
    /// </summary>
    private static int ResolveBaseHealth(NpcRecord npc, byte[] special)
    {
        if (npc.BaseHealth is > 0)
        {
            return npc.BaseHealth.Value;
        }

        var endurance = special[2];
        var level = npc.Stats?.Level ?? 1;
        return endurance * 5 + 50 + level * 10;
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

    private static byte[] BuildAcbsSubrecord(ActorBaseSubrecord s, bool forceAutoCalc = false)
    {
        // ACBS Flags bits (per FlagRegistry.ActorBaseFlags / fopdoc):
        //   0x01=Female, 0x02=Essential, 0x04=IsCharGenFacePreset, 0x08=Respawn,
        //   0x10=AutoCalcStats, 0x20=PCLevelMult, 0x40=UseTemplate,
        //   0x80=NoLowLevelProcessing, etc.
        var flags = s.Flags;
        if (forceAutoCalc)
        {
            // Force AutoCalcStats (bit 0x10) so the engine derives HP / AP from Level +
            // Class + SPECIAL instead of trusting the captured runtime Flags. The DMP often
            // captures Flags = 0x00 because the runtime cleared AutoCalc once stats were
            // computed; without re-setting it, the engine reads the manual-stat path which
            // can yield 0 HP on prototype data and spawn the NPC dead.
            //
            // DO NOT OR in 0x01 here — that bit is Female (NOT Biped, despite an earlier
            // misreading of the spec). Forcing 0x01 sex-swaps every male NPC in the output.
            flags |= 0x00000010u;
        }

        var acbs = new byte[24];
        SubrecordEncoder.WriteUInt32(acbs, 0, flags);
        SubrecordEncoder.WriteUInt16(acbs, 4, s.FatigueBase);
        SubrecordEncoder.WriteUInt16(acbs, 6, s.BarterGold);
        SubrecordEncoder.WriteInt16(acbs, 8, s.Level);
        SubrecordEncoder.WriteUInt16(acbs, 10, s.CalcMin);
        SubrecordEncoder.WriteUInt16(acbs, 12, s.CalcMax);
        SubrecordEncoder.WriteUInt16(acbs, 14, s.SpeedMultiplier == 0 ? (ushort)100 : s.SpeedMultiplier);
        SubrecordEncoder.WriteFloat(acbs, 16, s.KarmaAlignment);
        SubrecordEncoder.WriteInt16(acbs, 20, s.DispositionBase);
        SubrecordEncoder.WriteUInt16(acbs, 22, s.TemplateFlags);
        return acbs;
    }

    private static byte[] BuildDefaultAcbsSubrecord()
    {
        // FNV engine defaults when ACBS data is missing: SpeedMult=100, Level=1, others zero.
        var acbs = new byte[24];
        SubrecordEncoder.WriteInt16(acbs, 8, 1);
        SubrecordEncoder.WriteUInt16(acbs, 14, 100);
        return acbs;
    }

    /// <summary>
    ///     Build a fallback EditorID for a new NPC whose DMP capture didn't include one.
    ///     Uses the captured FullName when present (kebab-cased + FormID-suffixed) so the
    ///     name remains stable across runs; otherwise emits a plain <c>DmpNpc_NNNNNNNN</c>
    ///     prefix so the GECK Object Window can still list/sort/search the record.
    /// </summary>
    private static string SynthesizeEditorId(NpcRecord npc)
    {
        var suffix = $"_{npc.FormId:X8}";
        if (string.IsNullOrEmpty(npc.FullName))
        {
            return "DmpNpc" + suffix;
        }

        // Sanitize: keep alnum + underscore, collapse runs, cap length so EDID + suffix
        // stays under the engine's 64-byte cap with slack to spare.
        var maxBaseLen = Math.Max(1, 40 - suffix.Length);
        var buffer = new char[maxBaseLen];
        var write = 0;
        var lastWasUnderscore = false;
        foreach (var c in npc.FullName!)
        {
            if (write >= maxBaseLen)
            {
                break;
            }

            if (char.IsLetterOrDigit(c))
            {
                buffer[write++] = c;
                lastWasUnderscore = false;
            }
            else if (!lastWasUnderscore && write > 0)
            {
                buffer[write++] = '_';
                lastWasUnderscore = true;
            }
        }

        var baseName = write > 0 ? new string(buffer, 0, write).TrimEnd('_') : "DmpNpc";
        return baseName + suffix;
    }
}
