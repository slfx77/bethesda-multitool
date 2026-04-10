using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Reads NPC-specific fields from Xbox 360 memory dump buffers: ACBS stats, AI data,
///     S.P.E.C.I.A.L., skills, FaceGen morphs, inventory, factions, and package lists.
///     Used by <see cref="RuntimeActorReader" /> to populate NPC and Creature records.
/// </summary>
internal sealed class RuntimeNpcFieldReader
{
    private readonly int _appearanceShift;
    private readonly RuntimeMemoryContext _context;

    private readonly int _coreShift;
    private readonly int _lateAppearanceShift;
    private readonly RuntimeNpcLayout _layout;

    public RuntimeNpcFieldReader(RuntimeMemoryContext context, RuntimeNpcLayout layout)
    {
        _context = context;
        _layout = layout;
        _coreShift = layout.CoreShift;
        _appearanceShift = layout.AppearanceShift;
        _lateAppearanceShift = layout.LateAppearanceShift;
    }

    /// <summary>
    ///     Read AI behavior data from TESAIForm at dump offset +164.
    ///     Layout: aggression(1), confidence(1), energy(1), responsibility(1), mood(1 at +168),
    ///     padding(3), flags(4 at +172), ..., assistance(1 at +178).
    ///     Empirically verified via GSSunnySmiles (aggression=1, confidence=4, assistance=2).
    /// </summary>
    public NpcAiData? ReadNpcAiData(byte[] buffer)
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
            Logger.Instance.Debug("[AI] Rejected: aggression={0}, confidence={1}, assistance={2}", aggression,
                confidence, assistance);
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
    ///     Read NPC faction memberships from BSSimpleList&lt;FACTION_RANK*&gt; at +108 (PDB).
    ///     BSSimpleList nodes are 8 bytes: m_item (FACTION_RANK*, 4B) + m_pkNext (BSSimpleList*, 4B).
    ///     FACTION_RANK is 8 bytes: pFaction (TESFaction*, 4B at +0) + cRank (int8 at +4).
    ///     Returns a list of (FactionFormId, Rank) pairs.
    /// </summary>
    public List<FactionMembership> ReadNpcFactions(byte[] npcBuffer)
    {
        var factions = new List<FactionMembership>();

        if (NpcFactionListHeadOffset + 8 > npcBuffer.Length)
        {
            return factions;
        }

        // Read inline BSSimpleList head: m_item (FACTION_RANK*) + m_pkNext (BSSimpleList*)
        var itemPtr = BinaryUtils.ReadUInt32BE(npcBuffer, NpcFactionListHeadOffset);
        var nextPtr = BinaryUtils.ReadUInt32BE(npcBuffer, NpcFactionListHeadOffset + 4);

        // Process inline first item
        var first = ReadFactionRank(itemPtr);
        if (first != null)
        {
            factions.Add(first);
        }

        // Walk linked list
        var visited = new HashSet<uint>();
        while (nextPtr != 0 && factions.Count < RuntimeMemoryContext.MaxListItems &&
               _context.IsValidPointer(nextPtr) && !visited.Contains(nextPtr))
        {
            visited.Add(nextPtr);
            var nodeFileOffset = _context.VaToFileOffset(nextPtr);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuf = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuf == null)
            {
                break;
            }

            var nodeItemPtr = BinaryUtils.ReadUInt32BE(nodeBuf);
            nextPtr = BinaryUtils.ReadUInt32BE(nodeBuf, 4);

            var item = ReadFactionRank(nodeItemPtr);
            if (item != null)
            {
                factions.Add(item);
            }
        }

