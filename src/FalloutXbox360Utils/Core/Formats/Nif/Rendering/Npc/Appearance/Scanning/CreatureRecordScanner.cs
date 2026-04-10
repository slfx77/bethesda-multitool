using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

internal static class CreatureRecordScanner
{
    internal static CreatureScanEntry? Process(
        byte[] esmData,
        bool bigEndian,
        AnalyzerRecordInfo record)
    {
        var recordData = NpcRecordDataReader.ReadRecordData(esmData, bigEndian, record);
        if (recordData == null)
        {
            return null;
        }

        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);
        string? editorId = null;
        string? fullName = null;
        string? skeletonPath = null;
        string[]? bodyModelPaths = null;
        string[]? animationPaths = null;
        var inventoryItems = new List<InventoryItem>();
        byte creatureType = 0;
        uint? combatStyleFormId = null;
        byte? combatSkill = null;
        byte? strength = null;

        foreach (var subrecord in subrecords)
        {
            switch (subrecord.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "FULL":
                    fullName = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "MODL":
                    skeletonPath = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "NIFZ" when subrecord.Data.Length > 0:
                    bodyModelPaths = ParseNullSeparatedStrings(subrecord.Data);
                    break;
                case "KFFZ" when subrecord.Data.Length > 0:
                    animationPaths = ParseNullSeparatedStrings(subrecord.Data);
                    break;
                case "CNTO" when subrecord.Data.Length >= 8:
                    inventoryItems.Add(new InventoryItem(
                        BinaryUtils.ReadUInt32(subrecord.Data, 0, bigEndian),
                        BinaryUtils.ReadInt32(subrecord.Data, 4, bigEndian)));
                    break;
                case "DATA" when subrecord.Data.Length >= 1:
                    creatureType = subrecord.Data[0];
                    // FNV CREA DATA: [0]=type, [1]=combatSkill, [2]=magicSkill, [3]=stealthSkill,
                    //                [4..7]=int32 health, [8..9]=short damage, [10..16]=7 SPECIAL bytes
                    if (subrecord.Data.Length >= 4)
                    {
                        combatSkill = subrecord.Data[1];
                    }

                    if (subrecord.Data.Length >= 17)
                    {
                        strength = subrecord.Data[10];
                    }

                    break;
                case "ZNAM" when subrecord.Data.Length == 4:
                    combatStyleFormId = BinaryUtils.ReadUInt32(subrecord.Data, 0, bigEndian);
                    break;
            }
        }

        if (skeletonPath == null && bodyModelPaths == null && editorId == null && fullName == null)
        {
            return null;
        }

        return new CreatureScanEntry(
            editorId, fullName, skeletonPath, bodyModelPaths, animationPaths,
            inventoryItems.Count > 0 ? inventoryItems : null, creatureType)
        {
            CombatStyleFormId = combatStyleFormId,
            CombatSkill = combatSkill,
            Strength = strength
        };
    }

    /// <summary>
    ///     Parses NIFZ subrecord: null-separated list of model path strings.
    /// </summary>
    private static string[] ParseNullSeparatedStrings(byte[] data)
    {
        var paths = new List<string>();
        var start = 0;

        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == 0)
            {
                if (i > start)
                {
                    paths.Add(Encoding.ASCII.GetString(data, start, i - start));
                }

                start = i + 1;
            }
        }

        // Handle trailing string without null terminator
        if (start < data.Length)
        {
            paths.Add(Encoding.ASCII.GetString(data, start, data.Length - start));
        }

        return paths.ToArray();
    }
}
