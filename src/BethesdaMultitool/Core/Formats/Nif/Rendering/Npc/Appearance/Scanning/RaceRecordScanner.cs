using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

/// <summary>
///     Parses a race (RACE) record into a <see cref="RaceScanEntry" /> (head parts, default FaceGen coefficients,
///     body data).
/// </summary>
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

        // Oblivion's 20-byte TES4 RACE layout predates the gender-section layout used by
        // Fallout 3/New Vegas.  Its head indices are shared except for two adjacent ear
        // entries: 0 head, 1 male ear, 2 female ear, 3 mouth, 4 lower teeth, 5 upper
        // teeth, 6 tongue, 7 left eye, 8 right eye.  Treating those as the later 0/2..7
        // layout shifts every visible face part and was the source of the hollow-eye
        // Oblivion NPC render.
        var usesTes4HeadPartLayout = record.RecordHeaderSize == 20;
        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);
        string? editorId = null;
        var inMaleSection = true;
        var inHeadPartsSection = false;
        var inBodyPartsSection = false;

        string? maleHeadModel = null;
        string? femaleHeadModel = null;
        string? maleHeadTexture = null;
        string? femaleHeadTexture = null;
        string? maleEarModel = null;
        string? femaleEarModel = null;
        string? maleEarTexture = null;
        string? femaleEarTexture = null;
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
        string? maleLowerBody = null;
        string? femaleLowerBody = null;
        string? maleHand = null;
        string? femaleHand = null;
        string? maleFoot = null;
        string? femaleFoot = null;
        string? maleLeftHand = null;
        string? femaleLeftHand = null;
        string? maleRightHand = null;
        string? femaleRightHand = null;
        string? maleBodyTexture = null;
        string? femaleBodyTexture = null;
        string? maleLowerBodyTexture = null;
        string? femaleLowerBodyTexture = null;
        string? maleHandTexture = null;
        string? femaleHandTexture = null;
        string? maleFootTexture = null;
        string? femaleFootTexture = null;
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
                        usesTes4HeadPartLayout,
                        ref maleHeadModel,
                        ref femaleHeadModel);
                    break;
                case "MODL" when inHeadPartsSection &&
                                  ((usesTes4HeadPartLayout && currentIndex == 1) ||
                                   (!usesTes4HeadPartLayout && currentIndex == 1)):
                {
                    var path = EsmRecordParser.GetSubrecordString(subrecord);
                    if (usesTes4HeadPartLayout)
                    {
                        maleEarModel = path;
                    }
                    else
                    {
                        AssignPath(path, inMaleSection, false, ref maleEarModel, ref femaleEarModel);
                    }

                    break;
                }
                case "MODL" when inHeadPartsSection && usesTes4HeadPartLayout && currentIndex == 2:
                    femaleEarModel = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "MODL" when inHeadPartsSection &&
                                  currentIndex == (usesTes4HeadPartLayout ? 3 : 2):
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        usesTes4HeadPartLayout,
                        ref maleMouthModel,
                        ref femaleMouthModel);
                    break;
                case "MODL" when inHeadPartsSection &&
                                  currentIndex == (usesTes4HeadPartLayout ? 4 : 3):
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        usesTes4HeadPartLayout,
                        ref maleLowerTeethModel,
                        ref femaleLowerTeethModel);
                    break;
                case "MODL" when inHeadPartsSection &&
                                  currentIndex == (usesTes4HeadPartLayout ? 5 : 4):
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        usesTes4HeadPartLayout,
                        ref maleUpperTeethModel,
                        ref femaleUpperTeethModel);
                    break;
                case "MODL" when inHeadPartsSection &&
                                  currentIndex == (usesTes4HeadPartLayout ? 6 : 5):
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        usesTes4HeadPartLayout,
                        ref maleTongueModel,
                        ref femaleTongueModel);
                    break;
                case "MODL" when inHeadPartsSection &&
                                  currentIndex == (usesTes4HeadPartLayout ? 7 : 6):
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        usesTes4HeadPartLayout,
                        ref maleEyeLeftModel,
                        ref femaleEyeLeftModel);
                    break;
                case "MODL" when inHeadPartsSection &&
                                  currentIndex == (usesTes4HeadPartLayout ? 8 : 7):
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        usesTes4HeadPartLayout,
                        ref maleEyeRightModel,
                        ref femaleEyeRightModel);
                    break;
                case "ICON" when inHeadPartsSection && currentIndex == 0:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        usesTes4HeadPartLayout,
                        ref maleHeadTexture,
                        ref femaleHeadTexture);
                    break;
                case "ICON" when inHeadPartsSection && usesTes4HeadPartLayout && currentIndex == 1:
                    maleEarTexture = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "ICON" when inHeadPartsSection && usesTes4HeadPartLayout && currentIndex == 2:
                    femaleEarTexture = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "ICON" when inHeadPartsSection && !usesTes4HeadPartLayout && currentIndex == 1:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        false,
                        ref maleEarTexture,
                        ref femaleEarTexture);
                    break;
                case "MODL" when inBodyPartsSection && !usesTes4HeadPartLayout && currentIndex == 0:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        false,
                        ref maleUpperBody,
                        ref femaleUpperBody);
                    break;
                case "MODL" when inBodyPartsSection && !usesTes4HeadPartLayout && currentIndex == 1:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        false,
                        ref maleLeftHand,
                        ref femaleLeftHand);
                    break;
                case "MODL" when inBodyPartsSection && !usesTes4HeadPartLayout && currentIndex == 2:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        false,
                        ref maleRightHand,
                        ref femaleRightHand);
                    break;
                case "ICON" when inBodyPartsSection && currentIndex == 0:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        false,
                        ref maleBodyTexture,
                        ref femaleBodyTexture);
                    break;
                case "ICON" when inBodyPartsSection && usesTes4HeadPartLayout && currentIndex == 1:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        false,
                        ref maleLowerBodyTexture,
                        ref femaleLowerBodyTexture);
                    break;
                case "ICON" when inBodyPartsSection && usesTes4HeadPartLayout && currentIndex == 2:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        false,
                        ref maleHandTexture,
                        ref femaleHandTexture);
                    break;
                case "ICON" when inBodyPartsSection && usesTes4HeadPartLayout && currentIndex == 3:
                    AssignPath(
                        EsmRecordParser.GetSubrecordString(subrecord),
                        inMaleSection,
                        false,
                        ref maleFootTexture,
                        ref femaleFootTexture);
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
                        usesTes4HeadPartLayout,
                        ref maleFggs,
                        ref femaleFggs);
                    break;
                case "FGGA" when subrecord.Data.Length == 120:
                    AssignCoefficients(
                        subrecord.Data,
                        bigEndian,
                        inMaleSection,
                        usesTes4HeadPartLayout,
                        ref maleFgga,
                        ref femaleFgga);
                    break;
                case "FGTS" when subrecord.Data.Length == 200:
                    AssignCoefficients(
                        subrecord.Data,
                        bigEndian,
                        inMaleSection,
                        usesTes4HeadPartLayout,
                        ref maleFgts,
                        ref femaleFgts);
                    break;
            }
        }

        if (usesTes4HeadPartLayout)
        {
            // Oblivion RACE records store only per-race body textures. Geometry is
            // selected from the fixed combined TES4 body-part meshes.
            maleUpperBody ??= @"characters\_male\upperbody.nif";
            femaleUpperBody ??= @"characters\_male\femaleupperbody.nif";
            maleLowerBody = @"characters\_male\lowerbody.nif";
            femaleLowerBody = @"characters\_male\femalelowerbody.nif";
            maleHand = @"characters\_male\hand.nif";
            femaleHand = @"characters\_male\femalehand.nif";
            maleFoot = @"characters\_male\foot.nif";
            femaleFoot = @"characters\_male\femalefoot.nif";
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
            MaleEarModelPath = maleEarModel,
            FemaleEarModelPath = femaleEarModel,
            MaleEarTexturePath = maleEarTexture,
            FemaleEarTexturePath = femaleEarTexture,
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
            MaleLowerBodyPath = maleLowerBody,
            FemaleLowerBodyPath = femaleLowerBody,
            MaleHandPath = maleHand,
            FemaleHandPath = femaleHand,
            MaleFootPath = maleFoot,
            FemaleFootPath = femaleFoot,
            MaleLeftHandPath = maleLeftHand,
            FemaleLeftHandPath = femaleLeftHand,
            MaleRightHandPath = maleRightHand,
            FemaleRightHandPath = femaleRightHand,
            MaleBodyTexturePath = maleBodyTexture,
            FemaleBodyTexturePath = femaleBodyTexture,
            MaleLowerBodyTexturePath = maleLowerBodyTexture,
            FemaleLowerBodyTexturePath = femaleLowerBodyTexture,
            MaleHandTexturePath = maleHandTexture,
            FemaleHandTexturePath = femaleHandTexture,
            MaleFootTexturePath = maleFootTexture,
            FemaleFootTexturePath = femaleFootTexture
        };
    }

    private static void AssignPath(
        string? path,
        bool inMaleSection,
        bool sharedAcrossGenders,
        ref string? maleValue,
        ref string? femaleValue)
    {
        if (path == null)
        {
            return;
        }

        if (sharedAcrossGenders)
        {
            maleValue = path;
            femaleValue = path;
        }
        else if (inMaleSection)
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
        bool sharedAcrossGenders,
        ref float[]? maleValue,
        ref float[]? femaleValue)
    {
        var coefficients = NpcRecordDataReader.ReadFloatArray(data, bigEndian);
        if (sharedAcrossGenders)
        {
            maleValue = coefficients;
            femaleValue = coefficients;
        }
        else if (inMaleSection)
        {
            maleValue = coefficients;
        }
        else
        {
            femaleValue = coefficients;
        }
    }
}
