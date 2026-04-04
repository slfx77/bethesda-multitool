using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class CreatureRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{

    /// <summary>
    ///     Parse all Creature records from the scan result.
    ///     Uses two-track approach: ESM records for subrecord detail + runtime C++ structs
    ///     for records not found as raw ESM data.
    /// </summary>
    internal List<CreatureRecord> ParseCreatures()
    {
        var creatures = ParseRecordList("CREA", 16384,
            (record, buffer) => ParseCreatureFromAccessor(record, buffer),
            (record) => ParseCreatureFromScanResult(record));

        Context.MergeRuntimeRecords(creatures, 0x2B, c => c.FormId,
            (reader, entry) => reader.ReadRuntimeCreature(entry), "creatures");

        return creatures;
    }

    private CreatureRecord ParseCreatureFromScanResult(DetectedMainRecord record)
    {
        return new CreatureRecord
        {
            FormId = record.FormId,
            EditorId = Context.GetEditorId(record.FormId),
            FullName = Context.FindFullNameNear(record.Offset),
            Stats = Context.FindActorBaseNear(record.Offset),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private CreatureRecord ParseCreatureFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ParseCreatureFromScanResult(record);
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
        NpcAiData? aiData = null;
        var factions = new List<FactionMembership>();
        var spells = new List<uint>();
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
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "ACBS" when sub.DataLength == 24:
                    stats = ActorRecordHandler.ParseActorBase(subData, record.Offset + 24 + sub.DataOffset,
                        record.IsBigEndian);
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
                    script = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "INAM" when sub.DataLength == 4:
                    deathItem = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SNAM" when sub.DataLength >= 5:
                    var factionFormId = RecordParserContext.ReadFormId(subData[..4], record.IsBigEndian);
                    var rank = (sbyte)subData[4];
                    factions.Add(new FactionMembership(factionFormId, rank));
                    break;
                case "SPLO" when sub.DataLength == 4:
                    spells.Add(RecordParserContext.ReadFormId(subData, record.IsBigEndian));
                    break;
                case "PKID" when sub.DataLength == 4:
                    packages.Add(RecordParserContext.ReadFormId(subData, record.IsBigEndian));
                    break;
                case "AIDT" when sub.DataLength >= 12:
                    aiData = ActorRecordHandler.ParseAiData(subData, record.IsBigEndian);
                    break;
            }
        }

        return new CreatureRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            Stats = stats,
            CreatureType = creatureType,
            CombatSkill = combatSkill,
            MagicSkill = magicSkill,
            StealthSkill = stealthSkill,
            AttackDamage = attackDamage,
            Script = script,
            DeathItem = deathItem,
            AiData = aiData,
            ModelPath = modelPath,
            Factions = factions,
            Spells = spells,
            Packages = packages,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }
}
