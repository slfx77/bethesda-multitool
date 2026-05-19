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

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(proj)));

        if (proj.SoundLevel != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("VNAM", proj.SoundLevel));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(ProjectileRecord proj)
    {
        var data = new byte[84];
        SubrecordEncoder.WriteUInt16(data, 0, proj.Flags);
        SubrecordEncoder.WriteUInt16(data, 2, proj.ProjectileType);
        SubrecordEncoder.WriteFloat(data, 4, proj.Gravity);
        SubrecordEncoder.WriteFloat(data, 8, proj.Speed);
        SubrecordEncoder.WriteFloat(data, 12, proj.Range);
        SubrecordEncoder.WriteFormId(data, 16, proj.Light);
        SubrecordEncoder.WriteFormId(data, 20, proj.MuzzleFlashLight);
        SubrecordEncoder.WriteFloat(data, 24, proj.TracerChance);
        SubrecordEncoder.WriteFloat(data, 28, proj.ExplosionProximity);
        SubrecordEncoder.WriteFloat(data, 32, proj.ExplosionTimer);
        SubrecordEncoder.WriteFormId(data, 36, proj.Explosion);
        SubrecordEncoder.WriteFormId(data, 40, proj.Sound);
        SubrecordEncoder.WriteFloat(data, 44, proj.MuzzleFlashDuration);
        SubrecordEncoder.WriteFloat(data, 48, proj.FadeDuration);
        SubrecordEncoder.WriteFloat(data, 52, proj.ImpactForce);
        SubrecordEncoder.WriteFormId(data, 56, proj.CountdownSound);
        SubrecordEncoder.WriteFormId(data, 60, proj.DeactivateSound);
        SubrecordEncoder.WriteFormId(data, 64, proj.DefaultWeaponSource);
        SubrecordEncoder.WriteFloat(data, 68, proj.RotationX);
        SubrecordEncoder.WriteFloat(data, 72, proj.RotationY);
        SubrecordEncoder.WriteFloat(data, 76, proj.RotationZ);
        SubrecordEncoder.WriteFloat(data, 80, proj.BounceMultiplier);
        return data;
    }
}
