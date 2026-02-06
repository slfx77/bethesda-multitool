using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

public sealed partial class SemanticReconstructor
{
    #region ReconstructCreatures

    /// <summary>
    ///     Reconstruct all Creature records from the scan result.
    /// </summary>
    public List<ReconstructedCreature> ReconstructCreatures()
    {
        var creatures = new List<ReconstructedCreature>();
        var creatureRecords = GetRecordsByType("CREA").ToList();

        foreach (var record in creatureRecords)
        {
            creatures.Add(new ReconstructedCreature
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
        }

        // Merge creatures from runtime struct reading
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(creatures.Select(c => c.FormId));
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

    #endregion

    #region ReconstructFactions

    /// <summary>
    ///     Reconstruct all Faction records from the scan result.
    /// </summary>
    public List<ReconstructedFaction> ReconstructFactions()
    {
        var factions = new List<ReconstructedFaction>();
        var factionRecords = GetRecordsByType("FACT").ToList();

        foreach (var record in factionRecords)
        {
            factions.Add(new ReconstructedFaction
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
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

    #endregion

    #region Actor Parsing Helpers

    private static ActorBaseSubrecord? ParseActorBase(ReadOnlySpan<byte> data, long offset, bool bigEndian)
    {
        if (data.Length < 24)
        {
            return null;
        }

        uint flags;
        ushort fatigueBase, barterGold, calcMin, calcMax, speedMultiplier, templateFlags;
        short level, dispositionBase;
        float karmaAlignment;

        if (bigEndian)
        {
            flags = BinaryPrimitives.ReadUInt32BigEndian(data);
            fatigueBase = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
            barterGold = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
            level = BinaryPrimitives.ReadInt16BigEndian(data[8..]);
            calcMin = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
            calcMax = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
            speedMultiplier = BinaryPrimitives.ReadUInt16BigEndian(data[14..]);
            karmaAlignment = BinaryPrimitives.ReadSingleBigEndian(data[16..]);
            dispositionBase = BinaryPrimitives.ReadInt16BigEndian(data[20..]);
            templateFlags = BinaryPrimitives.ReadUInt16BigEndian(data[22..]);
        }
        else
        {
            flags = BinaryPrimitives.ReadUInt32LittleEndian(data);
            fatigueBase = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
            barterGold = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            level = BinaryPrimitives.ReadInt16LittleEndian(data[8..]);
            calcMin = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
            calcMax = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
            speedMultiplier = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
            karmaAlignment = BinaryPrimitives.ReadSingleLittleEndian(data[16..]);
            dispositionBase = BinaryPrimitives.ReadInt16LittleEndian(data[20..]);
            templateFlags = BinaryPrimitives.ReadUInt16LittleEndian(data[22..]);
        }

        return new ActorBaseSubrecord(
            flags, fatigueBase, barterGold, level, calcMin, calcMax,
            speedMultiplier, karmaAlignment, dispositionBase, templateFlags,
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
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return ReconstructNpcFromScanResult(record);
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        ActorBaseSubrecord? stats = null;
        uint? race = null;
        uint? script = null;
        uint? classFormId = null;
        uint? deathItem = null;
        uint? voiceType = null;
        uint? template = null;
        var factions = new List<FactionMembership>();
        var spells = new List<uint>();
        var inventory = new List<InventoryItem>();
        var packages = new List<uint>();

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

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
                case "SNAM" when sub.DataLength >= 5:
                    var factionFormId = ReadFormId(subData[..4], record.IsBigEndian);
                    var rank = (sbyte)subData[4];
                    factions.Add(new FactionMembership(factionFormId, rank));
                    break;
                case "SPLO" when sub.DataLength == 4:
                    spells.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
                case "CNTO" when sub.DataLength >= 8:
                    var itemFormId = ReadFormId(subData[..4], record.IsBigEndian);
                    var count = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    inventory.Add(new InventoryItem(itemFormId, count));
                    break;
                case "PKID" when sub.DataLength == 4:
                    packages.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
            }
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
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
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

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? description = null;

        // S.P.E.C.I.A.L. modifiers
        sbyte strength = 0, perception = 0, endurance = 0, charisma = 0;
        sbyte intelligence = 0, agility = 0, luck = 0;

        // Heights
        var maleHeight = 1.0f;
        var femaleHeight = 1.0f;

        // Related FormIDs
        uint? olderRace = null;
        uint? youngerRace = null;
        uint? maleVoice = null;
        uint? femaleVoice = null;
        var abilityFormIds = new List<uint>();

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

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
                    // S.P.E.C.I.A.L. bonuses at offsets 0-6
                    strength = (sbyte)subData[0];
                    perception = (sbyte)subData[1];
                    endurance = (sbyte)subData[2];
                    charisma = (sbyte)subData[3];
                    intelligence = (sbyte)subData[4];
                    agility = (sbyte)subData[5];
                    luck = (sbyte)subData[6];
                    // Heights at offsets 20-27
                    maleHeight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[20..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[20..]);
                    femaleHeight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[24..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[24..]);
                    break;
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
