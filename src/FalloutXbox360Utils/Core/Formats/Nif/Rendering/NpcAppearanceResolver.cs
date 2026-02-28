using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Scans ESM NPC_/RACE records and resolves NPC visual appearance data.
///     Built incrementally — only resolves fields needed by the current rendering phase.
/// </summary>
internal sealed class NpcAppearanceResolver
{
    private readonly Dictionary<uint, NpcScanEntry> _npcs;
    private readonly Dictionary<uint, RaceScanEntry> _races;
    private readonly Dictionary<uint, HairScanEntry> _hairs;
    private readonly Dictionary<uint, EyesScanEntry> _eyes;
    private readonly Dictionary<uint, HdptScanEntry> _headParts;

    private NpcAppearanceResolver(
        Dictionary<uint, NpcScanEntry> npcs,
        Dictionary<uint, RaceScanEntry> races,
        Dictionary<uint, HairScanEntry> hairs,
        Dictionary<uint, EyesScanEntry> eyes,
        Dictionary<uint, HdptScanEntry> headParts)
    {
        _npcs = npcs;
        _races = races;
        _hairs = hairs;
        _eyes = eyes;
        _headParts = headParts;
    }

    public int NpcCount => _npcs.Count;
    public int RaceCount => _races.Count;

    /// <summary>
    ///     Scans all NPC_ and RACE records in the ESM and builds lookup indices.
    /// </summary>
    public static NpcAppearanceResolver Build(byte[] esmData, bool bigEndian)
    {
        var npcs = new Dictionary<uint, NpcScanEntry>();
        var races = new Dictionary<uint, RaceScanEntry>();
        var hairs = new Dictionary<uint, HairScanEntry>();
        var eyes = new Dictionary<uint, EyesScanEntry>();
        var headParts = new Dictionary<uint, HdptScanEntry>();
        var records = EsmRecordParser.ScanAllRecords(esmData, bigEndian);

        foreach (var record in records)
        {
            switch (record.Signature)
            {
                case "NPC_":
                {
                    var entry = ProcessNpcRecord(esmData, bigEndian, record);
                    if (entry != null)
                        npcs[record.FormId] = entry;
                    break;
                }
                case "RACE":
                {
                    var entry = ProcessRaceRecord(esmData, bigEndian, record);
                    if (entry != null)
                        races[record.FormId] = entry;
                    break;
                }
                case "HAIR":
                {
                    var entry = ProcessHairRecord(esmData, bigEndian, record);
                    if (entry != null)
                        hairs[record.FormId] = entry;
                    break;
                }
                case "EYES":
                {
                    var entry = ProcessEyesRecord(esmData, bigEndian, record);
                    if (entry != null)
                        eyes[record.FormId] = entry;
                    break;
                }
                case "HDPT":
                {
                    var entry = ProcessHdptRecord(esmData, bigEndian, record);
                    if (entry != null)
                        headParts[record.FormId] = entry;
                    break;
                }
            }
        }

        return new NpcAppearanceResolver(npcs, races, hairs, eyes, headParts);
    }

    /// <summary>
    ///     Resolves a single NPC's head appearance.
    /// </summary>
    public NpcAppearance? ResolveHeadOnly(uint formId, string pluginName)
    {
        if (!_npcs.TryGetValue(formId, out var npc))
            return null;

        return BuildAppearance(formId, npc, pluginName);
    }

    /// <summary>
    ///     Resolves all NPCs' head appearances.
    /// </summary>
    public List<NpcAppearance> ResolveAllHeadOnly(string pluginName, bool filterNamed = false)
    {
        var results = new List<NpcAppearance>();

        foreach (var (formId, entry) in _npcs)
        {
            if (filterNamed && string.IsNullOrEmpty(entry.FullName))
                continue;

            results.Add(BuildAppearance(formId, entry, pluginName));
        }

        return results;
    }

