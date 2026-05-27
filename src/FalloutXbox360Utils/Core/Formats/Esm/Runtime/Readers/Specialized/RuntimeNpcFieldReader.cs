using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Reads NPC-specific fields from Xbox 360 memory dump buffers: ACBS stats, AI data,
///     S.P.E.C.I.A.L., skills, FaceGen morphs, inventory, factions, and package lists.
///     Used by <see cref="RuntimeActorReader" /> to populate NPC and Creature records.
///
///     <para>
///     Phase 1B.20 migrated 12 core-region offsets to PDB-driven lookups (via
///     <see cref="PdbStructView" />). Phase 5.1 migrated the appearance-region reads
///     too — the +32-byte runtime/PDB padding delta (FR2MatrixVTC) is now absorbed
///     by a single <c>WithShift("TESNPC", 16 + _appearanceShift)</c> registration at
///     the view-opening site in <see cref="RuntimeActorReader" />. Per-band coverage
///     for the late-appearance region only fires when the probe discovers
///     <c>LateAppearanceShift != AppearanceShift</c> (rare).
///     </para>
///     <para>
///     Two clusters stay as hardcoded constants by design:
///     <list type="bullet">
///         <item>Probe-only core offsets (NpcAcbsOffset / NpcScriptPtrOffset /
///         NpcRacePtrOffset / NpcClassPtrOffset / NpcStructSize) — used by
///         RuntimeNpcLayoutProbe.ScoreSample, which runs BEFORE a stable layout
///         (and view) exists.</item>
///         <item>Core scalars (NpcAiDataOffset / NpcMoodOffset / NpcAiFlagsOffset /
///         NpcAiAssistanceOffset / NpcSpecialOffset / NpcSkillsOffset) — they're
///         read against a raw buffer span on the hot path; PDB-name lookups would
///         add allocations.</item>
///     </list>
///     </para>
/// </summary>
internal sealed class RuntimeNpcFieldReader
{
    private readonly RuntimeMemoryContext _context;

    private readonly int _coreShift;
    private readonly RuntimeNpcLayout _layout;

