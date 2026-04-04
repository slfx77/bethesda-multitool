using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

internal static class RaceRecordScanner
{
    internal static RaceScanEntry? Process(
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
        var inMaleSection = true;
        var inHeadPartsSection = false;
        var inBodyPartsSection = false;

        string? maleHeadModel = null;
        string? femaleHeadModel = null;
        string? maleHeadTexture = null;
        string? femaleHeadTexture = null;
        string? maleMouthModel = null;
        string? femaleMouthModel = null;
        string? maleLowerTeethModel = null;
        string? femaleLowerTeethModel = null;
        string? maleUpperTeethModel = null;
        string? femaleUpperTeethModel = null;
        string? maleTongueModel = null;
        string? femaleTongueModel = null;
        string? maleEyeLeftModel = null;
        string? femaleEyeLeftModel = null;
        string? maleEyeRightModel = null;
        string? femaleEyeRightModel = null;
        float[]? maleFggs = null;
        float[]? femaleFggs = null;
        float[]? maleFgga = null;
        float[]? femaleFgga = null;
        float[]? maleFgts = null;
        float[]? femaleFgts = null;
        uint? defaultEyesFormId = null;
        uint? olderRaceFormId = null;
        uint? youngerRaceFormId = null;
        string? maleUpperBody = null;
        string? femaleUpperBody = null;
        string? maleLeftHand = null;
        string? femaleLeftHand = null;
        string? maleRightHand = null;
        string? femaleRightHand = null;
        string? maleBodyTexture = null;
        string? femaleBodyTexture = null;
        var currentIndex = -1;

        foreach (var subrecord in subrecords)
        {
            switch (subrecord.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "NAM0" when subrecord.Data.Length == 0:
                    inHeadPartsSection = true;
                    inBodyPartsSection = false;
                    break;
                case "NAM1" when subrecord.Data.Length == 0:
                    inHeadPartsSection = false;
                    inBodyPartsSection = true;
                    break;
                case "MNAM" when subrecord.Data.Length == 0:
                    inMaleSection = true;
                    currentIndex = -1;
                    break;
                case "FNAM" when subrecord.Data.Length == 0:
                    inMaleSection = false;
                    currentIndex = -1;
                    break;
                case "INDX"
                    when subrecord.Data.Length == 4 &&
                         (inHeadPartsSection || inBodyPartsSection):
                    currentIndex = (int)BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        0,
                        bigEndian);
                    break;
                case "MODL" when inHeadPartsSection && currentIndex == 0:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        ref maleHeadModel,
                        ref femaleHeadModel);
                    break;
                case "MODL" when inHeadPartsSection && currentIndex == 2:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        ref maleMouthModel,
                        ref femaleMouthModel);
                    break;
                case "MODL" when inHeadPartsSection && currentIndex == 3:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        ref maleLowerTeethModel,
                        ref femaleLowerTeethModel);
                    break;
                case "MODL" when inHeadPartsSection && currentIndex == 4:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        ref maleUpperTeethModel,
                        ref femaleUpperTeethModel);
                    break;
                case "MODL" when inHeadPartsSection && currentIndex == 5:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        ref maleTongueModel,
                        ref femaleTongueModel);
                    break;
                case "MODL" when inHeadPartsSection && currentIndex == 6:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        ref maleEyeLeftModel,
                        ref femaleEyeLeftModel);
                    break;
                case "MODL" when inHeadPartsSection && currentIndex == 7:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        ref maleEyeRightModel,
                        ref femaleEyeRightModel);
                    break;
                case "ICON" when inHeadPartsSection && currentIndex == 0:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        ref maleHeadTexture,
                        ref femaleHeadTexture);
                    break;
                case "MODL" when inBodyPartsSection && currentIndex == 0:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        ref maleUpperBody,
                        ref femaleUpperBody);
                    break;
                case "MODL" when inBodyPartsSection && currentIndex == 1:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        ref maleLeftHand,
                        ref femaleLeftHand);
                    break;
                case "MODL" when inBodyPartsSection && currentIndex == 2:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        ref maleRightHand,
                        ref femaleRightHand);
                    break;
                case "ICON" when inBodyPartsSection && currentIndex == 0:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        ref maleBodyTexture,
                        ref femaleBodyTexture);
                    break;
                case "ENAM" when subrecord.Data.Length >= 4:
                    defaultEyesFormId ??= BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        0,
                        bigEndian);
                    break;
                case "ONAM" when subrecord.Data.Length >= 4:
                    olderRaceFormId ??= BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        0,
                        bigEndian);
                    break;
                case "YNAM" when subrecord.Data.Length >= 4:
                    youngerRaceFormId ??= BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        0,
                        bigEndian);
                    break;
                case "FGGS" when subrecord.Data.Length == 200:
                    AssignCoefficients(
                        subrecord.Data,
                        bigEndian,
                        inMaleSection,
                        ref maleFggs,
                        ref femaleFggs);
                    break;
                case "FGGA" when subrecord.Data.Length == 120:
                    AssignCoefficients(
                        subrecord.Data,
                        bigEndian,
                        inMaleSection,
                        ref maleFgga,
                        ref femaleFgga);
                    break;
                case "FGTS" when subrecord.Data.Length == 200:
                    AssignCoefficients(
                        subrecord.Data,
                        bigEndian,
                        inMaleSection,
                        ref maleFgts,
                        ref femaleFgts);
                    break;
            }
        }

        return new RaceScanEntry
        {
            EditorId = editorId,
            OlderRaceFormId = olderRaceFormId,
            YoungerRaceFormId = youngerRaceFormId,
            DefaultEyesFormId = defaultEyesFormId,
            MaleHeadModelPath = maleHeadModel,
            FemaleHeadModelPath = femaleHeadModel,
            MaleHeadTexturePath = maleHeadTexture,
            FemaleHeadTexturePath = femaleHeadTexture,
            MaleMouthModelPath = maleMouthModel,
            FemaleMouthModelPath = femaleMouthModel,
            MaleLowerTeethModelPath = maleLowerTeethModel,
            FemaleLowerTeethModelPath = femaleLowerTeethModel,
            MaleUpperTeethModelPath = maleUpperTeethModel,
            FemaleUpperTeethModelPath = femaleUpperTeethModel,
            MaleTongueModelPath = maleTongueModel,
            FemaleTongueModelPath = femaleTongueModel,
            MaleEyeLeftModelPath = maleEyeLeftModel,
            FemaleEyeLeftModelPath = femaleEyeLeftModel,
            MaleEyeRightModelPath = maleEyeRightModel,
            FemaleEyeRightModelPath = femaleEyeRightModel,
            MaleFaceGenSymmetric = maleFggs,
            FemaleFaceGenSymmetric = femaleFggs,
            MaleFaceGenAsymmetric = maleFgga,
            FemaleFaceGenAsymmetric = femaleFgga,
            MaleFaceGenTexture = maleFgts,
            FemaleFaceGenTexture = femaleFgts,
            MaleUpperBodyPath = maleUpperBody,
            FemaleUpperBodyPath = femaleUpperBody,
            MaleLeftHandPath = maleLeftHand,
            FemaleLeftHandPath = femaleLeftHand,
            MaleRightHandPath = maleRightHand,
            FemaleRightHandPath = femaleRightHand,
            MaleBodyTexturePath = maleBodyTexture,
            FemaleBodyTexturePath = femaleBodyTexture
        };
    }

    private static void AssignPath(
        string? path,
        bool inMaleSection,
        ref string? maleValue,
        ref string? femaleValue)
    {
        if (path == null)
        {
            return;
        }

        if (inMaleSection)
        {
            maleValue = path;
        }
        else
        {
            femaleValue = path;
        }
    }

    private static void AssignCoefficients(
        byte[] data,
        bool bigEndian,
        bool inMaleSection,
        ref float[]? maleValue,
        ref float[]? femaleValue)
    {
        var coefficients = NpcRecordDataReader.ReadFloatArray(data, bigEndian);
        if (inMaleSection)
        {
            maleValue = coefficients;
        }
        else
        {
            femaleValue = coefficients;
        }
    }
}
