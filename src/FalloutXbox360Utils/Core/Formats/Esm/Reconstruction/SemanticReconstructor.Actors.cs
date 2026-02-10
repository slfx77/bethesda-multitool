using System.Buffers;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

public sealed partial class SemanticReconstructor
{
    #region ReconstructCreatures

    /// <summary>
    ///     Reconstruct all Creature records from the scan result.
    ///     Uses two-track approach: ESM records for subrecord detail + runtime C++ structs
    ///     for records not found as raw ESM data.
    /// </summary>
    public List<ReconstructedCreature> ReconstructCreatures()
    {
        var creatures = new List<ReconstructedCreature>();
        var creatureRecords = GetRecordsByType("CREA").ToList();

        // Track FormIDs from ESM records to avoid duplicates when merging runtime data
        var esmFormIds = new HashSet<uint>();

        if (_accessor == null)
        {
            // Without accessor, use already-parsed subrecords from scan result
            foreach (var record in creatureRecords)
            {
                var creature = ReconstructCreatureFromScanResult(record);
                creatures.Add(creature);
                esmFormIds.Add(creature.FormId);
            }
        }
        else
        {
            // With accessor, read full record data for better reconstruction
            var buffer = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                foreach (var record in creatureRecords)
                {
                    var creature = ReconstructCreatureFromAccessor(record, buffer);
                    creatures.Add(creature);
                    esmFormIds.Add(creature.FormId);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge creatures from runtime struct reading
        if (_runtimeReader != null)
        {
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x2B || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var creature = _runtimeReader.ReadRuntimeCreature(entry);
                if (creature != null)
                {
                    creatures.Add(creature);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} creatures from runtime struct reading " +
                    $"(total: {creatures.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return creatures;
    }

    private ReconstructedCreature ReconstructCreatureFromScanResult(DetectedMainRecord record)
    {
        return new ReconstructedCreature
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            FullName = FindFullNameNear(record.Offset),
            Stats = FindActorBaseNear(record.Offset),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedCreature ReconstructCreatureFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ReconstructCreatureFromScanResult(record);
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        ActorBaseSubrecord? stats = null;
        byte creatureType = 0;
        byte combatSkill = 0;
        byte magicSkill = 0;
        byte stealthSkill = 0;
        short attackDamage = 0;
        uint? script = null;
        uint? deathItem = null;
        var factions = new List<FactionMembership>();
        var spells = new List<uint>();

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "ACBS" when sub.DataLength == 24:
                    stats = ParseActorBase(subData, record.Offset + 24 + sub.DataOffset, record.IsBigEndian);
                    break;
                case "DATA" when sub.DataLength >= 8:
                {
                    var fields = SubrecordDataReader.ReadFields("DATA", "CREA", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        creatureType = SubrecordDataReader.GetByte(fields, "CreatureType");
                        combatSkill = SubrecordDataReader.GetByte(fields, "CombatSkill");
                        magicSkill = SubrecordDataReader.GetByte(fields, "MagicSkill");
                        stealthSkill = SubrecordDataReader.GetByte(fields, "StealthSkill");
                        attackDamage = (short)SubrecordDataReader.GetInt32(fields, "AttackDamage");
                    }
                    else
                    {
                        // Fallback for non-standard sizes without a matching schema
                        creatureType = subData[0];
                        combatSkill = subData[1];
                        magicSkill = subData[2];
                        stealthSkill = subData[3];
                    }

                    break;
                }
                case "SCRI" when sub.DataLength == 4:
                    script = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "INAM" when sub.DataLength == 4:
                    deathItem = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SNAM" when sub.DataLength >= 5:
                    var factionFormId = ReadFormId(subData[..4], record.IsBigEndian);
                    var rank = (sbyte)subData[4];
                    factions.Add(new FactionMembership(factionFormId, rank));
                    break;
                case "SPLO" when sub.DataLength == 4:
                    spells.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
            }
        }

        // Track FullName for display name map
        if (!string.IsNullOrEmpty(fullName))
        {
            _formIdToFullName.TryAdd(record.FormId, fullName);
        }

        return new ReconstructedCreature
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Stats = stats,
            CreatureType = creatureType,
            CombatSkill = combatSkill,
            MagicSkill = magicSkill,
            StealthSkill = stealthSkill,
            AttackDamage = attackDamage,
            Script = script,
            DeathItem = deathItem,
            ModelPath = modelPath,
            Factions = factions,
            Spells = spells,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region ReconstructFactions

    /// <summary>
    ///     Reconstruct all Faction records from the scan result.
    /// </summary>
    public List<ReconstructedFaction> ReconstructFactions()
    {
        var factions = new List<ReconstructedFaction>();
        var factionRecords = GetRecordsByType("FACT").ToList();

        if (_accessor == null)
        {
            foreach (var record in factionRecords)
            {
                factions.Add(new ReconstructedFaction
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameInRecordBounds(record),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in factionRecords)
                {
                    var faction = ReconstructFactionFromAccessor(record, buffer);
                    if (faction != null)
                    {
                        factions.Add(faction);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge factions from runtime struct reading
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(factions.Select(f => f.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x08 || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var faction = _runtimeReader.ReadRuntimeFaction(entry);
                if (faction != null)
                {
                    factions.Add(faction);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} factions from runtime struct reading " +
                    $"(total: {factions.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return factions;
    }

    private ReconstructedFaction? ReconstructFactionFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ReconstructedFaction
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameInRecordBounds(record),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        uint flags = 0;
        float crimeGoldMultiplier = 0;
        var relations = new List<FactionRelation>();
        var ranks = new List<FactionRank>();

        // Track current rank being built (RNAM groups MNAM/FNAM/INAM that follow)
        int? currentRankNumber = null;
        string? currentMaleTitle = null;
        string? currentFemaleTitle = null;
        string? currentInsignia = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 4:
                    // FACT DATA: 2 flag bytes + 2 unused
                    flags = (uint)(subData[0] | (subData[1] << 8));
                    break;
                case "XNAM" when sub.DataLength == 12:
                {
                    var fields = SubrecordDataReader.ReadFields("XNAM", "FACT", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        var factionFormId = SubrecordDataReader.GetUInt32(fields, "Faction");
                        var modifier = SubrecordDataReader.GetInt32(fields, "Modifier");
                        var combatReaction = SubrecordDataReader.GetUInt32(fields, "CombatReaction");
                        relations.Add(new FactionRelation(factionFormId, modifier, combatReaction));
                    }

                    break;
                }
                case "RNAM" when sub.DataLength == 4:
                {
                    // Flush previous rank if any
                    if (currentRankNumber.HasValue)
                    {
                        ranks.Add(new FactionRank(currentRankNumber.Value, currentMaleTitle, currentFemaleTitle,
                            currentInsignia));
                    }

                    var fields = SubrecordDataReader.ReadFields("RNAM", "FACT", subData, record.IsBigEndian);
                    currentRankNumber = fields.Count > 0
                        ? SubrecordDataReader.GetInt32(fields, "RankNumber")
                        : 0;
                    currentMaleTitle = null;
                    currentFemaleTitle = null;
                    currentInsignia = null;
                    break;
                }
                case "MNAM" when sub.DataLength > 0 && currentRankNumber.HasValue:
                    currentMaleTitle = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FNAM" when sub.DataLength > 0 && currentRankNumber.HasValue:
                    currentFemaleTitle = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "INAM" when sub.DataLength > 0 && currentRankNumber.HasValue:
                    currentInsignia = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "CRVA" when sub.DataLength >= 4:
                {
                    var fields = SubrecordDataReader.ReadFields("CRVA", "FACT", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        crimeGoldMultiplier = SubrecordDataReader.GetFloat(fields, "CrimeGoldMultiplier");
                    }

                    break;
                }
            }
        }

        // Flush last rank
        if (currentRankNumber.HasValue)
        {
            ranks.Add(new FactionRank(currentRankNumber.Value, currentMaleTitle, currentFemaleTitle, currentInsignia));
        }

        // Track FullName for display name map
        if (!string.IsNullOrEmpty(fullName))
        {
            _formIdToFullName.TryAdd(record.FormId, fullName);
        }

        return new ReconstructedFaction
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Flags = flags,
            CrimeGoldMultiplier = crimeGoldMultiplier,
            Relations = relations,
            Ranks = ranks,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Actor Parsing Helpers

    private static ActorBaseSubrecord? ParseActorBase(ReadOnlySpan<byte> data, long offset, bool bigEndian)
    {
        if (data.Length < 24)
        {
            return null;
        }

        var fields = SubrecordDataReader.ReadFields("ACBS", null, data, bigEndian);
        if (fields.Count == 0)
        {
            return null;
        }

        return new ActorBaseSubrecord(
            SubrecordDataReader.GetUInt32(fields, "Flags"),
            SubrecordDataReader.GetUInt16(fields, "Fatigue"),
            SubrecordDataReader.GetUInt16(fields, "BarterGold"),
            SubrecordDataReader.GetInt16(fields, "Level"),
            SubrecordDataReader.GetUInt16(fields, "CalcMin"),
            SubrecordDataReader.GetUInt16(fields, "CalcMax"),
            SubrecordDataReader.GetUInt16(fields, "SpeedMult"),
            SubrecordDataReader.GetFloat(fields, "KarmaAlignment"),
            SubrecordDataReader.GetInt16(fields, "Disposition"),
            SubrecordDataReader.GetUInt16(fields, "TemplateFlags"),
            offset, bigEndian);
    }

    #endregion

    #region ReconstructNpcs

    /// <summary>
    ///     Reconstruct all NPC records from the scan result.
    ///     Uses two-track approach: ESM records for subrecord detail + runtime C++ structs
    ///     for records not found as raw ESM data (typically thousands of NPCs vs ~7 ESM records).
    /// </summary>
    public List<ReconstructedNpc> ReconstructNpcs()
    {
        var npcs = new List<ReconstructedNpc>();
        var npcRecords = GetRecordsByType("NPC_").ToList();

        // Track FormIDs from ESM records to avoid duplicates when merging runtime data
        var esmFormIds = new HashSet<uint>();

        if (_accessor == null)
        {
            // Without accessor, use already-parsed subrecords from scan result
            foreach (var record in npcRecords)
            {
                var npc = ReconstructNpcFromScanResult(record);
                if (npc != null)
                {
                    npcs.Add(npc);
                    esmFormIds.Add(npc.FormId);
                }
            }
        }
        else
        {
            // With accessor, read full record data for better reconstruction
            var buffer = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                foreach (var record in npcRecords)
                {
                    var npc = ReconstructNpcFromAccessor(record, buffer);
                    if (npc != null)
                    {
                        npcs.Add(npc);
                        esmFormIds.Add(npc.FormId);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge NPCs from runtime struct reading (hash table entries not found as ESM records)
        if (_runtimeReader != null)
        {
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x2A || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var npc = _runtimeReader.ReadRuntimeNpc(entry);
                if (npc != null)
                {
                    npcs.Add(npc);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} NPCs from runtime struct reading " +
                    $"(total: {npcs.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return npcs;
    }

    private ReconstructedNpc? ReconstructNpcFromScanResult(DetectedMainRecord record)
    {
        // Find matching subrecords from scan result
        var editorId = GetEditorId(record.FormId);
        var fullName = FindFullNameNear(record.Offset);
        var stats = FindActorBaseNear(record.Offset);

        return new ReconstructedNpc
        {
            FormId = record.FormId,
            EditorId = editorId,
            FullName = fullName,
            Stats = stats,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedNpc? ReconstructNpcFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ReconstructNpcFromScanResult(record);
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        ActorBaseSubrecord? stats = null;
        uint? race = null;
        uint? script = null;
        uint? classFormId = null;
        uint? deathItem = null;
        uint? voiceType = null;
        uint? template = null;
        uint? hairFormId = null;
        float? hairLength = null;
        uint? eyesFormId = null;
        var factions = new List<FactionMembership>();
        var spells = new List<uint>();
        var inventory = new List<InventoryItem>();
        var packages = new List<uint>();

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "ACBS" when sub.DataLength == 24:
                    stats = ParseActorBase(subData, record.Offset + 24 + sub.DataOffset, record.IsBigEndian);
                    break;
                case "RNAM" when sub.DataLength == 4:
                    race = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SCRI" when sub.DataLength == 4:
                    script = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "CNAM" when sub.DataLength == 4:
                    classFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "INAM" when sub.DataLength == 4:
                    deathItem = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "VTCK" when sub.DataLength == 4:
                    voiceType = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TPLT" when sub.DataLength == 4:
                    template = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "HNAM" when sub.DataLength == 4:
                    hairFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "LNAM" when sub.DataLength == 4:
                {
                    var fields = SubrecordDataReader.ReadFields("LNAM", "NPC_", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        hairLength = SubrecordDataReader.GetFloat(fields, "HairLength");
                    }

                    break;
                }
                case "ENAM" when sub.DataLength == 4:
                    eyesFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SNAM" when sub.DataLength >= 5:
                    var factionFormId = ReadFormId(subData[..4], record.IsBigEndian);
                    var rank = (sbyte)subData[4];
                    factions.Add(new FactionMembership(factionFormId, rank));
                    break;
                case "SPLO" when sub.DataLength == 4:
                    spells.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
                case "CNTO" when sub.DataLength >= 8:
                {
                    var fields = SubrecordDataReader.ReadFields("CNTO", null, subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        var itemFormId = SubrecordDataReader.GetUInt32(fields, "Item");
                        var count = SubrecordDataReader.GetInt32(fields, "Count");
                        inventory.Add(new InventoryItem(itemFormId, count));
                    }

                    break;
                }
                case "PKID" when sub.DataLength == 4:
                    packages.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
            }
        }

        // Track FullName for display name map
        if (!string.IsNullOrEmpty(fullName))
        {
            _formIdToFullName.TryAdd(record.FormId, fullName);
        }

        return new ReconstructedNpc
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Stats = stats,
            Race = race,
            Script = script,
            Class = classFormId,
            DeathItem = deathItem,
            VoiceType = voiceType,
            Template = template,
            HairFormId = hairFormId,
            HairLength = hairLength,
            EyesFormId = eyesFormId,
            Factions = factions,
            Spells = spells,
            Inventory = inventory,
            Packages = packages,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region ReconstructRaces

    /// <summary>
    ///     Reconstruct all Race records from the scan result.
    /// </summary>
    public List<ReconstructedRace> ReconstructRaces()
    {
        var races = new List<ReconstructedRace>();
        var raceRecords = GetRecordsByType("RACE").ToList();

        if (_accessor == null)
        {
            foreach (var record in raceRecords)
            {
                races.Add(new ReconstructedRace
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                foreach (var record in raceRecords)
                {
                    var race = ReconstructRaceFromAccessor(record, buffer);
                    if (race != null)
                    {
                        races.Add(race);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return races;
    }

    private ReconstructedRace? ReconstructRaceFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ReconstructedRace
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? description = null;

        // S.P.E.C.I.A.L. modifiers (from DATA bytes 0-6)
        sbyte strength = 0, perception = 0, endurance = 0, charisma = 0;
        sbyte intelligence = 0, agility = 0, luck = 0;

        // Heights and weights (from DATA)
        var maleHeight = 1.0f;
        var femaleHeight = 1.0f;
        var maleWeight = 1.0f;
        var femaleWeight = 1.0f;
        uint dataFlags = 0;

        // Related FormIDs
        uint? olderRace = null;
        uint? youngerRace = null;
        uint? maleVoice = null;
        uint? femaleVoice = null;
        var abilityFormIds = new List<uint>();

        // Hair/Eyes
        uint? defaultHairMale = null;
        uint? defaultHairFemale = null;
        uint? defaultHairColor = null;
        var hairStyleFormIds = new List<uint>();
        var eyeColorFormIds = new List<uint>();

        // FaceGen
        float faceGenMainClamp = 0;
        float faceGenFaceClamp = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DESC":
                    description = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 36:
                {
                    // S.P.E.C.I.A.L. are individual bytes within the SkillBoosts blob
                    strength = (sbyte)subData[0];
                    perception = (sbyte)subData[1];
                    endurance = (sbyte)subData[2];
                    charisma = (sbyte)subData[3];
                    intelligence = (sbyte)subData[4];
                    agility = (sbyte)subData[5];
                    luck = (sbyte)subData[6];

                    var fields = SubrecordDataReader.ReadFields("DATA", "RACE", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        maleHeight = SubrecordDataReader.GetFloat(fields, "MaleHeight");
                        femaleHeight = SubrecordDataReader.GetFloat(fields, "FemaleHeight");
                        maleWeight = SubrecordDataReader.GetFloat(fields, "MaleWeight");
                        femaleWeight = SubrecordDataReader.GetFloat(fields, "FemaleWeight");
                        dataFlags = SubrecordDataReader.GetUInt32(fields, "Flags");
                    }

                    break;
                }
                case "DNAM" when sub.DataLength == 8:
                    // Default hair styles (Male FormID + Female FormID)
                    defaultHairMale = ReadFormId(subData[..4], record.IsBigEndian);
                    defaultHairFemale = ReadFormId(subData[4..], record.IsBigEndian);
                    break;
                case "HNAM" when sub.DataLength >= 4:
                    // Hair style FormID array
                    for (var i = 0; i + 4 <= sub.DataLength; i += 4)
                    {
                        hairStyleFormIds.Add(ReadFormId(subData[i..], record.IsBigEndian));
                    }

                    break;
                case "ENAM" when sub.DataLength >= 4:
                    // Eye color FormID array
                    for (var i = 0; i + 4 <= sub.DataLength; i += 4)
                    {
                        eyeColorFormIds.Add(ReadFormId(subData[i..], record.IsBigEndian));
                    }

                    break;
                case "CNAM" when sub.DataLength >= 1:
                    defaultHairColor = subData[0];
                    break;
                case "PNAM" when sub.DataLength == 4:
                {
                    var fields = SubrecordDataReader.ReadFields("PNAM", "RACE", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        faceGenMainClamp = SubrecordDataReader.GetFloat(fields, "FaceGenMainClamp");
                    }

                    break;
                }
                case "UNAM" when sub.DataLength == 4:
                {
                    var fields = SubrecordDataReader.ReadFields("UNAM", "RACE", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        faceGenFaceClamp = SubrecordDataReader.GetFloat(fields, "FaceGenFaceClamp");
                    }

                    break;
                }
                case "ONAM" when sub.DataLength == 4:
                    olderRace = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "YNAM" when sub.DataLength == 4:
                    youngerRace = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "VTCK" when sub.DataLength >= 8:
                    maleVoice = ReadFormId(subData[..4], record.IsBigEndian);
                    femaleVoice = ReadFormId(subData[4..], record.IsBigEndian);
                    break;
                case "SPLO" when sub.DataLength == 4:
                    abilityFormIds.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
            }
        }

        // Track FullName for display name map
        if (!string.IsNullOrEmpty(fullName))
        {
            _formIdToFullName.TryAdd(record.FormId, fullName);
        }

        return new ReconstructedRace
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Description = description,
            Strength = strength,
            Perception = perception,
            Endurance = endurance,
            Charisma = charisma,
            Intelligence = intelligence,
            Agility = agility,
            Luck = luck,
            MaleHeight = maleHeight,
            FemaleHeight = femaleHeight,
            MaleWeight = maleWeight,
            FemaleWeight = femaleWeight,
            DataFlags = dataFlags,
            DefaultHairMaleFormId = defaultHairMale != 0 ? defaultHairMale : null,
            DefaultHairFemaleFormId = defaultHairFemale != 0 ? defaultHairFemale : null,
            DefaultHairColorFormId = defaultHairColor,
            HairStyleFormIds = hairStyleFormIds,
            EyeColorFormIds = eyeColorFormIds,
            FaceGenMainClamp = faceGenMainClamp,
            FaceGenFaceClamp = faceGenFaceClamp,
            OlderRaceFormId = olderRace != 0 ? olderRace : null,
            YoungerRaceFormId = youngerRace != 0 ? youngerRace : null,
            MaleVoiceFormId = maleVoice != 0 ? maleVoice : null,
            FemaleVoiceFormId = femaleVoice != 0 ? femaleVoice : null,
            AbilityFormIds = abilityFormIds,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
