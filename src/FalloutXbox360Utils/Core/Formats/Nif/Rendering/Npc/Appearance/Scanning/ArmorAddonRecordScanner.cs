using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

internal static class ArmorAddonRecordScanner
{
    internal static ArmaAddonScanEntry? Process(
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
        string? maleModel = null;
        string? femaleModel = null;
        uint bipedFlags = 0;

        foreach (var subrecord in subrecords)
        {
            switch (subrecord.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "MODL":
                    maleModel = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "MOD2":
                    femaleModel = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "BMDT" when subrecord.Data.Length >= 8:
                {
                    var fields = SubrecordDataReader.ReadFields(
                        "BMDT",
                        null,
                        subrecord.Data,
                        bigEndian);
                    if (fields.Count > 0)
                    {
                        bipedFlags = SubrecordDataReader.GetUInt32(fields, "BipedFlags");
                    }

                    break;
                }
            }
        }

        if (bipedFlags == 0 || (maleModel == null && femaleModel == null))
        {
            return null;
        }

        return new ArmaAddonScanEntry
        {
            EditorId = editorId,
            BipedFlags = bipedFlags,
            MaleModelPath = maleModel,
            FemaleModelPath = femaleModel
        };
    }
}
