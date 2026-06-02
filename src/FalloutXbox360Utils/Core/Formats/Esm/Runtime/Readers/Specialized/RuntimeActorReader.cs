using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Reader for NPC, Creature, and Faction runtime structs from Xbox 360 memory dumps.
///     Extracts actor stats, inventory, factions, FaceGen morphs, and other character data.
/// </summary>
internal sealed class RuntimeActorReader
{
    private readonly RuntimeMemoryContext _context;
    private readonly RuntimeNpcLayoutProbeResult _npcLayoutProbe;
    private readonly RuntimePdbFieldAccessor _pdbFields;

    // Build-specific offset shift: Proto Debug PDB + _s = actual dump offset.
    private readonly int _s;

    // Delegate NPC field reading to the extracted helper class.
    private RuntimeNpcFieldReader? _npcFieldReader;

    public RuntimeActorReader(RuntimeMemoryContext context, RuntimeNpcLayoutProbeResult? npcLayoutProbe = null)
    {
        _context = context;
        _pdbFields = new RuntimePdbFieldAccessor(context);
        _s = RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(context.MinidumpInfo));
        _npcLayoutProbe = npcLayoutProbe ?? new RuntimeNpcLayoutProbeResult(
            RuntimeNpcLayout.CreateDefault(context.MinidumpInfo),
            true,
            0,
            0,
            0);
    }

    private RuntimeNpcFieldReader NpcFields =>
        _npcFieldReader ??= new RuntimeNpcFieldReader(_context, _npcLayoutProbe.Layout);

    /// <summary>
    ///     Wrap a freshly-read NPC/CREA buffer in a <see cref="PdbStructView" /> so
    ///     callers can look up PDB-named core-region fields via
    ///     <c>view.FormIdPointer / view.Offset</c> rather than hardcoded constants.
    ///     Returns null if the PDB layout for the FormType isn't loaded.
    /// </summary>
    private PdbStructView? WrapActorBufferInView(byte[] buffer, long fileOffset, RuntimeEditorIdEntry entry, byte pdbFormType)
    {
        var layout = PdbStructLayouts.Get(pdbFormType);
        return layout is null ? null : new PdbStructView(_pdbFields, layout, buffer, fileOffset, entry);
    }

    /// <summary>
    ///     Read extended NPC data from a runtime TESNPC struct.
    ///     Returns a NpcRecord populated with stats, race, class, etc.
    ///     Returns null if the struct cannot be read or validation fails.
    /// </summary>
    public NpcRecord? ReadRuntimeNpc(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x2A)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        var buffer = ReadNpcStructBuffer(entry, offset);
        if (buffer == null)
        {
            return null;
        }

        // Validate: FormID at offset 12 should match entry
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        // Open a PdbStructView over the already-read buffer. Core-region reads were
        // migrated in Phase 1B.20; appearance + late-appearance reads were migrated
        // in Phase 5.1 with the +32-byte FR2MatrixVTC padding delta absorbed by a
        // single per-owner WithShift on TESNPC. Late-appearance gets an additive band
        // shift on top only when the probe discovered a distinct LateAppearanceShift
        // (rare — defaults to AppearanceShift). pdb_layouts.json is an embedded
        // resource so view==null means the resource is broken — treat as hard fail.
        var view = WrapActorBufferInView(buffer, offset, entry, pdbFormType: 0x2A);
        if (view is null)
        {
            return null;
        }

        var appearanceShift = _npcLayoutProbe.Layout.AppearanceShift;
        var lateAppearanceShift = _npcLayoutProbe.Layout.LateAppearanceShift;
        view.WithShift("TESNPC", 16 + appearanceShift);
        if (lateAppearanceShift != appearanceShift)
        {
            // Late-region (pOriginalRace onward, PDB +476..+488) needs a delta on top
            // of the TESNPC owner shift. Both shifts compose additively per the
            // PdbStructView.WithShift(string,int) contract.
            view.WithShift(476, int.MaxValue, lateAppearanceShift - appearanceShift);
        }

        // Read script pointer before ACBS validation so it's available even for minimal NPCs.
        // When pFormScript is null we leave the binding null and let
        // PluginBuilder.AttachOrphanScriptsByEditorId resolve it from the parsed SCPT set
        // via the exact `{npcEditorId}Script` / `{npcEditorId}SCRIPT` naming convention.
        // The previous brute-force scan walked every 4-byte slot of the TESNPC struct and
        // accepted any Script* whose EditorId merely *started* with the NPC's EditorId —
        // VMS01DocMitchell falsely matched VMS01PartsSCRIPT, binding the wrong script and
        // producing "Variable ID 0x34 not found" errors plus the Doc Mitchell / Sunny Smiles
        // quest-state regressions. The orphan-attachment heuristic (exact name match against
        // parsed scripts) is the load-bearing recovery path; the prefix-based memory scan
        // adds nothing and false-positives. See memory/quest_script_brute_force_scan.md.
        var scriptFormId = view.FormIdPointer("pFormScript", "TESScriptableForm", 0x11);

        // Read ACBS stats block (PDB TESActorBaseData::actorData)
        var acbsOffset = view.Offset("actorData", "TESActorBaseData") ?? (52 + _s);
        var stats = ReadActorBaseStats(buffer, acbsOffset, offset);
        if (stats == null)
        {
            return CreateMinimalNpc(entry, offset, scriptFormId);
        }

        // Read iHealth (TESHealthForm::iHealth). Sanity-gate to discard reads that
        // overshoot the struct or capture cleared memory; zero is treated as
        // "unknown — let the encoder synthesize from SPECIAL".
        int? baseHealth = null;
        var rawHealth = (int)view.UInt32("iHealth", "TESHealthForm");
        if (rawHealth is > 0 and < 100_000)
        {
            baseHealth = rawHealth;
        }

        // Follow pointer fields to get FormIDs (all PDB-aligned in core region)
        var race = view.FormIdPointer("pFormRace", "TESRaceForm", 0x0C);
        var classFormId = view.FormIdPointer("pCl", "TESNPC", 0x07);
        var deathItem = view.FormIdPointer("pDeathItem", "TESActorBaseData");
        var voiceType = view.FormIdPointer("pVoiceType", "TESActorBaseData", 0x5D);
        var template = view.FormIdPointer("pTemplateForm", "TESActorBaseData", 0x2A);

        // Read sub-item lists (container inventory, faction memberships) — list-head
        // offsets come from PDB via the view; the walker chases heap nodes externally.
        var inventory = NpcFields.ReadNpcInventory(view);
        var factions = NpcFields.ReadNpcFactions(view);

        // Read S.P.E.C.I.A.L. stats (7 bytes at +204)
        var special = NpcFields.ReadNpcSpecial(buffer);

        // Read skills (14 bytes at +211, immediately after SPECIAL)
        var skills = NpcFields.ReadNpcSkills(buffer);

        // Read AI data (aggression, confidence, mood, etc. at +164)
        var aiData = NpcFields.ReadNpcAiData(buffer);

        // Read physical traits (hair, eyes, hair length, combat style, hair color,
        // head parts) via view-based PDB lookups. The TESNPC owner shift registered
        // above absorbs the +32-byte runtime-vs-PDB padding delta uniformly.
        var hair = view.FormIdPointer("pHair", "TESNPC", 0x0A);
        var eyes = view.FormIdPointer("pEyeColor", "TESNPC", 0x0B);
        var combatStyle = view.FormIdPointer("pCombatStyle", "TESNPC", 0x4A);
        var hairLength = RuntimeNpcFieldReader.ReadNpcHairLength(view);
        var hairColor = RuntimeNpcFieldReader.ReadNpcHairColor(view);
        var headPartFormIds = NpcFields.ReadNpcHeadPartFormIds(view);

        // Late-appearance PDB-derived fields. The owner shift + optional band shift
        // (registered above when LateAppearanceShift != AppearanceShift) take care
        // of any per-build drift.
        var originalRace = view.FormIdPointer("pOriginalRace", "TESNPC", 0x0C);
        var faceNpc = view.FormIdPointer("pFaceNPC", "TESNPC", 0x2A);
        var height = RuntimeNpcFieldReader.ReadNpcHeight(view);
        var weight = RuntimeNpcFieldReader.ReadNpcWeight(view);
        var bloodImpactMaterial = RuntimeNpcFieldReader.ReadNpcBloodImpactMaterial(view);
        var raceFacePreset = RuntimeNpcFieldReader.ReadNpcRaceFacePreset(view);

        // Read FaceGen morph data (follow pointers to float arrays in module space)
        var fggs = NpcFields.ReadFaceGenMorphArray(buffer, NpcFields.NpcFggsLayout);
        var fgga = NpcFields.ReadFaceGenMorphArray(buffer, NpcFields.NpcFggaLayout);
        var fgts = NpcFields.ReadFaceGenMorphArray(buffer, NpcFields.NpcFgtsLayout);

        if (!_npcLayoutProbe.IsHighConfidence)
        {
            if (fggs != null || fgga != null || fgts != null)
            {
                Logger.Instance.Debug("[NPC] FaceGen data discarded for 0x{0:X8}: layout probe not high-confidence",
                    entry.FormId);
            }

            fggs = null;
            fgga = null;
            fgts = null;
        }

        // Read AI package list (BSSimpleList<TESPackage*> at TESAIForm::AIPackList)
        var packages = view is not null ? NpcFields.ReadPackageList(view) : [];

        // Read spell/ability list (BSSimpleList<SpellItem*> at TESSpellList::spellList)
        var spells = view is not null ? NpcFields.ReadSpellList(view) : [];

        return new NpcRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Stats = stats,
            BaseHealth = baseHealth,
            Race = race,
            Class = classFormId,
            DeathItem = deathItem,
            VoiceType = voiceType,
            Template = template,
            Inventory = inventory,
            Factions = factions,
            Spells = spells,
            SpecialStats = special,
            Skills = skills,
            AiData = aiData,
            Packages = packages,
            HairFormId = hair,
            HairLength = hairLength,
            EyesFormId = eyes,
            CombatStyleFormId = combatStyle,
            HairColor = hairColor,
            HeadPartFormIds = headPartFormIds.Count > 0 ? headPartFormIds : null,
            OriginalRace = originalRace,
            FaceNpc = faceNpc,
            Height = height,
            Weight = weight,
            BloodImpactMaterial = bloodImpactMaterial,
            RaceFacePreset = raceFacePreset,
            Script = scriptFormId,
            FaceGenGeometrySymmetric = fggs,
            FaceGenGeometryAsymmetric = fgga,
            FaceGenTextureSymmetric = fgts,
            Offset = offset,
            IsBigEndian = true
        };
    }

    private byte[]? ReadNpcStructBuffer(RuntimeEditorIdEntry entry, long fileOffset)
    {
        if (entry.TesFormPointer.HasValue)
        {
            return _context.ReadBytesAtVa(entry.TesFormPointer.Value, NpcFields.NpcStructSize);
        }

        return _context.ReadBytes(fileOffset, NpcFields.NpcStructSize);
    }

    /// <summary>
    ///     Read extended creature data from a runtime TESCreature struct.
    ///     Returns a CreatureRecord with skills, type, and model path.
    /// </summary>
    public CreatureRecord? ReadRuntimeCreature(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x2B)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + NpcFields.CreaStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[NpcFields.CreaStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, NpcFields.CreaStructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        // Open a PdbStructView over the CREA buffer. TESCreature is fully PDB-aligned
        // (no FR2MatrixVTC padding drift like TESNPC has), so every field below comes
        // from view.* lookups instead of hardcoded offset constants — Phase 1B.21.
        var creaView = WrapActorBufferInView(buffer, offset, entry, pdbFormType: 0x2B);
        if (creaView is null)
        {
            return null;
        }

        // CREATURE_DATA struct (TESCreature::Data, PDB +316, 4 bytes): { Type, CombatSkill,
        // MagicSkill, StealthSkill } as 4 consecutive bytes.
        var dataOffset = creaView.Offset("Data", "TESCreature") ?? (300 + _s);
        byte creatureType = 0, combatSkill = 0, magicSkill = 0, stealthSkill = 0;
        if (dataOffset + 4 <= buffer.Length)
        {
            creatureType = buffer[dataOffset];
            combatSkill = buffer[dataOffset + 1];
            magicSkill = buffer[dataOffset + 2];
            stealthSkill = buffer[dataOffset + 3];
        }
        if (creatureType > 7)
        {
            creatureType = 0; // Validate creature type (0-7)
        }

        var attackDamage = (short)creaView.UInt16("sAttackDamage", "TESAttackDamageForm");
        var modelPath = creaView.BsString("cModel", "TESModel");
        var scriptFormId = creaView.FormIdPointer("pFormScript", "TESScriptableForm", 0x11);

        // Read ACBS (actor base stats); structure shared with TESNPC.
        var acbsOffset = creaView.Offset("actorData", "TESActorBaseData") ?? (52 + _s);
        var stats = ReadCreatureActorBaseStats(buffer, acbsOffset, offset);

        // Read AI data (TESAIForm AIData struct, shared base class with NPC).
        var aiData = NpcFields.ReadNpcAiData(buffer);

        // Shared TESActorBaseData / TESAIForm fields (same offsets as TESNPC).
        var deathItem = creaView.FormIdPointer("pDeathItem", "TESActorBaseData");
        var packages = NpcFields.ReadPackageList(creaView);
        var factions = NpcFields.ReadNpcFactions(creaView);

        // Read OBND bounding box from BoundData (TESBoundObject base — first 12 bytes are
        // X1,Y1,Z1,X2,Y2,Z2 as int16). Zero-bounds collapse to null so engine doesn't get
        // a degenerate bbox.
        ObjectBounds? bounds = null;
        var boundsOffset = creaView.Offset("BoundData", "TESBoundObject");
        if (boundsOffset is { } bOff && bOff + 12 <= buffer.Length)
        {
            var candidate = RecordParserContext.ReadObjectBounds(
                buffer.AsSpan(bOff, 12), true);
            if (candidate is not { X1: 0, Y1: 0, Z1: 0, X2: 0, Y2: 0, Z2: 0 })
            {
                bounds = candidate;
            }
        }

        var voiceType = creaView.FormIdPointer("pVoiceType", "TESActorBaseData", 0x5D);
        var template = creaView.FormIdPointer("pTemplateForm", "TESActorBaseData", 0x2A);
        var combatStyle = creaView.FormIdPointer("pCombatStyle", "TESCreature", 0x4A);
        var bodyPartData = creaView.FormIdPointer("pBodyPartData", "TESCreature");
        var impactDataSet = creaView.FormIdPointer("pImpactDataSet", "TESCreature");

        var turnSpeed = (float?)creaView.Float("fTurnSpeed", "TESCreature");
        var footWeight = (float?)creaView.Float("fFootWeight", "TESCreature");
        var baseScale = (float?)creaView.Float("fBaseScale", "TESCreature");
        var impactMaterial = (uint?)creaView.UInt32("eBloodImpactMaterial", "TESCreature");
        var soundLevel = (uint?)creaView.UInt32("eSoundLevel", "TESCreature");

        return new CreatureRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Bounds = bounds,
            Stats = stats,
            AiData = aiData,
            DeathItem = deathItem,
            VoiceType = voiceType,
            Template = template,
            CombatStyleFormId = combatStyle,
            BodyData = bodyPartData,
            ImpactDataSet = impactDataSet,
            TurningSpeed = turnSpeed,
            FootWeight = footWeight,
            BaseScale = baseScale,
            ImpactMaterialType = impactMaterial,
            SoundLevel = soundLevel,
            CreatureType = creatureType,
            CombatSkill = combatSkill,
            MagicSkill = magicSkill,
            StealthSkill = stealthSkill,
            AttackDamage = attackDamage,
            Packages = packages,
            Factions = factions,
            Script = scriptFormId,
            ModelPath = modelPath,
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read extended faction data from a runtime TESFaction struct.
    ///     Returns a FactionRecord with Flags and relation data, or null if validation fails.
    ///     Note: Rank lists require additional BSSimpleList traversal.
    /// </summary>
    public FactionRecord? ReadRuntimeFaction(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x08)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + FactStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[FactStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, FactStructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        var flags = BinaryUtils.ReadUInt32BE(buffer, FactFlagsOffset);
        var relations = ReadFactionRelations(buffer);

        // Read display name — hash table already has it from FullNameOffset=44
        var fullName = entry.DisplayName ?? _context.ReadBsStringT(offset, FactFullNameOffset);

        return new FactionRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            Flags = flags,
            Relations = relations,
            Offset = offset,
            IsBigEndian = true
        };
    }

    private List<FactionRelation> ReadFactionRelations(byte[] factionBuffer)
    {
        var relations = new List<FactionRelation>();

        foreach (var reactionVa in _context.WalkInlineBSSimpleListItemPointers(
                     factionBuffer, FactReactionListOffset))
        {
            var relation = ReadFactionRelation(reactionVa);
            if (relation != null)
            {
                relations.Add(relation);
            }
        }

        return relations;
    }

    private FactionRelation? ReadFactionRelation(uint reactionVa)
    {
        if (reactionVa == 0 || !_context.IsValidPointer(reactionVa))
        {
            return null;
        }

        var reactionBuffer = _context.ReadBytesAtVa(reactionVa, GroupReactionSize);
        if (reactionBuffer == null)
        {
            return null;
        }

        var factionVa = BinaryUtils.ReadUInt32BE(reactionBuffer);
        var factionFormId = _context.FollowPointerVaToFormId(factionVa, 0x08);
        if (factionFormId is null or 0)
        {
            return null;
        }

        var modifier = RuntimeMemoryContext.ReadInt32BE(reactionBuffer, 4);
        if (modifier is < MinFactionReactionModifier or > MaxFactionReactionModifier)
        {
            return null;
        }

        var combatReaction = BinaryUtils.ReadUInt32BE(reactionBuffer, 8);
        if (combatReaction > MaxFactionCombatReaction)
        {
            return null;
        }

        return new FactionRelation(factionFormId.Value, modifier, combatReaction);
    }

    /// <summary>
    ///     Read and validate the ACBS stats block (24 bytes).
    ///     Layout (all big-endian):
    ///     +0:  flags (uint32)
    ///     +4:  fatigueBase (uint16)
    ///     +6:  barterGold (uint16)
    ///     +8:  level (int16) — negative means level is offset-based
    ///     +10: calcMin (uint16)
    ///     +12: calcMax (uint16)
    ///     +14: speedMultiplier (uint16)
    ///     +16: karmaAlignment (float)
    ///     +20: dispositionBase (int16)
    ///     +22: templateFlags (uint16)
    /// </summary>
    internal static ActorBaseSubrecord? ReadActorBaseStats(byte[] buffer, int acbsStart, long structOffset)
    {
        if (acbsStart + 24 > buffer.Length)
        {
            return null;
        }

        var flags = BinaryUtils.ReadUInt32BE(buffer, acbsStart);
        var fatigueBase = BinaryUtils.ReadUInt16BE(buffer, acbsStart + 4);
        var barterGold = BinaryUtils.ReadUInt16BE(buffer, acbsStart + 6);
        var level = (short)BinaryUtils.ReadUInt16BE(buffer, acbsStart + 8);
        var calcMin = BinaryUtils.ReadUInt16BE(buffer, acbsStart + 10);
        var calcMax = BinaryUtils.ReadUInt16BE(buffer, acbsStart + 12);
        var speedMultiplier = BinaryUtils.ReadUInt16BE(buffer, acbsStart + 14);
        var karma = BinaryUtils.ReadFloatBE(buffer, acbsStart + 16);
        var disposition = (short)BinaryUtils.ReadUInt16BE(buffer, acbsStart + 20);
        var templateFlags = BinaryUtils.ReadUInt16BE(buffer, acbsStart + 22);

        // Validation: check values are in reasonable ranges
        // Fatigue: 0-5000 (most NPCs 0-200)
        if (fatigueBase > 5000)
        {
            Logger.Instance.Debug("[ACBS] Rejected at offset 0x{0:X}: fatigue={1}", structOffset + acbsStart,
                fatigueBase);
            return null;
        }

        // Barter gold: 0-50000
        if (barterGold > 50000)
        {
            Logger.Instance.Debug("[ACBS] Rejected at offset 0x{0:X}: barterGold={1}", structOffset + acbsStart,
                barterGold);
            return null;
        }

        // Level: when PC Level Mult flag (bit 7) is set, level is a multiplier × 100
        // (e.g., 800 = 8.00× player level), so allow up to 1000.
        // Otherwise, fixed level: -128 to 100.
        var isPcLevelMult = (flags & 0x80) != 0;
        var maxLevel = isPcLevelMult ? 1000 : 100;
        if (level < -128 || level > maxLevel)
        {
            Logger.Instance.Debug("[ACBS] Rejected at offset 0x{0:X}: level={1} (max={2})", structOffset + acbsStart,
                level, maxLevel);
            return null;
        }

        // Speed multiplier: typically 70-200 (100 = normal)
        if (speedMultiplier > 500)
        {
            Logger.Instance.Debug("[ACBS] Rejected at offset 0x{0:X}: speedMult={1}", structOffset + acbsStart,
                speedMultiplier);
            return null;
        }

        // Karma: should be a normal float
        if (!RuntimeMemoryContext.IsNormalFloat(karma))
        {
            Logger.Instance.Debug("[ACBS] Rejected at offset 0x{0:X}: karma={1} (not normal float)",
                structOffset + acbsStart, karma);
            return null;
        }

        // CalcMin/CalcMax: 0-100
        if (calcMin > 100 || calcMax > 100)
        {
            Logger.Instance.Debug("[ACBS] Rejected at offset 0x{0:X}: calcMin={1}, calcMax={2}",
                structOffset + acbsStart, calcMin, calcMax);
            return null;
        }

        return new ActorBaseSubrecord(
            flags, fatigueBase, barterGold, level, calcMin, calcMax,
            speedMultiplier, karma, disposition, templateFlags,
            structOffset + acbsStart, true);
    }

    /// <summary>
    ///     Read ActorBaseSubrecord (ACBS) from buffer at the given offset.
    ///     Same structure for both NPC and Creature. Uses the same layout as the
    ///     existing NPC ACBS reader.
    /// </summary>
    private static ActorBaseSubrecord? ReadCreatureActorBaseStats(byte[] buffer, int acbsOffset, long structOffset)
    {
        if (acbsOffset + 24 > buffer.Length)
        {
            return null;
        }

        var flags = BinaryUtils.ReadUInt32BE(buffer, acbsOffset);
        var fatigueBase = BinaryUtils.ReadUInt16BE(buffer, acbsOffset + 4);
        var barterGold = BinaryUtils.ReadUInt16BE(buffer, acbsOffset + 6);
        var level = (short)BinaryUtils.ReadUInt16BE(buffer, acbsOffset + 8);
        var calcMin = BinaryUtils.ReadUInt16BE(buffer, acbsOffset + 10);
        var calcMax = BinaryUtils.ReadUInt16BE(buffer, acbsOffset + 12);
        var speedMultiplier = BinaryUtils.ReadUInt16BE(buffer, acbsOffset + 14);
        var karma = BinaryUtils.ReadFloatBE(buffer, acbsOffset + 16);
        var dispositionBase = (short)BinaryUtils.ReadUInt16BE(buffer, acbsOffset + 20);
        var templateFlags = BinaryUtils.ReadUInt16BE(buffer, acbsOffset + 22);

        // Basic validation - reject garbage values
        if (fatigueBase > 5000 || barterGold > 50000 || speedMultiplier > 500)
        {
            return null;
        }

        // Level should be reasonable (-127 to 255 for creatures/NPCs)
        if (level < -127 || level > 255)
        {
            return null;
        }

        // Disposition should be reasonable (-200 to 200)
        if (dispositionBase < -200 || dispositionBase > 200)
        {
            return null;
        }

        // Karma must be a normal float in reasonable range (-1000 to 1000)
        if (!RuntimeMemoryContext.IsNormalFloat(karma) || karma < -1000 || karma > 1000)
        {
            return null;
        }

        return new ActorBaseSubrecord(
            flags, fatigueBase, barterGold, level, calcMin, calcMax,
            speedMultiplier, karma, dispositionBase, templateFlags,
            structOffset + acbsOffset, true);
    }

    // NPC field reading methods (ReadNpcAiData, ReadNpcFactions, ReadPackageList, ReadNpcHairLength,
    // ReadNpcInventory, ReadNpcSkills, ReadNpcSpecial, ReadFaceGenMorphArray) are delegated to NpcFields.

    /// <summary>
    ///     Creates a minimal NPC record using only hash table data (no struct reading).
    /// </summary>
    private static NpcRecord CreateMinimalNpc(RuntimeEditorIdEntry entry, long offset, uint? scriptFormId = null)
    {
        return new NpcRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Script = scriptFormId,
            Offset = offset,
            IsBigEndian = true
        };
    }

    // NPC struct layout constants and ReadContainerObject are provided by NpcFields.

    #region Faction Struct Layout (Proto Debug PDB base + _s)

    // TESFaction: PDB size 76, Debug dump 80, Release dump 92
    private int FactStructSize => 76 + _s;
    private int FactReactionListOffset => 40 + _s;
    private int FactFlagsOffset => 52 + _s;
    private int FactFullNameOffset => 28 + _s;
    private const int GroupReactionSize = 12;
    private const int MinFactionReactionModifier = -100000;
    private const int MaxFactionReactionModifier = 100000;
    private const uint MaxFactionCombatReaction = 10;

    #endregion

    // Creature struct layout constants are provided by NpcFields.

    #region Actor Value Info (AVIF) — Runtime Struct Layout

    // ActorValueInfo: PDB size 212
    // Key fields for skill name resolution: FullName, Abbreviation, Icon
    private const int AvifStructSize = 212;
    private const int AvifFullNameOffset = 44; // TESFullName.cFullName (BSStringT)
    private const int AvifTextureOffset = 64; // TESTexture.TextureName (BSStringT) — icon path
    private const int AvifAbbreviationOffset = 76; // ActorValueInfo.sAbbreviation (BSStringT)

    /// <summary>
    ///     Read an ActorValueInfo record from a runtime C++ struct.
    ///     Extracts FullName, Icon, and Abbreviation via BSStringT reads.
    /// </summary>
    public ActorValueInfoRecord? ReadRuntimeAvif(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x59)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + AvifStructSize > _context.FileSize)
        {
            return null;
        }

        // Validate FormID at +12
        var buffer = new byte[16];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, 16);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        var fullName = entry.DisplayName ?? _context.ReadBsStringT(offset, AvifFullNameOffset);
        var icon = _context.ReadBsStringT(offset, AvifTextureOffset);
        var abbreviation = _context.ReadBsStringT(offset, AvifAbbreviationOffset);

        return new ActorValueInfoRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            Icon = icon,
            Abbreviation = abbreviation,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #endregion
}
