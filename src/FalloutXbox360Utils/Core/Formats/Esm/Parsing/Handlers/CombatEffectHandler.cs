using System.Buffers;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Handles reconstruction of combat-related effect records: Projectiles (PROJ) and Explosions (EXPL).
///     Extracted from <see cref="EffectRecordHandler"/> to keep file sizes manageable.
/// </summary>
internal sealed class CombatEffectHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    #region Projectiles

    /// <summary>
    ///     Reconstruct all Projectile (PROJ) records.
    /// </summary>
    internal List<ProjectileRecord> ReconstructProjectiles()
    {
        var projectiles = new List<ProjectileRecord>();

        if (_context.Accessor == null)
        {
            return projectiles;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("PROJ"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, fullName = null, modelPath = null;
                ushort projFlags = 0, projType = 0;
                float gravity = 0, speed = 0, range = 0;
                float muzzleFlashDuration = 0, fadeDuration = 0, impactForce = 0, timer = 0;
                uint light = 0, muzzleFlashLight = 0, explosion = 0, sound = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "MODL":
                            modelPath = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset,
                                sub.DataLength));
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
                                explosion = SubrecordDataReader.GetUInt32(fields, "Explosion");
                                sound = SubrecordDataReader.GetUInt32(fields, "Sound");
                                muzzleFlashDuration = SubrecordDataReader.GetFloat(fields, "MuzzleFlashDuration");
                                fadeDuration = SubrecordDataReader.GetFloat(fields, "FadeDuration");
                                impactForce = SubrecordDataReader.GetFloat(fields, "ImpactForce");
                                timer = SubrecordDataReader.GetFloat(fields, "ExplosionAltTriggerTimer");
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
                    Explosion = explosion,
                    Sound = sound,
                    MuzzleFlashDuration = muzzleFlashDuration,
                    FadeDuration = fadeDuration,
                    ImpactForce = impactForce,
                    Timer = timer,
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

    #endregion

    #region Explosions

    /// <summary>
    ///     Reconstruct all Explosion (EXPL) records.
    /// </summary>
    internal List<ExplosionRecord> ReconstructExplosions()
    {
        var explosions = new List<ExplosionRecord>();

        if (_context.Accessor == null)
        {
            return explosions;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("EXPL"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
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

        return explosions;
    }

    #endregion
}