    /// <summary>
    ///     Resolves head appearance from a DMP-sourced NpcRecord.
    ///     Uses the same RACE/HAIR/EYES lookup dictionaries as ESM-based resolution,
    ///     but takes FaceGen coefficients directly from the DMP data.
    /// </summary>
    public NpcAppearance? ResolveFromDmpRecord(NpcRecord npcRecord, string pluginName)
    {
        // Gender: ACBS flags bit 0 = female
        var isFemale = npcRecord.Stats != null && (npcRecord.Stats.Flags & 1) != 0;

        // Look up RACE for mesh paths and base coefficients
        RaceScanEntry? race = null;
        if (npcRecord.Race.HasValue)
            _races.TryGetValue(npcRecord.Race.Value, out race);

        // Head mesh from RACE
        string? headNifPath = null;
        if (race != null)
            headNifPath = isFemale ? race.FemaleHeadModelPath : race.MaleHeadModelPath;

        string? headTexturePath = null;
        if (race != null)
            headTexturePath = isFemale ? race.FemaleHeadTexturePath : race.MaleHeadTexturePath;

        string? bsaHeadNifPath = headNifPath != null ? "meshes\\" + headNifPath : null;

        // Per-NPC FaceGen mesh fallback
        var faceGenNifPath = $"meshes\\characters\\facegendata\\facegeom\\{pluginName}\\{npcRecord.FormId:X8}.nif";

        // Hair mesh from HAIR record
        string? hairNifPath = null;
        if (npcRecord.HairFormId.HasValue && _hairs.TryGetValue(npcRecord.HairFormId.Value, out var hair))
            hairNifPath = hair.ModelPath != null ? "meshes\\" + hair.ModelPath : null;

        // Eye meshes from RACE
        string? leftEyeNifPath = null;
        string? rightEyeNifPath = null;
        if (race != null)
        {
            var leftModel = isFemale ? race.FemaleEyeLeftModelPath : race.MaleEyeLeftModelPath;
            var rightModel = isFemale ? race.FemaleEyeRightModelPath : race.MaleEyeRightModelPath;
            leftEyeNifPath = leftModel != null ? "meshes\\" + leftModel : null;
            rightEyeNifPath = rightModel != null ? "meshes\\" + rightModel : null;
        }

        // Eye texture from EYES record
        string? eyeTexturePath = null;
        if (npcRecord.EyesFormId.HasValue && _eyes.TryGetValue(npcRecord.EyesFormId.Value, out var eyesRecord))
            eyeTexturePath = eyesRecord.TexturePath != null ? "textures\\" + eyesRecord.TexturePath : null;

        // Merge DMP coefficients with race base (element-wise addition, same as ESM path)
        var raceFggs = race != null ? (isFemale ? race.FemaleFaceGenSymmetric : race.MaleFaceGenSymmetric) : null;
        var raceFgga = race != null ? (isFemale ? race.FemaleFaceGenAsymmetric : race.MaleFaceGenAsymmetric) : null;
        var raceFgts = race != null ? (isFemale ? race.FemaleFaceGenTexture : race.MaleFaceGenTexture) : null;
        var mergedFggs = MergeCoefficients(npcRecord.FaceGenGeometrySymmetric, raceFggs);
        var mergedFgga = MergeCoefficients(npcRecord.FaceGenGeometryAsymmetric, raceFgga);
        var mergedFgts = MergeCoefficients(npcRecord.FaceGenTextureSymmetric, raceFgts);

        // DMP records don't carry PNAM (head parts) — leave null
        return new NpcAppearance
        {
            NpcFormId = npcRecord.FormId,
            EditorId = npcRecord.EditorId,
            FullName = npcRecord.FullName,
            IsFemale = isFemale,
            BaseHeadNifPath = bsaHeadNifPath,
            HeadDiffuseOverride = headTexturePath,
            FaceGenNifPath = faceGenNifPath,
            HairNifPath = hairNifPath,
            LeftEyeNifPath = leftEyeNifPath,
            RightEyeNifPath = rightEyeNifPath,
            EyeTexturePath = eyeTexturePath,
            FaceGenSymmetricCoeffs = mergedFggs,
            FaceGenAsymmetricCoeffs = mergedFgga,
            FaceGenTextureCoeffs = mergedFgts
        };
    }

    public IReadOnlyDictionary<uint, NpcScanEntry> GetAllNpcs() => _npcs;
    public IReadOnlyDictionary<uint, RaceScanEntry> GetAllRaces() => _races;

