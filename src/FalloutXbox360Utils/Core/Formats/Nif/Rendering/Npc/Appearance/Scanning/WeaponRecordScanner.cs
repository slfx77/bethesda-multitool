using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

internal static class WeaponRecordScanner
{
    internal static WeapScanEntry? Process(
        byte[] esmData,
        bool bigEndian,
        AnalyzerRecordInfo record)
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
                    var fields = SubrecordDataReader.ReadFields(
                        "DNAM",
                        "WEAP",
                        subrecord.Data,
                        bigEndian);
                    if (fields.Count > 0)
                    {
                        var rawWeaponType = SubrecordDataReader.GetByte(fields, "WeaponType");
                        weaponType = Enum.IsDefined(typeof(WeaponType), rawWeaponType)
                            ? (WeaponType)rawWeaponType
                            : WeaponType.HandToHandMelee;
                        flags = SubrecordDataReader.GetByte(fields, "Flags");
                        handGripAnim = SubrecordDataReader.GetByte(fields, "HandGripAnim");
                        spread = SubrecordDataReader.GetFloat(fields, "Spread");
                        minRange = SubrecordDataReader.GetFloat(fields, "MinRange");
                        maxRange = SubrecordDataReader.GetFloat(fields, "MaxRange");
                        flagsEx = SubrecordDataReader.GetUInt32(fields, "FlagsEx");
                        shotsPerSec = SubrecordDataReader.GetFloat(fields, "ShotsPerSec");
                        skillActorValue = SubrecordDataReader.GetUInt32(fields, "Skill");
                        strengthRequirement = SubrecordDataReader.GetUInt32(fields, "StrengthRequirement");
                        skillRequirement = SubrecordDataReader.GetUInt32(fields, "SkillRequirement");
                    }

                    break;
                }
                case "DATA" when subrecord.Data.Length >= 14:
                {
                    var fields = SubrecordDataReader.ReadFields(
                        "DATA",
                        "WEAP",
                        subrecord.Data,
                        bigEndian);
                    if (fields.Count > 0)
                    {
                        health = SubrecordDataReader.GetInt32(fields, "Health");
                        damage = SubrecordDataReader.GetInt16(fields, "Damage");
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
            EmbeddedWeaponNode = embeddedWeaponNode
        };
    }
}
