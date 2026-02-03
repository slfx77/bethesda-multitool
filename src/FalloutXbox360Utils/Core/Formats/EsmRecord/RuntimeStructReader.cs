using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Enums;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

/// <summary>
///     Reads extended data from runtime C++ struct objects in Xbox 360 memory dumps.
///     Uses PDB-derived field offsets (July 2010 PDB, Fallout: New Vegas) to extract data
///     from TESForm-derived objects found via the game's EditorID hash table.
///     All offsets are from the start of the TESForm object in the dump file (TesFormOffset).
///     Xbox 360 uses big-endian (PowerPC) byte order for all fields.
/// </summary>
public sealed partial class RuntimeStructReader
{
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly long _fileSize;
    private readonly MinidumpInfo _minidumpInfo;

    public RuntimeStructReader(MemoryMappedViewAccessor accessor, long fileSize, MinidumpInfo minidumpInfo)
    {
        _accessor = accessor;
        _fileSize = fileSize;
        _minidumpInfo = minidumpInfo;
    }

    #region TESNPC

    // PDB layout: TESNPC inherits TESActorBase(268+16) + TESRaceForm(8) + own fields
    //
    // IMPORTANT: The PDB is from July 2010, but the dumps are Dec 2009 – Apr 2010.
    // TESBoundAnimObject is 64 bytes in the dump (48 in PDB). All offsets shift +16.
    // Empirically verified via hex dump of Doc Mitchell (FormID 0x00104C0C):
    //   Level=1 at +76, Speed=100 at +82, Karma=500.0 at +84 — all correct.
    //   TESFullName.pString confirmed at +228 (empirically verified in hash table code).
    //
    // TESActorBase layout (284 bytes in dump, 268 in PDB):
    //   +0    TESBoundAnimObject (64 bytes in dump, 48 in PDB)
    //   +64   TESActorBaseData vtable (4 bytes)
    //   +68   TESActorBaseData.actorData (ACBS stats, 24 bytes)
    //   +92   TESActorBaseData.pDeathItem (4 bytes, pointer)
    //   +96   TESActorBaseData.pVoiceType (4 bytes, pointer)
    //   +100  TESActorBaseData.pTemplateForm (4 bytes, pointer)
    //   +116  TESContainer vtable + data
    //   +128  TESSpellList vtable + data
    //   +224  TESFullName vtable (4 bytes)
    //   +228  TESFullName.pString (empirically verified)
    //
    // TESRaceForm at +284 (PDB: +268, +16):
    //   +288  pRace (TESRace pointer)
    //
    // TESNPC-specific:
    //   +320  pCl (TESClass pointer) (PDB: +304, +16)
    //
    // NOTE: Height/Weight (PDB +468/+472, dump +484/+488) NOT found in inline struct.
    // These are stored via pointer indirection and require further R&D to extract.
    //
    // Empirically verified component layout within TESActorBase (hex dump scan):
    //   +160  TESAIForm vtable (0x82041DA0)
    //   +164  TESAIForm.AIData: aggression(1), confidence(1), energy(1), responsibility(1)
    //   +168  AI flags (uint32 BE)
    //   +178  Assistance (uint8)
    //   +192  Component vtable (0x82041D74)
    //   +200  TESAttributes vtable (0x82041D60)
    //   +204  S.P.E.C.I.A.L. stats: 7 consecutive bytes (ST, PE, EN, CH, IN, AG, LK)
    //   +211  Unknown byte (TESAttributes padding or extra data)
    //   +212  Component vtable (0x82041D4C)
    //   +224  TESFullName vtable (0x82041D8C)
    //   +292  TESNPCData: Skills (14 bytes: Barter..Unarmed)

    private const int NpcStructSize = 508; // PDB: 492, +16

    // ACBS stats block (24 bytes) within TESActorBaseData
    private const int NpcAcbsOffset = 68; // PDB: 52, +16

    // Pointer fields (4 bytes each, all are TESForm* requiring pointer following)
    private const int NpcDeathItemPtrOffset = 92; // PDB: 76, +16
    private const int NpcVoiceTypePtrOffset = 96; // PDB: 80, +16
    private const int NpcTemplatePtrOffset = 100; // PDB: 84, +16
    private const int NpcRacePtrOffset = 288; // PDB: 272, +16
    private const int NpcClassPtrOffset = 320; // PDB: 304, +16

    // AI Data (empirically verified at dump +164 via GSSunnySmiles: aggression=1, confidence=4)
    private const int NpcAiDataOffset = 164; // TESAIForm.AIData start (aggression, confidence, energy, responsibility)
    private const int NpcMoodOffset = 168; // TESAIForm.AIData.mood (uint8) — was misread as flags MSB
    private const int NpcAiFlagsOffset = 172; // TESAIForm.AIData.buySellAndServices (uint32 BE) — shifted from 168
    private const int NpcAiAssistanceOffset = 178; // TESAIForm.AIData.assistance (uint8)

    // S.P.E.C.I.A.L. stats (empirically verified at dump +204 via GSSunnySmiles: 6,5,4,4,4,6,4)
    private const int NpcSpecialOffset = 204; // TESAttributes.cAttribute[7]
    private const int NpcSpecialSize = 7; // 7 bytes: ST, PE, EN, CH, IN, AG, LK

    // Skills (14 bytes at dump +292, within TESNPCData struct — PDB predicted offset confirmed)
    // Order: Barter, BigGuns, EnergyWeapons, Explosives, Lockpick, Medicine, MeleeWeapons,
    //        Repair, Science, Guns, Sneak, Speech, Survival, Unarmed
    // FNV doesn't use BigGuns (index 1) — Xbox beta may still populate it.
    // Empirically verified: GSSunnySmiles at +292 shows [12,12,14,14,14,12,47,12,12,47,47,12,12,12]
    private const int NpcSkillsOffset = 292; // TESNPCData skills start (PDB +276 + 16 = +292)
    private const int NpcSkillsSize = 14; // 14 skill slots

    // FaceGen morph data pointers (empirically verified via npc_struct_scan.py on xex3 + xex44)
    // Three groups of float arrays stored in module space (0x660xxxxx), each group has:
    //   ptr(4) + ptr(4) + ptr(4) + count(4) + flag(4) + padding(12)
    private const int NpcFggsPointerOffset = 336; // FGGS (geometry-symmetric) ptr → 50 floats
    private const int NpcFggsCountOffset = 348; // Count (always 50)
    private const int NpcFggaPointerOffset = 368; // FGGA (geometry-asymmetric) ptr → 30 floats
    private const int NpcFggaCountOffset = 380; // Count (always 30)
    private const int NpcFgtsPointerOffset = 400; // FGTS (texture-symmetric) ptr → 50 floats
    private const int NpcFgtsCountOffset = 412; // Count (always 50)

    // Physical traits (empirically verified via npc_struct_scan.py across 5+ NPCs, both dumps)
    private const int NpcHairPtrOffset = 456; // TESHair* (FormType=10)
    private const int NpcHairLengthOffset = 460; // float32 BE (0.0-1.0 range, NULL=0 for some NPCs)
    private const int NpcEyesPtrOffset = 464; // TESEyes* (FormType=11)

    // Combat style (empirically verified: type=74/CSTY, NOT height as PDB predicted)
    private const int NpcCombatStylePtrOffset = 484; // TESCombatStyle* (FormType=74)

    /// <summary>
    ///     Read extended NPC data from a runtime TESNPC struct.
    ///     Returns a ReconstructedNpc populated with stats, race, class, etc.
    ///     Returns null if the struct cannot be read or validation fails.
    /// </summary>
    public ReconstructedNpc? ReadRuntimeNpc(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x2A)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + NpcStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[NpcStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, NpcStructSize);
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
        var race = FollowPointerToFormId(buffer, NpcRacePtrOffset);
        var classFormId = FollowPointerToFormId(buffer, NpcClassPtrOffset);
        var deathItem = FollowPointerToFormId(buffer, NpcDeathItemPtrOffset);
        var voiceType = FollowPointerToFormId(buffer, NpcVoiceTypePtrOffset);
        var template = FollowPointerToFormId(buffer, NpcTemplatePtrOffset);

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
        var hair = FollowPointerToFormId(buffer, NpcHairPtrOffset);
        var eyes = FollowPointerToFormId(buffer, NpcEyesPtrOffset);
        var combatStyle = FollowPointerToFormId(buffer, NpcCombatStylePtrOffset);
        var hairLength = ReadNpcHairLength(buffer);

        // Read FaceGen morph data (follow pointers to float arrays in module space)
        var fggs = ReadFaceGenMorphArray(buffer, NpcFggsPointerOffset, NpcFggsCountOffset);
        var fgga = ReadFaceGenMorphArray(buffer, NpcFggaPointerOffset, NpcFggaCountOffset);
        var fgts = ReadFaceGenMorphArray(buffer, NpcFgtsPointerOffset, NpcFgtsCountOffset);

