using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reader for NPC, Creature, and Faction runtime structs from Xbox 360 memory dumps.
///     Extracts actor stats, inventory, factions, FaceGen morphs, and other character data.
/// </summary>
internal sealed class RuntimeActorReader(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;

    // Build-specific offset shift: Proto Debug PDB + _s = actual dump offset.
    // Debug dumps: _s=4, Release dumps: _s=16.
    private readonly int _s = RuntimeBuildOffsets.GetPdbShift(
        MinidumpAnalyzer.DetectBuildType(context.MinidumpInfo));

    // Delegate NPC field reading to the extracted helper class.
    private RuntimeNpcFieldReader? _npcFieldReader;
    private RuntimeNpcFieldReader NpcFields => _npcFieldReader ??= new RuntimeNpcFieldReader(_context, _s);

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
        if (offset + NpcFields.NpcStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[NpcFields.NpcStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, NpcFields.NpcStructSize);
        }
        catch
        {
            return null;
        }

        // Validate: FormID at offset 12 should match entry
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        // Read script pointer before ACBS validation so it's available even for minimal NPCs
        var scriptFormId = _context.FollowPointerToFormId(buffer, NpcFields.NpcScriptPtrOffset);

        // Read ACBS stats block at empirically verified offset +68
        var stats = ReadActorBaseStats(buffer, NpcFields.NpcAcbsOffset, offset);
        if (stats == null)
        {
            return CreateMinimalNpc(entry, offset, scriptFormId);
        }

        // Follow pointer fields to get FormIDs
        var race = _context.FollowPointerToFormId(buffer, NpcFields.NpcRacePtrOffset);
        var classFormId = _context.FollowPointerToFormId(buffer, NpcFields.NpcClassPtrOffset);
        var deathItem = _context.FollowPointerToFormId(buffer, NpcFields.NpcDeathItemPtrOffset);
        var voiceType = _context.FollowPointerToFormId(buffer, NpcFields.NpcVoiceTypePtrOffset);
        var template = _context.FollowPointerToFormId(buffer, NpcFields.NpcTemplatePtrOffset);

        // Read sub-item lists (container inventory, faction memberships)
        var inventory = NpcFields.ReadNpcInventory(buffer, offset);
        var factions = NpcFields.ReadNpcFactions(buffer);

        // Read S.P.E.C.I.A.L. stats (7 bytes at +204)
        var special = NpcFields.ReadNpcSpecial(buffer);

        // Read skills (14 bytes at +211, immediately after SPECIAL)
        var skills = NpcFields.ReadNpcSkills(buffer);

        // Read AI data (aggression, confidence, mood, etc. at +164)
        var aiData = NpcFields.ReadNpcAiData(buffer);

        // Read physical traits (hair, eyes, hair length, combat style)
        var hair = _context.FollowPointerToFormId(buffer, NpcFields.NpcHairPtrOffset);
        var eyes = _context.FollowPointerToFormId(buffer, NpcFields.NpcEyesPtrOffset);
        var combatStyle = _context.FollowPointerToFormId(buffer, NpcFields.NpcCombatStylePtrOffset);
        var hairLength = NpcFields.ReadNpcHairLength(buffer);

        // Read FaceGen morph data (follow pointers to float arrays in module space)
        var fggs = NpcFields.ReadFaceGenMorphArray(buffer, NpcFields.NpcFggsPointerOffset, NpcFields.NpcFggsCountOffset);
        var fgga = NpcFields.ReadFaceGenMorphArray(buffer, NpcFields.NpcFggaPointerOffset, NpcFields.NpcFggaCountOffset);
        var fgts = NpcFields.ReadFaceGenMorphArray(buffer, NpcFields.NpcFgtsPointerOffset, NpcFields.NpcFgtsCountOffset);

        // Read AI package list (BSSimpleList<TESPackage*> at TESAIForm+24)
        var packages = NpcFields.ReadPackageList(buffer);

        return new NpcRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Stats = stats,
            Race = race,
            Class = classFormId,
            DeathItem = deathItem,
            VoiceType = voiceType,
            Template = template,
            Inventory = inventory,
            Factions = factions,
            SpecialStats = special,
            Skills = skills,
            AiData = aiData,
            Packages = packages,
            HairFormId = hair,
            HairLength = hairLength,
            EyesFormId = eyes,
            CombatStyleFormId = combatStyle,
            Script = scriptFormId,
            FaceGenGeometrySymmetric = fggs,
            FaceGenGeometryAsymmetric = fgga,
            FaceGenTextureSymmetric = fgts,
            Offset = offset,
            IsBigEndian = true
        };
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

        // Read skills and type
        var combatSkill = buffer[NpcFields.CreaCombatSkillOffset];
        var magicSkill = buffer[NpcFields.CreaMagicSkillOffset];
        var stealthSkill = buffer[NpcFields.CreaStealthSkillOffset];
        var attackDamage = (short)BinaryUtils.ReadUInt16BE(buffer, NpcFields.CreaAttackDamageOffset);
        var creatureType = buffer[NpcFields.CreaTypeOffset];

        // Validate creature type (0-7)
        if (creatureType > 7)
        {
            creatureType = 0;
        }

        // Read model path
        var modelPath = _context.ReadBSStringT(offset, NpcFields.CreaModelPathOffset);

        // Read script pointer
        var scriptFormId = _context.FollowPointerToFormId(buffer, NpcFields.CreaScriptOffset);

        // Read ACBS (actor base stats) at +24, same structure as NPC
        var stats = ReadCreatureActorBaseStats(buffer, NpcFields.CreaAcbsOffset, offset);

        // Read AI package list (BSSimpleList<TESPackage*> at TESAIForm+24)
        // TESCreature inherits TESActorBase at offset 0, same layout as TESNPC
        var packages = NpcFields.ReadPackageList(buffer);

        return new CreatureRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Stats = stats,
            CreatureType = creatureType,
            CombatSkill = combatSkill,
            MagicSkill = magicSkill,
            StealthSkill = stealthSkill,
            AttackDamage = attackDamage,
            Packages = packages,
            Script = scriptFormId,
            ModelPath = modelPath,
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read extended faction data from a runtime TESFaction struct.
    ///     Returns a FactionRecord with Flags, or null if validation fails.
    ///     Note: Rank and Relation lists require BSSimpleList traversal (Phase 5D).
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

        // Read display name — hash table already has it from FullNameOffset=44
        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, FactFullNameOffset);

        return new FactionRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            Flags = flags,
            Offset = offset,
            IsBigEndian = true
        };
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
    private static ActorBaseSubrecord? ReadActorBaseStats(byte[] buffer, int acbsStart, long structOffset)
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
            return null;
        }

        // Barter gold: 0-50000
        if (barterGold > 50000)
        {
            return null;
        }

        // Level: -128 to 100 (negative = level-offset based, positive = fixed level)
        if (level < -128 || level > 100)
        {
            return null;
        }

        // Speed multiplier: typically 70-200 (100 = normal)
        if (speedMultiplier > 500)
        {
            return null;
        }

        // Karma: should be a normal float
        if (!RuntimeMemoryContext.IsNormalFloat(karma))
        {
            return null;
        }

        // CalcMin/CalcMax: 0-100
        if (calcMin > 100 || calcMax > 100)
        {
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
    private int FactFlagsOffset => 52 + _s;
    private int FactFullNameOffset => 28 + _s;

    #endregion

    // Creature struct layout constants are provided by NpcFields.
}