        return factions;
    }

    /// <summary>
    ///     Follow a FACTION_RANK* pointer to read the faction FormID and rank.
    ///     FACTION_RANK is 8 bytes: pFaction (TESFaction*, 4B) + cRank (int8, 1B) + 3B padding.
    /// </summary>
    private FactionMembership? ReadFactionRank(uint factionRankVA)
    {
        if (factionRankVA == 0 || !_context.IsValidPointer(factionRankVA))
        {
            return null;
        }

        var fileOffset = _context.VaToFileOffset(factionRankVA);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = _context.ReadBytes(fileOffset.Value, 8);
        if (buf == null)
        {
            return null;
        }

        // FACTION_RANK: pFaction (4B pointer) + cRank (1B)
        var pFaction = BinaryUtils.ReadUInt32BE(buf);
        var rank = (sbyte)buf[4];

        if (pFaction == 0 || !_context.IsValidPointer(pFaction))
        {
            return null;
        }

        // Follow pFaction to read TESFaction FormID, validate it's FACT (0x08)
        var factionFileOffset = _context.VaToFileOffset(pFaction);
        if (factionFileOffset == null)
        {
            return null;
        }

        var formBuf = _context.ReadBytes(factionFileOffset.Value, 16);
        if (formBuf == null)
        {
            return null;
        }

        var formType = formBuf[4];
        var formId = BinaryUtils.ReadUInt32BE(formBuf, 12);
        if (formType != 0x08 || formId == 0 || formId == 0xFFFFFFFF)
        {
            return null;
        }

        return new FactionMembership(formId, rank);
    }

    /// <summary>
    ///     Read AI package list from BSSimpleList&lt;TESPackage*&gt; at PackageListOffset.
    ///     BSSimpleList is a singly-linked list: m_item (TESPackage*, 4 bytes) + m_pkNext (BSSimpleList*, 4 bytes).
    ///     The head node is inline in the struct; subsequent nodes are heap-allocated.
    ///     Returns a list of PACK FormIDs.
    /// </summary>
    public List<uint> ReadPackageList(byte[] buffer)
    {
        var packages = new List<uint>();

        if (PackageListOffset + 8 > buffer.Length)
        {
            return packages;
        }

        // Read inline BSSimpleList head: m_item (TESPackage*) + m_pkNext (BSSimpleList*)
        var itemPtr = BinaryUtils.ReadUInt32BE(buffer, PackageListOffset);
        var nextPtr = BinaryUtils.ReadUInt32BE(buffer, PackageListOffset + 4);

        // Follow first item pointer to TESPackage → read FormID at +12
        if (itemPtr != 0 && _context.IsValidPointer(itemPtr))
        {
            var formId = _context.FollowPointerVaToFormId(itemPtr);
            if (formId is > 0 and < 0x01000000)
            {
                packages.Add(formId.Value);
            }
        }

        // Walk linked list (max 50 nodes to prevent infinite loops)
        var visited = new HashSet<uint>();
        for (var i = 0; i < 50 && nextPtr != 0 && _context.IsValidPointer(nextPtr) && !visited.Contains(nextPtr); i++)
        {
            visited.Add(nextPtr);
            var nodeFileOffset = _context.VaToFileOffset(nextPtr);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuf = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuf == null)
            {
                break;
            }

            var nodeItemPtr = BinaryUtils.ReadUInt32BE(nodeBuf);
            nextPtr = BinaryUtils.ReadUInt32BE(nodeBuf, 4);

            if (nodeItemPtr != 0 && _context.IsValidPointer(nodeItemPtr))
            {
                var formId = _context.FollowPointerVaToFormId(nodeItemPtr);
                if (formId is > 0 and < 0x01000000)
                {
                    packages.Add(formId.Value);
                }
            }
        }

        return packages;
    }

    /// <summary>
    ///     Read spell/ability list from BSSimpleList&lt;SpellItem*&gt; at NpcSpellListOffset.
    ///     Same linked list pattern as ReadPackageList. Returns a list of SPEL FormIDs.
    /// </summary>
    public List<uint> ReadSpellList(byte[] buffer)
    {
        var spells = new List<uint>();

        if (NpcSpellListOffset + 8 > buffer.Length)
        {
            return spells;
        }

        var itemPtr = BinaryUtils.ReadUInt32BE(buffer, NpcSpellListOffset);
        var nextPtr = BinaryUtils.ReadUInt32BE(buffer, NpcSpellListOffset + 4);

        if (itemPtr != 0 && _context.IsValidPointer(itemPtr))
        {
            var formId = _context.FollowPointerVaToFormId(itemPtr);
            if (formId is > 0 and < 0x01000000)
            {
                spells.Add(formId.Value);
            }
        }

        var visited = new HashSet<uint>();
        for (var i = 0; i < 50 && nextPtr != 0 && _context.IsValidPointer(nextPtr) && !visited.Contains(nextPtr); i++)
        {
            visited.Add(nextPtr);
            var nodeFileOffset = _context.VaToFileOffset(nextPtr);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuf = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuf == null)
            {
                break;
            }

            var nodeItemPtr = BinaryUtils.ReadUInt32BE(nodeBuf);
            nextPtr = BinaryUtils.ReadUInt32BE(nodeBuf, 4);

            if (nodeItemPtr != 0 && _context.IsValidPointer(nodeItemPtr))
            {
                var formId = _context.FollowPointerVaToFormId(nodeItemPtr);
                if (formId is > 0 and < 0x01000000)
                {
                    spells.Add(formId.Value);
                }
            }
        }

        return spells;
    }

    /// <summary>
    ///     Read hair length float from dump offset +460.
    ///     Returns null if the value is zero (NULL/unset) or invalid.
    ///     Empirically verified: Sunny=0.60, Doc=0.29, Arcade=0.69, Boone=NULL, Raul=NULL.
    /// </summary>
    public float? ReadNpcHairLength(byte[] buffer)
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
    ///     Read hair color uint32 (packed 0x00BBGGRR) from NPC struct.
    ///     Returns null if the value is zero (unset).
    /// </summary>
    public uint? ReadNpcHairColor(byte[] buffer)
    {
        if (NpcHairColorOffset + 4 > buffer.Length)
            return null;

        var value = BinaryUtils.ReadUInt32BE(buffer, NpcHairColorOffset);
        return value == 0 ? null : value;
    }

    /// <summary>
    ///     Read head part FormIDs from BSSimpleList&lt;BGSHeadPart*&gt; at NpcHeadPartListOffset.
    ///     Same linked list pattern as ReadPackageList: inline head node + heap-allocated chain.
    /// </summary>
    public List<uint> ReadNpcHeadPartFormIds(byte[] buffer)
    {
        var parts = new List<uint>();

        if (NpcHeadPartListOffset + 8 > buffer.Length)
            return parts;

        // Read inline BSSimpleList head: m_item (BGSHeadPart*) + m_pkNext (BSSimpleList*)
        var itemPtr = BinaryUtils.ReadUInt32BE(buffer, NpcHeadPartListOffset);
        var nextPtr = BinaryUtils.ReadUInt32BE(buffer, NpcHeadPartListOffset + 4);

        // Follow first item pointer to BGSHeadPart → read FormID at +12
        if (itemPtr != 0 && _context.IsValidPointer(itemPtr))
        {
            var formId = _context.FollowPointerVaToFormId(itemPtr, 0x09);
            if (formId is > 0 and < 0x01000000)
                parts.Add(formId.Value);
        }

        // Walk linked list (max 20 nodes — NPCs have few head parts)
        var visited = new HashSet<uint>();
        for (var i = 0; i < 20 && nextPtr != 0 && _context.IsValidPointer(nextPtr) && !visited.Contains(nextPtr); i++)
        {
            visited.Add(nextPtr);
            var nodeFileOffset = _context.VaToFileOffset(nextPtr);
            if (nodeFileOffset == null)
                break;

            var nodeBuf = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuf == null)
                break;

            var nodeItemPtr = BinaryUtils.ReadUInt32BE(nodeBuf);
            nextPtr = BinaryUtils.ReadUInt32BE(nodeBuf, 4);

            if (nodeItemPtr != 0 && _context.IsValidPointer(nodeItemPtr))
            {
                var formId = _context.FollowPointerVaToFormId(nodeItemPtr, 0x09);
                if (formId is > 0 and < 0x01000000)
                    parts.Add(formId.Value);
            }
        }

        return parts;
    }

    /// <summary>
    ///     Read NPC height multiplier (fHeight, float ~0.9-1.1, default 1.0).
    ///     Returns null if the value is zero/unset or out of reasonable range.
    /// </summary>
    public float? ReadNpcHeight(byte[] buffer)
    {
        if (NpcHeightOffset + 4 > buffer.Length)
        {
            return null;
        }

        var raw = BinaryUtils.ReadUInt32BE(buffer, NpcHeightOffset);
        if (raw == 0)
        {
            return null;
        }

        var value = BinaryUtils.ReadFloatBE(buffer, NpcHeightOffset);

        // Reject subnormal/denormalized floats — likely garbage data.
        if (!float.IsNormal(value) || value <= 0 || value > 3)
        {
            return null;
        }

        return value;
    }

    /// <summary>
    ///     Read NPC weight (fWeight, float 0-100, GECK body morph slider).
    ///     Returns null if the value looks invalid.
    /// </summary>
    public float? ReadNpcWeight(byte[] buffer)
    {
        if (NpcWeightOffset + 4 > buffer.Length)
        {
            return null;
        }

        var raw = BinaryUtils.ReadUInt32BE(buffer, NpcWeightOffset);
        if (raw == 0)
        {
            return null;
        }

        var value = BinaryUtils.ReadFloatBE(buffer, NpcWeightOffset);

        // Reject subnormal/denormalized floats — likely garbage data.
        if (!float.IsNormal(value) || value < 0 || value > 100)
        {
            return null;
        }

        return value;
    }

    /// <summary>
    ///     Read blood impact material enum (eBloodImpactMaterial, byte).
    ///     FNV values: 0=Default, 1=Metal, 2=CinderBlock, 3=Stone.
    /// </summary>
    public byte? ReadNpcBloodImpactMaterial(byte[] buffer)
    {
        if (NpcBloodImpactMaterialOffset >= buffer.Length)
        {
            return null;
        }

        var value = buffer[NpcBloodImpactMaterialOffset];
        return value > 10 ? null : value; // sanity check
    }

    /// <summary>
    ///     Read last race face preset number (sLastRaceFaceNum, uint16).
    /// </summary>
    public ushort? ReadNpcRaceFacePreset(byte[] buffer)
    {
        if (NpcRaceFacePresetOffset + 2 > buffer.Length)
        {
            return null;
        }

        return BinaryUtils.ReadUInt16BE(buffer, NpcRaceFacePresetOffset);
    }

    /// <summary>
    ///     Read NPC inventory items from TESContainer tList at +120/+124.
    ///     Returns a list of (ItemFormId, Count) pairs.
    /// </summary>
    public List<InventoryItem> ReadNpcInventory(byte[] npcBuffer, long _npcFileOffset)
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
    public byte[]? ReadNpcSkills(byte[] buffer)
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
    public byte[]? ReadNpcSpecial(byte[] buffer)
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
    public float[]? ReadFaceGenMorphArray(byte[] npcBuffer, RuntimeNpcFaceGenFieldLayout fieldLayout)
    {
        if (fieldLayout.PointerOffset + 4 > npcBuffer.Length || fieldLayout.CountOffset + 4 > npcBuffer.Length)
        {
            return null;
        }

        var pointer = BinaryUtils.ReadUInt32BE(npcBuffer, fieldLayout.PointerOffset);
        if (pointer == 0 || !_context.IsValidPointer(pointer))
        {
            return null;
        }

        var count = (int)BinaryUtils.ReadUInt32BE(npcBuffer, fieldLayout.CountOffset);
        if (count <= 0 || count > 200)
        {
            return null;
        }

        if (_layout.FaceGenMode == RuntimeNpcFaceGenArrayMode.PrimitiveArray)
        {
            if (fieldLayout.EndPointerOffset is not int endPointerOffset || endPointerOffset + 4 > npcBuffer.Length)
            {
                return null;
            }

            var endPointer = BinaryUtils.ReadUInt32BE(npcBuffer, endPointerOffset);
            if (endPointer == 0 || !_context.IsValidPointer(endPointer) || endPointer < pointer)
            {
                return null;
            }

            var expectedByteCount = count * 4;
            if (endPointer - pointer != expectedByteCount)
            {
                return null;
            }
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
            Logger.Instance.Debug("[NPC] Rejected inventory item: count={0} (range 1-100000)", count);
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

    #region NPC Struct Layout (Proto Debug PDB base + selected shifts)

    // TESNPC: PDB size 492, MemDebug PDB 508. Fields after the RaceFaceOffsetCoord inline
    // array (PDB 308-404) are +32 bytes higher at runtime vs PDB, likely due to 8-byte alignment
    // padding in FR2MatrixVTC (24B padded to 32B × 4 = 128B vs PDB's 96B).
    // Probe reads a fixed window so appearance-tail candidates can move independently.
    public int NpcStructSize => _layout.ReadSize;
    public int NpcAcbsOffset => 52 + _coreShift;
    public int NpcDeathItemPtrOffset => 76 + _coreShift;
    public int NpcVoiceTypePtrOffset => 80 + _coreShift;
    public int NpcTemplatePtrOffset => 84 + _coreShift;
    public int NpcRacePtrOffset => 272 + _coreShift;
    public int NpcClassPtrOffset => 304 + _coreShift;
    private int NpcAiDataOffset => 148 + _coreShift;
    private int NpcMoodOffset => 152 + _coreShift;
    private int NpcAiFlagsOffset => 156 + _coreShift;
    private int NpcAiAssistanceOffset => 162 + _coreShift;
    private int NpcSpecialOffset => 188 + _coreShift;
    private const int NpcSpecialSize = 7;
    private int NpcSkillsOffset => 276 + _coreShift;
    private const int NpcSkillsSize = 14;
    public RuntimeNpcFaceGenFieldLayout NpcFggsLayout => _layout.Fggs;
    public RuntimeNpcFaceGenFieldLayout NpcFggaLayout => _layout.Fgga;
    public RuntimeNpcFaceGenFieldLayout NpcFgtsLayout => _layout.Fgts;
    public int NpcHairPtrOffset => 440 + _appearanceShift;
    private int NpcHairLengthOffset => 444 + _appearanceShift;
    public int NpcEyesPtrOffset => 448 + _appearanceShift;
    public int NpcCombatStylePtrOffset => 468 + _appearanceShift;
    private int NpcHairColorOffset => 472 + _appearanceShift; // iHairColor (uint32, packed 0x00BBGGRR)
    private int NpcHeadPartListOffset => 476 + _appearanceShift; // listHeadParts (BSSimpleList<BGSHeadPart*>, 8B)
    public int NpcScriptPtrOffset => 248 + _coreShift; // TESScriptableForm::pFormScript (base+244, field+4)
    private int NpcContainerDataOffset => 104 + _coreShift;
    private int NpcContainerNextOffset => 108 + _coreShift;

    private int NpcFactionListHeadOffset => 92 + _coreShift;

    // TESSpellList.spellList (BSSimpleList<SpellItem*>, 8B inline head)
    private int NpcSpellListOffset => 128 + _coreShift;

    // TESAIForm at offset 144 in TESActorBase; AIPackList (BSSimpleList<TESPackage*>) at +24 within TESAIForm
    private int PackageListOffset => 168 + _coreShift;

    // Additional TESNPC fields from PDB (Proto Debug offset + 32 runtime shift + _s).
    // The +32 matches the empirical shift applied to all post-FaceGen fields (Hair, Eyes, etc.).
    private int NpcRaceFacePresetOffset => 464 + _appearanceShift; // PDB 432 + 32: sLastRaceFaceNum (uint16)
    private int NpcBloodImpactMaterialOffset => 484 + _appearanceShift; // PDB 452 + 32: eBloodImpactMaterial (enum)
    public int NpcOriginalRacePtrOffset => 492 + _lateAppearanceShift; // PDB 460 + 32: pOriginalRace (TESRace*)
    public int NpcFaceNpcPtrOffset => 496 + _lateAppearanceShift; // PDB 464 + 32: pFaceNPC (TESNPC*)
    private int NpcHeightOffset => 500 + _lateAppearanceShift; // PDB 468 + 32: fHeight (float, ~0.9-1.1)
    private int NpcWeightOffset => 504 + _lateAppearanceShift; // PDB 472 + 32: fWeight (float, 0-100)

    #endregion

    #region Creature Struct Layout (Proto Debug PDB base + core shift)

    // TESCreature: PDB struct size 368, base values = PDB offset - 16
    public int CreaStructSize => 352 + _coreShift;
    public int CreaAcbsOffset => 52 + _coreShift; // PDB 68: actorData (ACTOR_BASE_DATA, same as NPC)
    public int CreaModelPathOffset => 224 + _coreShift; // PDB 240: cModel (BSStringT<char>)
    public int CreaScriptOffset => 248 + _coreShift; // PDB 264: pFormScript (TESScriptableForm)
    public int CreaAttackDamageOffset => 272 + _coreShift; // PDB 288: sAttackDamage (uint16)
    public int CreaTypeOffset => 300 + _coreShift; // PDB 316: CREATURE_DATA byte 0 (type)
    public int CreaCombatSkillOffset => 301 + _coreShift; // PDB 317: CREATURE_DATA byte 1 (combat)
    public int CreaMagicSkillOffset => 302 + _coreShift; // PDB 318: CREATURE_DATA byte 2 (magic)
    public int CreaStealthSkillOffset => 303 + _coreShift; // PDB 319: CREATURE_DATA byte 3 (stealth)

    #endregion
}