    private NpcAppearance BuildAppearance(uint formId, NpcScanEntry npc, string pluginName)
    {
        RaceScanEntry? race = null;
        if (npc.RaceFormId.HasValue)
            _races.TryGetValue(npc.RaceFormId.Value, out race);

        // Head mesh: from RACE INDX 0 MODL in NAM0 (head parts) section
        string? headNifPath = null;
        if (race != null)
            headNifPath = npc.IsFemale ? race.FemaleHeadModelPath : race.MaleHeadModelPath;

        // Head texture: from RACE INDX 0 ICON in NAM0 section
        string? headTexturePath = null;
        if (race != null)
            headTexturePath = npc.IsFemale ? race.FemaleHeadTexturePath : race.MaleHeadTexturePath;

        // BSA paths include "meshes\" prefix
        string? bsaHeadNifPath = headNifPath != null ? "meshes\\" + headNifPath : null;

        // Per-NPC FaceGen mesh fallback
        var faceGenNifPath = $"meshes\\characters\\facegendata\\facegeom\\{pluginName}\\{formId:X8}.nif";

        // Hair mesh: from HAIR record MODL via NPC_ HNAM
        string? hairNifPath = null;
        if (npc.HairFormId.HasValue && _hairs.TryGetValue(npc.HairFormId.Value, out var hair))
            hairNifPath = hair.ModelPath != null ? "meshes\\" + hair.ModelPath : null;

        // Eye meshes: from RACE INDX 6 (left) and 7 (right) in NAM0 section
        string? leftEyeNifPath = null;
        string? rightEyeNifPath = null;
        if (race != null)
        {
            var leftModel = npc.IsFemale ? race.FemaleEyeLeftModelPath : race.MaleEyeLeftModelPath;
            var rightModel = npc.IsFemale ? race.FemaleEyeRightModelPath : race.MaleEyeRightModelPath;
            leftEyeNifPath = leftModel != null ? "meshes\\" + leftModel : null;
            rightEyeNifPath = rightModel != null ? "meshes\\" + rightModel : null;
        }

        // Eye texture: from EYES record ICON via NPC_ ENAM
        string? eyeTexturePath = null;
        if (npc.EyesFormId.HasValue && _eyes.TryGetValue(npc.EyesFormId.Value, out var eyesRecord))
            eyeTexturePath = eyesRecord.TexturePath != null ? "textures\\" + eyesRecord.TexturePath : null;

        // Head parts from PNAM → HDPT (eyebrows, beards, etc.)
        List<string>? headPartNifPaths = null;
        if (npc.HeadPartFormIds is { Count: > 0 })
        {
            headPartNifPaths = new List<string>();
            foreach (var hdptId in npc.HeadPartFormIds)
            {
                if (_headParts.TryGetValue(hdptId, out var hdpt) && hdpt.ModelPath != null)
                    headPartNifPaths.Add("meshes\\" + hdpt.ModelPath);
            }

            if (headPartNifPaths.Count == 0)
                headPartNifPaths = null;
        }

        // Merge NPC + race FaceGen coefficients (element-wise addition)
        var raceFggs = race != null ? (npc.IsFemale ? race.FemaleFaceGenSymmetric : race.MaleFaceGenSymmetric) : null;
        var raceFgga = race != null ? (npc.IsFemale ? race.FemaleFaceGenAsymmetric : race.MaleFaceGenAsymmetric) : null;
        var mergedFggs = MergeCoefficients(npc.FaceGenSymmetric, raceFggs);
        var mergedFgga = MergeCoefficients(npc.FaceGenAsymmetric, raceFgga);

        var raceFgts = race != null ? (npc.IsFemale ? race.FemaleFaceGenTexture : race.MaleFaceGenTexture) : null;
        var mergedFgts = MergeCoefficients(npc.FaceGenTexture, raceFgts);

        return new NpcAppearance
        {
            NpcFormId = formId,
            EditorId = npc.EditorId,
            FullName = npc.FullName,
            IsFemale = npc.IsFemale,
            BaseHeadNifPath = bsaHeadNifPath,
            HeadDiffuseOverride = headTexturePath,
            FaceGenNifPath = faceGenNifPath,
            HairNifPath = hairNifPath,
            LeftEyeNifPath = leftEyeNifPath,
            RightEyeNifPath = rightEyeNifPath,
            EyeTexturePath = eyeTexturePath,
            HeadPartNifPaths = headPartNifPaths,
            FaceGenSymmetricCoeffs = mergedFggs,
            FaceGenAsymmetricCoeffs = mergedFgga,
            FaceGenTextureCoeffs = mergedFgts
        };
    }

