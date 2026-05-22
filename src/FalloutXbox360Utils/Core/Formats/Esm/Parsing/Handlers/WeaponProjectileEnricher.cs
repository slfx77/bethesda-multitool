using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Enriches weapon records with projectile physics data from ESM PROJ records
///     and runtime C++ structs. Extracted from WeaponRecordHandler.
/// </summary>
internal sealed class WeaponProjectileEnricher(RecordParserContext context)
{
    /// <summary>
    ///     Enrich weapon records with projectile physics data parsed from PROJ ESM records.
    ///     This is the ESM-only path — no DMP required. Parses the PROJ DATA subrecord
    ///     using the existing schema to extract speed, gravity, range, etc.
    /// </summary>
    internal void EnrichWeaponsWithEsmProjectileData(List<WeaponRecord> weapons)
    {
        if (context.Accessor == null || weapons.Count == 0)
        {
            return;
        }

        // Collect unique projectile FormIDs that need enrichment
        var neededProjIds = new HashSet<uint>();
        foreach (var weapon in weapons)
        {
            if (weapon.ProjectileFormId.HasValue && weapon.ProjectileData == null)
            {
                neededProjIds.Add(weapon.ProjectileFormId.Value);
            }
        }

        if (neededProjIds.Count == 0)
        {
            return;
        }

        // Parse PROJ records from ESM and build lookup
        var projDataLookup = ParseProjectileRecords(neededProjIds);

        if (projDataLookup.Count == 0)
        {
            return;
        }

        // Merge into weapons
        var enrichedCount = 0;
        for (var i = 0; i < weapons.Count; i++)
        {
            var weapon = weapons[i];
            if (weapon.ProjectileData != null || !weapon.ProjectileFormId.HasValue)
            {
                continue;
            }

            if (projDataLookup.TryGetValue(weapon.ProjectileFormId.Value, out var projData))
            {
                weapons[i] = weapon with { ProjectileData = projData };
                enrichedCount++;
            }
        }

        if (enrichedCount > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Enriched {enrichedCount}/{weapons.Count} weapons with ESM projectile data " +
                $"({projDataLookup.Count} PROJ records parsed)");
        }
    }

    private Dictionary<uint, ProjectilePhysicsData> ParseProjectileRecords(HashSet<uint> neededProjIds)
    {
        var projDataLookup = new Dictionary<uint, ProjectilePhysicsData>();
        var buffer = new byte[4096];

        foreach (var projRecord in context.GetRecordsByType("PROJ"))
        {
            if (!neededProjIds.Contains(projRecord.FormId))
            {
                continue;
            }

            var recordData = context.ReadRecordData(projRecord, buffer);
            if (recordData == null)
            {
                continue;
            }

            var (data, dataSize) = recordData.Value;
            string? modelPath = null;
            string? fullName = null;
            ProjectilePhysicsData? projPhysics = null;

            foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, projRecord.IsBigEndian))
            {
                var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                switch (sub.Signature)
                {
                    case "MODL":
                        modelPath = EsmStringUtils.ReadNullTermString(subData);
                        break;
                    case "FULL":
                        fullName = EsmStringUtils.ReadNullTermString(subData);
                        break;
                    case "DATA" when sub.DataLength >= 64:
                    {
                        if (SubrecordSchemaView.TryRead("DATA", "PROJ", subData, projRecord.IsBigEndian) is { } v)
                        {
                            projPhysics = new ProjectilePhysicsData
                            {
                                Flags = v.UInt32("FlagsAndType"),
                                Gravity = v.Float("Gravity"),
                                Speed = v.Float("Speed"),
                                Range = v.Float("Range"),
                                LightFormId = NullIfZero(v.UInt32("Light")),
                                MuzzleFlashLightFormId =
                                    NullIfZero(v.UInt32("MuzzleFlashLight")),
                                TracerChance = v.Float("TracerChance"),
                                ExplosionProximity =
                                    v.Float("ExplosionAltTriggerProximity"),
                                ExplosionTimer =
                                    v.Float("ExplosionAltTriggerTimer"),
                                ExplosionFormId =
                                    NullIfZero(v.UInt32("Explosion")),
                                ActiveSoundLoopFormId =
                                    NullIfZero(v.UInt32("Sound")),
                                MuzzleFlashDuration =
                                    v.Float("MuzzleFlashDuration"),
                                FadeOutTime = v.Float("FadeDuration"),
                                Force = v.Float("ImpactForce"),
                                CountdownSoundFormId =
                                    NullIfZero(v.UInt32("SoundCountdown")),
                                DeactivateSoundFormId =
                                    NullIfZero(v.UInt32("SoundDisable")),
                                DefaultWeaponSourceFormId =
                                    NullIfZero(v.UInt32("DefaultWeaponSource")),
                                RotationX = v.Float("RotationX"),
                                RotationY = v.Float("RotationY"),
                                RotationZ = v.Float("RotationZ"),
                                BounceMultiplier = v.Float("BouncyMult")
                            };
                        }

                        break;
                    }
                }
            }

            if (projPhysics != null)
            {
                projDataLookup[projRecord.FormId] = projPhysics with
                {
                    ModelPath = modelPath ?? projPhysics.ModelPath,
                    FullName = fullName ?? projPhysics.FullName
                };
            }
        }

        return projDataLookup;
    }

    /// <summary>
    ///     Enrich weapon records with projectile physics data (gravity, speed, range,
    ///     explosion, in-flight sounds) read from the BGSProjectile runtime struct.
    /// </summary>
    internal void EnrichWeaponsWithProjectileData(List<WeaponRecord> weapons)
    {
        if (context.RuntimeReader == null || weapons.Count == 0)
        {
            return;
        }

        // Build: projectile FormID -> TesFormOffset (from runtime EditorID hash table)
        var projectileEntries = new Dictionary<uint, RuntimeEditorIdEntry>();
        foreach (var entry in context.ScanResult.RuntimeEditorIds)
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
        for (var i = 0; i < weapons.Count; i++)
        {
            var weapon = weapons[i];
            if (!weapon.ProjectileFormId.HasValue)
            {
                continue;
            }

            if (!projectileEntries.TryGetValue(weapon.ProjectileFormId.Value, out var projEntry))
            {
                continue;
            }

            var projData = context.RuntimeReader.ReadProjectilePhysics(
                projEntry.TesFormOffset!.Value, projEntry.FormId);

            if (projData != null)
            {
                weapons[i] = weapon with { ProjectileData = projData };
                enrichedCount++;
            }
        }

        if (enrichedCount > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Enriched {enrichedCount}/{weapons.Count} weapons with projectile physics " +
                $"({projectileEntries.Count} projectiles in hash table)");
        }
    }

    private static uint? NullIfZero(uint value)
    {
        return value == 0 ? null : value;
    }
}
