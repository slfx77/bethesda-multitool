using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Handles reconstruction of ALCH (consumable/aid/food) and AMMO records
///     from ESM data and runtime structs.
/// </summary>
internal sealed class ConsumableRecordHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    #region ReconstructAmmo

    /// <summary>
    ///     Reconstruct all Ammo records from the scan result.
    /// </summary>
    internal List<AmmoRecord> ReconstructAmmo()
    {
        var ammo = new List<AmmoRecord>();
        var ammoRecords = _context.GetRecordsByType("AMMO").ToList();

        if (_context.Accessor == null)
        {
            foreach (var record in ammoRecords)
            {
                ammo.Add(new AmmoRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = _context.FindFullNameNear(record.Offset),
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
                foreach (var record in ammoRecords)
                {
                    var item = ReconstructAmmoFromAccessor(record, buffer);
                    if (item != null)
                    {
                        ammo.Add(item);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        _context.MergeRuntimeRecords(ammo, 0x29, a => a.FormId,
            (reader, entry) => reader.ReadRuntimeAmmo(entry), "ammo");

        return ammo;
    }

    private AmmoRecord? ReconstructAmmoFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new AmmoRecord
            {
                FormId = record.FormId,
                EditorId = _context.GetEditorId(record.FormId),
                FullName = _context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        ObjectBounds? bounds = null;
        float speed = 0;
        byte flags = 0;
        uint value = 0;
        byte clipRounds = 0;
        uint? projectileFormId = null;
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
                case "DATA" when sub.DataLength >= 13:
                {
                    var fields = SubrecordDataReader.ReadFields("DATA", "AMMO", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        speed = SubrecordDataReader.GetFloat(fields, "Speed");
                        flags = SubrecordDataReader.GetByte(fields, "Flags");
                        value = SubrecordDataReader.GetUInt32(fields, "Value");
                        clipRounds = SubrecordDataReader.GetByte(fields, "ClipRounds");
                    }

                    break;
                }
                case "DAT2" when sub.DataLength >= 8:
                    // DAT2 layout: ProjectilePerShot (U32), Projectile FormID (U32), Weight (float), ...
                    var projId = RecordParserContext.ReadFormId(subData[4..], record.IsBigEndian);
                    if (projId != 0)
                    {
                        projectileFormId = projId;
                    }

                    if (sub.DataLength >= 12)
                    {
                        weight = record.IsBigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(subData[8..])
                            : BinaryPrimitives.ReadSingleLittleEndian(subData[8..]);
                    }

                    break;
            }
        }

        return new AmmoRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Bounds = bounds,
            Speed = speed,
            Flags = flags,
            Value = value,
            ClipRounds = clipRounds,
            ProjectileFormId = projectileFormId,
            Weight = weight,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Cross-references weapons and ammo to populate ProjectileFormId and ProjectileModelPath
    ///     on ammo records. Each weapon has an AmmoFormId and a ProjectileFormId. We reverse-map:
    ///     ammo FormID -> weapon -> projectile FormID -> BGSProjectile model path at dump offset +80.
    /// </summary>
    internal void EnrichAmmoWithProjectileModels(
        List<WeaponRecord> weapons,
        List<AmmoRecord> ammo)
    {
        if (_context.RuntimeReader == null || ammo.Count == 0)
        {
            return;
        }

        // Build: ammo FormID -> projectile FormID (from weapons that reference both)
        var ammoToProjectile = new Dictionary<uint, uint>();
        foreach (var weapon in weapons)
        {
            if (weapon.AmmoFormId is > 0 && weapon.ProjectileFormId is > 0)
            {
                ammoToProjectile.TryAdd(weapon.AmmoFormId.Value, weapon.ProjectileFormId.Value);
            }
        }

        if (ammoToProjectile.Count == 0)
        {
            return;
        }

        // Build: projectile FormID -> TesFormOffset (from runtime EditorID hash table)
        // PROJ = FormType 0x33
        var projectileOffsets = new Dictionary<uint, long>();
        foreach (var entry in _context.ScanResult.RuntimeEditorIds)
        {
            if (entry.FormType == 0x33 && entry.TesFormOffset.HasValue)
            {
                projectileOffsets.TryAdd(entry.FormId, entry.TesFormOffset.Value);
            }
        }

        // Enrich each ammo record with projectile FormID and model path
        var enrichedCount = 0;
        for (var i = 0; i < ammo.Count; i++)
        {
            var a = ammo[i];
            if (!ammoToProjectile.TryGetValue(a.FormId, out var projFormId))
            {
                continue;
            }

            string? projModelPath = null;
            if (projectileOffsets.TryGetValue(projFormId, out var projFileOffset))
            {
                // Read model path BSStringT at dump offset +80 (TESModel.cModel in BGSProjectile)
                projModelPath = _context.RuntimeReader.ReadBSStringT(projFileOffset, 80);
            }

            // Create updated record with projectile data
            // (records are immutable, so we replace in the list)
            ammo[i] = a with
            {
                ProjectileFormId = projFormId,
                ProjectileModelPath = projModelPath
            };
            enrichedCount++;
        }

        if (enrichedCount > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Enriched {enrichedCount}/{ammo.Count} ammo records with projectile data " +
                $"({projectileOffsets.Count} projectiles in hash table)");
        }
    }

    #endregion

    #region ReconstructConsumables

    /// <summary>
    ///     Reconstruct all Consumable (ALCH) records from the scan result.
    /// </summary>
    internal List<ConsumableRecord> ReconstructConsumables()
    {
        var consumables = new List<ConsumableRecord>();
        var alchRecords = _context.GetRecordsByType("ALCH").ToList();

        if (_context.Accessor == null)
        {
            foreach (var record in alchRecords)
            {
                consumables.Add(new ConsumableRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = _context.FindFullNameNear(record.Offset),
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
                foreach (var record in alchRecords)
                {
                    var item = ReconstructConsumableFromAccessor(record, buffer);
                    if (item != null)
                    {
                        consumables.Add(item);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        _context.MergeRuntimeRecords(consumables, 0x2F, c => c.FormId,
            (reader, entry) => reader.ReadRuntimeConsumable(entry), "consumables");

        return consumables;
    }

    private ConsumableRecord? ReconstructConsumableFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ConsumableRecord
            {
                FormId = record.FormId,
                EditorId = _context.GetEditorId(record.FormId),
                FullName = _context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        ObjectBounds? bounds = null;
        float weight = 0;
        uint value = 0;
        uint flags = 0;
        uint? addictionFormId = null;
        float addictionChance = 0;
        uint? withdrawalEffectFormId = null;
        var effectFormIds = new List<uint>();

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
                case "DATA" when sub.DataLength >= 4:
                {
                    var fields = SubrecordDataReader.ReadFields("DATA", "ALCH", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        weight = SubrecordDataReader.GetFloat(fields, "Weight");
                    }

                    break;
                }
                case "ENIT" when sub.DataLength >= 16:
                {
                    var fields = SubrecordDataReader.ReadFields("ENIT", "ALCH", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        value = SubrecordDataReader.GetUInt32(fields, "Value");
                        addictionFormId = SubrecordDataReader.GetUInt32(fields, "Addiction");
                        addictionChance = SubrecordDataReader.GetFloat(fields, "AddictionChance");
                    }

                    // Flags are at bytes 4-7 (stored as raw Bytes in schema, read directly)
                    if (sub.DataLength >= 8)
                    {
                        flags = record.IsBigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(subData[4..])
                            : BinaryPrimitives.ReadUInt32LittleEndian(subData[4..]);
                    }

                    // WithdrawalEffect/UseSound at bytes 16-19
                    if (sub.DataLength >= 20)
                    {
                        var weFormId = RecordParserContext.ReadFormId(subData[16..], record.IsBigEndian);
                        if (weFormId != 0)
                        {
                            withdrawalEffectFormId = weFormId;
                        }
                    }

                    break;
                }
                case "EFID" when sub.DataLength == 4:
                    effectFormIds.Add(RecordParserContext.ReadFormId(subData, record.IsBigEndian));
                    break;
            }
        }

        return new ConsumableRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Bounds = bounds,
            Weight = weight,
            Value = value,
            Flags = flags,
            AddictionFormId = addictionFormId != 0 ? addictionFormId : null,
            AddictionChance = addictionChance,
            WithdrawalEffectFormId = withdrawalEffectFormId,
            EffectFormIds = effectFormIds,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
