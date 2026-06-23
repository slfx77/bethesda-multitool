using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

/// <summary>Parses an AI-package (PACK) record into a scan entry of the weapon/equipment hints used to pick an NPC's displayed gear.</summary>
internal static class PackageRecordScanner
{
    internal static PackageScanEntry? Process(
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
        byte type = 0;
        uint generalFlags = 0;
        uint? useWeaponFormId = null;

        foreach (var subrecord in subrecords)
        {
            switch (subrecord.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "PKDT" when subrecord.Data.Length >= 10:
                    generalFlags = BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        0,
                        bigEndian);
                    type = subrecord.Data[4];
                    break;
                case "PKW3" when subrecord.Data.Length >= 24:
                    useWeaponFormId = BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        20,
                        bigEndian);
                    if (useWeaponFormId == 0)
                    {
                        useWeaponFormId = null;
                    }

                    break;
            }
        }

        if (generalFlags == 0 && type == 0 && useWeaponFormId == null)
        {
            return null;
        }

        return new PackageScanEntry
        {
            EditorId = editorId,
            Type = type,
            GeneralFlags = generalFlags,
            UseWeaponFormId = useWeaponFormId
        };
    }
}

