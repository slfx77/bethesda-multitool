using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

/// <summary>Parses an armor or clothing record into a scan entry of its model references and biped slots.</summary>
internal static class ArmorRecordScanner
{
    private const int OblivionArmorDataSize = 14;

    internal static ArmoScanEntry? Process(
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
        string? maleBipedModel = null;
        string? femaleBipedModel = null;
        uint bipedFlags = 0;
        byte generalFlags = 0;
        ushort baseArmorRating = 0;
        uint? bipedModelListFormId = null;
        var isClothing = record.Signature == "CLOT";

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
                case "BMDT" when game == BethesdaGame.Oblivion && subrecord.Data.Length >= 3:
                    // TES4's four-byte BMDT is uint16 slots + uint8 general flags + padding.
                    // Reading it as Fallout's uint32 slot mask blends Heavy/Hide flags into the
                    // slot value (Mazoga's shield became 0x00802000 instead of 0x2000).
                    bipedFlags = BinaryUtils.ReadUInt16(
                        subrecord.Data,
                        0,
                        bigEndian);
                    generalFlags = subrecord.Data[2];
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
                case "DATA" when game == BethesdaGame.Oblivion && !isClothing &&
                                      subrecord.Data.Length >= OblivionArmorDataSize:
                    baseArmorRating = BinaryUtils.ReadUInt16(
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
            IsClothing = isClothing,
            BaseArmorRating = baseArmorRating,
            BipedFlags = bipedFlags,
            GeneralFlags = generalFlags,
            MaleBipedModelPath = maleBipedModel,
            FemaleBipedModelPath = femaleBipedModel,
            BipedModelListFormId = bipedModelListFormId
        };
    }
}
