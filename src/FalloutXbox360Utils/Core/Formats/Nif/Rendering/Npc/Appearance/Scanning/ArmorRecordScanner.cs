using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

internal static class ArmorRecordScanner
{
    internal static ArmoScanEntry? Process(
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
        string? maleBipedModel = null;
        string? femaleBipedModel = null;
        uint bipedFlags = 0;

        foreach (var subrecord in subrecords)
        {
            switch (subrecord.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "MODL":
                    maleBipedModel = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "MOD3":
                    femaleBipedModel = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "BMDT" when subrecord.Data.Length >= 4:
                    bipedFlags = BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        0,
                        bigEndian);
                    break;
            }
        }

        if (bipedFlags == 0 || (maleBipedModel == null && femaleBipedModel == null))
        {
            return null;
        }

        return new ArmoScanEntry
        {
            EditorId = editorId,
            BipedFlags = bipedFlags,
            MaleBipedModelPath = maleBipedModel,
            FemaleBipedModelPath = femaleBipedModel
        };
    }
}