        return new ReconstructedNpc
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
    ///     Creates a minimal NPC record using only hash table data (no struct reading).
    /// </summary>
    private static ReconstructedNpc CreateMinimalNpc(RuntimeEditorIdEntry entry, long offset)
    {
        return new ReconstructedNpc
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
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
        if (!IsNormalFloat(karma))
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
        if (!IsNormalFloat(value) || value < 0 || value > 10)
        {
            return null;
        }

        return value;
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
        var fileOffset = VaToFileOffset(pointer);
        if (fileOffset == null)
        {
            return null;
        }

        var byteCount = count * 4;
        var floatData = ReadBytes(fileOffset.Value, byteCount);
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
            if (IsNormalFloat(result[i]) && Math.Abs(result[i]) < 100)
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

    #endregion

    #region TESObjectWEAP (empirically mapped)

    // EMPIRICALLY VERIFIED weapon struct layout (from hex dumps of 10mm Pistol,
    // Hunting Rifle, and Laser Pistol in Fallout_Release_Beta.xex3.dmp):
    //
    // The weapon struct does NOT follow a simple +16 shift from PDB offsets.
    // TESBoundObject is 64 bytes (same as NPC), but intermediate base classes
    // between TESIcon and TESWeightForm are collectively 32 bytes smaller than PDB.
    // Each offset below is empirically verified via cross-referencing 3 weapons.
    //
    // Base classes (all dump offsets, empirically verified):
    //   +0    TESBoundObject (64 bytes)
    //   +64   TESFullName vtable → +68 pString (empirically verified via hash table)
    //   +76   TESModelTextureSwap (32 bytes)
    //   +108  TESIcon (12 bytes)
    //   +120  TESScriptableForm (12 bytes)
    //   +132  TESEnchantableForm (16 bytes)
    //   +148  TESValueForm vtable → +152 iValue (int32 BE)
    //   +156  TESWeightForm vtable → +160 fWeight (float BE) ✓✓✓
    //   +164  TESHealthForm vtable → +168 iHealth (int32 BE)
    //   +172..+259: remaining base classes (BGSDestructible, BGSEquipType,
    //         BGSRepairItemList, BGSBipedModelList, BGSPickupPutdownSounds,
    //         TESAttackDamageForm, BGSAmmoForm, BGSClipRoundsForm)
    //
    // DNAM data struct (weapon combat parameters):
    //   +260  animationType (uint8, NOT uint32!)
    //         10mm=3(Pistol), HR=5(Rifle), LP=4(PistolAuto) ✓✓✓
    //   +264  animMult/speed (float)
    //   +268  reach (float)
    //   +272  flags1(1)+gripAnim(1)+ammoUse(1)+reloadAnim(1)
    //   +276  minSpread (float) ✓✓✓
    //   +280  spread (float) ✓✓✓
    //   +284  unknown (uint32)
    //   +288  sightFov (float)
    //   +292  unknown
    //   +296  pProjectile (pointer)
    //   +300  baseVATSChance(1)+attackAnim(1)+projCount(1)+embedAV(1)
    //   +304  minRange (float) ✓✓✓
    //   +308  maxRange (float) ✓✓✓
    //   +312  onHit (uint32)
    //   +316  flags2 (uint32)
    //   +320  animAttackMult (float)
    //   ... remaining DNAM fields through ~+464
    //
    // Critical data (position uncertain, needs further validation):
    //   +456  critDamage area (approximate)
    //   +460  critChance (float, f460=1.0 for 10mm ✓)

    private const int WeapStructSize = 924;

    // Base class field offsets (cross-dump verified: Debug Jan2010, Late Apr2010)
    private const int WeapModelPathOffset = 80; // TESModel.model BSStringT (pString+sLen)
    private const int WeapValueOffset = 152; // TESValueForm.iValue (int32 BE)
    private const int WeapWeightOffset = 160; // TESWeightForm.fWeight (float BE) ✓✓✓
    private const int WeapHealthOffset = 168; // TESHealthForm.iHealth (int32 BE)
    private const int WeapDamageOffset = 176; // TESAttackDamageForm.sAttackDamage (uint16 BE) ✓✓✓
    private const int WeapAmmoPtrOffset = 184; // BGSAmmoForm.pFormAmmo (pointer BE) ✓✓✓
    private const int WeapClipRoundsOffset = 192; // BGSClipRoundsForm.cClipRounds (uint8) ✓✓✓

    // DNAM data struct offsets
    private const int WeapDataStart = 260;
    private const int WeapAnimTypeOffset = 260; // uint8 (first byte only!)
    private const int WeapSpeedOffset = 264; // float (animation multiplier)
    private const int WeapReachOffset = 268; // float

    // DNAM relative offsets (from data struct start at +260)
    private const int DnamMinSpreadRelOffset = 16; // +276 = data+16
    private const int DnamSpreadRelOffset = 20; // +280 = data+20
    private const int DnamProjectileRelOffset = 36; // +296 = data+36
    private const int DnamVatsChanceRelOffset = 40; // +300 = data+40 (uint8)
    private const int DnamMinRangeRelOffset = 44; // +304 = data+44
    private const int DnamMaxRangeRelOffset = 48; // +308 = data+48
    private const int DnamActionPointsRelOffset = 68; // +328 = data+68 ✓✓✓

    private const int DnamShotsPerSecRelOffset = 88; // +348 = data+88 ✓✓✓

    // Critical data (approximate — validated with f460=1.0 crit mult pattern)
    private const int WeapCritDamageOffset = 456; // approximate — int16 BE
    private const int WeapCritChanceOffset = 460; // float BE (f460=1.0 for 10mm)

    // Sound pointers (TESSound*, empirically verified via weapon_struct_scan.py
    // across HuntingRifle, LaserPistol, and YourMom/Atomic Baby Launcher):
    //
    // BGSPickupPutdownSounds base class:
    private const int WeapPickupSoundOffset = 252; // ITMRifleUp / ITMPistolUp ✓✓✓

    private const int WeapPutdownSoundOffset = 256; // ITMRifleDown / ITMPistolDown ✓✓✓

    //
    // TESObjectWEAP sound array (PDB +540..+584, dump +548..+580, shift +8):
    private const int WeapFireSound3DOffset = 548; // WPNRifleHuntingFire3D / WPNPistolLaserFire3D ✓✓✓
    private const int WeapFireSoundDistOffset = 552; // WPNRifleHuntingFire3DDIST ✓✓✓

    private const int WeapFireSound2DOffset = 556; // WPNRifleHuntingFire2D / WPNPistolLaserFire2D ✓✓✓

    // +560: Attack Loop (null for all tested weapons)
    private const int WeapDryFireSoundOffset = 564; // WPNPistol10mmFireDry (shared default) ✓✓✓

    // +568: Melee Block Sound (null for all tested weapons)
    private const int WeapIdleSoundOffset = 572; // WPNRockItLauncherIdleLPM (YourMom only) ✓✓
    private const int WeapEquipSoundOffset = 576; // WPNRifle0Equip / WPNPistol0Equip ✓✓✓

    private const int WeapUnequipSoundOffset = 580; // WPNRifle0EquipUn / WPNPistol0EquipUn ✓✓✓

    //
    // Impact data set (BGSImpactDataSet*, PDB +588 → dump ~+584..+596):
    private const int WeapImpactDataSetOffset = 584; // BGSImpactDataSet* (FormType=98/IPDS)

    /// <summary>
    ///     Read extended weapon data from a runtime TESObjectWEAP struct.
    ///     Returns a ReconstructedWeapon with combat stats, or null if validation fails.
    /// </summary>
    public ReconstructedWeapon? ReadRuntimeWeapon(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x28)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + WeapStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[WeapStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, WeapStructSize);
        }
        catch
        {
            return null;
        }

        // Validate: FormID at offset 12 should match
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        // Read fields from base classes and weapon data struct
        var baseFields = ReadWeaponBaseClassFields(buffer);
        var combatFields = ReadWeaponCombatFields(buffer);
        var critFields = ReadWeaponCriticalFields(buffer);

        // Follow ammo pointer to get ammo FormID
        var ammoFormId = FollowPointerToFormId(buffer, WeapAmmoPtrOffset);

        // Read model path via BSStringT at TESModel offset
        var modelPath = ReadBSStringT(offset, WeapModelPathOffset);

        // Read sound pointers (TESSound* at various offsets)
        var pickupSound = FollowPointerToFormId(buffer, WeapPickupSoundOffset);
        var putdownSound = FollowPointerToFormId(buffer, WeapPutdownSoundOffset);
        var fireSound3D = FollowPointerToFormId(buffer, WeapFireSound3DOffset);
        var fireSoundDist = FollowPointerToFormId(buffer, WeapFireSoundDistOffset);
        var fireSound2D = FollowPointerToFormId(buffer, WeapFireSound2DOffset);
        var dryFireSound = FollowPointerToFormId(buffer, WeapDryFireSoundOffset);
        var idleSound = FollowPointerToFormId(buffer, WeapIdleSoundOffset);
        var equipSound = FollowPointerToFormId(buffer, WeapEquipSoundOffset);
        var unequipSound = FollowPointerToFormId(buffer, WeapUnequipSoundOffset);
        var impactDataSet = FollowPointerToFormId(buffer, WeapImpactDataSetOffset);

