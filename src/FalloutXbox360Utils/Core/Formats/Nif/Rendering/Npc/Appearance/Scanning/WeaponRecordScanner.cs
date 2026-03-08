using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
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
        var weaponType = WeaponType.HandToHand;
        short damage = 0;

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
                case "DNAM" when subrecord.Data.Length >= 1:
                {
                    var rawWeaponType = subrecord.Data[0];
                    weaponType = rawWeaponType <= 11
                        ? (WeaponType)rawWeaponType
                        : WeaponType.HandToHand;
                    break;
                }
                case "DATA" when subrecord.Data.Length >= 14:
                    damage = (short)BinaryUtils.ReadUInt16(
                        subrecord.Data,
                        12,
                        bigEndian);
                    break;
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
            WeaponType = weaponType,
            Damage = damage
        };
    }
}