    private static NpcScanEntry? ProcessNpcRecord(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
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
        bool isFemale = false;
        float[]? fggs = null;
        float[]? fgga = null;
        float[]? fgts = null;
        var headPartFormIds = new List<uint>();

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
                case "ACBS" when sub.Data.Length >= 4:
                {
                    var flags = BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian);
                    isFemale = (flags & 1) != 0;
                    break;
                }
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
            HeadPartFormIds = headPartFormIds.Count > 0 ? headPartFormIds : null
        };
    }

    private static RaceScanEntry? ProcessRaceRecord(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
    {
        var recordData = ReadRecordData(esmData, bigEndian, record);
        if (recordData == null)
            return null;

        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

        string? editorId = null;
        var inMaleSection = true;
        var inHeadPartsSection = false;

        string? maleHeadModel = null, femaleHeadModel = null;
        string? maleHeadTexture = null, femaleHeadTexture = null;
        string? maleEyeLeftModel = null, femaleEyeLeftModel = null;
        string? maleEyeRightModel = null, femaleEyeRightModel = null;
        float[]? maleFggs = null, femaleFggs = null;
        float[]? maleFgga = null, femaleFgga = null;
        float[]? maleFgts = null, femaleFgts = null;

        // Current INDX value within head parts section
        int currentIndx = -1;

        foreach (var sub in subrecords)
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(sub);
                    break;

                // Section markers (empty subrecords)
                case "NAM0" when sub.Data.Length == 0:
                    inHeadPartsSection = true;
                    break;
                case "NAM1" when sub.Data.Length == 0:
                    inHeadPartsSection = false;
                    break;
                case "MNAM" when sub.Data.Length == 0:
                    inMaleSection = true;
                    currentIndx = -1;
                    break;
                case "FNAM" when sub.Data.Length == 0:
                    inMaleSection = false;
                    currentIndx = -1;
                    break;

                // Part index within head parts section
                case "INDX" when sub.Data.Length == 4 && inHeadPartsSection:
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
            FemaleFaceGenTexture = femaleFgts
        };
    }

    private static float[] ReadFloatArray(byte[] data, bool bigEndian)
    {
        var count = data.Length / 4;
        var result = new float[count];
        for (var i = 0; i < count; i++)
            result[i] = BinaryUtils.ReadFloat(data, i * 4, bigEndian);
        return result;
    }

    /// <summary>
    ///     Element-wise addition of NPC + race base coefficients.
    ///     Engine merges: merged[i] = npc_coeff[i] + race_base_coeff[i]
    /// </summary>
    private static float[]? MergeCoefficients(float[]? npcCoeffs, float[]? raceCoeffs)
    {
        if (npcCoeffs == null)
            return raceCoeffs;
        if (raceCoeffs == null)
            return npcCoeffs;

        var count = Math.Min(npcCoeffs.Length, raceCoeffs.Length);
        var merged = new float[count];
        for (var i = 0; i < count; i++)
            merged[i] = npcCoeffs[i] + raceCoeffs[i];
        return merged;
    }

    private static EyesScanEntry? ProcessEyesRecord(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
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

    private static HdptScanEntry? ProcessHdptRecord(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
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

    private static HairScanEntry? ProcessHairRecord(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
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

        return new HairScanEntry
        {
            EditorId = editorId,
            ModelPath = modelPath
        };
    }

    /// <summary>
    ///     Reads and optionally decompresses a record's data section.
    ///     Uses EsmParser.DecompressRecordData for zlib decompression (not raw deflate).
    /// </summary>
    private static byte[]? ReadRecordData(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
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
}

/// <summary>
///     Scanned NPC_ record data.
/// </summary>
internal sealed class NpcScanEntry
{
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public uint? RaceFormId { get; init; }
    public bool IsFemale { get; init; }
    public uint? HairFormId { get; init; }
    public uint? EyesFormId { get; init; }
    public List<uint>? HeadPartFormIds { get; init; }
    public float[]? FaceGenSymmetric { get; init; }
    public float[]? FaceGenAsymmetric { get; init; }
    public float[]? FaceGenTexture { get; init; }
}

/// <summary>
///     Scanned RACE record data.
/// </summary>
internal sealed class RaceScanEntry
{
    public string? EditorId { get; init; }
    public string? MaleHeadModelPath { get; init; }
    public string? FemaleHeadModelPath { get; init; }
    public string? MaleHeadTexturePath { get; init; }
    public string? FemaleHeadTexturePath { get; init; }
    public string? MaleEyeLeftModelPath { get; init; }
    public string? FemaleEyeLeftModelPath { get; init; }
    public string? MaleEyeRightModelPath { get; init; }
    public string? FemaleEyeRightModelPath { get; init; }
    public float[]? MaleFaceGenSymmetric { get; init; }
    public float[]? FemaleFaceGenSymmetric { get; init; }
    public float[]? MaleFaceGenAsymmetric { get; init; }
    public float[]? FemaleFaceGenAsymmetric { get; init; }
    public float[]? MaleFaceGenTexture { get; init; }
    public float[]? FemaleFaceGenTexture { get; init; }
}

/// <summary>
///     Scanned HAIR record data.
/// </summary>
internal sealed class HairScanEntry
{
    public string? EditorId { get; init; }
    public string? ModelPath { get; init; }
}

/// <summary>
///     Scanned EYES record data (eye texture).
/// </summary>
internal sealed class EyesScanEntry
{
    public string? EditorId { get; init; }
    public string? TexturePath { get; init; }
}

/// <summary>
///     Scanned HDPT record data (head part mesh).
/// </summary>
internal sealed class HdptScanEntry
{
    public string? EditorId { get; init; }
    public string? ModelPath { get; init; }
}
