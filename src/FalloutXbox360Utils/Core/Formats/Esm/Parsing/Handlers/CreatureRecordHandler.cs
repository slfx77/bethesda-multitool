using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

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
            record => ParseCreatureFromScanResult(record));

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
        ObjectBounds? bounds = null;
        ActorBaseSubrecord? stats = null;
        byte creatureType = 0;
        byte combatSkill = 0;
        byte magicSkill = 0;
        byte stealthSkill = 0;
        short attackDamage = 0;
        uint? script = null;
        uint? deathItem = null;
        uint? equippedItem = null;
        ushort? equippedAttackAnimation = null;
        uint? template = null;
        uint? voiceType = null;
        uint? combatStyleFormId = null;
        uint? inheritsSoundsFrom = null;
        uint? deathItemLootList = null;
        uint? impactDataSet = null;
        uint? bodyData = null;
        byte? soundType = null;
        float? turningSpeed = null;
        float? baseScale = null;
        float? footWeight = null;
        uint? impactMaterialType = null;
        uint? soundLevel = null;
        NpcAiData? aiData = null;
        var factions = new List<FactionMembership>();
        var inventory = new List<InventoryItem>();
        var spells = new List<uint>();
        var packages = new List<uint>();
        byte[]? modelFilesRaw = null;
        byte[]? textureFilesRaw = null;
        byte[]? animationFilesRaw = null;
        byte[]? animationNamesRaw = null;
        List<KeyValuePair<string, byte[]>>? soundDefinitionsRaw = null;
        List<KeyValuePair<string, byte[]>>? destructionDataRaw = null;

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
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
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
                    if (SubrecordSchemaView.TryRead("DATA", "CREA", subData, record.IsBigEndian) is { } v)
                    {
                        creatureType = v.Byte("CreatureType");
                        combatSkill = v.Byte("CombatSkill");
                        magicSkill = v.Byte("MagicSkill");
                        stealthSkill = v.Byte("StealthSkill");
                        if (sub.DataLength >= 10)
                        {
                            attackDamage = record.IsBigEndian
                                ? BinaryPrimitives.ReadInt16BigEndian(subData[8..])
                                : BinaryPrimitives.ReadInt16LittleEndian(subData[8..]);
                        }
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
                case "NIFZ":
                    modelFilesRaw = subData.ToArray();
                    break;
                case "NIFT":
                    textureFilesRaw = subData.ToArray();
                    break;
                case "KFFZ":
                    animationFilesRaw = subData.ToArray();
                    break;
                case "KFNM":
                    animationNamesRaw = subData.ToArray();
                    break;
                case "EITM" when sub.DataLength == 4:
                    equippedItem = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "EAMT" when sub.DataLength == 2:
                    equippedAttackAnimation = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(subData)
                        : BinaryPrimitives.ReadUInt16LittleEndian(subData);
                    break;
                case "TPLT" when sub.DataLength == 4:
                    template = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "VTCK" when sub.DataLength == 4:
                    voiceType = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "ZNAM" when sub.DataLength == 4:
                    combatStyleFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "CSCR" when sub.DataLength == 4:
                    inheritsSoundsFrom = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "LNAM" when sub.DataLength == 4:
                    deathItemLootList = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "CNAM" when sub.DataLength == 4:
                    impactDataSet = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "PNAM" when sub.DataLength == 4:
                    bodyData = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "RNAM" when sub.DataLength == 1:
                    soundType = subData[0];
                    break;
                case "TNAM" when sub.DataLength == 4:
                    turningSpeed = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    break;
                case "BNAM" when sub.DataLength == 4:
                    baseScale = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    break;
                case "WNAM" when sub.DataLength == 4:
                    footWeight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    break;
                case "NAM4" when sub.DataLength == 4:
                    impactMaterialType = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    break;
                case "NAM5" when sub.DataLength == 4:
                    soundLevel = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    break;
                case "CNTO" when sub.DataLength >= 8:
                {
                    var itemFormId = RecordParserContext.ReadFormId(subData[..4], record.IsBigEndian);
                    var count = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    inventory.Add(new InventoryItem(itemFormId, count));
                    break;
                }
                case "COED" when sub.DataLength >= 12 && inventory.Count > 0:
                {
                    var owner = RecordParserContext.ReadFormId(subData[..4], record.IsBigEndian);
                    var globalOrRank = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[4..]);
                    var condition = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[8..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[8..]);
                    inventory[^1] = inventory[^1] with
                    {
                        OwnerFormId = owner != 0 ? owner : null,
                        GlobalOrRank = globalOrRank,
                        ItemCondition = condition
                    };
                    break;
                }
                case "CSDT":
                case "CSDI":
                case "CSDC":
                    soundDefinitionsRaw ??= [];
                    soundDefinitionsRaw.Add(new KeyValuePair<string, byte[]>(sub.Signature, subData.ToArray()));
                    break;
                case "DEST":
                case "DSTD":
                case "DMDL":
                case "DMDT":
                case "DSTF":
                    destructionDataRaw ??= [];
                    destructionDataRaw.Add(new KeyValuePair<string, byte[]>(sub.Signature, subData.ToArray()));
                    break;
            }
        }

        return new CreatureRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            Bounds = bounds,
            Stats = stats,
            CreatureType = creatureType,
            CombatSkill = combatSkill,
            MagicSkill = magicSkill,
            StealthSkill = stealthSkill,
            AttackDamage = attackDamage,
            Script = script,
            DeathItem = deathItem,
            EquippedItem = equippedItem,
            EquippedAttackAnimation = equippedAttackAnimation,
            Template = template,
            VoiceType = voiceType,
            CombatStyleFormId = combatStyleFormId,
            InheritsSoundsFrom = inheritsSoundsFrom,
            DeathItemLootList = deathItemLootList,
            ImpactDataSet = impactDataSet,
            BodyData = bodyData,
            SoundType = soundType,
            TurningSpeed = turningSpeed,
            BaseScale = baseScale,
            FootWeight = footWeight,
            ImpactMaterialType = impactMaterialType,
            SoundLevel = soundLevel,
            AiData = aiData,
            ModelPath = modelPath,
            Factions = factions,
            Inventory = inventory,
            Spells = spells,
            Packages = packages,
            ModelFilesRaw = modelFilesRaw,
            TextureFilesRaw = textureFilesRaw,
            AnimationFilesRaw = animationFilesRaw,
            AnimationNamesRaw = animationNamesRaw,
            SoundDefinitionsRaw = soundDefinitionsRaw,
            DestructionDataRaw = destructionDataRaw,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }
}
