using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Handles parsing of KEYM, ARMO, MISC, and CONT records
///     from ESM data and runtime structs.
/// </summary>
internal sealed class ItemRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{

    /// <summary>
    ///     Parse all Key records from the scan result.
    /// </summary>
    internal List<KeyRecord> ParseKeys()
    {
        var keys = ParseRecordList<KeyRecord>("KEYM", 256,
            (record, _) => new KeyRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            },
            record => new KeyRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(keys, 0x2E, k => k.FormId,
            (reader, entry) => reader.ReadRuntimeKey(entry), "keys");

        return keys;
    }

    /// <summary>
    ///     Parse all Armor records from the scan result.
    /// </summary>
    internal List<ArmorRecord> ParseArmor()
    {
        var armor = ParseRecordList("ARMO", 4096, ParseArmorFromAccessor,
            record => new ArmorRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(armor, 0x18, a => a.FormId,
            (reader, entry) => reader.ReadRuntimeArmor(entry), "armor");

        return armor;
    }

    private ArmorRecord? ParseArmorFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ArmorRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        ObjectBounds? bounds = null;
        var value = 0;
        var health = 0;
        float weight = 0;
        float damageThreshold = 0;
        var damageResistance = 0;
        uint bipedFlags = 0;
        byte generalFlags = 0;
        var equipmentType = EquipmentType.None;

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
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
                case "DATA" when sub.DataLength >= 12:
                {
                    var fields = SubrecordDataReader.ReadFields("DATA", "ARMO", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        value = SubrecordDataReader.GetInt32(fields, "Value");
                        health = SubrecordDataReader.GetInt32(fields, "Health");
                        weight = SubrecordDataReader.GetFloat(fields, "Weight");
                    }

                    break;
                }
                case "DNAM" when sub.DataLength >= 8:
                {
                    var fields = SubrecordDataReader.ReadFields("DNAM", "ARMO", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        damageResistance = SubrecordDataReader.GetInt16(fields, "DamageResistance");
                        damageThreshold = SubrecordDataReader.GetFloat(fields, "DamageThreshold");
                    }

                    break;
                }
                case "BMDT" when sub.DataLength >= 8:
                {
                    var fields = SubrecordDataReader.ReadFields("BMDT", null, subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        bipedFlags = SubrecordDataReader.GetUInt32(fields, "BipedFlags");
                        generalFlags = SubrecordDataReader.GetByte(fields, "GeneralFlags");
                    }

                    break;
                }
                case "ETYP" when sub.DataLength == 4:
                {
                    var etypValue = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    if (etypValue >= -1 && etypValue <= 13)
                    {
                        equipmentType = (EquipmentType)etypValue;
                    }

                    break;
                }
            }
        }

        return new ArmorRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Bounds = bounds,
            Value = value,
            Health = health,
            Weight = weight,
            DamageThreshold = damageThreshold,
            DamageResistance = damageResistance,
            BipedFlags = bipedFlags,
            GeneralFlags = generalFlags,
            EquipmentType = equipmentType,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Parse all Misc Item records from the scan result.
    /// </summary>
    internal List<MiscItemRecord> ParseMiscItems()
    {
        var miscItems = ParseRecordList("MISC", 4096, ParseMiscItemFromAccessor,
            record => new MiscItemRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(miscItems, 0x1F, m => m.FormId,
            (reader, entry) => reader.ReadRuntimeMiscItem(entry), "misc items");

        return miscItems;
    }

    private MiscItemRecord? ParseMiscItemFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new MiscItemRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        ObjectBounds? bounds = null;
        var value = 0;
        float weight = 0;

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
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
                case "DATA" when sub.DataLength >= 8:
                {
                    var fields = SubrecordDataReader.ReadFields("DATA", "MISC", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        value = SubrecordDataReader.GetInt32(fields, "Value");
                        weight = SubrecordDataReader.GetFloat(fields, "Weight");
                    }

                    break;
                }
            }
        }

        return new MiscItemRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Bounds = bounds,
            Value = value,
            Weight = weight,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Parse all Container records from the scan result.
    /// </summary>
    internal List<ContainerRecord> ParseContainers()
    {
        var containers = new List<ContainerRecord>();
        var containerRecords = Context.GetRecordsByType("CONT").ToList();

        // Track FormIDs from ESM records to avoid duplicates when merging runtime data
        var esmFormIds = new HashSet<uint>();

        if (Context.Accessor == null)
        {
            // Without accessor, use basic parsing (no CNTO parsing)
            foreach (var record in containerRecords)
            {
                containers.Add(new ContainerRecord
                {
                    FormId = record.FormId,
                    EditorId = Context.GetEditorId(record.FormId),
                    FullName = Context.FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
                esmFormIds.Add(record.FormId);
            }
        }
        else
        {
            // With accessor, read full record data for CNTO subrecord parsing
            var buffer = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                foreach (var record in containerRecords)
                {
                    var container = ParseContainerFromAccessor(record, buffer);
                    if (container != null)
                    {
                        containers.Add(container);
                        esmFormIds.Add(container.FormId);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge containers from runtime struct reading
        if (Context.RuntimeReader != null)
        {
            // Enrich ESM containers with runtime contents (current game state)
            var runtimeEnrichments = new Dictionary<uint, ContainerRecord>();
            foreach (var entry in Context.ScanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x1B || !esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var rtc = Context.RuntimeReader.ReadRuntimeContainer(entry);
                if (rtc != null && rtc.Contents.Count > 0)
                {
                    runtimeEnrichments[entry.FormId] = rtc;
                }
            }

            if (runtimeEnrichments.Count > 0)
            {
                for (var i = 0; i < containers.Count; i++)
                {
                    if (runtimeEnrichments.TryGetValue(containers[i].FormId, out var rtc))
                    {
                        containers[i] = containers[i] with
                        {
                            Contents = rtc.Contents,
                            Flags = rtc.Flags,
                            ModelPath = containers[i].ModelPath ?? rtc.ModelPath,
                            Script = containers[i].Script ?? rtc.Script
                        };
                    }
                }

                Logger.Instance.Debug(
                    $"  [Semantic] Enriched {runtimeEnrichments.Count} ESM containers with runtime contents");
            }

            // Add runtime-only containers (not in ESM)
            var runtimeCount = 0;
            foreach (var entry in Context.ScanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x1B || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var container = Context.RuntimeReader.ReadRuntimeContainer(entry);
                if (container != null)
                {
                    containers.Add(container);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} containers from runtime struct reading " +
                    $"(total: {containers.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return containers;
    }

    private ContainerRecord? ParseContainerFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ContainerRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        uint? script = null;
        var contents = new List<InventoryItem>();

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
                case "SCRI" when sub.DataLength == 4:
                    script = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "CNTO" when sub.DataLength >= 8:
                {
                    var fields = SubrecordDataReader.ReadFields("CNTO", null, subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        var itemFormId = SubrecordDataReader.GetUInt32(fields, "Item");
                        var count = SubrecordDataReader.GetInt32(fields, "Count");
                        contents.Add(new InventoryItem(itemFormId, count));
                    }

                    break;
                }
            }
        }

        return new ContainerRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Script = script,
            Contents = contents,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }
}
