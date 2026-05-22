using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;

/// <summary>
///     Encodes a <see cref="ProjectileRecord" /> (PROJ) as PC-format subrecord bytes.
///     New-record-only path: override emission is a no-op (master ESM bytes retained verbatim).
///     fopdoc canonical order: EDID, OBND?, FULL?, MODL?, DATA, VNAM?.
///     DATA layout (84 bytes per PDB BGSProjectileData):
///     uint16 Flags(0) + uint16 ProjectileType(2) + float Gravity(4) + float Speed(8) +
///     float Range(12) + FormID Light(16) + FormID MuzzleFlashLight(20) + float TracerChance(24) +
///     float ExplosionProximity(28) + float ExplosionTimer(32) + FormID Explosion(36) +
///     FormID Sound(40) + float MuzzleFlashDuration(44) + float FadeDuration(48) +
///     float ImpactForce(52) + FormID CountdownSound(56) + FormID DeactivateSound(60) +
///     FormID DefaultWeaponSource(64) + float RotationX(68) + float RotationY(72) +
///     float RotationZ(76) + float BounceMultiplier(80).
/// </summary>
public sealed class ProjEncoder : IRecordEncoder
{
    // Schema collapses Flags (ushort) + ProjectileType (ushort) into one UInt32 "FlagsAndType"
    // field at offset 0. Combine via shift; the LE encoding matches the prior two-WriteUInt16
    // pattern byte-for-byte.
    private static readonly Dictionary<string, Func<ProjectileRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["FlagsAndType"] = m => (uint)m.Flags | ((uint)m.ProjectileType << 16),
        ["Gravity"] = m => m.Gravity,
        ["Speed"] = m => m.Speed,
        ["Range"] = m => m.Range,
        ["Light"] = m => m.Light,
        ["MuzzleFlashLight"] = m => m.MuzzleFlashLight,
        ["TracerChance"] = m => m.TracerChance,
        ["ExplosionAltTriggerProximity"] = m => m.ExplosionProximity,
        ["ExplosionAltTriggerTimer"] = m => m.ExplosionTimer,
        ["Explosion"] = m => m.Explosion,
        ["Sound"] = m => m.Sound,
        ["MuzzleFlashDuration"] = m => m.MuzzleFlashDuration,
        ["FadeDuration"] = m => m.FadeDuration,
        ["ImpactForce"] = m => m.ImpactForce,
        ["SoundCountdown"] = m => m.CountdownSound,
        ["SoundDisable"] = m => m.DeactivateSound,
        ["DefaultWeaponSource"] = m => m.DefaultWeaponSource,
        ["RotationX"] = m => m.RotationX,
        ["RotationY"] = m => m.RotationY,
        ["RotationZ"] = m => m.RotationZ,
        ["BouncyMult"] = m => m.BounceMultiplier,
    };

    public string RecordType => "PROJ";
    public Type ModelType => typeof(ProjectileRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(ProjectileRecord proj)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(proj.EditorId))
        {
            warnings.Add($"New PROJ 0x{proj.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", proj.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(proj.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", proj.FullName));
        }

        if (!string.IsNullOrEmpty(proj.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", proj.ModelPath));
        }

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "PROJ", 84, proj, DataExtractors));

        if (proj.SoundLevel != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("VNAM", proj.SoundLevel));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
