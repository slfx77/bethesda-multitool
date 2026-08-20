using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

/// <summary>Parses an armor (ARMO) record into a scan entry of its add-on references and biped slots.</summary>
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
        byte generalFlags = 0;
        uint? bipedModelListFormId = null;

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
                    if (subrecord.Data.Length >= 5)
                    {
                        generalFlags = subrecord.Data[4];
                    }

                    break;
                case "BIPL" when subrecord.Data.Length == 4:
                    bipedModelListFormId = BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        0,
                        bigEndian);
                    break;
            }
        }

        if (bipedFlags == 0 ||
            (maleBipedModel == null && femaleBipedModel == null && !bipedModelListFormId.HasValue))
        {
            return null;
        }

        return new ArmoScanEntry
        {
            EditorId = editorId,
            BipedFlags = bipedFlags,
            GeneralFlags = generalFlags,
            MaleBipedModelPath = maleBipedModel,
            FemaleBipedModelPath = femaleBipedModel,
            BipedModelListFormId = bipedModelListFormId
        };
    }
}
