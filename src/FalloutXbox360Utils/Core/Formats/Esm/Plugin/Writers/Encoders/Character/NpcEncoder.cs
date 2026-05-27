using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;

/// <summary>
///     Encodes an <see cref="NpcRecord" /> as PC-format NPC_ subrecord bytes.
///     Override encoding emits captured actor stats, gameplay references, inventory, AI,
///     appearance, FaceGen, SPECIAL, and skill data while preserving master identity fields
///     such as EDID/FULL and the master record FormID.
///     ACBS layout: uint32 Flags(0) + uint16 Fatigue(4) + uint16 BarterGold(6) +
///     int16 Level(8) + uint16 CalcMin(10) + uint16 CalcMax(12) +
///     uint16 SpeedMult(14) + float KarmaAlignment(16) +
///     int16 DispositionBase(20) + uint16 TemplateFlags(22).
/// </summary>
public sealed class NpcEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<FactionMembership, object?>> SnamExtractors = new(StringComparer.Ordinal)
    {
        ["Faction"] = m => m.FactionFormId,
        ["Rank"] = m => (byte)m.Rank,
    };

    private static readonly Dictionary<string, Func<NpcAiData, object?>> AidtExtractors = new(StringComparer.Ordinal)
    {
        ["Aggression"] = m => m.Aggression,
        ["Confidence"] = m => m.Confidence,
        ["Energy"] = m => m.EnergyLevel,
        ["Responsibility"] = m => m.Responsibility,
        ["Mood"] = m => m.Mood,
        ["ServiceFlags"] = m => m.Flags,
        ["Assistance"] = m => m.Assistance,
        // TrainingSkill / TrainingLevel / AggroRadiusBehavior / AggroRadius not modeled → zero-fill.
    };

    private static readonly Dictionary<string, Func<NpcRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["BaseHealth"] = m => ResolveBaseHealth(m, m.SpecialStats!),
        ["Strength"] = m => m.SpecialStats![0],
        ["Perception"] = m => m.SpecialStats![1],
        ["Endurance"] = m => m.SpecialStats![2],
        ["Charisma"] = m => m.SpecialStats![3],
        ["Intelligence"] = m => m.SpecialStats![4],
        ["Agility"] = m => m.SpecialStats![5],
        ["Luck"] = m => m.SpecialStats![6],
    };

    public string RecordType => "NPC_";
    public Type ModelType => typeof(NpcRecord);

    public EncodedRecord Encode(object model)
    {
        var npc = (NpcRecord)model;
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (npc.Stats is not null)
        {
            // Force AutoCalcStats on override ACBS too. The override path replaces the
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

        // Override-mode runtime/gameplay and appearance subrecords. RecordMergeEngine picks
        // DMP bytes when both ESM and DMP have the same signature, so emitting these here lets
        // prototype inventory, packages, class/race, voice, and FaceGen override the final
        // master NPC while EDID/FULL/FormID remain anchored to the master.
        AppendActorGameplaySubrecords(npc, subs);
        AppendAppearanceSubrecords(npc, subs);
        AppendActorTailSubrecords(npc, subs);

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static void AppendActorGameplaySubrecords(
        NpcRecord npc,
        List<EncodedSubrecord> subs,
        uint? resolvedTemplate = null,
        IReadOnlySet<uint>? validPackageFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null,
        Action<uint>? onPkidDropped = null,
        Action<uint, uint>? onPkidRemapped = null,
        IReadOnlySet<uint>? validFormIds = null,
        List<string>? warnings = null)
    {
        // SNAM faction memberships — 8 bytes each: FormID + uint8 rank + 3 padding.
        foreach (var membership in npc.Factions)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("SNAM", "NPC_", 8, membership, SnamExtractors));
        }

        if (npc.DeathItem.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("INAM", npc.DeathItem.Value));
        }

        if (npc.VoiceType.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("VTCK", npc.VoiceType.Value));
        }

        if (resolvedTemplate.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("TPLT", resolvedTemplate.Value));
        }

        if (npc.Race.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("RNAM", npc.Race.Value));
        }

        // SPLO — spell/ability list. One subrecord per spell. Skip dangling entries.
        foreach (var spellId in npc.Spells)
        {
            var resolvedSpell = FormIdReferenceResolver.Resolve(spellId, validFormIds, remapTable);
            if (resolvedSpell.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SPLO", resolvedSpell.Value));
            }
        }

        if (npc.Script.HasValue)
        {
            var resolvedScript = FormIdReferenceResolver.Resolve(
                npc.Script.Value, validFormIds, remapTable);
            if (resolvedScript.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", resolvedScript.Value));
            }
            else
            {
                warnings?.Add(
                    $"New NPC_ 0x{npc.FormId:X8} SCRI 0x{npc.Script.Value:X8} dangles — subrecord skipped.");
            }
        }

        // CNTO inventory entries — 8 bytes each: FormID + int32 count. Each CNTO may be
        // followed by an optional COED (12 bytes) carrying ownership/condition data. Skip
        // entries whose item FormID dangles in master ∪ emitted (engine would log
        // "Unable to find container object on owner object" and drop the line anyway).
        var droppedItems = 0;
        foreach (var item in npc.Inventory)
        {
            var resolvedItem = FormIdReferenceResolver.Resolve(
                item.ItemFormId, validFormIds, remapTable);
            if (!resolvedItem.HasValue)
            {
                droppedItems++;
                continue;
            }

            var cnto = new byte[8];
            SubrecordEncoder.WriteFormId(cnto, 0, resolvedItem.Value);
            SubrecordEncoder.WriteInt32(cnto, 4, item.Count);
            subs.Add(new EncodedSubrecord("CNTO", cnto));
            if (ContEncoder.HasOwnership(item))
            {
                subs.Add(new EncodedSubrecord("COED", ContEncoder.BuildCoedSubrecord(item)));
            }
        }
        if (droppedItems > 0)
        {
            warnings?.Add(
                $"New NPC_ 0x{npc.FormId:X8} dropped {droppedItems} CNTO inventory entry/entries " +
                "with dangling item FormID (engine 'Unable to find container object on owner object').");
        }

        // AIDT — 20 bytes: AI behavior data.
        if (npc.AiData is not null)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("AIDT", "NPC_", 20, npc.AiData, AidtExtractors));
        }

        // PKID — AI packages. One subrecord per package FormID.
        // Dangling PKIDs (PACK FormIDs not in master and not in our emitted set) were the
        // leading suspect for the "every NPC plays the crucified idle every few seconds"
        // regression: an NPC with all-dangling packages has no AI driver, so the engine
        // falls through to a default idle pose. Remap via the runtime→emitted alias table
        // when possible; otherwise drop the PKID entry entirely.
        foreach (var rawPkgId in npc.Packages)
        {
            if (rawPkgId == 0)
            {
                continue;
            }

            var pkgId = rawPkgId;
            if (validPackageFormIds is not null && !validPackageFormIds.Contains(pkgId))
            {
                if (remapTable is not null
                    && remapTable.TryGetValue(pkgId, out var remapped)
                    && remapped != pkgId
                    && validPackageFormIds.Contains(remapped))
                {
                    onPkidRemapped?.Invoke(rawPkgId, remapped);
                    pkgId = remapped;
                }
                else
                {
                    onPkidDropped?.Invoke(rawPkgId);
                    continue;
                }
            }

            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("PKID", pkgId));
        }

        if (npc.Class.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("CNAM", npc.Class.Value));
        }
    }

    /// <summary>
    ///     Emit the appearance subrecords (head parts / hair / eyes / hair color / FaceGen morphs)
    ///     that a runtime NPC capture carries. Shared between override <see cref="Encode" /> and
    ///     new-record <see cref="EncodeNew" />.
    ///     Canonical order per FNVEdit wbNPC_:
    ///     PNAM*(head parts, one per HDPT) → HNAM (hair) → LNAM (hair length) → ENAM (eyes) →
    ///     HCLR (hair color) → FGGS/FGGA/FGTS (FaceGen morphs).
    /// </summary>
    private static void AppendAppearanceSubrecords(NpcRecord npc, List<EncodedSubrecord> subs)
    {
        // PNAM — head parts. One subrecord per HDPT FormID. Skip zero entries (placeholder).
        // The renderer attaches each head part to the NPC's head node; missing PNAMs leave
        // the head subgraph incomplete and can AV in BSFadeNode during render.
        if (npc.HeadPartFormIds is { Count: > 0 })
        {
            foreach (var headPartFormId in npc.HeadPartFormIds)
            {
                if (headPartFormId == 0u)
                {
                    continue;
                }

                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("PNAM", headPartFormId));
            }
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

    private static void AppendActorTailSubrecords(NpcRecord npc, List<EncodedSubrecord> subs)
    {
        if (npc.CombatStyleFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ZNAM", npc.CombatStyleFormId.Value));
        }

        if (npc.Height.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("NAM6", npc.Height.Value));
        }

        if (npc.Weight.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("NAM7", npc.Weight.Value));
        }

        // NPC_ DATA — 11 bytes per FNV schema: int32 BaseHealth + 7 SPECIAL bytes
        // (Strength, Perception, Endurance, Charisma, Intelligence, Agility, Luck).
        if (npc.SpecialStats is { Length: 7 })
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "NPC_", 11, npc, DataExtractors));
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
    }

    /// <summary>
    ///     ACBS TemplateFlags bit that enables inheriting traits (face / race / weight /
    ///     height / hair / eyes / facegen) from the Template NPC. Without this bit the
    ///     engine tries to load the new NPC's own FaceGen .NIF / .dds files, which we don't
    ///     generate — the missing files leave a half-initialized scene graph that the
    ///     renderer access-violates while walking. With this bit set, the engine inherits
    ///     the template's renderable face/body and never tries to load our missing files.
    /// </summary>
    private const ushort TemplateFlagUseTraits = 0x0001;

    /// <summary>
    ///     Encode a new NPC_ record from scratch. Includes EDID, FULL?, ACBS, RNAM (race),
    ///     plus optional FormID / faction / spell / inventory / package / AI subrecords.
    ///     NPCs without complete captured FaceGen get a renderable master template with
    ///     UseTraits forced; NPCs with complete captured FaceGen keep their own race/face
    ///     data so template trait inheritance does not bleed in the wrong body tint.
    /// </summary>
    internal static EncodedRecord EncodeNew(
        NpcRecord npc,
        IReadOnlySet<uint>? masterFormIds = null,
        IReadOnlyDictionary<uint, uint>? masterNpcByRace = null,
        IReadOnlySet<uint>? validPackageFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null,
        IReadOnlySet<uint>? validFormIds = null)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        var hasCompleteCapturedFaceGen = HasCompleteCapturedFaceGen(npc);

        // Pick a renderable master NPC to use as Template so the engine inherits its face/
        // body instead of trying to load missing FaceGen files. NPCs with complete captured
        // FaceGen deliberately skip this path: their own RNAM/HCLR/FGGS/FGGA/FGTS should
        // drive appearance, and UseTraits can inherit the template's body skin instead.
        //
        // Strategy for incomplete captures:
        //  1. If npc.Template is already a master NPC, keep it.
        //  2. Otherwise prefer the captured face-template NPC if it is a master record.
        //  3. Otherwise look up a master NPC of the same race.
        //  4. If neither is available, fall through with template unset and warn — engine
        //     will still try to render but at least won't have a TPLT loop into a dead NPC.
        var resolvedTemplate = hasCompleteCapturedFaceGen ? null : npc.Template;
        var templateIsMaster = resolvedTemplate.HasValue
                               && masterFormIds is not null
                               && masterFormIds.Contains(resolvedTemplate.Value);
        string? templateRetargetReason = null;
        if (!hasCompleteCapturedFaceGen
            && !templateIsMaster
            && npc.FaceNpc.HasValue
            && masterFormIds is not null
            && masterFormIds.Contains(npc.FaceNpc.Value))
        {
            resolvedTemplate = npc.FaceNpc.Value;
            templateIsMaster = true;
            templateRetargetReason = "face template";
        }
        if (!hasCompleteCapturedFaceGen
            && !templateIsMaster
            && masterNpcByRace is not null
            && npc.Race.HasValue
            && masterNpcByRace.TryGetValue(npc.Race.Value, out var raceTemplate))
        {
            resolvedTemplate = raceTemplate;
            templateIsMaster = true;
            templateRetargetReason = "same race";
        }

        // GECK and in-game tooltips show the FormID as the NPC's name when the EDID
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

        // OR-in the Use-Traits template-flag bit only when an incomplete capture needs a
        // renderable template. Complete captures keep TemplateFlags untouched so own race /
        // face data controls body skin and head appearance.
        var extraTemplateFlags = templateIsMaster ? TemplateFlagUseTraits : (ushort)0;

        if (npc.Stats is null)
        {
            warnings.Add($"New NPC 0x{npc.FormId:X8} has no ACBS — emitting default actor base stats.");
            subs.Add(new EncodedSubrecord("ACBS", BuildDefaultAcbsSubrecord(extraTemplateFlags)));
        }
        else
        {
            // Force AutoCalcStats (bit 0x10) on new NPCs so the engine derives HP / AP
            // from Level + Class + SPECIAL instead of trusting the captured runtime Flags.
            // Without AutoCalc, prototype NPCs (e.g. Ulysses) spawn dead because the
            // captured Flags is just 0x01 (Biped only) and the manual stats path computes
            // 0 health when class derived-attributes don't match the captured level.
            subs.Add(new EncodedSubrecord("ACBS",
                BuildAcbsSubrecord(npc.Stats, true, extraTemplateFlags)));
        }

        if (templateIsMaster && resolvedTemplate.HasValue && resolvedTemplate.Value != npc.Template)
        {
            warnings.Add(
                $"New NPC 0x{npc.FormId:X8} template retargeted from " +
                $"0x{npc.Template ?? 0u:X8} to master 0x{resolvedTemplate.Value:X8} " +
                $"({templateRetargetReason ?? "master fallback"}) — avoids missing-FaceGen render crash.");
        }

        if (!npc.Race.HasValue)
        {
            warnings.Add($"New NPC 0x{npc.FormId:X8} has no race — racial traits will use engine default.");
        }

        var droppedPkids = 0;
        var remappedPkids = 0;
        AppendActorGameplaySubrecords(npc, subs, resolvedTemplate,
            validPackageFormIds,
            remapTable,
            onPkidDropped: _ => droppedPkids++,
            onPkidRemapped: (_, _) => remappedPkids++,
            validFormIds: validFormIds,
            warnings: warnings);
        if (droppedPkids > 0)
        {
            warnings.Add(
                $"New NPC 0x{npc.FormId:X8} dropped {droppedPkids} PKID(s) referencing dangling " +
                "PACK FormIDs (not in master ∪ emitted, no remap available). The engine logged " +
                "these as 'Could not find Package' previously and the NPC fell through to a " +
                "default idle.");
        }
        if (remappedPkids > 0)
        {
            warnings.Add(
                $"New NPC 0x{npc.FormId:X8} remapped {remappedPkids} PKID FormID(s) via the " +
                "runtime→emitted alias table.");
        }

        // Appearance subrecords (HNAM/LNAM/ENAM/HCLR/FGGS/FGGA/FGTS) emit via the shared
        // helper so override and new paths stay in lockstep.
        AppendAppearanceSubrecords(npc, subs);
        AppendActorTailSubrecords(npc, subs);

        // FGGS/FGGA/FGTS and PNAM (head parts) now emitted by AppendAppearanceSubrecords
        // above so override and new paths share the same FaceGen / head-part emit logic.

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

    // ACBS bytes-builder + flag-policy lives in ActorBaseAcbsBuilder, shared with CreaEncoder.
    // BuildAcbsSubrecord(stats, forceAutoCalc, extra) → ActorBaseAcbsBuilder.Build("NPC_", ...).
    // BuildDefaultAcbsSubrecord(extra)              → ActorBaseAcbsBuilder.BuildDefault("NPC_", extra).
    private static byte[] BuildAcbsSubrecord(
        ActorBaseSubrecord s,
        bool forceAutoCalc = false,
        ushort extraTemplateFlags = 0)
    {
        return ActorBaseAcbsBuilder.Build("NPC_", s, forceAutoCalc, extraTemplateFlags);
    }

    private static byte[] BuildDefaultAcbsSubrecord(ushort extraTemplateFlags = 0)
    {
        return ActorBaseAcbsBuilder.BuildDefault("NPC_", extraTemplateFlags);
    }

    internal static bool HasCompleteCapturedFaceGen(NpcRecord npc)
    {
        return npc.Race.HasValue
               && npc.FaceGenGeometrySymmetric is { Length: 50 }
               && npc.FaceGenGeometryAsymmetric is { Length: 30 }
               && npc.FaceGenTextureSymmetric is { Length: 50 };
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