    public RuntimeNpcFieldReader(RuntimeMemoryContext context, RuntimeNpcLayout layout)
    {
        _context = context;
        _layout = layout;
        _coreShift = layout.CoreShift;
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
    ///     Read NPC faction memberships from BSSimpleList&lt;FACTION_RANK*&gt; at PDB
    ///     <c>TESActorBaseData::listFactions</c>.
    ///     BSSimpleList nodes are 8 bytes: m_item (FACTION_RANK*, 4B) + m_pkNext (BSSimpleList*, 4B).
    ///     FACTION_RANK is 8 bytes: pFaction (TESFaction*, 4B at +0) + cRank (int8 at +4).
    ///     Returns a list of (FactionFormId, Rank) pairs.
    /// </summary>
    public List<FactionMembership> ReadNpcFactions(PdbStructView view)
    {
        var factions = new List<FactionMembership>();
        var npcBuffer = view.Buffer;
        var headOffset = view.Offset("listFactions", "TESActorBaseData") ?? (92 + _coreShift);

        if (headOffset + 8 > npcBuffer.Length)
        {
            return factions;
        }

        // Read inline BSSimpleList head: m_item (FACTION_RANK*) + m_pkNext (BSSimpleList*)
        var itemPtr = BinaryUtils.ReadUInt32BE(npcBuffer, headOffset);
        var nextPtr = BinaryUtils.ReadUInt32BE(npcBuffer, headOffset + 4);

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
    ///     Read AI package list from BSSimpleList&lt;TESPackage*&gt; at PDB
    ///     <c>TESAIForm::AIPackList</c>.
    ///     BSSimpleList is a singly-linked list: m_item (TESPackage*, 4 bytes) + m_pkNext (BSSimpleList*, 4 bytes).
    ///     The head node is inline in the struct; subsequent nodes are heap-allocated.
    ///     Returns a list of PACK FormIDs.
    /// </summary>
    public List<uint> ReadPackageList(PdbStructView view)
    {
        var packages = new List<uint>();
        var buffer = view.Buffer;
        var headOffset = view.Offset("AIPackList", "TESAIForm") ?? (168 + _coreShift);

        if (headOffset + 8 > buffer.Length)
        {
            return packages;
        }

        // Read inline BSSimpleList head: m_item (TESPackage*) + m_pkNext (BSSimpleList*)
        var itemPtr = BinaryUtils.ReadUInt32BE(buffer, headOffset);
        var nextPtr = BinaryUtils.ReadUInt32BE(buffer, headOffset + 4);

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
    ///     Read spell/ability list from BSSimpleList&lt;SpellItem*&gt; at PDB
    ///     <c>TESSpellList::spellList</c>.
    ///     Same linked list pattern as ReadPackageList. Returns a list of SPEL FormIDs.
    /// </summary>
    public List<uint> ReadSpellList(PdbStructView view)
    {
        var spells = new List<uint>();
        var buffer = view.Buffer;
        var headOffset = view.Offset("spellList", "TESSpellList") ?? (128 + _coreShift);

        if (headOffset + 8 > buffer.Length)
        {
            return spells;
        }

        var itemPtr = BinaryUtils.ReadUInt32BE(buffer, headOffset);
        var nextPtr = BinaryUtils.ReadUInt32BE(buffer, headOffset + 4);

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
    ///     Read hair length float (fHairLength) via the view's PDB-name lookup.
    ///     Returns null if the value is zero (NULL/unset) or invalid.
    ///     Empirically verified: Sunny=0.60, Doc=0.29, Arcade=0.69, Boone=NULL, Raul=NULL.
    /// </summary>
    public float? ReadNpcHairLength(PdbStructView view)
    {
        var off = view.Offset("fHairLength", "TESNPC");
        if (off is not { } o || o + 4 > view.Buffer.Length)
        {
            return null;
        }

        var raw = BinaryUtils.ReadUInt32BE(view.Buffer, o);
        if (raw == 0)
        {
            return null; // NULL/unset
        }

        var value = BinaryUtils.ReadFloatBE(view.Buffer, o);
        if (!RuntimeMemoryContext.IsNormalFloat(value) || value < 0 || value > 10)
        {
            return null;
        }

        return value;
    }

    /// <summary>
    ///     Read hair color uint32 (iHairColor, packed 0x00BBGGRR) via the view's
    ///     PDB-name lookup. Returns null if the value is zero (unset).
    /// </summary>
    public uint? ReadNpcHairColor(PdbStructView view)
    {
        var value = view.UInt32("iHairColor", "TESNPC");
        return value == 0 ? null : value;
    }

    /// <summary>
    ///     Read head part FormIDs from BSSimpleList&lt;BGSHeadPart*&gt; at PDB
    ///     <c>TESNPC::listHeadParts</c>. Same linked-list pattern as ReadPackageList:
    ///     inline head node + heap-allocated chain.
    /// </summary>
    public List<uint> ReadNpcHeadPartFormIds(PdbStructView view)
    {
        var parts = new List<uint>();
        var off = view.Offset("listHeadParts", "TESNPC");
        if (off is not { } headOffset || headOffset + 8 > view.Buffer.Length)
        {
            return parts;
        }

        var buffer = view.Buffer;

        // Read inline BSSimpleList head: m_item (BGSHeadPart*) + m_pkNext (BSSimpleList*)
        var itemPtr = BinaryUtils.ReadUInt32BE(buffer, headOffset);
        var nextPtr = BinaryUtils.ReadUInt32BE(buffer, headOffset + 4);

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
    ///     Read NPC height multiplier (fHeight, float ~0.9-1.1, default 1.0) via the
    ///     view's PDB-name lookup. Returns null if the value is zero/unset or out of
    ///     reasonable range.
    /// </summary>
    public float? ReadNpcHeight(PdbStructView view)
    {
        var off = view.Offset("fHeight", "TESNPC");
        if (off is not { } o || o + 4 > view.Buffer.Length)
        {
            return null;
        }

        var raw = BinaryUtils.ReadUInt32BE(view.Buffer, o);
        if (raw == 0)
        {
            return null;
        }

        var value = BinaryUtils.ReadFloatBE(view.Buffer, o);

        // Match NpcRecordHandler.ReadNpcHeight's broader plausibility range so
        // proto-build NPCs with stretched values aren't filtered out (audit
        // showed 464 height gaps under the previous <=3 cap).
        if (!float.IsNormal(value) || value is < 0.1f or > 10.0f)
        {
            return null;
        }

        return value;
    }

    /// <summary>
    ///     Read NPC weight (fWeight, float 0-1000, GECK body morph slider) via the
    ///     view's PDB-name lookup. Returns null if the value looks invalid.
    /// </summary>
    public float? ReadNpcWeight(PdbStructView view)
    {
        var off = view.Offset("fWeight", "TESNPC");
        if (off is not { } o || o + 4 > view.Buffer.Length)
        {
            return null;
        }

        var raw = BinaryUtils.ReadUInt32BE(view.Buffer, o);
        if (raw == 0)
        {
            return null;
        }

        var value = BinaryUtils.ReadFloatBE(view.Buffer, o);

        // Match NpcRecordHandler.ReadNpcWeight's broader plausibility range
        // so proto-build NPCs with values above 100 aren't filtered out
        // (audit showed 2,580 weight gaps under the previous <=100 cap).
        if (!float.IsNormal(value) || value is < 0f or > 1000f)
        {
            return null;
        }

        return value;
    }

    /// <summary>
    ///     Read blood impact material enum (eBloodImpactMaterial, byte) via the view's
    ///     PDB-name lookup. FNV values: 0=Default, 1=Metal, 2=CinderBlock, 3=Stone.
    /// </summary>
    public byte? ReadNpcBloodImpactMaterial(PdbStructView view)
    {
        var off = view.Offset("eBloodImpactMaterial", "TESNPC");
        if (off is not { } o || o >= view.Buffer.Length)
        {
            return null;
        }

        var value = view.Buffer[o];
        return value > 10 ? null : value; // sanity check
    }

    /// <summary>
    ///     Read last race face preset number (sLastRaceFaceNum, uint16) via the view's
    ///     PDB-name lookup.
    /// </summary>
    public ushort? ReadNpcRaceFacePreset(PdbStructView view)
    {
        var off = view.Offset("sLastRaceFaceNum", "TESNPC");
        if (off is not { } o || o + 2 > view.Buffer.Length)
        {
            return null;
        }

        return BinaryUtils.ReadUInt16BE(view.Buffer, o);
    }

    /// <summary>
    ///     Read NPC inventory items from TESContainer tList at PDB
    ///     <c>TESContainer::objectList</c>. The head is 8 bytes: first node data
    ///     pointer + next pointer.
    ///     Returns a list of (ItemFormId, Count) pairs.
    /// </summary>
    public List<InventoryItem> ReadNpcInventory(PdbStructView view)
    {
        var items = new List<InventoryItem>();
        var npcBuffer = view.Buffer;
        var headOffset = view.Offset("objectList", "TESContainer") ?? (104 + _coreShift);

        if (headOffset + 8 > npcBuffer.Length)
        {
            return items;
        }

        // Read inline first node
        var firstDataPtr = BinaryUtils.ReadUInt32BE(npcBuffer, headOffset);
        var firstNextPtr = BinaryUtils.ReadUInt32BE(npcBuffer, headOffset + 4);

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

    #region NPC Struct Layout — probe-only constants + hot-path core scalars

    // TESNPC: PDB size 492, MemDebug PDB 508. Phase 5.1 routes all appearance-region
    // reads (Hair / Eyes / CombatStyle / HairLength / HairColor / HeadParts /
    // OriginalRace / FaceNpc / Height / Weight / RaceFacePreset / BloodImpactMaterial)
    // through view.FormIdPointer / view.Offset against the PDB-named TESNPC fields,
    // with the +32-byte FR2MatrixVTC padding delta absorbed by a single
    // WithShift("TESNPC", 16 + _appearanceShift) registration at the view-opening
    // site in RuntimeActorReader.ReadRuntimeNpc.
    //
    // Core-region reads (TESActorBaseData / TESScriptableForm / TESRaceForm /
    // TESHealthForm / TESAIForm / TESContainer / TESSpellList / TESNPC fields up
    // through offset 320) were migrated to view-based lookups in Phase 1B.20.
    //
    // ReadSize is probe-derived (Debug 492 / Release 508). NpcAcbsOffset /
    // NpcScriptPtrOffset / NpcRacePtrOffset / NpcClassPtrOffset feed
    // RuntimeNpcLayoutProbe.ScoreSample, which runs BEFORE a stable view exists;
    // they're kept here as probe-only duplicates of what the view eventually
    // resolves the same fields to.
    public int NpcStructSize => _layout.ReadSize;

    // Probe-only core offsets (RuntimeNpcLayoutProbe.ScoreSample needs them without
    // a view; production reads in RuntimeActorReader go through the view).
    internal int NpcAcbsOffset => 52 + _coreShift;
    internal int NpcScriptPtrOffset => 248 + _coreShift;
    internal int NpcRacePtrOffset => 272 + _coreShift;
    internal int NpcClassPtrOffset => 304 + _coreShift;

    // Probe-only appearance shifts — exposed so RuntimeNpcLayoutProbe.ScoreSample can
    // configure a candidate-specific PdbStructView.WithShift("TESNPC", ...) when
    // scoring appearance signals. Production reads in RuntimeActorReader pull the
    // same values directly from the probe result; the field reader holds them so
    // the probe doesn't need a second copy of the layout per candidate.
    internal int AppearanceShift => _layout.AppearanceShift;
    internal int LateAppearanceShift => _layout.LateAppearanceShift;

    // Hot-path core scalars — kept as raw-buffer reads to avoid view allocations on
    // the SPECIAL/skills/AIDT byte-scrape paths.
    private const int NpcSpecialSize = 7;
    private const int NpcSkillsSize = 14;
    private int NpcAiDataOffset => 148 + _coreShift;
    private int NpcMoodOffset => 152 + _coreShift;
    private int NpcAiFlagsOffset => 156 + _coreShift;
    private int NpcAiAssistanceOffset => 162 + _coreShift;
    private int NpcSpecialOffset => 188 + _coreShift;
    private int NpcSkillsOffset => 276 + _coreShift;

    // FaceGen field layouts come baked from RuntimeNpcLayout; offsets include the
    // probe-discovered appearance shift at layout-construction time.
    public RuntimeNpcFaceGenFieldLayout NpcFggsLayout => _layout.Fggs;
    public RuntimeNpcFaceGenFieldLayout NpcFggaLayout => _layout.Fgga;
    public RuntimeNpcFaceGenFieldLayout NpcFgtsLayout => _layout.Fgts;

    #endregion

    #region Creature Struct Layout

    // TESCreature: PDB struct size 368, fully PDB-aligned (no FR2MatrixVTC padding
    // drift). Phase 1B.21 migrated every field to view.* lookups in
    // RuntimeActorReader.ReadRuntimeCreature. Only the struct size remains here so
    // RuntimeActorReader can allocate the read buffer.
    public int CreaStructSize => 352 + _coreShift;

    #endregion
}
