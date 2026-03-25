using System.Buffers;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Handles parsing of combat-related effect records: Projectiles (PROJ) and Explosions (EXPL).
///     Extracted from <see cref="EffectRecordHandler" /> to keep file sizes manageable.
/// </summary>
internal sealed class CombatEffectHandler(RecordParserContext context) : RecordHandlerBase(context)
{

    #region Explosions

    /// <summary>
    ///     Parse all Explosion (EXPL) records.
    /// </summary>
    internal List<ExplosionRecord> ParseExplosions()
    {
        var explosions = new List<ExplosionRecord>();

        if (Context.Accessor == null)
        {
            return explosions;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in Context.GetRecordsByType("EXPL"))
            {
                var recordData = Context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, fullName = null, modelPath = null;
                float force = 0, damage = 0, radius = 0, isRadius = 0;
                uint light = 0, sound1 = 0, flags = 0, impactDataSet = 0, sound2 = 0, enchantment = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                Context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "MODL":
                            modelPath = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset,
                                sub.DataLength));
                            break;
                        case "EITM" when sub.DataLength >= 4:
                            enchantment =
                                RecordParserContext.ReadFormId(data.AsSpan(sub.DataOffset, 4), record.IsBigEndian);
                            break;
                        case "DATA" when sub.DataLength >= 36:
                        {
                            var fields = SubrecordDataReader.ReadFields("DATA", "EXPL",
                                data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                force = SubrecordDataReader.GetFloat(fields, "Force");
                                damage = SubrecordDataReader.GetFloat(fields, "Damage");
                                radius = SubrecordDataReader.GetFloat(fields, "Radius");
                                light = SubrecordDataReader.GetUInt32(fields, "Light");
                                sound1 = SubrecordDataReader.GetUInt32(fields, "Sound1");
                                flags = SubrecordDataReader.GetUInt32(fields, "Flags");
                                isRadius = SubrecordDataReader.GetFloat(fields, "ISRadius");
                                impactDataSet = SubrecordDataReader.GetUInt32(fields, "ImpactDataSet");
                                sound2 = SubrecordDataReader.GetUInt32(fields, "Sound2");
                            }

                            break;
                        }
                    }
                }

                explosions.Add(new ExplosionRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    ModelPath = modelPath,
                    Force = force,
                    Damage = damage,
                    Radius = radius,
                    Light = light,
                    Sound1 = sound1,
                    Flags = flags,
                    ISRadius = isRadius,
                    ImpactDataSet = impactDataSet,
                    Sound2 = sound2,
                    Enchantment = enchantment,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        Context.MergeRuntimeRecords(explosions, 0x51, e => e.FormId,
            (reader, entry) => reader.ReadRuntimeExplosion(entry), "explosions");

        return explosions;
    }

    #endregion

    #region Projectiles

    /// <summary>
    ///     Parse all Projectile (PROJ) records.
    /// </summary>
    internal List<ProjectileRecord> ParseProjectiles()
    {
        var projectiles = new List<ProjectileRecord>();

        if (Context.Accessor == null)
        {
            return projectiles;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in Context.GetRecordsByType("PROJ"))
            {
                var recordData = Context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, fullName = null, modelPath = null;
                ushort projFlags = 0, projType = 0;
                float gravity = 0, speed = 0, range = 0;
                float tracerChance = 0, explosionProximity = 0, explosionTimer = 0;
                float muzzleFlashDuration = 0, fadeDuration = 0, impactForce = 0;
                float rotationX = 0, rotationY = 0, rotationZ = 0, bounceMultiplier = 0;
                uint light = 0, muzzleFlashLight = 0, explosion = 0, sound = 0;
                uint countdownSound = 0, deactivateSound = 0, defaultWeaponSource = 0;
                uint soundLevel = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                Context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "MODL":
                            modelPath = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset,
                                sub.DataLength));
                            break;
                        case "VNAM" when sub.DataLength >= 4:
                            soundLevel = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                            break;
                        case "DATA" when sub.DataLength >= 52:
                        {
                            var fields = SubrecordDataReader.ReadFields("DATA", "PROJ",
                                data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                var flagsAndType = SubrecordDataReader.GetUInt32(fields, "FlagsAndType");
                                projFlags = (ushort)(flagsAndType & 0xFFFF);
                                projType = (ushort)((flagsAndType >> 16) & 0xFFFF);
                                gravity = SubrecordDataReader.GetFloat(fields, "Gravity");
                                speed = SubrecordDataReader.GetFloat(fields, "Speed");
                                range = SubrecordDataReader.GetFloat(fields, "Range");
                                light = SubrecordDataReader.GetUInt32(fields, "Light");
                                muzzleFlashLight = SubrecordDataReader.GetUInt32(fields, "MuzzleFlashLight");
                                tracerChance = SubrecordDataReader.GetFloat(fields, "TracerChance");
                                explosionProximity =
                                    SubrecordDataReader.GetFloat(fields, "ExplosionAltTriggerProximity");
                                explosionTimer = SubrecordDataReader.GetFloat(fields, "ExplosionAltTriggerTimer");
                                explosion = SubrecordDataReader.GetUInt32(fields, "Explosion");
                                sound = SubrecordDataReader.GetUInt32(fields, "Sound");
                                muzzleFlashDuration = SubrecordDataReader.GetFloat(fields, "MuzzleFlashDuration");
                                fadeDuration = SubrecordDataReader.GetFloat(fields, "FadeDuration");
                                impactForce = SubrecordDataReader.GetFloat(fields, "ImpactForce");
                                countdownSound = SubrecordDataReader.GetUInt32(fields, "SoundCountdown");
                                deactivateSound = SubrecordDataReader.GetUInt32(fields, "SoundDisable");
                                defaultWeaponSource = SubrecordDataReader.GetUInt32(fields, "DefaultWeaponSource");
                                rotationX = SubrecordDataReader.GetFloat(fields, "RotationX");
                                rotationY = SubrecordDataReader.GetFloat(fields, "RotationY");
                                rotationZ = SubrecordDataReader.GetFloat(fields, "RotationZ");
                                bounceMultiplier = SubrecordDataReader.GetFloat(fields, "BouncyMult");
                            }

                            break;
                        }
                    }
                }

                projectiles.Add(new ProjectileRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    ModelPath = modelPath,
                    Flags = projFlags,
                    ProjectileType = projType,
                    Gravity = gravity,
                    Speed = speed,
                    Range = range,
                    Light = light,
                    MuzzleFlashLight = muzzleFlashLight,
                    TracerChance = tracerChance,
                    ExplosionProximity = explosionProximity,
                    ExplosionTimer = explosionTimer,
                    Explosion = explosion,
                    Sound = sound,
                    MuzzleFlashDuration = muzzleFlashDuration,
                    FadeDuration = fadeDuration,
                    ImpactForce = impactForce,
                    CountdownSound = countdownSound,
                    DeactivateSound = deactivateSound,
                    DefaultWeaponSource = defaultWeaponSource,
                    RotationX = rotationX,
                    RotationY = rotationY,
                    RotationZ = rotationZ,
                    BounceMultiplier = bounceMultiplier,
                    SoundLevel = soundLevel,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return projectiles;
    }

    /// <summary>
    ///     Enrich parsed ProjectileRecords with runtime data from memory dump.
    ///     Runtime fills gaps in ESM data (FullName, ModelPath) and provides cross-validation.
    /// </summary>
    internal void EnrichProjectilesWithRuntime(List<ProjectileRecord> projectiles)
    {
        if (Context.RuntimeReader == null)
        {
            return;
        }

        // Build FormID → RuntimeEditorIdEntry lookup for PROJ (FormType 0x33)
        var projectileEntries = new Dictionary<uint, RuntimeEditorIdEntry>();
        foreach (var entry in Context.ScanResult.RuntimeEditorIds)
        {
            if (entry.FormType == 0x33 && entry.TesFormOffset.HasValue)
            {
                projectileEntries.TryAdd(entry.FormId, entry);
            }
        }

        if (projectileEntries.Count == 0)
        {
            return;
        }

        var enrichedCount = 0;
        for (var i = 0; i < projectiles.Count; i++)
        {
            var proj = projectiles[i];
            if (!projectileEntries.TryGetValue(proj.FormId, out var entry))
            {
                continue;
            }

            var runtimeData = Context.RuntimeReader.ReadProjectilePhysics(
                entry.TesFormOffset!.Value, entry.FormId);
            if (runtimeData == null)
            {
                continue;
            }

            // ESM wins for fields it has; runtime fills gaps
            projectiles[i] = proj with
            {
                FullName = proj.FullName ?? runtimeData.FullName,
                ModelPath = proj.ModelPath ?? runtimeData.ModelPath,
                SoundLevel = proj.SoundLevel != 0 ? proj.SoundLevel : runtimeData.SoundLevel
            };
            enrichedCount++;
        }

        if (enrichedCount > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Enriched {enrichedCount}/{projectiles.Count} projectiles with runtime data " +
                $"({projectileEntries.Count} PROJ entries in hash table)");
        }
    }

    #endregion
}
