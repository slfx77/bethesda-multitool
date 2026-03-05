using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Static ESM record parsers for NPC appearance scanning.
///     Extracts NPC_, RACE, HAIR, EYES, HDPT, ARMO, and LVLI record data
///     into scan entry models used by <see cref="NpcAppearanceResolver" />.
/// </summary>
internal static class NpcEsmRecordParsers
{
    internal static NpcScanEntry? ProcessNpcRecord(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
    {
        var recordData = ReadRecordData(esmData, bigEndian, record);
        if (recordData == null)
            return null;

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
        float[]? fggs = null;
        float[]? fgga = null;
        float[]? fgts = null;
        var headPartFormIds = new List<uint>();
        var inventoryFormIds = new List<uint>();

        foreach (var sub in subrecords)
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(sub);
                    break;
                case "FULL":
                    fullName = EsmRecordParser.GetSubrecordString(sub);
                    break;
                case "RNAM" when sub.Data.Length == 4:
                    raceFormId = BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian);
                    break;
                case "HNAM" when sub.Data.Length == 4:
                    hairFormId = BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian);
                    break;
                case "ENAM" when sub.Data.Length == 4:
                    eyesFormId = BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian);
                    break;
                case "PNAM" when sub.Data.Length == 4:
                    headPartFormIds.Add(BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian));
                    break;
                case "HCLR" when sub.Data.Length == 4:
                    hairColor = BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian);
                    break;
                case "ACBS" when sub.Data.Length >= 24:
                {
                    var flags = BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian);
                    isFemale = (flags & 1) != 0;
                    templateFlags = BinaryUtils.ReadUInt16(sub.Data, 22, bigEndian);
                    break;
                }
                case "ACBS" when sub.Data.Length >= 4:
                {
                    var flags = BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian);
                    isFemale = (flags & 1) != 0;
                    break;
                }
                case "CNTO" when sub.Data.Length >= 8:
                    inventoryFormIds.Add(BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian));
                    break;
                case "TPLT" when sub.Data.Length == 4:
                    templateFormId = BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian);
                    break;
                case "FGGS" when sub.Data.Length >= 4:
                    fggs = ReadFloatArray(sub.Data, bigEndian);
                    break;
                case "FGGA" when sub.Data.Length >= 4:
                    fgga = ReadFloatArray(sub.Data, bigEndian);
                    break;
                case "FGTS" when sub.Data.Length >= 4:
                    fgts = ReadFloatArray(sub.Data, bigEndian);
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
            FaceGenSymmetric = fggs,
            FaceGenAsymmetric = fgga,
            FaceGenTexture = fgts,
            HeadPartFormIds = headPartFormIds.Count > 0 ? headPartFormIds : null,
            HairColor = hairColor,
            InventoryFormIds = inventoryFormIds.Count > 0 ? inventoryFormIds : null,
            TemplateFormId = templateFormId,
            TemplateFlags = templateFlags
        };
    }

    internal static RaceScanEntry? ProcessRaceRecord(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
    {
        var recordData = ReadRecordData(esmData, bigEndian, record);
        if (recordData == null)
            return null;

        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

        string? editorId = null;
        var inMaleSection = true;
        var inHeadPartsSection = false;
        var inBodyPartsSection = false;

        string? maleHeadModel = null, femaleHeadModel = null;
        string? maleHeadTexture = null, femaleHeadTexture = null;
        string? maleEyeLeftModel = null, femaleEyeLeftModel = null;
        string? maleEyeRightModel = null, femaleEyeRightModel = null;
        float[]? maleFggs = null, femaleFggs = null;
        float[]? maleFgga = null, femaleFgga = null;
        float[]? maleFgts = null, femaleFgts = null;
        uint? defaultEyesFormId = null;

        // Body mesh paths (from section after NAM1)
        string? maleUpperBody = null, femaleUpperBody = null;
        string? maleLeftHand = null, femaleLeftHand = null;
        string? maleRightHand = null, femaleRightHand = null;
        string? maleBodyTexture = null, femaleBodyTexture = null;

        // Current INDX value within head/body parts sections
        int currentIndx = -1;

        foreach (var sub in subrecords)
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(sub);
                    break;

                // Section markers (empty subrecords)
                // NAM0 = head parts section start, NAM1 = head parts end / body parts start
                case "NAM0" when sub.Data.Length == 0:
                    inHeadPartsSection = true;
                    inBodyPartsSection = false;
                    break;
                case "NAM1" when sub.Data.Length == 0:
                    inHeadPartsSection = false;
                    inBodyPartsSection = true;
                    break;
                case "MNAM" when sub.Data.Length == 0:
                    inMaleSection = true;
                    currentIndx = -1;
                    break;
                case "FNAM" when sub.Data.Length == 0:
                    inMaleSection = false;
                    currentIndx = -1;
                    break;

                // Part index within head or body parts section
                case "INDX" when sub.Data.Length == 4 && (inHeadPartsSection || inBodyPartsSection):
                    currentIndx = (int)BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian);
                    break;

                // Head mesh: MODL at INDX 0 in NAM0 section
                case "MODL" when inHeadPartsSection && currentIndx == 0:
                {
                    var path = EsmRecordParser.GetSubrecordString(sub);
                    if (path != null)
                    {
                        if (inMaleSection)
                            maleHeadModel = path;
                        else
                            femaleHeadModel = path;
                    }
                    break;
                }

                // Eye meshes: MODL at INDX 6 (left) and 7 (right) in NAM0 section
                case "MODL" when inHeadPartsSection && currentIndx == 6:
                {
                    var path = EsmRecordParser.GetSubrecordString(sub);
                    if (path != null)
                    {
                        if (inMaleSection)
                            maleEyeLeftModel = path;
                        else
                            femaleEyeLeftModel = path;
                    }
                    break;
                }
                case "MODL" when inHeadPartsSection && currentIndx == 7:
                {
                    var path = EsmRecordParser.GetSubrecordString(sub);
                    if (path != null)
                    {
                        if (inMaleSection)
                            maleEyeRightModel = path;
                        else
                            femaleEyeRightModel = path;
                    }
                    break;
                }

                // Head texture: ICON at INDX 0 in NAM0 section
                case "ICON" when inHeadPartsSection && currentIndx == 0:
                {
                    var path = EsmRecordParser.GetSubrecordString(sub);
                    if (path != null)
                    {
                        if (inMaleSection)
                            maleHeadTexture = path;
                        else
                            femaleHeadTexture = path;
                    }
                    break;
                }

                // Body meshes: MODL at INDX 0/1/2 in body parts section (after NAM1)
                case "MODL" when inBodyPartsSection && currentIndx == 0:
                {
                    var path = EsmRecordParser.GetSubrecordString(sub);
                    if (path != null)
                    {
                        if (inMaleSection) maleUpperBody = path;
                        else femaleUpperBody = path;
                    }
                    break;
                }
                case "MODL" when inBodyPartsSection && currentIndx == 1:
                {
                    var path = EsmRecordParser.GetSubrecordString(sub);
                    if (path != null)
                    {
                        if (inMaleSection) maleLeftHand = path;
                        else femaleLeftHand = path;
                    }
                    break;
                }
                case "MODL" when inBodyPartsSection && currentIndx == 2:
                {
                    var path = EsmRecordParser.GetSubrecordString(sub);
                    if (path != null)
                    {
                        if (inMaleSection) maleRightHand = path;
                        else femaleRightHand = path;
                    }
                    break;
                }

                // Body texture: ICON at INDX 0 in body parts section
                case "ICON" when inBodyPartsSection && currentIndx == 0:
                {
                    var path = EsmRecordParser.GetSubrecordString(sub);
                    if (path != null)
                    {
                        if (inMaleSection) maleBodyTexture = path;
                        else femaleBodyTexture = path;
                    }
                    break;
                }

                // Default eyes: ENAM is a list of valid EYES FormIDs for this race.
                // The first entry is the default (used when NPC has no ENAM).
                case "ENAM" when sub.Data.Length >= 4:
                    defaultEyesFormId ??= BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian);
                    break;

                // FaceGen morph coefficients (outside head parts section)
                case "FGGS" when sub.Data.Length == 200:
                    if (inMaleSection)
                        maleFggs = ReadFloatArray(sub.Data, bigEndian);
                    else
                        femaleFggs = ReadFloatArray(sub.Data, bigEndian);
                    break;
                case "FGGA" when sub.Data.Length == 120:
                    if (inMaleSection)
                        maleFgga = ReadFloatArray(sub.Data, bigEndian);
                    else
                        femaleFgga = ReadFloatArray(sub.Data, bigEndian);
                    break;
                case "FGTS" when sub.Data.Length == 200:
                    if (inMaleSection)
                        maleFgts = ReadFloatArray(sub.Data, bigEndian);
                    else
                        femaleFgts = ReadFloatArray(sub.Data, bigEndian);
                    break;
            }
        }

        return new RaceScanEntry
        {
            EditorId = editorId,
            DefaultEyesFormId = defaultEyesFormId,
            MaleHeadModelPath = maleHeadModel,
            FemaleHeadModelPath = femaleHeadModel,
            MaleHeadTexturePath = maleHeadTexture,
            FemaleHeadTexturePath = femaleHeadTexture,
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

    internal static EyesScanEntry? ProcessEyesRecord(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
    {
        var recordData = ReadRecordData(esmData, bigEndian, record);
        if (recordData == null)
            return null;

        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

        string? editorId = null;
        string? texturePath = null;

        foreach (var sub in subrecords)
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(sub);
                    break;
                case "ICON":
                    texturePath = EsmRecordParser.GetSubrecordString(sub);
                    break;
            }
        }

        return new EyesScanEntry
        {
            EditorId = editorId,
            TexturePath = texturePath
        };
    }

    internal static HdptScanEntry? ProcessHdptRecord(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
    {
        var recordData = ReadRecordData(esmData, bigEndian, record);
        if (recordData == null)
            return null;

        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

        string? editorId = null;
        string? modelPath = null;

        foreach (var sub in subrecords)
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(sub);
                    break;
                case "MODL":
                    modelPath = EsmRecordParser.GetSubrecordString(sub);
                    break;
            }
        }

        return new HdptScanEntry
        {
            EditorId = editorId,
            ModelPath = modelPath
        };
    }

    internal static HairScanEntry? ProcessHairRecord(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
    {
        var recordData = ReadRecordData(esmData, bigEndian, record);
        if (recordData == null)
            return null;

        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

        string? editorId = null;
        string? modelPath = null;
        string? texturePath = null;

        foreach (var sub in subrecords)
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(sub);
                    break;
                case "MODL":
                    modelPath = EsmRecordParser.GetSubrecordString(sub);
                    break;
                case "ICON":
                    texturePath = EsmRecordParser.GetSubrecordString(sub);
                    break;
            }
        }

        return new HairScanEntry
        {
            EditorId = editorId,
            ModelPath = modelPath,
            TexturePath = texturePath
        };
    }

    internal static ArmoScanEntry? ProcessArmoRecord(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
    {
        var recordData = ReadRecordData(esmData, bigEndian, record);
        if (recordData == null)
            return null;

        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

        string? editorId = null;
        string? maleBipedModel = null;
        string? femaleBipedModel = null;
        uint bipedFlags = 0;

        // FNV ARMO subrecord layout:
        //   MODL = male biped model (3rd person worn)
        //   MOD2 = male world/ground model (NOT biped)
        //   MOD3 = female biped model (3rd person worn)
        //   MOD4 = female world/ground model (NOT biped)
        foreach (var sub in subrecords)
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(sub);
                    break;
                case "MODL":
                    maleBipedModel = EsmRecordParser.GetSubrecordString(sub);
                    break;
                case "MOD3":
                    femaleBipedModel = EsmRecordParser.GetSubrecordString(sub);
                    break;
                case "BMDT" when sub.Data.Length >= 4:
                    bipedFlags = BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian);
                    break;
            }
        }

        // Only keep armors that have at least one biped model and visual biped flags
        if (bipedFlags == 0 || (maleBipedModel == null && femaleBipedModel == null))
            return null;

        return new ArmoScanEntry
        {
            EditorId = editorId,
            BipedFlags = bipedFlags,
            MaleBipedModelPath = maleBipedModel,
            FemaleBipedModelPath = femaleBipedModel
        };
    }

    /// <summary>
    ///     Scans an LVLI record for entry FormIDs (from LVLO subrecords).
    ///     LVLO subrecords are 12 bytes: Level(2) + Padding(2) + FormID(4) + Count(2) + Padding(2).
    /// </summary>
    internal static List<uint>? ProcessLvliRecord(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
    {
        var recordData = ReadRecordData(esmData, bigEndian, record);
        if (recordData == null)
            return null;

        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);
        var entryFormIds = new List<uint>();

        foreach (var sub in subrecords)
        {
            if (sub.Signature == "LVLO" && sub.Data.Length >= 8)
            {
                // LVLO: bytes 0-1 = level, 2-3 = padding, 4-7 = FormID
                var entryFormId = BinaryUtils.ReadUInt32(sub.Data, 4, bigEndian);
                if (entryFormId != 0)
                    entryFormIds.Add(entryFormId);
            }
        }

        return entryFormIds.Count > 0 ? entryFormIds : null;
    }

    /// <summary>
    ///     Reads and optionally decompresses a record's data section.
    ///     Uses EsmParser.DecompressRecordData for zlib decompression (not raw deflate).
    /// </summary>
    internal static byte[]? ReadRecordData(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
    {
        var headerSize = EsmParser.MainRecordHeaderSize;
        var dataStart = (int)(record.Offset + headerSize);
        var dataSize = (int)record.DataSize;

        if (dataStart + dataSize > esmData.Length)
            return null;

        var rawSpan = esmData.AsSpan(dataStart, dataSize);

        if (record.IsCompressed)
            return EsmParser.DecompressRecordData(rawSpan, bigEndian);

        return rawSpan.ToArray();
    }

    internal static float[] ReadFloatArray(byte[] data, bool bigEndian)
    {
        var count = data.Length / 4;
        var result = new float[count];
        for (var i = 0; i < count; i++)
            result[i] = BinaryUtils.ReadFloat(data, i * 4, bigEndian);
        return result;
    }
}
