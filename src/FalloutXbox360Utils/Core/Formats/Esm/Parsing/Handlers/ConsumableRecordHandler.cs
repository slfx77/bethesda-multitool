using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Handles parsing of ALCH (consumable/aid/food) and AMMO records
///     from ESM data and runtime structs.
/// </summary>
internal sealed class ConsumableRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    #region ParseAmmo

    /// <summary>
    ///     Parse all Ammo records from the scan result.
    /// </summary>
    internal List<AmmoRecord> ParseAmmo()
    {
        var ammo = ParseRecordList("AMMO", 4096, ParseAmmoFromAccessor,
            record => new AmmoRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(ammo, 0x29, a => a.FormId,
            (reader, entry) => reader.ReadRuntimeAmmo(entry), "ammo");

        return ammo;
    }

    private AmmoRecord? ParseAmmoFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new AmmoRecord
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
        string? iconPath = null;
        string? messageIconPath = null;
        byte[]? textureHashData = null;
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
                case "ICON":
                    iconPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MICO":
                    messageIconPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODT" when sub.DataLength > 0:
                    textureHashData = subData.ToArray();
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
                case "DAT2" when sub.DataLength >= 4:
                    projectileFormId = TryReadAmmoProjectileFromDat2(subData, record.IsBigEndian)
                                       ?? projectileFormId;
                    weight = TryReadAmmoWeightFromDat2(subData, record.IsBigEndian) ?? weight;
                    break;
            }
        }

        return new AmmoRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            IconPath = iconPath,
            MessageIconPath = messageIconPath,
            TextureHashData = textureHashData,
            Bounds = bounds,
            Speed = speed,
            Flags = flags,
            Value = value,
            ClipRounds = clipRounds,
            ProjectileFormId = projectileFormId,
            ProjectileFormIds = projectileFormId.HasValue ? [projectileFormId.Value] : [],
            Weight = weight,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private uint? TryReadAmmoProjectileFromDat2(ReadOnlySpan<byte> data, bool bigEndian)
    {
        foreach (var offset in new[] { 12, 4, 0, 8, 16 })
        {
            if (offset + 4 > data.Length)
            {
                continue;
            }

            // DAT2 is mixed-endian in the Xbox ESMs: scalar values are big-endian,
            // but projectile FormIDs are stored in the little-endian PC byte order.
            var candidate = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            if (candidate == 0 && bigEndian)
            {
                candidate = RecordParserContext.ReadFormId(data[offset..], bigEndian);
            }

            if (candidate == 0)
            {
                continue;
            }

            if (Context.RecordsByFormId.TryGetValue(candidate, out var target) &&
                string.Equals(target.RecordType, "PROJ", StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private static float? TryReadAmmoWeightFromDat2(ReadOnlySpan<byte> data, bool bigEndian)
    {
        foreach (var offset in new[] { 8, 12, 16 })
        {
            if (offset + 4 > data.Length)
            {
                continue;
            }

            var candidate = bigEndian
                ? BinaryPrimitives.ReadSingleBigEndian(data[offset..])
                : BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
            if (!float.IsNaN(candidate) && !float.IsInfinity(candidate) && candidate is >= 0 and <= 100)
            {
                return candidate;
            }
        }

        return null;
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
        if (ammo.Count == 0)
        {
            return;
        }

        // Build: ammo FormID -> projectile FormIDs from weapons that reference both.
        // This is needed for ESM-only sources where AMMO may lack a direct DAT2 projectile.
        var ammoToProjectiles = new Dictionary<uint, HashSet<uint>>();
        foreach (var weapon in weapons)
        {
            if (weapon.AmmoFormId is > 0 && weapon.ProjectileFormId is > 0)
            {
                if (!ammoToProjectiles.TryGetValue(weapon.AmmoFormId.Value, out var projectiles))
                {
                    projectiles = [];
                    ammoToProjectiles[weapon.AmmoFormId.Value] = projectiles;
                }

                projectiles.Add(weapon.ProjectileFormId.Value);
            }
        }

        if (ammoToProjectiles.Count == 0 && !ammo.Any(a => a.ProjectileFormId is > 0))
        {
            return;
        }

        // Build: projectile FormID -> TesFormOffset (from runtime EditorID hash table)
        // PROJ = FormType 0x33
        var projectileOffsets = new Dictionary<uint, long>();
        if (Context.RuntimeReader != null)
        {
            foreach (var entry in Context.ScanResult.RuntimeEditorIds)
            {
                if (entry.FormType == 0x33 && entry.TesFormOffset.HasValue)
                {
                    projectileOffsets.TryAdd(entry.FormId, entry.TesFormOffset.Value);
                }
            }
        }

        // Enrich each ammo record with projectile FormID and model path
        var enrichedCount = 0;
        for (var i = 0; i < ammo.Count; i++)
        {
            var a = ammo[i];
            var projectileFormIds = new SortedSet<uint>();
            if (a.ProjectileFormId is > 0)
            {
                projectileFormIds.Add(a.ProjectileFormId.Value);
            }

            foreach (var existingProjectile in a.ProjectileFormIds)
            {
                if (existingProjectile != 0)
                {
                    projectileFormIds.Add(existingProjectile);
                }
            }

            if (ammoToProjectiles.TryGetValue(a.FormId, out var inferredProjectiles))
            {
                foreach (var inferredProjectile in inferredProjectiles)
                {
                    projectileFormIds.Add(inferredProjectile);
                }
            }

            var projFormId = a.ProjectileFormId;
            if (projFormId is not > 0 &&
                projectileFormIds.Count == 1)
            {
                projFormId = projectileFormIds.First();
            }

            if (projFormId is not > 0 && projectileFormIds.Count == 0)
            {
                continue;
            }

            string? projModelPath = null;
            if (Context.RuntimeReader != null &&
                projFormId is > 0 &&
                projectileOffsets.TryGetValue(projFormId.Value, out var projFileOffset))
            {
                // Read model path BSStringT at dump offset +80 (TESModel.cModel in BGSProjectile)
                projModelPath = Context.RuntimeReader.ReadBsStringT(projFileOffset, 80);
            }

            // Create updated record with projectile data
            // (records are immutable, so we replace in the list)
            ammo[i] = a with
            {
                ProjectileFormId = projFormId,
                ProjectileFormIds = projectileFormIds.ToList(),
                ProjectileModelPath = projModelPath ?? a.ProjectileModelPath
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

    #region ParseConsumables

    /// <summary>
    ///     Parse all Consumable (ALCH) records from the scan result.
    /// </summary>
    internal List<ConsumableRecord> ParseConsumables()
    {
        var consumables = ParseRecordList("ALCH", 4096, ParseConsumableFromAccessor,
            record => new ConsumableRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(consumables, 0x2F, c => c.FormId,
            (reader, entry) => reader.ReadRuntimeConsumable(entry), "consumables");

        return consumables;
    }

    private ConsumableRecord? ParseConsumableFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ConsumableRecord
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
        string? iconPath = null;
        string? messageIconPath = null;
        byte[]? textureHashData = null;
        ObjectBounds? bounds = null;
        float weight = 0;
        uint value = 0;
        uint flags = 0;
        uint? addictionFormId = null;
        float addictionChance = 0;
        uint? withdrawalEffectFormId = null;
        var effects = new List<EnchantmentEffect>();
        uint currentEffectId = 0;

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
                case "ICON":
                    iconPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MICO":
                    messageIconPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODT" when sub.DataLength > 0:
                    textureHashData = subData.ToArray();
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
                case "EFID" when sub.DataLength >= 4:
                    currentEffectId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "EFIT" when sub.DataLength >= 12:
                {
                    var fields = SubrecordDataReader.ReadFields("EFIT", null, subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        var magnitude = GameStatNormalizer.EffectMagnitude(subData, record.IsBigEndian);
                        var area = SubrecordDataReader.GetUInt32(fields, "Area");
                        var duration = SubrecordDataReader.GetUInt32(fields, "Duration");
                        var type = SubrecordDataReader.GetUInt32(fields, "Type");
                        var actorValue = SubrecordDataReader.GetInt32(fields, "ActorValue", -1);

                        effects.Add(new EnchantmentEffect
                        {
                            EffectFormId = currentEffectId,
                            Magnitude = magnitude,
                            Area = GameStatNormalizer.IsPlausibleEffectArea(area) ? area : 0,
                            Duration = GameStatNormalizer.IsPlausibleEffectDuration(duration) ? duration : 0,
                            Type = GameStatNormalizer.IsPlausibleEffectTarget(type) ? type : 0,
                            ActorValue = GameStatNormalizer.IsPlausibleActorValue(actorValue) ? actorValue : -1
                        });
                    }

                    break;
                }
            }
        }

        return new ConsumableRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            IconPath = iconPath,
            MessageIconPath = messageIconPath,
            TextureHashData = textureHashData,
            Bounds = bounds,
            Weight = weight,
            Value = value,
            Flags = flags,
            AddictionFormId = addictionFormId != 0 ? addictionFormId : null,
            AddictionChance = addictionChance,
            WithdrawalEffectFormId = withdrawalEffectFormId,
            Effects = effects,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
