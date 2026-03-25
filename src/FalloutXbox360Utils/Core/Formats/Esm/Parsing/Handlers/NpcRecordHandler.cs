using System.Buffers;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class NpcRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{

    /// <summary>
    ///     Parse all NPC records from the scan result.
    ///     Uses two-track approach: ESM records for subrecord detail + runtime C++ structs
    ///     for records not found as raw ESM data (typically thousands of NPCs vs ~7 ESM records).
    /// </summary>
    internal List<NpcRecord> ParseNpcs()
    {
        var npcs = new List<NpcRecord>();
        var npcRecords = Context.GetRecordsByType("NPC_").ToList();

        if (Context.Accessor == null)
        {
            foreach (var record in npcRecords)
            {
                var npc = ParseNpcFromScanResult(record);
                if (npc != null)
                {
                    npcs.Add(npc);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                foreach (var record in npcRecords)
                {
                    var npc = ParseNpcFromAccessor(record, buffer);
                    if (npc != null)
                    {
                        npcs.Add(npc);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        Context.MergeRuntimeRecords(npcs, 0x2A, n => n.FormId,
            (reader, entry) => reader.ReadRuntimeNpc(entry), "NPCs");

        return npcs;
    }

    private NpcRecord? ParseNpcFromScanResult(DetectedMainRecord record)
    {
        // Find matching subrecords from scan result
        var editorId = Context.GetEditorId(record.FormId);
        var fullName = Context.FindFullNameNear(record.Offset);
        var stats = Context.FindActorBaseNear(record.Offset);

        return new NpcRecord
        {
            FormId = record.FormId,
            EditorId = editorId,
            FullName = fullName,
            Stats = stats,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private NpcRecord? ParseNpcFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ParseNpcFromScanResult(record);
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        ActorBaseSubrecord? stats = null;
        byte[]? specialStats = null;
        byte[]? skills = null;
        uint? race = null;
        uint? script = null;
        uint? classFormId = null;
        uint? deathItem = null;
        uint? voiceType = null;
        uint? template = null;
        uint? hairFormId = null;
        float? hairLength = null;
        uint? eyesFormId = null;
        uint? hairColor = null;
        float[]? fggs = null;
        float[]? fgga = null;
        float[]? fgts = null;
        var factions = new List<FactionMembership>();
        var spells = new List<uint>();
        var inventory = new List<InventoryItem>();
        var packages = new List<uint>();
        var headPartFormIds = new List<uint>();

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
                    stats = ActorRecordHandler.ParseActorBase(subData, record.Offset + 24 + sub.DataOffset,
                        record.IsBigEndian);
                    break;
                case "RNAM" when sub.DataLength == 4:
                    race = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SCRI" when sub.DataLength == 4:
                    script = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "CNAM" when sub.DataLength == 4:
                    classFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "INAM" when sub.DataLength == 4:
                    deathItem = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "VTCK" when sub.DataLength == 4:
                    voiceType = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TPLT" when sub.DataLength == 4:
                    template = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "HNAM" when sub.DataLength == 4:
                    hairFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
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
                    eyesFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "HCLR" when sub.DataLength == 4:
                    hairColor = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SNAM" when sub.DataLength >= 5:
                    var factionFormId = RecordParserContext.ReadFormId(subData[..4], record.IsBigEndian);
                    var rank = (sbyte)subData[4];
                    factions.Add(new FactionMembership(factionFormId, rank));
                    break;
                case "SPLO" when sub.DataLength == 4:
                    spells.Add(RecordParserContext.ReadFormId(subData, record.IsBigEndian));
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
                case "DATA" when sub.DataLength == 11:
                {
                    // NPC_ DATA: Int32 BaseHealth + 7 UInt8 SPECIAL (ST, PE, EN, CH, IN, AG, LK)
                    specialStats =
                        [subData[4], subData[5], subData[6], subData[7], subData[8], subData[9], subData[10]];
                    break;
                }
                case "DNAM" when sub.DataLength == 28:
                {
                    // NPC_ DNAM: 14 skill base values (each 2 bytes: base + modifier)
                    skills = new byte[14];
                    for (var i = 0; i < 14; i++)
                    {
                        skills[i] = subData[i * 2]; // Base value (skip modifier byte)
                    }

                    break;
                }
                case "PKID" when sub.DataLength == 4:
                    packages.Add(RecordParserContext.ReadFormId(subData, record.IsBigEndian));
                    break;
                case "PNAM" when sub.DataLength == 4:
                    headPartFormIds.Add(RecordParserContext.ReadFormId(subData, record.IsBigEndian));
                    break;
                case "FGGS" when sub.DataLength >= 4:
                    fggs = ActorRecordHandler.ReadFloatArray(subData, record.IsBigEndian);
                    break;
                case "FGGA" when sub.DataLength >= 4:
                    fgga = ActorRecordHandler.ReadFloatArray(subData, record.IsBigEndian);
                    break;
                case "FGTS" when sub.DataLength >= 4:
                    fgts = ActorRecordHandler.ReadFloatArray(subData, record.IsBigEndian);
                    break;
            }
        }

        return new NpcRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            Stats = stats,
            SpecialStats = specialStats,
            Skills = skills,
            Race = race,
            Script = script,
            Class = classFormId,
            DeathItem = deathItem,
            VoiceType = voiceType,
            Template = template,
            HairFormId = hairFormId,
            HairLength = hairLength,
            HairColor = hairColor,
            EyesFormId = eyesFormId,
            FaceGenGeometrySymmetric = fggs,
            FaceGenGeometryAsymmetric = fgga,
            FaceGenTextureSymmetric = fgts,
            Factions = factions,
            Spells = spells,
            Inventory = inventory,
            Packages = packages,
            HeadPartFormIds = headPartFormIds.Count > 0 ? headPartFormIds : null,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }
}
