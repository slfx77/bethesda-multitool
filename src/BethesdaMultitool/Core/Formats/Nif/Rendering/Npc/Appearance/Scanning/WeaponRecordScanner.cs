using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

/// <summary>Parses a weapon (WEAP) record into a <see cref="WeapScanEntry" /> (mesh path, weapon type, flags).</summary>
internal static class WeaponRecordScanner
{
    internal static WeapScanEntry? Process(
        byte[] esmData,
        bool bigEndian,
        AnalyzerRecordInfo record,
        BethesdaGame game = BethesdaGame.Unknown)
    {
        var recordData = NpcRecordDataReader.ReadRecordData(
            esmData,
            bigEndian,
            record);
        if (recordData == null)
        {
            return null;
        }

        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);
        string? editorId = null;
        string? modelPath = null;
        string? mod2ModelPath = null;
        string? embeddedWeaponNode = null;
        var weaponType = WeaponType.HandToHandMelee;
        short damage = 0;
        var health = 0;
        var shotsPerSec = 1f;
        var spread = 0f;
        var minRange = 0f;
        var maxRange = 0f;
        byte flags = 0;
        uint flagsEx = 0;
        uint? ammoFormId = null;
        uint skillActorValue = 0;
        uint skillRequirement = 0;
        uint strengthRequirement = 0;
        byte handGripAnim = 0xff;
        string? attachmentPoseKfPath = null;

        foreach (var subrecord in subrecords)
        {
            switch (subrecord.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "MODL":
                    modelPath = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "MOD2":
                    mod2ModelPath = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "NNAM":
                    embeddedWeaponNode = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "ENAM" when subrecord.Data.Length == 4:
                    ammoFormId = BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        0,
                        bigEndian);
                    if (ammoFormId == 0)
                    {
                        ammoFormId = null;
                    }

                    break;
                case "DNAM" when subrecord.Data.Length >= 64:
                {
                    if (SubrecordSchemaView.TryRead("DNAM", "WEAP", subrecord.Data, bigEndian) is { } v)
                    {
                        var rawWeaponType = v.Byte("WeaponType");
                        weaponType = Enum.IsDefined(typeof(WeaponType), rawWeaponType)
                            ? (WeaponType)rawWeaponType
                            : WeaponType.HandToHandMelee;
                        flags = v.Byte("Flags");
                        handGripAnim = v.Byte("HandGripAnim");
                        spread = v.Float("Spread");
                        minRange = v.Float("MinRange");
                        maxRange = v.Float("MaxRange");
                        flagsEx = v.UInt32("FlagsEx");
                        shotsPerSec = v.Float("ShotsPerSec");
                        skillActorValue = v.UInt32("Skill");
                        strengthRequirement = v.UInt32("StrengthRequirement");
                        skillRequirement = v.UInt32("SkillRequirement");
                    }

                    break;
                }
                case "DATA" when game == BethesdaGame.Oblivion && subrecord.Data.Length >= 1:
                {
                    // Oblivion stores its six-value weapon animation type at DATA byte 0;
                    // Fallout 3/New Vegas moved the unrelated 0..13 animation enum to DNAM.
                    // Normalize the selection category while retaining the retail TES4 pose family.
                    var rawWeaponType = subrecord.Data[0];
                    weaponType = ResolveOblivionWeaponType(rawWeaponType);
                    attachmentPoseKfPath = ResolveOblivionAttachmentPose(rawWeaponType);

                    // TES4 DATA is 30 bytes: type/pad, speed, reach, ignore-resistance,
                    // value, health, weight, damage. Preserve selection inputs where present.
                    if (subrecord.Data.Length >= 30)
                    {
                        shotsPerSec = MathF.Max(BinaryUtils.ReadFloat(subrecord.Data, 4, bigEndian), 0.1f);
                        health = (int)Math.Min(
                            BinaryUtils.ReadUInt32(subrecord.Data, 20, bigEndian),
                            (uint)int.MaxValue);
                        damage = (short)Math.Min(
                            BinaryUtils.ReadUInt16(subrecord.Data, 28, bigEndian),
                            (ushort)short.MaxValue);
                    }

                    break;
                }
                case "DATA" when subrecord.Data.Length >= 14:
                {
                    if (SubrecordSchemaView.TryRead("DATA", "WEAP", subrecord.Data, bigEndian) is { } v)
                    {
                        health = v.Int32("Health");
                        damage = v.Int16("Damage");
                    }

                    break;
                }
            }
        }

        if (modelPath == null)
        {
            return null;
        }

        return new WeapScanEntry
        {
            EditorId = editorId,
            ModelPath = modelPath,
            Mod2ModelPath = mod2ModelPath,
            WeaponType = weaponType,
            Damage = damage,
            Health = health,
            ShotsPerSec = shotsPerSec,
            Spread = spread,
            MinRange = minRange,
            MaxRange = maxRange,
            Flags = flags,
            FlagsEx = flagsEx,
            AmmoFormId = ammoFormId,
            SkillActorValue = skillActorValue,
            SkillRequirement = skillRequirement,
            StrengthRequirement = strengthRequirement,
            HandGripAnim = handGripAnim,
            EmbeddedWeaponNode = embeddedWeaponNode,
            AttachmentPoseKfPath = attachmentPoseKfPath
        };
    }

    private static WeaponType ResolveOblivionWeaponType(byte rawWeaponType)
    {
        return rawWeaponType switch
        {
            0 or 2 => WeaponType.OneHandMelee, // blade/blunt one hand
            1 or 3 => WeaponType.TwoHandMelee, // blade/blunt two hand
            4 => WeaponType.TwoHandHandle, // staff
            5 => WeaponType.TwoHandRifle, // bow: ranged selection category
            _ => WeaponType.OneHandMelee
        };
    }

    private static string? ResolveOblivionAttachmentPose(byte rawWeaponType)
    {
        return rawWeaponType switch
        {
            0 or 2 => "onehandidle.kf",
            1 or 3 => "twohandidle.kf",
            4 => "staffidle.kf",
            5 => "bowidle.kf",
            _ => null
        };
    }
}
