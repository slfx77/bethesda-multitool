using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reader for NPC, Creature, and Faction runtime structs from Xbox 360 memory dumps.
///     Extracts actor stats, inventory, factions, FaceGen morphs, and other character data.
/// </summary>
internal sealed class RuntimeActorReader(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;

    #region NPC Struct Constants

    private const int NpcStructSize = 508;
    private const int NpcAcbsOffset = 68;
    private const int NpcDeathItemPtrOffset = 92;
    private const int NpcVoiceTypePtrOffset = 96;
    private const int NpcTemplatePtrOffset = 100;
    private const int NpcRacePtrOffset = 288;
    private const int NpcClassPtrOffset = 320;
    private const int NpcAiDataOffset = 164;
    private const int NpcMoodOffset = 168;
    private const int NpcAiFlagsOffset = 172;
    private const int NpcAiAssistanceOffset = 178;
    private const int NpcSpecialOffset = 204;
    private const int NpcSpecialSize = 7;
    private const int NpcSkillsOffset = 292;
    private const int NpcSkillsSize = 14;
    private const int NpcFggsPointerOffset = 336;
    private const int NpcFggsCountOffset = 348;
    private const int NpcFggaPointerOffset = 368;
    private const int NpcFggaCountOffset = 380;
    private const int NpcFgtsPointerOffset = 400;
    private const int NpcFgtsCountOffset = 412;
    private const int NpcHairPtrOffset = 456;
    private const int NpcHairLengthOffset = 460;
    private const int NpcEyesPtrOffset = 464;
    private const int NpcCombatStylePtrOffset = 484;
    private const int NpcContainerDataOffset = 120;
    private const int NpcContainerNextOffset = 124;
    private const int NpcFactionListHeadOffset = 112;

    #endregion

    #region Faction Struct Constants

    private const int FactStructSize = 108;
    private const int FactFlagsOffset = 68;
    private const int FactFullNameOffset = 44;

    #endregion

    #region Creature Struct Constants

    private const int CreaStructSize = 440;
    private const int CreaModelPathOffset = 188;
    private const int CreaScriptOffset = 220;
    private const int CreaCombatSkillOffset = 228;
    private const int CreaMagicSkillOffset = 229;
    private const int CreaStealthSkillOffset = 230;
    private const int CreaAttackDamageOffset = 232;
    private const int CreaTypeOffset = 236;
    private const int CreaAcbsOffset = 24;

    #endregion

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
        if (offset + NpcStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[NpcStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, NpcStructSize);
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

        // Read ACBS stats block at empirically verified offset +68
        var stats = ReadActorBaseStats(buffer, NpcAcbsOffset, offset);
        if (stats == null)
        {
            return CreateMinimalNpc(entry, offset);
        }

        // Follow pointer fields to get FormIDs
        var race = _context.FollowPointerToFormId(buffer, NpcRacePtrOffset);
        var classFormId = _context.FollowPointerToFormId(buffer, NpcClassPtrOffset);
        var deathItem = _context.FollowPointerToFormId(buffer, NpcDeathItemPtrOffset);
        var voiceType = _context.FollowPointerToFormId(buffer, NpcVoiceTypePtrOffset);
        var template = _context.FollowPointerToFormId(buffer, NpcTemplatePtrOffset);

        // Read sub-item lists (container inventory, faction memberships)
        var inventory = ReadNpcInventory(buffer, offset);
        var factions = ReadNpcFactions(buffer);

        // Read S.P.E.C.I.A.L. stats (7 bytes at +204)
        var special = ReadNpcSpecial(buffer);

        // Read skills (14 bytes at +211, immediately after SPECIAL)
        var skills = ReadNpcSkills(buffer);

        // Read AI data (aggression, confidence, mood, etc. at +164)
        var aiData = ReadNpcAiData(buffer);

        // Read physical traits (hair, eyes, hair length, combat style)
        var hair = _context.FollowPointerToFormId(buffer, NpcHairPtrOffset);
        var eyes = _context.FollowPointerToFormId(buffer, NpcEyesPtrOffset);
        var combatStyle = _context.FollowPointerToFormId(buffer, NpcCombatStylePtrOffset);
        var hairLength = ReadNpcHairLength(buffer);

        // Read FaceGen morph data (follow pointers to float arrays in module space)
        var fggs = ReadFaceGenMorphArray(buffer, NpcFggsPointerOffset, NpcFggsCountOffset);
        var fgga = ReadFaceGenMorphArray(buffer, NpcFggaPointerOffset, NpcFggaCountOffset);
        var fgts = ReadFaceGenMorphArray(buffer, NpcFgtsPointerOffset, NpcFgtsCountOffset);

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
            HairFormId = hair,
            HairLength = hairLength,
            EyesFormId = eyes,
            CombatStyleFormId = combatStyle,
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
        if (offset + CreaStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[CreaStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, CreaStructSize);
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
        var combatSkill = buffer[CreaCombatSkillOffset];
        var magicSkill = buffer[CreaMagicSkillOffset];
        var stealthSkill = buffer[CreaStealthSkillOffset];
        var attackDamage = (short)BinaryUtils.ReadUInt16BE(buffer, CreaAttackDamageOffset);
        var creatureType = buffer[CreaTypeOffset];

        // Validate creature type (0-7)
        if (creatureType > 7)
        {
            creatureType = 0;
        }

        // Read model path
        var modelPath = _context.ReadBSStringT(offset, CreaModelPathOffset);

        // Read script pointer
        var scriptFormId = _context.FollowPointerToFormId(buffer, CreaScriptOffset);

        // Read ACBS (actor base stats) at +24, same structure as NPC
        var stats = ReadCreatureActorBaseStats(buffer, CreaAcbsOffset, offset);

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

    /// <summary>
    ///     Read AI behavior data from TESAIForm at dump offset +164.
    ///     Layout: aggression(1), confidence(1), energy(1), responsibility(1), mood(1 at +168),
    ///     padding(3), flags(4 at +172), ..., assistance(1 at +178).
    ///     Empirically verified via GSSunnySmiles (aggression=1, confidence=4, assistance=2).
    /// </summary>
    private static NpcAiData? ReadNpcAiData(byte[] buffer)
    {
        if (NpcAiAssistanceOffset + 1 > buffer.Length)
        {
            return null;
        }

        var aggression = buffer[NpcAiDataOffset];
        var confidence = buffer[NpcAiDataOffset + 1];
        var energy = buffer[NpcAiDataOffset + 2];
        var responsibility = buffer[NpcAiDataOffset + 3];
        var mood = buffer[NpcMoodOffset];
        var flags = BinaryUtils.ReadUInt32BE(buffer, NpcAiFlagsOffset);
        var assistance = buffer[NpcAiAssistanceOffset];

        // Validate ranges
        if (aggression > 3 || confidence > 4 || assistance > 2)
        {
            return null;
        }

        // Clamp mood to valid range (0-7)
        if (mood > 7)
        {
            mood = 0;
        }

        return new NpcAiData(aggression, confidence, energy, responsibility, mood, flags, assistance);
    }

    /// <summary>
    ///     Read NPC faction memberships from the NiTListItem chain at +112.
    ///     Each NiTListItem is 16 bytes: { pPrev(4), pNext(4), pFaction(4), rankData(4) }.
    ///     Returns a list of (FactionFormId, Rank) pairs.
    /// </summary>
    public List<FactionMembership> ReadNpcFactions(byte[] npcBuffer)
    {
        var factions = new List<FactionMembership>();

        var headVA = BinaryUtils.ReadUInt32BE(npcBuffer, NpcFactionListHeadOffset);
        if (headVA == 0)
        {
            return factions; // empty list
        }

        var nodeVA = headVA;
        var visited = new HashSet<uint>();
        while (nodeVA != 0 && factions.Count < RuntimeMemoryContext.MaxListItems && !visited.Contains(nodeVA))
        {
            visited.Add(nodeVA);
            var nodeFileOffset = _context.VaToFileOffset(nodeVA);
            if (nodeFileOffset == null)
            {
                break;
            }

            // Read 16-byte NiTListItem
            var nodeBuf = _context.ReadBytes(nodeFileOffset.Value, 16);
            if (nodeBuf == null)
            {
                break;
            }

            // Layout: pPrev(4) + pNext(4) + pFaction(4) + rankData(4)
            var pNext = BinaryUtils.ReadUInt32BE(nodeBuf, 4);
            var pFaction = BinaryUtils.ReadUInt32BE(nodeBuf, 8);
            var rankByte = nodeBuf[12]; // first byte of rankData = rank (int8)

            // Follow pFaction to read FormID and validate it's a FACT (0x08)
            if (pFaction != 0)
            {
                var factionFileOffset = _context.VaToFileOffset(pFaction);
                if (factionFileOffset != null)
                {
                    var formBuf = _context.ReadBytes(factionFileOffset.Value, 16);
                    if (formBuf != null)
                    {
                        var formType = formBuf[4];
                        var formId = BinaryUtils.ReadUInt32BE(formBuf, 12);
                        if (formType == 0x08 && formId != 0 && formId != 0xFFFFFFFF) // FACT
                        {
                            factions.Add(new FactionMembership(formId, (sbyte)rankByte));
                        }
                    }
                }
            }

            nodeVA = pNext;
        }

        return factions;
    }

    /// <summary>
    ///     Read hair length float from dump offset +460.
    ///     Returns null if the value is zero (NULL/unset) or invalid.
    ///     Empirically verified: Sunny=0.60, Doc=0.29, Arcade=0.69, Boone=NULL, Raul=NULL.
    /// </summary>
    private static float? ReadNpcHairLength(byte[] buffer)
    {
        if (NpcHairLengthOffset + 4 > buffer.Length)
        {
            return null;
        }

        var raw = BinaryUtils.ReadUInt32BE(buffer, NpcHairLengthOffset);
        if (raw == 0)
        {
            return null; // NULL/unset
        }

        var value = BinaryUtils.ReadFloatBE(buffer, NpcHairLengthOffset);
        if (!RuntimeMemoryContext.IsNormalFloat(value) || value < 0 || value > 10)
        {
            return null;
        }

        return value;
    }

    /// <summary>
    ///     Read NPC inventory items from TESContainer tList at +120/+124.
    ///     Returns a list of (ItemFormId, Count) pairs.
    /// </summary>
    public List<InventoryItem> ReadNpcInventory(byte[] npcBuffer, long npcFileOffset)
    {
        var items = new List<InventoryItem>();

        // Read inline first node
        var firstDataPtr = BinaryUtils.ReadUInt32BE(npcBuffer, NpcContainerDataOffset);
        var firstNextPtr = BinaryUtils.ReadUInt32BE(npcBuffer, NpcContainerNextOffset);

        // Process inline first item
        var firstItem = ReadContainerObject(firstDataPtr);
        if (firstItem != null)
        {
            items.Add(firstItem);
        }

        // Follow chain of _Node (8 bytes each: data ptr + next ptr)
        var nextVA = firstNextPtr;
        var visited = new HashSet<uint>();
        while (nextVA != 0 && items.Count < RuntimeMemoryContext.MaxListItems && !visited.Contains(nextVA))
        {
            visited.Add(nextVA);
            var nodeFileOffset = _context.VaToFileOffset(nextVA);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuf = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuf == null)
            {
                break;
            }

            var dataPtr = BinaryUtils.ReadUInt32BE(nodeBuf);
            var nextPtr = BinaryUtils.ReadUInt32BE(nodeBuf, 4);

            var item = ReadContainerObject(dataPtr);
            if (item != null)
            {
                items.Add(item);
            }

            nextVA = nextPtr;
        }

        return items;
    }

    /// <summary>
    ///     Read skill values from TESNPCData at dump offset +292 (PDB offset +276 + 16).
    ///     Returns 14 bytes or null if values look invalid.
    ///     Order: Barter(0), BigGuns(1), EnergyWeapons(2), Explosives(3), Lockpick(4),
    ///     Medicine(5), MeleeWeapons(6), Repair(7), Science(8), Guns(9), Sneak(10),
    ///     Speech(11), Survival(12), Unarmed(13).
    ///     Empirically verified via GSSunnySmiles: [12,12,14,14,14,12,47,12,12,47,47,12,12,12].
    /// </summary>
    private static byte[]? ReadNpcSkills(byte[] buffer)
    {
        if (NpcSkillsOffset + NpcSkillsSize > buffer.Length)
        {
            return null;
        }

        var skills = new byte[NpcSkillsSize];
        Array.Copy(buffer, NpcSkillsOffset, skills, 0, NpcSkillsSize);

        // Validate: skill values should be 0-100
        for (var i = 0; i < NpcSkillsSize; i++)
        {
            if (skills[i] > 100)
            {
                return null;
            }
        }

        // Additional validation: at least one skill should be non-zero
        // (unless SPECIAL was also null — template NPCs may have all zeros)
        var sum = 0;
        for (var i = 0; i < NpcSkillsSize; i++)
        {
            sum += skills[i];
        }

        if (sum == 0)
        {
            return null;
        }

        return skills;
    }

    /// <summary>
    ///     Read S.P.E.C.I.A.L. stats from TESAttributes at dump offset +204.
    ///     Returns 7 bytes (ST, PE, EN, CH, IN, AG, LK) or null if values look invalid.
    ///     Empirically verified via GSSunnySmiles (6,5,4,4,4,6,4) and CraigBoone (4,9,5,3,4,7,8).
    /// </summary>
    private static byte[]? ReadNpcSpecial(byte[] buffer)
    {
        if (NpcSpecialOffset + NpcSpecialSize > buffer.Length)
        {
            return null;
        }

        var special = new byte[NpcSpecialSize];
        Array.Copy(buffer, NpcSpecialOffset, special, 0, NpcSpecialSize);

        // Validate: each stat should be 1-10 for base values (some NPCs may exceed via perks)
        // Allow 0-15 range to account for modified/capped values
        for (var i = 0; i < NpcSpecialSize; i++)
        {
            if (special[i] > 15)
            {
                return null;
            }
        }

        // Additional validation: at least one stat should be non-zero
        var sum = 0;
        for (var i = 0; i < NpcSpecialSize; i++)
        {
            sum += special[i];
        }

        if (sum == 0)
        {
            return null;
        }

        return special;
    }

    /// <summary>
    ///     Read a FaceGen morph float array by following a pointer in the NPC struct.
    ///     The pointer at pointerOffset points to a float array in module space (0x660xxxxx).
    ///     The count at countOffset tells how many floats to read.
    ///     Returns null if the pointer is invalid or the data cannot be read.
    ///     Empirically verified across xex3 + xex44 dumps, all tested NPCs have consistent data.
    /// </summary>
    private float[]? ReadFaceGenMorphArray(byte[] npcBuffer, int pointerOffset, int countOffset)
    {
        if (pointerOffset + 4 > npcBuffer.Length || countOffset + 4 > npcBuffer.Length)
        {
            return null;
        }

        var pointer = BinaryUtils.ReadUInt32BE(npcBuffer, pointerOffset);
        if (pointer == 0)
        {
            return null;
        }

        var count = (int)BinaryUtils.ReadUInt32BE(npcBuffer, countOffset);
        if (count <= 0 || count > 200)
        {
            return null;
        }

        // Convert VA to file offset (these are in module space, not heap)
        var fileOffset = _context.VaToFileOffset(pointer);
        if (fileOffset == null)
        {
            return null;
        }

        var byteCount = count * 4;
        var floatData = _context.ReadBytes(fileOffset.Value, byteCount);
        if (floatData == null)
        {
            return null;
        }

        // Parse big-endian floats
        var result = new float[count];
        var validCount = 0;
        for (var i = 0; i < count; i++)
        {
            result[i] = BinaryUtils.ReadFloatBE(floatData, i * 4);
            if (RuntimeMemoryContext.IsNormalFloat(result[i]) && Math.Abs(result[i]) < 100)
            {
                validCount++;
            }
        }

        // Require at least 50% of values to be valid small floats
        if (validCount < count * 0.5)
        {
            return null;
        }

        return result;
    }

    /// <summary>
    ///     Creates a minimal NPC record using only hash table data (no struct reading).
    /// </summary>
    private static NpcRecord CreateMinimalNpc(RuntimeEditorIdEntry entry, long offset)
    {
        return new NpcRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Follow a ContainerObject* pointer to read { count(int32 BE), pItem(TESForm*) }.
    ///     Returns an InventoryItem or null.
    /// </summary>
    private InventoryItem? ReadContainerObject(uint containerObjectVA)
    {
        if (containerObjectVA == 0)
        {
            return null;
        }

        var fileOffset = _context.VaToFileOffset(containerObjectVA);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = _context.ReadBytes(fileOffset.Value, 8);
        if (buf == null)
        {
            return null;
        }

        var count = RuntimeMemoryContext.ReadInt32BE(buf, 0);
        var pItem = BinaryUtils.ReadUInt32BE(buf, 4);

        // Validate count (reasonable range for inventory)
        if (count <= 0 || count > 100000)
        {
            return null;
        }

        // Follow pItem to read the item's FormID
        var itemFormId = _context.FollowPointerVaToFormId(pItem);
        if (itemFormId == null)
        {
            return null;
        }

        return new InventoryItem(itemFormId.Value, count);
    }
}
