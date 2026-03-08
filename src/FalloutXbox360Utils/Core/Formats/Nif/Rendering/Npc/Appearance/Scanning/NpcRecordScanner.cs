using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

internal static class NpcRecordScanner
{
    internal static NpcScanEntry? Process(
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
        string? fullName = null;
        uint? raceFormId = null;
        uint? hairFormId = null;
        uint? eyesFormId = null;
        uint? hairColor = null;
        bool isFemale = false;
        ushort templateFlags = 0;
        uint? templateFormId = null;
        float[]? faceGenSymmetric = null;
        float[]? faceGenAsymmetric = null;
        float[]? faceGenTexture = null;
        var headPartFormIds = new List<uint>();
        var inventoryFormIds = new List<uint>();

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
                case "RNAM" when subrecord.Data.Length == 4:
                    raceFormId = BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        0,
                        bigEndian);
                    break;
                case "HNAM" when subrecord.Data.Length == 4:
                    hairFormId = BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        0,
                        bigEndian);
                    break;
                case "ENAM" when subrecord.Data.Length == 4:
                    eyesFormId = BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        0,
                        bigEndian);
                    break;
                case "PNAM" when subrecord.Data.Length == 4:
                    headPartFormIds.Add(
                        BinaryUtils.ReadUInt32(subrecord.Data, 0, bigEndian));
                    break;
                case "HCLR" when subrecord.Data.Length == 4:
                    hairColor = BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        0,
                        bigEndian);
                    break;
                case "ACBS" when subrecord.Data.Length >= 24:
                {
                    var flags = BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        0,
                        bigEndian);
                    isFemale = (flags & 1) != 0;
                    templateFlags = BinaryUtils.ReadUInt16(
                        subrecord.Data,
                        22,
                        bigEndian);
                    break;
                }
                case "ACBS" when subrecord.Data.Length >= 4:
                {
                    var flags = BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        0,
                        bigEndian);
                    isFemale = (flags & 1) != 0;
                    break;
                }
                case "CNTO" when subrecord.Data.Length >= 8:
                    inventoryFormIds.Add(
                        BinaryUtils.ReadUInt32(subrecord.Data, 0, bigEndian));
                    break;
                case "TPLT" when subrecord.Data.Length == 4:
                    templateFormId = BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        0,
                        bigEndian);
                    break;
                case "FGGS" when subrecord.Data.Length >= 4:
                    faceGenSymmetric = NpcRecordDataReader.ReadFloatArray(
                        subrecord.Data,
                        bigEndian);
                    break;
                case "FGGA" when subrecord.Data.Length >= 4:
                    faceGenAsymmetric = NpcRecordDataReader.ReadFloatArray(
                        subrecord.Data,
                        bigEndian);
                    break;
                case "FGTS" when subrecord.Data.Length >= 4:
                    faceGenTexture = NpcRecordDataReader.ReadFloatArray(
                        subrecord.Data,
                        bigEndian);
                    break;
            }
        }

        return new NpcScanEntry
        {
            EditorId = editorId,
            FullName = fullName,
            RaceFormId = raceFormId,
            HairFormId = hairFormId,
            EyesFormId = eyesFormId,
            IsFemale = isFemale,
            FaceGenSymmetric = faceGenSymmetric,
            FaceGenAsymmetric = faceGenAsymmetric,
            FaceGenTexture = faceGenTexture,
            HeadPartFormIds = headPartFormIds.Count > 0 ? headPartFormIds : null,
            HairColor = hairColor,
            InventoryFormIds = inventoryFormIds.Count > 0 ? inventoryFormIds : null,
            TemplateFormId = templateFormId,
            TemplateFlags = templateFlags
        };
    }
}