        return new ReconstructedWeapon
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Value = baseFields.Value,
            Health = baseFields.Health,
            Weight = baseFields.Weight,
            Damage = baseFields.Damage,
            ClipSize = baseFields.ClipSize,
            WeaponType = combatFields.WeaponType,
            AnimationType = combatFields.AnimationType,
            Speed = combatFields.Speed,
            Reach = combatFields.Reach,
            MinSpread = combatFields.MinSpread,
            Spread = combatFields.Spread,
            MinRange = combatFields.MinRange,
            MaxRange = combatFields.MaxRange,
            ActionPoints = combatFields.ActionPoints,
            ShotsPerSec = combatFields.ShotsPerSec,
            VatsToHitChance = combatFields.VatsChance,
            AmmoFormId = ammoFormId,
            ProjectileFormId = FollowPointerToFormId(buffer, WeapDataStart + DnamProjectileRelOffset),
            CriticalDamage = critFields.Damage,
            CriticalChance = critFields.Chance,
            ModelPath = modelPath,
            PickupSoundFormId = pickupSound,
            PutdownSoundFormId = putdownSound,
            FireSound3DFormId = fireSound3D,
            FireSoundDistFormId = fireSoundDist,
            FireSound2DFormId = fireSound2D,
            DryFireSoundFormId = dryFireSound,
            IdleSoundFormId = idleSound,
            EquipSoundFormId = equipSound,
            UnequipSoundFormId = unequipSound,
            ImpactDataSetFormId = impactDataSet,
            Offset = offset,
            IsBigEndian = true
        };
    }

    private static (int Value, int Health, float Weight, short Damage, byte ClipSize)
        ReadWeaponBaseClassFields(byte[] buffer)
    {
        var value = ReadInt32BE(buffer, WeapValueOffset);
        var health = ReadInt32BE(buffer, WeapHealthOffset);
        var weight = BinaryUtils.ReadFloatBE(buffer, WeapWeightOffset);
        var damage = (short)BinaryUtils.ReadUInt16BE(buffer, WeapDamageOffset);
        var clipSize = buffer[WeapClipRoundsOffset];

        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        if (health < 0 || health > 100000)
        {
            health = 0;
        }

        if (!IsNormalFloat(weight) || weight < 0 || weight > 500)
        {
            weight = 0;
        }

        if (damage < 0 || damage > 10000)
        {
            damage = 0;
        }

        return (value, health, weight, damage, clipSize);
    }

    private static (Enums.WeaponType WeaponType, uint AnimationType, float Speed, float Reach,
        float MinSpread, float Spread, float MinRange, float MaxRange,
        byte VatsChance, float ActionPoints, float ShotsPerSec) ReadWeaponCombatFields(byte[] buffer)
    {
        // animationType is stored as uint8 at the first byte of a 4-byte aligned field
        var animTypeByte = buffer[WeapAnimTypeOffset];
        var animationType = animTypeByte <= 20 ? animTypeByte : 0u;

        var speed = BinaryUtils.ReadFloatBE(buffer, WeapSpeedOffset);
        var reach = BinaryUtils.ReadFloatBE(buffer, WeapReachOffset);

        if (!IsNormalFloat(speed) || speed < 0 || speed > 100)
        {
            speed = 1.0f;
        }

        if (!IsNormalFloat(reach) || reach < 0 || reach > 1000)
        {
            reach = 0;
        }

        // Animation type byte maps directly to WeaponType enum
        var weaponType = animTypeByte <= 11 ? (WeaponType)animTypeByte : 0;

        var minSpread = ReadValidatedFloat(buffer, WeapDataStart + DnamMinSpreadRelOffset, 0, 1000);
        var spread = ReadValidatedFloat(buffer, WeapDataStart + DnamSpreadRelOffset, 0, 1000);
        var minRange = ReadValidatedFloat(buffer, WeapDataStart + DnamMinRangeRelOffset, 0, 100000);
        var maxRange = ReadValidatedFloat(buffer, WeapDataStart + DnamMaxRangeRelOffset, 0, 100000);
        var actionPoints = ReadValidatedFloat(buffer, WeapDataStart + DnamActionPointsRelOffset, 0, 1000);
        var shotsPerSec = ReadValidatedFloat(buffer, WeapDataStart + DnamShotsPerSecRelOffset, 0, 1000);

        var vatsChance = buffer[WeapDataStart + DnamVatsChanceRelOffset];
        if (vatsChance > 100)
        {
            vatsChance = 0;
        }

        return (weaponType, animationType, speed, reach, minSpread, spread,
            minRange, maxRange, vatsChance, actionPoints, shotsPerSec);
    }

    private static (short Damage, float Chance) ReadWeaponCriticalFields(byte[] buffer)
    {
        var damage = (short)BinaryUtils.ReadUInt16BE(buffer, WeapCritDamageOffset);
        var chance = BinaryUtils.ReadFloatBE(buffer, WeapCritChanceOffset);

        if (!IsNormalFloat(chance) || chance < 0 || chance > 100)
        {
            chance = 0;
        }

        if (damage < 0 || damage > 10000)
        {
            damage = 0;
        }

        return (damage, chance);
    }

    #endregion

    #region TESObjectARMO (empirically mapped)

    // EMPIRICALLY VERIFIED armor struct layout (from hex dumps of Leather Armor and
    // Combat Armor in Fallout_Release_Beta.xex3.dmp):
    //
    // Base class chain (all dump offsets, empirically verified):
    //   +0    TESBoundObject (64 bytes)
    //   +64   TESFullName vtable → +68 pString
    //   +76   TESModelTextureSwap (12 bytes in dump — model path is null for armor)
    //   +88   TESIcon/TESScriptableForm/TESEnchantableForm (16 bytes)
    //   +104  TESValueForm vtable → +108 iValue (int32 BE) ✓✓
    //   +112  TESWeightForm vtable → +116 fWeight (float BE) ✓✓ (Leather=15.0, Combat=25.0)
    //   +120  TESHealthForm vtable → +124 iHealth (int32 BE) ✓✓
    //   +128..+391: TESRaceForm, TESBipedModelForm×4, BGSDestructible, BGSEquipType, etc.
    //   +392  armorRating (uint16 BE, stored as AR×100) ✓✓ (Leather=2400→24, Combat=3200→32)

    private const int ArmoStructSize = 416;
    private const int ArmoValueOffset = 108; // TESValueForm.iValue
    private const int ArmoWeightOffset = 116; // TESWeightForm.fWeight
    private const int ArmoHealthOffset = 124; // TESHealthForm.iHealth
    private const int ArmoRatingOffset = 392; // armorRating (uint16 BE, ×100)

    /// <summary>
    ///     Read extended armor data from a runtime TESObjectARMO struct.
    ///     Returns a ReconstructedArmor with Value/Weight/Health/AR, or null if validation fails.
    /// </summary>
    public ReconstructedArmor? ReadRuntimeArmor(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x18)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + ArmoStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[ArmoStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, ArmoStructSize);
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

        var value = ReadInt32BE(buffer, ArmoValueOffset);
        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        var weight = ReadValidatedFloat(buffer, ArmoWeightOffset, 0, 500);

        var health = ReadInt32BE(buffer, ArmoHealthOffset);
        if (health < 0 || health > 100000)
        {
            health = 0;
        }

        var armorRatingRaw = BinaryUtils.ReadUInt16BE(buffer, ArmoRatingOffset);
        var damageThreshold = armorRatingRaw / 100.0f;

        return new ReconstructedArmor
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Value = value,
            Weight = weight,
            Health = health,
            DamageThreshold = damageThreshold,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #endregion

    #region TESObjectAMMO (empirically mapped)

    // EMPIRICALLY VERIFIED ammo struct layout (from hex dumps of 10mm Round and
    // 5mm Round in Fallout_Release_Beta.xex3.dmp):
    //
    // Base class chain:
    //   +0    TESBoundObject (64 bytes)
    //   +64   TESFullName vtable → +68 pString
    //   +76..+135: TESModel, TESIcon, BGSMessageIcon
    //   +136  TESValueForm vtable → +140 iValue (int32 BE) ✓✓ (10mm=1, 5mm=1)
    //
    // AMMO does not have TESWeightForm or TESHealthForm in its inheritance chain.

    private const int AmmoStructSize = 236;
    private const int AmmoValueOffset = 140; // TESValueForm.iValue

    /// <summary>
    ///     Read extended ammo data from a runtime TESObjectAMMO struct.
    ///     Returns a ReconstructedAmmo with Value, or null if validation fails.
    /// </summary>
    public ReconstructedAmmo? ReadRuntimeAmmo(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x29)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + AmmoStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[AmmoStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, AmmoStructSize);
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

        var value = ReadInt32BE(buffer, AmmoValueOffset);
        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        // Read world model path via BSStringT at TESModel offset (+80)
        var modelPath = ReadBSStringT(offset, WeapModelPathOffset);

        return new ReconstructedAmmo
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Value = (uint)value,
            ModelPath = modelPath,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #endregion

    #region TESObjectALCH (empirically mapped)

    // EMPIRICALLY VERIFIED consumable struct layout (from hex dumps of Stimpak,
    // RadAway, and Beer in Fallout_Release_Beta.xex3.dmp):
    //
    // Base class chain:
    //   +0    TESBoundObject (64 bytes)
    //   +64   TESFullName vtable → +68 pString
    //   +76..+163: TESModel, TESIcon, TESScriptableForm
    //   +164  TESWeightForm vtable → +168 fWeight (float BE) ✓ (Beer=1.0)
    //   +172..+199: BGSMessageIcon, TESDescription, BGSDestructible, BGSEquipType, etc.
    //   +200  iValue (int32 BE, direct member) ✓✓✓ (Stimpak=25, RadAway=20, Beer=2)
    //
    // Note: Value is a direct member of TESObjectALCH, NOT from TESValueForm.

    private const int AlchStructSize = 232;
    private const int AlchWeightOffset = 168; // TESWeightForm.fWeight
    private const int AlchValueOffset = 200; // TESObjectALCH.iValue (direct member)

    /// <summary>
    ///     Read extended consumable data from a runtime TESObjectALCH struct.
    ///     Returns a ReconstructedConsumable with Value/Weight, or null if validation fails.
    /// </summary>
    public ReconstructedConsumable? ReadRuntimeConsumable(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x2F)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + AlchStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[AlchStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, AlchStructSize);
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

        var weight = ReadValidatedFloat(buffer, AlchWeightOffset, 0, 500);

        var value = ReadInt32BE(buffer, AlchValueOffset);
        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        return new ReconstructedConsumable
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Value = (uint)value,
            Weight = weight,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #endregion

    #region TESObjectMISC / TESKey (empirically mapped)

    // EMPIRICALLY VERIFIED misc item struct layout (from hex dumps of Abraxo Cleaner
    // and Pre-war Money in Fallout_Release_Beta.xex3.dmp):
    //
    // Base class chain:
    //   +0    TESBoundObject (64 bytes)
    //   +64   TESFullName vtable → +68 pString
    //   +76..+131: TESModelTextureSwap, TESIcon, TESScriptableForm
    //   +132  TESValueForm vtable → +136 iValue (int32 BE) ✓✓ (PrewarMoney=10, Abraxo=5)
    //   +140  TESWeightForm vtable → +144 fWeight (float BE) ✓ (Abraxo=1.0)
    //
    // TESKey inherits from TESObjectMISC — identical layout, same offsets.
    // Verified with BethRuinsSafeKey and DashwoodSafeKey (both Value=5, Weight=0.0).

    private const int MiscStructSize = 188;
    private const int MiscValueOffset = 136; // TESValueForm.iValue
    private const int MiscWeightOffset = 144; // TESWeightForm.fWeight

    /// <summary>
    ///     Read extended misc item data from a runtime TESObjectMISC struct.
    ///     Returns a ReconstructedMiscItem with Value/Weight, or null if validation fails.
    /// </summary>
    public ReconstructedMiscItem? ReadRuntimeMiscItem(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x1F)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + MiscStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[MiscStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, MiscStructSize);
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

        var value = ReadInt32BE(buffer, MiscValueOffset);
        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        var weight = ReadValidatedFloat(buffer, MiscWeightOffset, 0, 500);

        // Read model path via BSStringT at TESModel offset (+80)
        var modelPath = ReadBSStringT(offset, WeapModelPathOffset);

        return new ReconstructedMiscItem
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Value = value,
            Weight = weight,
            ModelPath = modelPath,
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read extended key data from a runtime TESKey struct.
    ///     TESKey inherits TESObjectMISC — same layout, same offsets.
    ///     Returns a ReconstructedKey with Value/Weight, or null if validation fails.
    /// </summary>
    public ReconstructedKey? ReadRuntimeKey(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x2E)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + MiscStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[MiscStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, MiscStructSize);
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

        var value = ReadInt32BE(buffer, MiscValueOffset);
        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        var weight = ReadValidatedFloat(buffer, MiscWeightOffset, 0, 500);

        // Read model path via BSStringT at TESModel offset (+80)
        var modelPath = ReadBSStringT(offset, WeapModelPathOffset);

        return new ReconstructedKey
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Value = value,
            Weight = weight,
            ModelPath = modelPath,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #endregion

    #region BGSNote (empirically mapped)

    // BGSNote PDB inheritance: TESBoundObject → TESModel → TESFullName → TESIcon →
    //   BGSPickupPutdownSounds → TESWeightForm → TESDescription → BGSNote-specific
    //
    // IMPORTANT: BGSNote puts TESModel BEFORE TESFullName (unlike MISC/KEYM which reverse the order).
    //   +0    TESBoundObject (64 bytes)
    //   +64   TESModel vtable → +68 BSStringT (model path, e.g. "Clutter\Holodisk\Holodisk01.NIF")
    //   +88   TESFullName vtable → +92 BSStringT (display name)
    //   +100  TESIcon vtable → +104 BSStringT (icon path)
    //   +112  BGSPickupPutdownSounds
    //   ...
    //   +140  noteType (uint8): 0=Sound, 1=Text, 2=Image, 3=Voice ✓✓ (both samples = 1 = Text)
    //   +144  pointer to note content (text string or sound file)

    private const int NoteStructSize = 160;
    private const int NoteTypeOffset = 140; // BGSNote.cNoteType (uint8)
    private const int NoteModelPathOffset = 68; // TESModel.cModel BSStringT
    private const int NoteFullNameOffset = 92; // TESFullName.cFullName BSStringT

    /// <summary>
    ///     Read extended note data from a runtime BGSNote struct.
    ///     Returns a ReconstructedNote with NoteType and FullName, or null if validation fails.
    /// </summary>
    public ReconstructedNote? ReadRuntimeNote(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x31)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + NoteStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[NoteStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, NoteStructSize);
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

        var noteType = buffer[NoteTypeOffset];
        if (noteType > 3)
        {
            noteType = 0; // invalid, default to Sound
        }

        // BGSNote has TESModel at +64, TESFullName at +88 (reversed vs MISC/KEYM)
        var fullName = entry.DisplayName ?? ReadBSStringT(offset, NoteFullNameOffset);
        var modelPath = ReadBSStringT(offset, NoteModelPathOffset);

        return new ReconstructedNote
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            ModelPath = modelPath,
            NoteType = noteType,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #endregion

    #region TESFaction (empirically mapped)

    // EMPIRICALLY VERIFIED faction struct layout (from hex dumps of AntFaction
    // and AgathasFaction in Fallout_Release_Beta.xex3.dmp):
    //
    // TESFaction does NOT inherit TESBoundObject. It inherits TESForm directly.
    // However, TESForm appears to be 40 bytes in dumps (vs 24 in PDB), giving
    // the same +16 shift as TESBoundObject-derived types.
    //
    //   +0    TESForm (40 bytes in dump)
    //   +40   TESFullName vtable → +44 BSStringT ✓ (AntFaction: sLen=4 "Ants")
    //   +52   TESReactionForm vtable
    //   ...
    //   +68   Flags (uint32 BE) ✓ (AgathasFaction: 0x101 = TrackCrime+HiddenFromPC)
    //
    // PDB size = 76, dump size ≈ 92 (76 + 16)

    private const int FactStructSize = 108; // Read extra for safety
    private const int FactFlagsOffset = 68; // TESFaction.flags (uint32 BE)
    private const int FactFullNameOffset = 44; // TESFullName.cFullName BSStringT

    /// <summary>
    ///     Read extended faction data from a runtime TESFaction struct.
    ///     Returns a ReconstructedFaction with Flags, or null if validation fails.
    ///     Note: Rank and Relation lists require BSSimpleList traversal (Phase 5D).
    /// </summary>
    public ReconstructedFaction? ReadRuntimeFaction(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x08)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + FactStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[FactStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, FactStructSize);
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
        var fullName = entry.DisplayName ?? ReadBSStringT(offset, FactFullNameOffset);

        return new ReconstructedFaction
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            Flags = flags,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #endregion

    #region TESQuest (empirically mapped)

    // EMPIRICALLY VERIFIED quest struct layout (from hex dumps of AchievementQuest
    // and CG00 in Fallout_Release_Beta.xex3.dmp):
    //
    // TESQuest does NOT inherit TESBoundObject. Like TESFaction, it inherits TESForm
    // with the same +16 dump shift.
    //
    //   +0    TESForm (40 bytes in dump)
    //   +40   TESScriptableForm vtable → +44 script pointer
    //   +52   vtable
    //   +64   TESFullName-like vtable → +68 BSStringT (quest name?)
    //   +76   flags (uint8) ✓ (AchievementQuest: 0x11 = StartEnabled+RunOnce, CG00: 0x04 = AllowRepeated)
    //   +77   priority (uint8) ✓ (AchievementQuest: 5, CG00: 0)
    //
    // PDB size = 108, dump size ≈ 124 (108 + 16)

    private const int QustStructSize = 140; // Read extra for safety
    private const int QustFlagsOffset = 76; // TESQuest.flags (uint8)
    private const int QustPriorityOffset = 77; // TESQuest.priority (uint8)
    private const int QustFullNameOffset = 68; // BSStringT for quest display name

    /// <summary>
    ///     Read extended quest data from a runtime TESQuest struct.
    ///     Returns a ReconstructedQuest with Flags/Priority, or null if validation fails.
    ///     Note: Stage and Objective lists require BSSimpleList traversal (Phase 5D).
    /// </summary>
    public ReconstructedQuest? ReadRuntimeQuest(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x47)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + QustStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[QustStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, QustStructSize);
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

        var flags = buffer[QustFlagsOffset];
        var priority = buffer[QustPriorityOffset];

        // Try to read quest display name from BSStringT at +68
        var fullName = ReadBSStringT(offset, QustFullNameOffset);

        return new ReconstructedQuest
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            Flags = flags,
            Priority = priority,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #endregion

    #region TESObjectCONT (empirically mapped)

    // TESObjectCONT PDB inheritance chain (PDB size 156, dump 172 with +16):
    //   +0    TESBoundAnimObject (64 bytes, extends TESBoundObject)
    //   +48   TESContainer (12 bytes) — vtable at +48, objectList at +52
    //         tList<ContainerObject*> inline: { data(4), next(4) } at +68/+72 in dump
    //   +60   TESFullName vtable → +68 pString [overlaps TESContainer due to MI]
    //   +64   TESFullName vtable → +68 pString
    //   +76   TESModel vtable → +80 pString (model path)
    //   +104  TESWeightForm vtable → +108 fWeight
    //   +116  TESScriptableForm vtable → +120 pScript
    //   +140  pOpenSound, +144 pCloseSound, +148 pLoopingSound
    //   +152  Data (flags, etc.)
    //
    // PDB: TESContainer at offset 48 within TESObjectCONT (NOT 100 as in TESNPC!)
    //       objectList (tList) at TESContainer+4 = PDB 52, dump 68.
    // NPC uses TESContainer at PDB 100 (dump 120) — different inheritance chain.

    private const int ContStructSize = 172;
    private const int ContModelPathOffset = 80; // TESModel.cModel BSStringT
    private const int ContScriptOffset = 116; // TESScriptableForm vtable area (empirically verified)
    private const int ContContentsDataOffset = 68; // tList inline first node: data ptr (PDB 52+16)
    private const int ContContentsNextOffset = 72; // tList inline first node: next ptr (PDB 56+16)
    private const int ContFlagsOffset = 140; // TESObjectCONT flags (byte)

    /// <summary>
    ///     Read extended container data from a runtime TESObjectCONT struct.
    ///     Returns a ReconstructedContainer with weight, contents, and flags.
    /// </summary>
    public ReconstructedContainer? ReadRuntimeContainer(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x1B)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + ContStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[ContStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, ContStructSize);
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

        // Read flags
        var flags = buffer[ContFlagsOffset];

        // Read model path
        var modelPath = ReadBSStringT(offset, ContModelPathOffset);

        // Read script pointer
        var scriptFormId =
            FollowPointerToFormId(buffer,
                ContScriptOffset - 4); // -4 because offset is to TESScriptableForm, pointer is inside

        // Read container contents using same pattern as NPC inventory
        var contents = ReadContainerContents(buffer);

        return new ReconstructedContainer
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Flags = flags,
            Contents = contents,
            ModelPath = modelPath,
            Script = scriptFormId,
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read container contents from TESContainer tList at +120/+124.
    ///     Reuses the same ContainerObject reading logic as NPC inventory.
    /// </summary>
    private List<InventoryItem> ReadContainerContents(byte[] buffer)
    {
        var items = new List<InventoryItem>();

        // Read inline first node
        var firstDataPtr = BinaryUtils.ReadUInt32BE(buffer, ContContentsDataOffset);
        var firstNextPtr = BinaryUtils.ReadUInt32BE(buffer, ContContentsNextOffset);

        // Process inline first item
        var firstItem = ReadContainerObject(firstDataPtr);
        if (firstItem != null)
        {
            items.Add(firstItem);
        }

        // Follow chain of _Node (8 bytes each: data ptr + next ptr)
        var nextVA = firstNextPtr;
        var visited = new HashSet<uint>();
        while (nextVA != 0 && items.Count < MaxListItems && !visited.Contains(nextVA))
        {
            visited.Add(nextVA);
            var nodeFileOffset = VaToFileOffset(nextVA);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuf = ReadBytes(nodeFileOffset.Value, 8);
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

    #endregion

    #region BGSTerminal (empirically mapped)

    // BGSTerminal PDB inheritance chain (PDB size 168, dump 184 with +16):
    //   +0    TESBoundAnimObject (64 bytes)
    //   +64   TESFullName vtable → +68 pString
    //   +76   TESModel vtable → +80 pString (model path)
    //   +100  TESScriptableForm → +104 pScript
    //   +116  BGSDestructibleObjectForm
    //   +128  BGSTerminal-specific data
    //   +132  Difficulty (byte)
    //   +133  Flags (byte)
    //   +136  Password BSStringT (if any)
    //   +144  Menu items list (needs BSSimpleList traversal)
    //
    // NOTE: Terminal menu items are complex (120 bytes each) and may require
    // additional work to parse fully. For now, we just get basic terminal info.

    private const int TermStructSize = 184;
    private const int TermDifficultyOffset = 132; // Difficulty (byte 0-4)
    private const int TermFlagsOffset = 133; // Flags (byte)
    private const int TermPasswordOffset = 136; // Password BSStringT (8 bytes)

    /// <summary>
    ///     Read extended terminal data from a runtime BGSTerminal struct.
    ///     Returns a ReconstructedTerminal with difficulty, flags, and password.
    /// </summary>
    public ReconstructedTerminal? ReadRuntimeTerminal(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x17)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + TermStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[TermStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, TermStructSize);
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

        // Read difficulty and flags
        var difficulty = buffer[TermDifficultyOffset];
        var flags = buffer[TermFlagsOffset];

        // Validate difficulty (0-4 range)
        if (difficulty > 4)
        {
            difficulty = 0; // Default to very easy if invalid
        }

        // Read password (optional)
        var password = ReadBSStringT(offset, TermPasswordOffset);

        // TODO: Parse menu items via BSSimpleList at +144 if needed

        return new ReconstructedTerminal
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Difficulty = difficulty,
            Flags = flags,
            Password = password,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #endregion

    #region TESCreature (empirically mapped)

    // TESCreature PDB inheritance chain (PDB size 424, dump 440 with +16):
    //   +0    TESActorBase (complex hierarchy similar to TESNPC)
    //         TESBoundAnimObject(64) + TESActorBaseData(56=120) + TESContainer(12=132) +
    //         TESSpellList(16=148) + TESAIForm(24=172) + ...
    //   +172  TESFullName vtable → +176 pString
    //   +184  TESModel vtable → +188 pString (model path)
    //   +212  TESScriptableForm vtable → +216 pScript
    //   +228  Combat skill (byte)
    //   +229  Magic skill (byte)
    //   +230  Stealth skill (byte)
    //   +232  Attack damage (int16 BE)
    //   +236  Creature type (byte): 0=Animal, 1=MutatedAnimal, 2=MutatedInsect, etc.
    //
    // ACBS (actor base stats) at same +24 offset as NPC

    private const int CreaStructSize = 440;
    private const int CreaModelPathOffset = 188; // TESModel.cModel BSStringT
    private const int CreaScriptOffset = 220; // TESScriptableForm.pScript
    private const int CreaCombatSkillOffset = 228; // Combat skill (byte)
    private const int CreaMagicSkillOffset = 229; // Magic skill (byte)
    private const int CreaStealthSkillOffset = 230; // Stealth skill (byte)
    private const int CreaAttackDamageOffset = 232; // Attack damage (int16 BE)
    private const int CreaTypeOffset = 236; // Creature type (byte)
    private const int CreaAcbsOffset = 24; // ACBS at same location as NPC

    /// <summary>
    ///     Read extended creature data from a runtime TESCreature struct.
    ///     Returns a ReconstructedCreature with skills, type, and model path.
    /// </summary>
    public ReconstructedCreature? ReadRuntimeCreature(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x2B)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + CreaStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[CreaStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, CreaStructSize);
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
        var modelPath = ReadBSStringT(offset, CreaModelPathOffset);

        // Read script pointer
        var scriptFormId = FollowPointerToFormId(buffer, CreaScriptOffset);

        // Read ACBS (actor base stats) at +24, same structure as NPC
        var stats = ReadCreatureActorBaseStats(buffer, CreaAcbsOffset, offset);

        return new ReconstructedCreature
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

        // Basic validation
        if (fatigueBase > 5000 || barterGold > 50000 || speedMultiplier > 500)
        {
            return null;
        }

        if (!IsNormalFloat(karma))
        {
            return null;
        }

        return new ActorBaseSubrecord(
            flags, fatigueBase, barterGold, level, calcMin, calcMax,
            speedMultiplier, karma, dispositionBase, templateFlags,
            structOffset + acbsOffset, true);
    }

    #endregion

    #region BGSProjectile Physics (PDB-derived offsets, +16 universal shift)

    // BGSProjectile: PDB 208, dump 224
    // Class chain: TESBoundObject(64) + TESFullName(12) + TESModel(24) = +100
    // BGSProjectileData embedded struct at PDB +96 → dump +112
    // BGSProjectileData fields (offsets within the sub-struct):
    //   +0:  vtable (4 bytes)
    //   +4:  fGravity (float BE)
    //   +8:  fSpeed (float BE)
    //   +12: fRange (float BE)
    //   +36: pExplosionType (BGSExplosion*, 4 bytes)
    //   +40: pActiveSoundLoop (TESSound*, 4 bytes)
    //   +44: fMuzzleFlashDuration (float BE)
    //   +52: fForce (float BE)
    //   +56: pCountdownSound (TESSound*, 4 bytes)
    //   +60: pDeactivateSound (TESSound*, 4 bytes)

    private const int ProjStructSize = 224; // PDB 208 + 16
    private const int ProjDataBase = 112; // BGSProjectileData start (PDB 96 + 16)
    private const int ProjGravityOffset = ProjDataBase + 4; // 116
    private const int ProjSpeedOffset = ProjDataBase + 8; // 120
    private const int ProjRangeOffset = ProjDataBase + 12; // 124
    private const int ProjExplosionOffset = ProjDataBase + 36; // 148
    private const int ProjActiveSoundOffset = ProjDataBase + 40; // 152
    private const int ProjMuzzleFlashDurOffset = ProjDataBase + 44; // 156
    private const int ProjForceOffset = ProjDataBase + 52; // 164
    private const int ProjCountdownSoundOffset = ProjDataBase + 56; // 168
    private const int ProjDeactivateSoundOffset = ProjDataBase + 60; // 172

    /// <summary>
    ///     Read BGSProjectile physics/sound data from a runtime struct at the given file offset.
    ///     Returns null if validation fails (struct not readable or values out of range).
    /// </summary>
    public ProjectilePhysicsData? ReadProjectilePhysics(long fileOffset, uint expectedFormId)
    {
        if (fileOffset + ProjStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[ProjStructSize];
        try
        {
            _accessor.ReadArray(fileOffset, buffer, 0, ProjStructSize);
        }
        catch
        {
            return null;
        }

        // Validate FormID at +12
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != expectedFormId)
        {
            return null;
        }

        // Validate FormType at +4 should be 0x33 (PROJ)
        if (buffer[4] != 0x33)
        {
            return null;
        }

        // Read physics floats
        var gravity = BinaryUtils.ReadFloatBE(buffer, ProjGravityOffset);
        var speed = BinaryUtils.ReadFloatBE(buffer, ProjSpeedOffset);
        var range = BinaryUtils.ReadFloatBE(buffer, ProjRangeOffset);
        var muzzleFlashDuration = BinaryUtils.ReadFloatBE(buffer, ProjMuzzleFlashDurOffset);
        var force = BinaryUtils.ReadFloatBE(buffer, ProjForceOffset);

        // Basic validation: at least one physics value should be non-zero and reasonable
        if (float.IsNaN(speed) || float.IsInfinity(speed) ||
            float.IsNaN(gravity) || float.IsInfinity(gravity) ||
            float.IsNaN(range) || float.IsInfinity(range))
        {
            return null;
        }

        // Follow pointer fields
        var explosion = FollowPointerToFormId(buffer, ProjExplosionOffset);
        var activeSound = FollowPointerToFormId(buffer, ProjActiveSoundOffset);
        var countdownSound = FollowPointerToFormId(buffer, ProjCountdownSoundOffset);
        var deactivateSound = FollowPointerToFormId(buffer, ProjDeactivateSoundOffset);

        // Read world model path
        var modelPath = ReadBSStringT(fileOffset, WeapModelPathOffset); // Same +80 offset as other TESBoundObject types

        return new ProjectilePhysicsData
        {
            Gravity = gravity,
            Speed = speed,
            Range = range,
            ExplosionFormId = explosion,
            ActiveSoundLoopFormId = activeSound,
            CountdownSoundFormId = countdownSound,
            DeactivateSoundFormId = deactivateSound,
            MuzzleFlashDuration = muzzleFlashDuration,
            Force = force,
            ModelPath = modelPath
        };
    }

    #endregion

    #region TESTopic (DIAL) — PDB 72 bytes, TESForm + TESFullName

    // TESTopic: PDB 72 bytes. Inherits TESForm (24 bytes) + TESFullName (12 bytes).
    //
    // PDB layout:
    //   +0    TESForm base (vtable, cFormType, iFormFlags, iFormID, pSourceFiles) — 24 bytes
    //   +24   TESFullName vtable (4 bytes)
    //   +28   TESFullName BSStringT (pString + sLen) — 8 bytes
    //   +36   m_Data.type (char) — 0=Topic..7=Radio
    //   +37   m_Data.cFlags (char) — bit0=Rumors, bit1=TopLevel
    //   +38   padding (2 bytes)
    //   +40   m_fPriority (float)
    //   +44   m_listQuestInfo (BSSimpleList<QUEST_INFO*>, 8 bytes)
    //   +52   cDummyPrompt (BSStringT, 8 bytes)
    //   +60   m_unk3C (uint32)
    //   +64   m_unk40 (uint32)
    //   +68   m_uiTopicCount (uint32)
    //
    // Dump shift: TBD — determined empirically by ProbeDialTopicLayout().
    // TESTopicInfo (also TESForm-derived, no TESBoundObject) has +4 shift.
    // TESTopic has the same inheritance depth, so +4 is the initial hypothesis.

    // Empirically verified dump shift: +16 (confirmed on early + debug dumps)
    // TESTopic has extra base class bytes vs PDB, similar to TESBoundObject-derived types.
    // Verified: FullName="Steal" at +44 (PDB+28+16), type=2 at +52 (PDB+36+16).
    private const int DialStructSize = 88; // PDB 72 + 16
    private const int DialFullNameOffset = 44; // PDB+28 + 16 (BSStringT for display name)
    private const int DialDataTypeOffset = 52; // PDB+36 + 16 (m_Data.type: 0=Topic..7=Radio)
    private const int DialDataFlagsOffset = 53; // PDB+37 + 16 (m_Data.cFlags: bit0=Rumors, bit1=TopLevel)
    private const int DialPriorityOffset = 56; // PDB+40 + 16 (m_fPriority: float)
    private const int DialQuestInfoListOffset = 60; // PDB+44 + 16 (BSSimpleList<QUEST_INFO*>, 8 bytes)
    private const int DialDummyPromptOffset = 68; // PDB+52 + 16 (cDummyPrompt: BSStringT)
    private const int DialTopicCountOffset = 84; // PDB+68 + 16 (m_uiTopicCount: uint32)

    /// <summary>
    ///     Probe a known DIAL runtime struct to determine the correct dump shift.
    ///     Tries +0, +4, +8, +16 shift hypotheses and logs which one produces valid data.
    ///     Returns the best shift value, or -1 if none worked.
    /// </summary>
    public int ProbeDialTopicLayout(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null)
        {
            return -1;
        }

        var offset = entry.TesFormOffset.Value;
        var readSize = 96; // Read extra bytes to accommodate larger shifts
        if (offset + readSize > _fileSize)
        {
            return -1;
        }

        var buffer = new byte[readSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, readSize);
        }
        catch
        {
            return -1;
        }

        // Validate FormID at +12 (no shift — standard TESForm header)
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return -1;
        }

        var log = Logger.Instance;
        log.Info($"  [DIAL Probe] Entry: {entry.EditorId} (FormID 0x{entry.FormId:X8}), TesFormOffset=0x{offset:X}");

        // Try each shift hypothesis
        int[] shifts = [0, 4, 8, 16];
        var bestShift = -1;
        var bestScore = 0;

        foreach (var shift in shifts)
        {
            var score = 0;
            var details = new StringBuilder();
            details.Append($"    Shift +{shift}: ");

            // Check BSStringT for FullName at PDB+28+shift
            var bstOff = 28 + shift;
            if (bstOff + 8 <= buffer.Length)
            {
                var pStr = BinaryUtils.ReadUInt32BE(buffer, bstOff);
                var sLen = BinaryUtils.ReadUInt16BE(buffer, bstOff + 4);
                var strValid = pStr != 0 && sLen > 0 && sLen < 256 && IsValidPointer(pStr);
                if (strValid)
                {
                    // Try to read the actual string
                    var name = ReadBSStringT(offset, bstOff);
                    if (name != null)
                    {
                        details.Append($"FullName=\"{name}\" ✓, ");
                        score += 3;
                    }
                    else
                    {
                        details.Append("FullName=<ptr valid but string unreadable>, ");
                        score += 1;
                    }
                }
                else
                {
                    details.Append($"FullName=<invalid ptr=0x{pStr:X8} len={sLen}>, ");
                }
            }

            // Check m_Data.type at PDB+36+shift (should be 0-7)
            var typeOff = 36 + shift;
            if (typeOff < buffer.Length)
            {
                var topicType = buffer[typeOff];
                if (topicType <= 7)
                {
                    details.Append($"type={topicType} ✓, ");
                    score += 2;
                }
                else
                {
                    details.Append($"type={topicType} ✗, ");
                }
            }

            // Check m_Data.cFlags at PDB+37+shift (should be 0-3, only bits 0-1 used)
            var flagsOff = 37 + shift;
            if (flagsOff < buffer.Length)
            {
                var flags = buffer[flagsOff];
                if (flags <= 3)
                {
                    details.Append($"flags={flags} ✓, ");
                    score += 1;
                }
                else
                {
                    details.Append($"flags=0x{flags:X2} ✗, ");
                }
            }

            // Check m_fPriority at PDB+40+shift (should be a reasonable float, typically 50.0)
            var priorityOff = 40 + shift;
            if (priorityOff + 4 <= buffer.Length)
            {
                var priority = BinaryUtils.ReadFloatBE(buffer, priorityOff);
                if (IsNormalFloat(priority) && priority >= 0 && priority <= 200)
                {
                    details.Append($"priority={priority:F1} ✓, ");
                    score += 2;
                }
                else
                {
                    details.Append($"priority={priority:F1} ✗, ");
                }
            }

            // Check m_uiTopicCount at PDB+68+shift (should be a reasonable count, 0-10000)
            var countOff = 68 + shift;
            if (countOff + 4 <= buffer.Length)
            {
                var count = BinaryUtils.ReadUInt32BE(buffer, countOff);
                if (count <= 10000)
                {
                    details.Append($"topicCount={count} ✓");
                    score += 1;
                }
                else
                {
                    details.Append($"topicCount={count} ✗");
                }
            }

            log.Info(details.ToString());
            log.Info($"      Score: {score}/9");

            if (score > bestScore)
            {
                bestScore = score;
                bestShift = shift;
            }
        }

        if (bestShift >= 0)
        {
            log.Info($"  [DIAL Probe] Best shift: +{bestShift} (score {bestScore}/9)");
        }

        return bestShift;
    }

    /// <summary>
    ///     Read extended topic data from a runtime TESTopic struct.
    ///     Returns topic metadata, or null if validation fails.
    /// </summary>
    public RuntimeDialogTopicInfo? ReadRuntimeDialogTopic(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + DialStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[DialStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, DialStructSize);
        }
        catch
        {
            return null;
        }

        // Validate: FormID at offset 12 should match
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        // Read topic type and flags
        var topicType = buffer[DialDataTypeOffset];
        var flags = buffer[DialDataFlagsOffset];

        // Validate topic type (0-7)
        if (topicType > 7)
        {
            return null;
        }

        // Read priority
        var priority = BinaryUtils.ReadFloatBE(buffer, DialPriorityOffset);
        if (!IsNormalFloat(priority) || priority < 0 || priority > 200)
        {
            priority = 0;
        }

        // Read topic count
        var topicCount = BinaryUtils.ReadUInt32BE(buffer, DialTopicCountOffset);
        if (topicCount > 10000)
        {
            topicCount = 0;
        }

        // Read FullName via BSStringT
        var fullName = entry.DisplayName ?? ReadBSStringT(offset, DialFullNameOffset);

        // Read DummyPrompt via BSStringT
        var dummyPrompt = ReadBSStringT(offset, DialDummyPromptOffset);

        return new RuntimeDialogTopicInfo
        {
            FormId = formId,
            TopicType = topicType,
            Flags = flags,
            Priority = priority,
            TopicCount = topicCount,
            FullName = fullName,
            DummyPrompt = dummyPrompt
        };
    }

    /// <summary>
    ///     Walk the m_listQuestInfo BSSimpleList on a TESTopic struct to extract
    ///     Quest → INFO mappings. Each list node points to a QUEST_INFO struct
    ///     (52 bytes) containing pQuest and infoLinkArray.
    ///     Returns a list of (QuestFormId, [InfoFormIds]) pairs.
    /// </summary>
    public List<TopicQuestLink> WalkTopicQuestInfoList(RuntimeEditorIdEntry entry)
    {
        var results = new List<TopicQuestLink>();

        if (entry.TesFormOffset == null)
        {
            return results;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + DialStructSize > _fileSize)
        {
            return results;
        }

        // Read the BSSimpleList inline node (8 bytes: m_item + m_pkNext)
        var listOffset = offset + DialQuestInfoListOffset;
        var listBuf = ReadBytes(listOffset, 8);
        if (listBuf == null)
        {
            return results;
        }

        var firstItem = BinaryUtils.ReadUInt32BE(listBuf); // QUEST_INFO* pointer
        var firstNext = BinaryUtils.ReadUInt32BE(listBuf, 4); // _Node* pointer

        // Process inline first item
        var firstLink = ReadQuestInfo(firstItem);
        if (firstLink != null)
        {
            results.Add(firstLink);
        }

        // Follow BSSimpleList chain (same pattern as NPC inventory traversal)
        var nextVA = firstNext;
        var visited = new HashSet<uint>();
        while (nextVA != 0 && results.Count < MaxListItems && !visited.Contains(nextVA))
        {
            visited.Add(nextVA);
            var nodeFileOffset = VaToFileOffset(nextVA);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuf = ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuf == null)
            {
                break;
            }

            var dataPtr = BinaryUtils.ReadUInt32BE(nodeBuf); // QUEST_INFO*
            var nextPtr = BinaryUtils.ReadUInt32BE(nodeBuf, 4); // _Node*

            var link = ReadQuestInfo(dataPtr);
            if (link != null)
            {
                results.Add(link);
            }

            nextVA = nextPtr;
        }

        return results;
    }

    /// <summary>
    ///     Read a QUEST_INFO struct (52 bytes) to extract Quest FormID and INFO FormIDs.
    ///     QUEST_INFO layout:
    ///     +0  pQuest (TESQuest*, 4 bytes)
    ///     +4  infoArray (TopicInfoArray/NiTLargeArray, 24 bytes) — not used, parallel array
    ///     +28 infoLinkArray (BSSimpleArray&lt;INFO_LINK_ELEMENT,1024&gt;, 16 bytes)
    ///     +44 pRemovedQuest (TESQuest*, 4 bytes)
    ///     +48 bInitialized (bool, 1 byte)
    /// </summary>
    private TopicQuestLink? ReadQuestInfo(uint questInfoVA)
    {
        if (questInfoVA == 0)
        {
            return null;
        }

        var fileOffset = VaToFileOffset(questInfoVA);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = ReadBytes(fileOffset.Value, 52);
        if (buf == null)
        {
            return null;
        }

        // Follow pQuest pointer at +0 → TESQuest FormID
        var pQuest = BinaryUtils.ReadUInt32BE(buf);
        var questFormId = FollowPointerVaToFormId(pQuest);

        if (questFormId == null)
        {
            return null;
        }

        // Read infoArray (NiTLargeArray<TESTopicInfo*>) at +4:
        //   +4:  vtable (4)
        //   +8:  m_pBase (4) — pointer to TESTopicInfo*[]
        //   +12: m_uiMaxSize (4)
        //   +16: m_uiSize (4) — actual number of elements
        //   +20: m_uiESize (4)
        //   +24: m_uiGrowBy (4)
        var pBase = BinaryUtils.ReadUInt32BE(buf, 8);
        var arraySize = BinaryUtils.ReadUInt32BE(buf, 16);

        var infoEntries = new List<InfoPointerEntry>();

        if (pBase != 0 && arraySize > 0 && arraySize <= 2000)
        {
            var baseFileOffset = VaToFileOffset(pBase);
            if (baseFileOffset != null)
            {
                // Each element is a TESTopicInfo* pointer (4 bytes)
                var elementCount = (int)Math.Min(arraySize, 1024);
                var elementBytes = ReadBytes(baseFileOffset.Value, elementCount * 4);
                if (elementBytes != null)
                {
                    for (var i = 0; i < elementCount; i++)
                    {
                        var pInfo = BinaryUtils.ReadUInt32BE(elementBytes, i * 4);
                        var infoFormId = FollowPointerVaToFormId(pInfo);
                        if (infoFormId != null)
                        {
                            infoEntries.Add(new InfoPointerEntry(infoFormId.Value, pInfo));
                        }
                    }
                }
            }
        }

        return new TopicQuestLink(questFormId.Value, infoEntries);
    }

    #endregion

    #region TESTopicInfo (INFO) — PDB 80 bytes, TESForm-derived with +4 field shift

    // TESTopicInfo: PDB 80 bytes, records spaced 80 bytes in dump.
    // TESForm base: vtable(+0), cFormType(+4), iFormFlags(+8), iFormID(+12), pSourceFiles(+16).
    //
    // PDB vs dump offset mapping: all TESTopicInfo-specific fields shift +4 from PDB offsets.
    // Empirically confirmed: cPrompt BSStringT.pString at dump+44 (PDB+40), FormID at dump+12 (PDB+12).
    //
    // PDB+24 → dump+28: objConditions (TESCondition, 8 bytes PDB)
    // PDB+32 → dump+36: iInfoIndex (uint16) — ordering within topic
    // PDB+34 → dump+38: bSaidOnce (bool)
    // PDB+35 → dump+39: m_Data (TOPIC_INFO_DATA, 4 bytes: type, nextSpeaker, flags, flagsExt)
    // PDB+40 → dump+44: cPrompt (BSStringT, 8 bytes) — player-visible prompt text (InfoPromptOffset=44)
    // PDB+48 → dump+52: m_listAddTopics (BSSimpleList<TESTopic*>, 8 bytes)
    // PDB+56 → dump+60: m_pConversationData (pointer, 4 bytes)
    // PDB+60 → dump+64: pSpeaker (TESActorBase*, 4 bytes)
    // PDB+64 → dump+68: pPerkSkillStat (pointer, 4 bytes)
    // PDB+68 → dump+72: eDifficulty (uint32)
    // PDB+72 → dump+76: pOwnerQuest (TESQuest*, 4 bytes)

    private const int InfoStructSize = 80;
    private const int InfoIndexOffset = 36; // PDB+32, uint16 BE
    private const int InfoDataOffset = 39; // PDB+35, TOPIC_INFO_DATA (4 bytes)
    private const int InfoSpeakerPtrOffset = 64; // PDB+60, TESActorBase*
    private const int InfoDifficultyOffset = 72; // PDB+68, uint32 BE
    private const int InfoQuestPtrOffset = 76; // PDB+72, TESQuest*

    /// <summary>
    ///     Read extended dialogue info data from a runtime TESTopicInfo struct.
    ///     Extracts speaker, quest, flags, difficulty, and info index.
    ///     Returns null if the struct cannot be read or validation fails.
    /// </summary>
    public RuntimeDialogueInfo? ReadRuntimeDialogueInfo(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + InfoStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[InfoStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, InfoStructSize);
        }
        catch
        {
            return null;
        }

        // Validate: FormID at offset 12 should match the hash table entry
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        // Read iInfoIndex (uint16 BE at dump+36)
        var infoIndex = BinaryUtils.ReadUInt16BE(buffer, InfoIndexOffset);

        // Read TOPIC_INFO_DATA (4 bytes at dump+39): type, nextSpeaker, flags, flagsExt
        byte dataType = 0;
        byte dataNextSpeaker = 0;
        byte dataFlags = 0;
        byte dataFlagsExt = 0;
        if (InfoDataOffset + 4 <= buffer.Length)
        {
            dataType = buffer[InfoDataOffset];
            dataNextSpeaker = buffer[InfoDataOffset + 1];
            dataFlags = buffer[InfoDataOffset + 2];
            dataFlagsExt = buffer[InfoDataOffset + 3];
        }

        // Follow pSpeaker pointer at dump+64 → TESActorBase* → get NPC FormID
        var speakerFormId = FollowPointerToFormId(buffer, InfoSpeakerPtrOffset);

        // Read eDifficulty (uint32 BE at dump+72)
        var difficulty = BinaryUtils.ReadUInt32BE(buffer, InfoDifficultyOffset);
        if (difficulty > 10)
        {
            difficulty = 0; // Sanity check: difficulty should be 0-5
        }

        // Follow pOwnerQuest pointer at dump+76 → TESQuest* → get Quest FormID
        var questFormId = FollowPointerToFormId(buffer, InfoQuestPtrOffset);

        return new RuntimeDialogueInfo
        {
            FormId = formId,
            InfoIndex = infoIndex,
            TopicType = dataType,
            NextSpeaker = dataNextSpeaker,
            InfoFlags = dataFlags,
            InfoFlagsExt = dataFlagsExt,
            SpeakerFormId = speakerFormId,
            Difficulty = difficulty,
            QuestFormId = questFormId,
            PromptText = entry.DialogueLine
        };
    }

    /// <summary>
    ///     Read a TESTopicInfo struct from a virtual address (found via topic walk).
    ///     Similar to ReadRuntimeDialogueInfo but starts from a VA instead of a hash table entry.
    /// </summary>
    public RuntimeDialogueInfo? ReadRuntimeDialogueInfoFromVA(uint va)
    {
        var fileOffset = VaToFileOffset(va);
        if (fileOffset == null || fileOffset.Value + InfoStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[InfoStructSize];
        try
        {
            _accessor.ReadArray(fileOffset.Value, buffer, 0, InfoStructSize);
        }
        catch
        {
            return null;
        }

        // Read FormID
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId == 0 || formId == 0xFFFFFFFF)
        {
            return null;
        }

        // Read iInfoIndex
        var infoIndex = BinaryUtils.ReadUInt16BE(buffer, InfoIndexOffset);

        // Read TOPIC_INFO_DATA
        byte dataType = 0, dataNextSpeaker = 0, dataFlags = 0, dataFlagsExt = 0;
        if (InfoDataOffset + 4 <= buffer.Length)
        {
            dataType = buffer[InfoDataOffset];
            dataNextSpeaker = buffer[InfoDataOffset + 1];
            dataFlags = buffer[InfoDataOffset + 2];
            dataFlagsExt = buffer[InfoDataOffset + 3];
        }

        // Follow pSpeaker pointer
        var speakerFormId = FollowPointerToFormId(buffer, InfoSpeakerPtrOffset);

        // Read eDifficulty
        var difficulty = BinaryUtils.ReadUInt32BE(buffer, InfoDifficultyOffset);
        if (difficulty > 10)
        {
            difficulty = 0;
        }

        // Follow pOwnerQuest pointer
        var questFormId = FollowPointerToFormId(buffer, InfoQuestPtrOffset);

        // Read cPrompt BSStringT
        var promptText = ReadBSStringT(fileOffset.Value, 44); // InfoPromptOffset = 44

        return new RuntimeDialogueInfo
        {
            FormId = formId,
            InfoIndex = infoIndex,
            TopicType = dataType,
            NextSpeaker = dataNextSpeaker,
            InfoFlags = dataFlags,
            InfoFlagsExt = dataFlagsExt,
            SpeakerFormId = speakerFormId,
            Difficulty = difficulty,
            QuestFormId = questFormId,
            PromptText = promptText,
            DumpOffset = fileOffset.Value
        };
    }

    #endregion

    #region Pointer Following

    /// <summary>
    ///     Follow a 4-byte big-endian pointer at the given buffer offset to a TESForm object,
    ///     then read and return the FormID (uint32 BE at offset 12 in TESForm header).
    ///     Returns null if the pointer is invalid or the target is not a valid TESForm.
    /// </summary>
    private uint? FollowPointerToFormId(byte[] buffer, int pointerOffset)
    {
        if (pointerOffset + 4 > buffer.Length)
        {
            return null;
        }

        var pointer = BinaryUtils.ReadUInt32BE(buffer, pointerOffset);
        if (pointer == 0)
        {
            return null;
        }

        // Validate pointer is in dump memory range
        if (!IsValidPointer(pointer))
        {
            return null;
        }

        // Convert virtual address to file offset
        var fileOffset = _minidumpInfo.VirtualAddressToFileOffset(Xbox360VaToLong(pointer));
        if (!fileOffset.HasValue || fileOffset.Value + 24 > _fileSize)
        {
            return null;
        }

        // Read 24-byte TESForm header at target
        var tesFormBuffer = new byte[24];
        try
        {
            _accessor.ReadArray(fileOffset.Value, tesFormBuffer, 0, 24);
        }
        catch
        {
            return null;
        }

        // Validate form type (byte at offset 4, should be < 200)
        var formType = tesFormBuffer[4];
        if (formType > 200)
        {
            return null;
        }

        // Read FormID (uint32 BE at offset 12)
        var formId = BinaryUtils.ReadUInt32BE(tesFormBuffer, 12);

        // Basic validation
        if (formId == 0 || formId == 0xFFFFFFFF)
        {
            return null;
        }

        return formId;
    }

    /// <summary>
    ///     Check if a 32-bit value is a valid Xbox 360 pointer within captured memory.
    /// </summary>
    private bool IsValidPointer(uint value)
    {
        if (value == 0)
        {
            return false;
        }

        return _minidumpInfo.VirtualAddressToFileOffset(Xbox360VaToLong(value)).HasValue;
    }

    /// <summary>
    ///     Convert a 32-bit Xbox 360 virtual address to the 64-bit representation
    ///     used by minidump memory regions.
    /// </summary>
    private static long Xbox360VaToLong(uint address)
    {
        return unchecked((int)address);
    }

    #endregion

    #region NPC Sub-Item Extraction (BSSimpleList / NiTListItem traversal)

    // EMPIRICALLY VERIFIED NPC sub-item list layouts from hex dumps of
    // DocMitchell, MoiraBrown, LucasSimms, and GSSunnySmiles:
    //
    // TESContainer at NPC +116 uses tList (BSSimpleList, 8-byte inline node):
    //   +120: ContainerObject* firstData (4 bytes, pointer)
    //   +124: _Node* nextNode (4 bytes, pointer)
    //   Each heap _Node: { ContainerObject* data(4), _Node* next(4) }
    //   ContainerObject: { int32 count(4 BE), TESForm* pItem(4 BE) }
    //
    // Faction list at NPC +112 uses NiTListItem (doubly-linked, 16-byte nodes):
    //   +112: NiTListItem* pHead (4 bytes, pointer; NULL = no factions)
    //   Each NiTListItem: { pPrev(4), pNext(4), TESFaction* pFaction(4), rank_byte(1)+pad(3) }
    //   pNext chain traverses forward. Follow pFaction to read faction FormID at TESForm+12.

    private const int NpcContainerDataOffset = 120; // tList inline first node: data ptr
    private const int NpcContainerNextOffset = 124; // tList inline first node: next ptr
    private const int NpcFactionListHeadOffset = 112; // NiTListItem* head (NULL = empty)
    private const int MaxListItems = 50; // safety limit per list

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
        while (nextVA != 0 && items.Count < MaxListItems && !visited.Contains(nextVA))
        {
            visited.Add(nextVA);
            var nodeFileOffset = VaToFileOffset(nextVA);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuf = ReadBytes(nodeFileOffset.Value, 8);
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
    ///     Follow a ContainerObject* pointer to read { count(int32 BE), pItem(TESForm*) }.
    ///     Returns an InventoryItem or null.
    /// </summary>
    private InventoryItem? ReadContainerObject(uint containerObjectVA)
    {
        if (containerObjectVA == 0)
        {
            return null;
        }

        var fileOffset = VaToFileOffset(containerObjectVA);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = ReadBytes(fileOffset.Value, 8);
        if (buf == null)
        {
            return null;
        }

        var count = ReadInt32BE(buf, 0);
        var pItem = BinaryUtils.ReadUInt32BE(buf, 4);

        // Validate count (reasonable range for inventory)
        if (count <= 0 || count > 100000)
        {
            return null;
        }

        // Follow pItem to read the item's FormID
        var itemFormId = FollowPointerVaToFormId(pItem);
        if (itemFormId == null)
        {
            return null;
        }

        return new InventoryItem(itemFormId.Value, count);
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
        while (nodeVA != 0 && factions.Count < MaxListItems && !visited.Contains(nodeVA))
        {
            visited.Add(nodeVA);
            var nodeFileOffset = VaToFileOffset(nodeVA);
            if (nodeFileOffset == null)
            {
                break;
            }

            // Read 16-byte NiTListItem
            var nodeBuf = ReadBytes(nodeFileOffset.Value, 16);
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
                var factionFileOffset = VaToFileOffset(pFaction);
                if (factionFileOffset != null)
                {
                    var formBuf = ReadBytes(factionFileOffset.Value, 16);
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
    ///     Follow a virtual address pointer to a TESForm and return its FormID.
    ///     Similar to FollowPointerToFormId but takes a VA directly (not buffer offset).
    /// </summary>
    private uint? FollowPointerVaToFormId(uint va)
    {
        if (va == 0)
        {
            return null;
        }

        var fileOffset = VaToFileOffset(va);
        if (fileOffset == null)
        {
            return null;
        }

        var formBuf = ReadBytes(fileOffset.Value, 16);
        if (formBuf == null)
        {
            return null;
        }

        var formType = formBuf[4];
        if (formType > 200)
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(formBuf, 12);
        if (formId == 0 || formId == 0xFFFFFFFF)
        {
            return null;
        }

        return formId;
    }

    /// <summary>
    ///     Convert a 32-bit Xbox 360 virtual address to a file offset in the dump.
    ///     Returns null if the VA is not in any captured memory region.
    /// </summary>
    private long? VaToFileOffset(uint va)
    {
        if (va == 0)
        {
            return null;
        }

        return _minidumpInfo.VirtualAddressToFileOffset(Xbox360VaToLong(va));
    }

    /// <summary>
    ///     Read a byte array from the dump file at a given file offset.
    ///     Returns null if the read fails.
    /// </summary>
    private byte[]? ReadBytes(long fileOffset, int count)
    {
        if (fileOffset + count > _fileSize)
        {
            return null;
        }

        var buf = new byte[count];
        try
        {
            _accessor.ReadArray(fileOffset, buf, 0, count);
            return buf;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    ///     Read a BSStringT string from a TESForm object.
    ///     BSStringT layout (8 bytes, big-endian):
    ///     Offset 0: pString (char* pointer, 4 bytes BE)
    ///     Offset 4: sLen (uint16 BE)
    /// </summary>
    public string? ReadBSStringT(long tesFormFileOffset, int fieldOffset)
    {
        var bstOffset = tesFormFileOffset + fieldOffset;
        if (bstOffset + 8 > _fileSize)
        {
            return null;
        }

        var bstBuffer = new byte[8];
        _accessor.ReadArray(bstOffset, bstBuffer, 0, 8);

        var pString = BinaryUtils.ReadUInt32BE(bstBuffer);
        var sLen = BinaryUtils.ReadUInt16BE(bstBuffer, 4);

        if (pString == 0 || sLen == 0 || sLen > 4096)
        {
            return null;
        }

        if (!IsValidPointer(pString))
        {
            return null;
        }

        var strFileOffset = _minidumpInfo.VirtualAddressToFileOffset(Xbox360VaToLong(pString));
        if (!strFileOffset.HasValue || strFileOffset.Value + sLen > _fileSize)
        {
            return null;
        }

        var strBuffer = new byte[sLen];
        _accessor.ReadArray(strFileOffset.Value, strBuffer, 0, sLen);

        // Validate: mostly printable ASCII
        var printable = 0;
        for (var i = 0; i < sLen; i++)
        {
            var c = strBuffer[i];
            if ((c >= 32 && c <= 126) || c == '\n' || c == '\r' || c == '\t')
            {
                printable++;
            }
        }

        if (printable < sLen * 0.8)
        {
            return null;
        }

        return Encoding.ASCII.GetString(strBuffer, 0, sLen);
    }

    private static int ReadInt32BE(byte[] data, int offset)
    {
        return (int)BinaryUtils.ReadUInt32BE(data, offset);
    }

    /// <summary>
    ///     Read a float and validate it's within an expected range.
    ///     Returns 0 if the value is NaN, Inf, or outside range.
    /// </summary>
    private static float ReadValidatedFloat(byte[] buffer, int offset, float min, float max)
    {
        if (offset + 4 > buffer.Length)
        {
            return 0;
        }

        var value = BinaryUtils.ReadFloatBE(buffer, offset);
        if (!IsNormalFloat(value) || value < min || value > max)
        {
            return 0;
        }

        return value;
    }

    /// <summary>
    ///     Check if a float is a normal (non-NaN, non-Infinity) value.
    /// </summary>
    private static bool IsNormalFloat(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    #endregion

    #region TESObjectLAND / LoadedLandData

    // TESObjectLAND (PDB 44, runtime 60 bytes, FormType 0x45)
    // Runtime offset +56 contains pLoadedData (LoadedLandData*) pointer.
    //
    // LoadedLandData (164 bytes / 0xA4):
    //   +152: iCellX (int32 BE)
    //   +156: iCellY (int32 BE)
    //   +160: fBaseHeight (float32 BE)

    private const int LandStructSize = 60; // TESObjectLAND runtime size
    private const int LandLoadedDataPtrOffset = 56; // pLoadedData pointer offset
    private const int LoadedDataSize = 164; // LoadedLandData struct size
    private const int LoadedDataCellXOffset = 152; // iCellX offset in LoadedLandData
    private const int LoadedDataCellYOffset = 156; // iCellY offset in LoadedLandData
    private const int LoadedDataBaseHeightOffset = 160; // fBaseHeight offset in LoadedLandData

    /// <summary>
    ///     Read cell coordinates from a runtime TESObjectLAND struct's LoadedLandData.
    ///     Returns null if the LAND has no loaded data or the pointer is invalid.
    /// </summary>
    public RuntimeLoadedLandData? ReadRuntimeLandData(RuntimeEditorIdEntry entry)
    {
        // FormType 0x45 = LAND (stable across builds)
        if (entry.TesFormOffset == null || entry.FormType != 0x45)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + LandStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[LandStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, LandStructSize);
        }
        catch
        {
            return null;
        }

        // Validate FormID at offset 16 (runtime TESForm layout)
        var formId = BinaryUtils.ReadUInt32BE(buffer, 16);
        if (formId != entry.FormId)
        {
            return null;
        }

        // Read pLoadedData pointer at offset 56
        var pLoadedData = BinaryUtils.ReadUInt32BE(buffer, LandLoadedDataPtrOffset);
        if (pLoadedData == 0 || !IsValidPointer(pLoadedData))
        {
            return null;
        }

        // Convert to file offset
        var loadedDataFileOffset = VaToFileOffset(pLoadedData);
        if (loadedDataFileOffset == null || loadedDataFileOffset.Value + LoadedDataSize > _fileSize)
        {
            return null;
        }

        // Read LoadedLandData struct
        var loadedDataBuffer = new byte[LoadedDataSize];
        try
        {
            _accessor.ReadArray(loadedDataFileOffset.Value, loadedDataBuffer, 0, LoadedDataSize);
        }
        catch
        {
            return null;
        }

        // Extract cell coordinates and base height
        var cellX = ReadInt32BE(loadedDataBuffer, LoadedDataCellXOffset);
        var cellY = ReadInt32BE(loadedDataBuffer, LoadedDataCellYOffset);
        var baseHeight = BinaryUtils.ReadFloatBE(loadedDataBuffer, LoadedDataBaseHeightOffset);

        // Validate cell coordinates are reasonable (-128 to 127 for typical worldspace)
        if (cellX < -1000 || cellX > 1000 || cellY < -1000 || cellY > 1000)
        {
            return null;
        }

        // Validate base height is reasonable
        if (!IsNormalFloat(baseHeight) || baseHeight < -100000 || baseHeight > 100000)
        {
            baseHeight = 0;
        }

        return new RuntimeLoadedLandData
        {
            FormId = formId,
            CellX = cellX,
            CellY = cellY,
            BaseHeight = baseHeight,
            LandOffset = offset,
            LoadedDataOffset = loadedDataFileOffset.Value
        };
    }

    /// <summary>
    ///     Read all LAND records from runtime data and extract cell coordinates.
    ///     Returns a dictionary mapping LAND FormID to LoadedLandData.
    /// </summary>
    public Dictionary<uint, RuntimeLoadedLandData> ReadAllRuntimeLandData(IEnumerable<RuntimeEditorIdEntry> entries)
    {
        var result = new Dictionary<uint, RuntimeLoadedLandData>();

        foreach (var entry in entries.Where(e => e.FormType == 0x45))
        {
            var landData = ReadRuntimeLandData(entry);
            if (landData != null)
            {
                result[landData.FormId] = landData;
            }
        }

        return result;
    }

    #endregion
}
