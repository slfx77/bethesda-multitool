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
    private readonly Dictionary<uint, ArmoScanEntry> _armors;
    private readonly Dictionary<uint, List<uint>> _leveledItems;
    private readonly Dictionary<uint, List<uint>> _leveledNpcs;

    private NpcAppearanceResolver(
        Dictionary<uint, NpcScanEntry> npcs,
        Dictionary<uint, RaceScanEntry> races,
        Dictionary<uint, HairScanEntry> hairs,
        Dictionary<uint, EyesScanEntry> eyes,
        Dictionary<uint, HdptScanEntry> headParts,
        Dictionary<uint, ArmoScanEntry> armors,
        Dictionary<uint, List<uint>> leveledItems,
        Dictionary<uint, List<uint>> leveledNpcs)
    {
        _npcs = npcs;
        _races = races;
        _hairs = hairs;
        _eyes = eyes;
        _headParts = headParts;
        _armors = armors;
        _leveledItems = leveledItems;
        _leveledNpcs = leveledNpcs;
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
        var armors = new Dictionary<uint, ArmoScanEntry>();
        var leveledItems = new Dictionary<uint, List<uint>>();
        var leveledNpcs = new Dictionary<uint, List<uint>>();
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
                case "ARMO":
                {
                    var entry = ProcessArmoRecord(esmData, bigEndian, record);
                    if (entry != null)
                        armors[record.FormId] = entry;
                    break;
                }
                case "LVLI":
                {
                    var entries = ProcessLvliRecord(esmData, bigEndian, record);
                    if (entries != null)
                        leveledItems[record.FormId] = entries;
                    break;
                }
                case "LVLN":
                {
                    // LVLN uses same LVLO subrecord format as LVLI
                    var entries = ProcessLvliRecord(esmData, bigEndian, record);
                    if (entries != null)
                        leveledNpcs[record.FormId] = entries;
                    break;
                }
            }
        }

        return new NpcAppearanceResolver(npcs, races, hairs, eyes, headParts, armors, leveledItems, leveledNpcs);
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

        // Hair mesh and texture from HAIR record
        string? hairNifPath = null;
        string? hairTexturePath = null;
        if (npcRecord.HairFormId.HasValue && _hairs.TryGetValue(npcRecord.HairFormId.Value, out var hair))
        {
            hairNifPath = hair.ModelPath != null ? "meshes\\" + hair.ModelPath : null;
            hairTexturePath = hair.TexturePath != null ? "textures\\" + hair.TexturePath : null;
        }

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

        // Eye texture: from EYES record ICON via NPC_ ENAM, or race default
        string? eyeTexturePath = null;
        var eyesFormId = npcRecord.EyesFormId ?? race?.DefaultEyesFormId;
        if (eyesFormId.HasValue && _eyes.TryGetValue(eyesFormId.Value, out var eyesRecord))
            eyeTexturePath = eyesRecord.TexturePath != null ? "textures\\" + eyesRecord.TexturePath : null;

        // Merge DMP coefficients with race base (element-wise addition, same as ESM path)
        var raceFggs = race != null ? (isFemale ? race.FemaleFaceGenSymmetric : race.MaleFaceGenSymmetric) : null;
        var raceFgga = race != null ? (isFemale ? race.FemaleFaceGenAsymmetric : race.MaleFaceGenAsymmetric) : null;
        var raceFgts = race != null ? (isFemale ? race.FemaleFaceGenTexture : race.MaleFaceGenTexture) : null;
        var mergedFggs = MergeCoefficients(npcRecord.FaceGenGeometrySymmetric, raceFggs);
        var mergedFgga = MergeCoefficients(npcRecord.FaceGenGeometryAsymmetric, raceFgga);
        var mergedFgts = MergeCoefficients(npcRecord.FaceGenTextureSymmetric, raceFgts);

        // Use DMP-sourced hair color and head parts, fall back to ESM if not available
        var hairColor = npcRecord.HairColor;
        var headPartFormIdSource = npcRecord.HeadPartFormIds;

        if (hairColor == null || headPartFormIdSource == null)
        {
            if (_npcs.TryGetValue(npcRecord.FormId, out var esmEntry))
            {
                hairColor ??= esmEntry.HairColor;
                headPartFormIdSource ??= esmEntry.HeadPartFormIds;
            }
        }

        List<string>? headPartNifPaths = null;
        if (headPartFormIdSource is { Count: > 0 })
        {
            headPartNifPaths = new List<string>();
            foreach (var hdptId in headPartFormIdSource)
            {
                if (_headParts.TryGetValue(hdptId, out var hdpt) && hdpt.ModelPath != null)
                    headPartNifPaths.Add("meshes\\" + hdpt.ModelPath);
            }

            if (headPartNifPaths.Count == 0)
                headPartNifPaths = null;
        }

        // Equipment: use ESM NPC_ CNTO inventory (DMP doesn't carry inventory)
        // Follow TPLT template chains if NPC has no own inventory
        List<EquippedItem>? equippedItems = null;
        if (_npcs.TryGetValue(npcRecord.FormId, out var esmNpc))
        {
            var inventoryFormIds = ResolveInventoryFormIds(esmNpc);
            equippedItems = ResolveEquipment(inventoryFormIds, isFemale);
        }

        // Body mesh paths from RACE
        string? upperBodyPath = null, leftHandPath = null, rightHandPath = null;
        string? bodyTexturePath = null;
        if (race != null)
        {
            upperBodyPath = isFemale ? race.FemaleUpperBodyPath : race.MaleUpperBodyPath;
            leftHandPath = isFemale ? race.FemaleLeftHandPath : race.MaleLeftHandPath;
            rightHandPath = isFemale ? race.FemaleRightHandPath : race.MaleRightHandPath;
            bodyTexturePath = isFemale ? race.FemaleBodyTexturePath : race.MaleBodyTexturePath;
        }

        var handTexturePath = DeriveHandTexturePath(bodyTexturePath, isFemale);
        var (bodyEgt, leftHandEgt, rightHandEgt) = DeriveBodyEgtPaths(headNifPath, isFemale);

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
            HairTexturePath = hairTexturePath,
            LeftEyeNifPath = leftEyeNifPath,
            RightEyeNifPath = rightEyeNifPath,
            EyeTexturePath = eyeTexturePath,
            HeadPartNifPaths = headPartNifPaths,
            HairColor = hairColor,
            FaceGenSymmetricCoeffs = mergedFggs,
            FaceGenAsymmetricCoeffs = mergedFgga,
            FaceGenTextureCoeffs = mergedFgts,
            EquippedItems = equippedItems,
            UpperBodyNifPath = upperBodyPath != null ? "meshes\\" + upperBodyPath : null,
            LeftHandNifPath = leftHandPath != null ? "meshes\\" + leftHandPath : null,
            RightHandNifPath = rightHandPath != null ? "meshes\\" + rightHandPath : null,
            BodyTexturePath = bodyTexturePath != null ? "textures\\" + bodyTexturePath : null,
            HandTexturePath = handTexturePath,
            SkeletonNifPath = "meshes\\characters\\_Male\\skeleton.nif",
            BodyEgtPath = bodyEgt,
            LeftHandEgtPath = leftHandEgt,
            RightHandEgtPath = rightHandEgt
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

        // Hair mesh and texture: from HAIR record MODL/ICON via NPC_ HNAM
        string? hairNifPath = null;
        string? hairTexturePath = null;
        if (npc.HairFormId.HasValue && _hairs.TryGetValue(npc.HairFormId.Value, out var hair))
        {
            hairNifPath = hair.ModelPath != null ? "meshes\\" + hair.ModelPath : null;
            hairTexturePath = hair.TexturePath != null ? "textures\\" + hair.TexturePath : null;
        }

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

        // Eye texture: from EYES record ICON via NPC_ ENAM, or race default
        string? eyeTexturePath = null;
        var eyesFormId = npc.EyesFormId ?? race?.DefaultEyesFormId;
        if (eyesFormId.HasValue && _eyes.TryGetValue(eyesFormId.Value, out var eyesRecord))
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

        // Equipment from NPC_ CNTO inventory → ARMO biped models
        // Follow TPLT template chains if NPC has no own inventory
        var inventoryFormIds = ResolveInventoryFormIds(npc);
        var equippedItems = ResolveEquipment(inventoryFormIds, npc.IsFemale);

        // Body mesh paths from RACE (body parts section after NAM1)
        string? upperBodyPath = null, leftHandPath = null, rightHandPath = null;
        string? bodyTexturePath = null;
        if (race != null)
        {
            upperBodyPath = npc.IsFemale ? race.FemaleUpperBodyPath : race.MaleUpperBodyPath;
            leftHandPath = npc.IsFemale ? race.FemaleLeftHandPath : race.MaleLeftHandPath;
            rightHandPath = npc.IsFemale ? race.FemaleRightHandPath : race.MaleRightHandPath;
            bodyTexturePath = npc.IsFemale ? race.FemaleBodyTexturePath : race.MaleBodyTexturePath;
        }

        var handTexturePath = DeriveHandTexturePath(bodyTexturePath, npc.IsFemale);
        var (bodyEgt, leftHandEgt, rightHandEgt) = DeriveBodyEgtPaths(headNifPath, npc.IsFemale);

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
            HairTexturePath = hairTexturePath,
            LeftEyeNifPath = leftEyeNifPath,
            RightEyeNifPath = rightEyeNifPath,
            EyeTexturePath = eyeTexturePath,
            HeadPartNifPaths = headPartNifPaths,
            HairColor = npc.HairColor,
            FaceGenSymmetricCoeffs = mergedFggs,
            FaceGenAsymmetricCoeffs = mergedFgga,
            FaceGenTextureCoeffs = mergedFgts,
            EquippedItems = equippedItems,
            UpperBodyNifPath = upperBodyPath != null ? "meshes\\" + upperBodyPath : null,
            LeftHandNifPath = leftHandPath != null ? "meshes\\" + leftHandPath : null,
            RightHandNifPath = rightHandPath != null ? "meshes\\" + rightHandPath : null,
            BodyTexturePath = bodyTexturePath != null ? "textures\\" + bodyTexturePath : null,
            HandTexturePath = handTexturePath,
            SkeletonNifPath = "meshes\\characters\\_Male\\skeleton.nif",
            BodyEgtPath = bodyEgt,
            LeftHandEgtPath = leftHandEgt,
            RightHandEgtPath = rightHandEgt
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

    private static RaceScanEntry? ProcessRaceRecord(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
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

    /// <summary>
    ///     Derives the hand texture path from the RACE body texture path.
    ///     FNV convention: same directory, replace UpperBody{Male|Female} with Hand{Male|Female}.
    ///     E.g., Characters\Ghoul\UpperBodyMale.dds → textures\Characters\Ghoul\HandMale.dds
    /// </summary>
    private static string? DeriveHandTexturePath(string? bodyTexturePath, bool isFemale)
    {
        if (bodyTexturePath == null)
            return null;

        var dir = Path.GetDirectoryName(bodyTexturePath);
        var handFilename = isFemale ? "HandFemale.dds" : "HandMale.dds";
        var handPath = dir != null ? Path.Combine(dir, handFilename) : handFilename;
        return "textures\\" + handPath;
    }

    /// <summary>
    ///     Derives body and hand EGT paths from the head NIF path.
    ///     Body EGT files use the same FREGT003 format as head EGTs and contain
    ///     per-texel RGB morph deltas for body skin tinting (same algorithm as head FaceGen).
    ///     The race variant is derived from the head model name:
    ///       headhuman → body.egt (default), headghoul → upperbodyhumanghoul.egt, etc.
    ///     Hand EGTs follow: lefthand{variant}.egt / righthand{variant}.egt.
    /// </summary>
    private static (string? bodyEgt, string? leftHandEgt, string? rightHandEgt) DeriveBodyEgtPaths(
        string? headNifPath, bool isFemale)
    {
        if (headNifPath == null)
            return (null, null, null);

        // Extract the head variant from the NIF filename (e.g., "headhuman" → "human")
        var headFileName = Path.GetFileNameWithoutExtension(headNifPath);
        if (headFileName == null)
            return (null, null, null);

        // Determine the body EGT variant based on head model name.
        // Head models: headhuman, headghoul, headghoulfemale, headold, headoldfemale,
        //              headchild, headchildfemale, headfemale
        // Body EGTs in BSA:
        //   body.egt (default human, 88.8 KB), upperbodyhumanghoul.egt (ghoul, 88.8 KB),
        //   upperbodyhumanold.egt (old, 88.8 KB), upperbodyhumanoldfemale.egt (old female, 83.5 KB)
        // Hand EGTs: lefthandmale/female/old/ghoul/etc.
        string bodyEgtName;
        string handVariant;

        var lowerHead = headFileName.ToLowerInvariant();
        if (lowerHead.Contains("ghoul"))
        {
            bodyEgtName = "upperbodyhumanghoul.egt";
            handVariant = isFemale ? "ghoulfemale" : "ghoul";
        }
        else if (lowerHead.Contains("old"))
        {
            bodyEgtName = isFemale ? "upperbodyhumanoldfemale.egt" : "upperbodyhumanold.egt";
            handVariant = isFemale ? "oldfemale" : "old";
        }
        else if (lowerHead.Contains("child"))
        {
            bodyEgtName = isFemale ? "upperbodychildfemale.egt" : "upperbodychild.egt";
            handVariant = isFemale ? "childfemale" : "child";
        }
        else
        {
            // Standard human (caucasian, african american, hispanic, asian)
            // body.egt is the main EGT with real morph data (88.8 KB)
            bodyEgtName = "body.egt";
            handVariant = isFemale ? "female" : "male";
        }

        const string bodyDir = "meshes\\characters\\_male\\";
        return (
            bodyDir + bodyEgtName,
            bodyDir + "lefthand" + handVariant + ".egt",
            bodyDir + "righthand" + handVariant + ".egt"
        );
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

    private static ArmoScanEntry? ProcessArmoRecord(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
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
    ///     Resolves inventory FormIDs for an NPC, following TPLT template chains if needed.
    ///     FNV NPC_ records can inherit inventory from a template NPC when template flag bit 0x0100
    ///     ("Use Inventory") is set and the NPC has no own CNTO subrecords.
    /// </summary>
    private List<uint>? ResolveInventoryFormIds(NpcScanEntry npc)
    {
        if (npc.InventoryFormIds != null && npc.InventoryFormIds.Count > 0)
            return npc.InventoryFormIds;

        // Template flag bit 0x0100 = "Use Inventory" (inherit inventory from template NPC)
        if (npc.TemplateFormId == null || (npc.TemplateFlags & 0x0100) == 0)
            return null;

        // Follow template chain (max 5 levels to prevent infinite loops)
        var currentTemplateId = npc.TemplateFormId.Value;
        for (var depth = 0; depth < 5; depth++)
        {
            // Try direct NPC_ template
            if (_npcs.TryGetValue(currentTemplateId, out var templateNpc))
            {
                if (templateNpc.InventoryFormIds != null && templateNpc.InventoryFormIds.Count > 0)
                    return templateNpc.InventoryFormIds;

                // Template itself may inherit from another template
                if (templateNpc.TemplateFormId == null || (templateNpc.TemplateFlags & 0x0100) == 0)
                    break;

                currentTemplateId = templateNpc.TemplateFormId.Value;
                continue;
            }

            // Try LVLN (leveled NPC list) → resolve first NPC_ entry with inventory
            if (_leveledNpcs.TryGetValue(currentTemplateId, out var lvlnEntries))
            {
                foreach (var entryId in lvlnEntries)
                {
                    if (_npcs.TryGetValue(entryId, out var lvlnNpc))
                    {
                        var inv = ResolveInventoryFormIds(lvlnNpc);
                        if (inv != null)
                            return inv;
                    }
                }
            }

            break;
        }

        return null;
    }

    private List<EquippedItem>? ResolveEquipment(List<uint>? inventoryFormIds, bool isFemale)
    {
        if (inventoryFormIds == null || inventoryFormIds.Count == 0)
            return null;

        // Track first armor per biped slot (first in inventory order wins — matches game behavior)
        var slotToArmor = new Dictionary<uint, (uint BipedFlags, string MeshPath)>();

        foreach (var formId in inventoryFormIds)
        {
            if (!_armors.TryGetValue(formId, out var armor))
            {
                // Try LVLI resolution: walk leveled item lists to find armor entries
                armor = ResolveLvliToArmor(formId);
                if (armor == null)
                    continue;
            }

            var meshPath = isFemale
                ? (armor.FemaleBipedModelPath ?? armor.MaleBipedModelPath)
                : armor.MaleBipedModelPath;
            if (meshPath == null)
                continue;

            // An armor can cover multiple slots — assign to each slot it occupies (first wins)
            for (var bit = 0; bit < 20; bit++)
            {
                var slot = 1u << bit;
                if ((armor.BipedFlags & slot) != 0)
                {
                    slotToArmor.TryAdd(slot, (armor.BipedFlags, meshPath));
                }
            }
        }

        if (slotToArmor.Count == 0)
            return null;

        // Deduplicate: multiple slots may point to the same mesh (e.g., UpperBody armor covers 0x04+0x08+0x10)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<EquippedItem>();
        foreach (var (_, (bipedFlags, meshPath)) in slotToArmor)
        {
            if (seen.Add(meshPath))
            {
                result.Add(new EquippedItem
                {
                    BipedFlags = bipedFlags,
                    MeshPath = "meshes\\" + meshPath
                });
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    ///     Resolves a leveled item list (LVLI) FormID to the first ARMO entry found.
    ///     Recursively walks nested LVLI chains. Returns the first armor found
    ///     (deterministic for rendering — we just need representative equipment).
    /// </summary>
    private ArmoScanEntry? ResolveLvliToArmor(uint formId, int depth = 0)
    {
        if (depth > 5 || !_leveledItems.TryGetValue(formId, out var entries))
            return null;

        foreach (var entryFormId in entries)
        {
            if (_armors.TryGetValue(entryFormId, out var armor))
                return armor;

            // Recurse into nested LVLI
            var nested = ResolveLvliToArmor(entryFormId, depth + 1);
            if (nested != null)
                return nested;
        }

        return null;
    }

    /// <summary>
    ///     Scans an LVLI record for entry FormIDs (from LVLO subrecords).
    ///     LVLO subrecords are 12 bytes: Level(2) + Padding(2) + FormID(4) + Count(2) + Padding(2).
    /// </summary>
    private static List<uint>? ProcessLvliRecord(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
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
    public uint? HairColor { get; init; }
    public float[]? FaceGenSymmetric { get; init; }
    public float[]? FaceGenAsymmetric { get; init; }
    public float[]? FaceGenTexture { get; init; }
    public List<uint>? InventoryFormIds { get; init; }
    public uint? TemplateFormId { get; init; }
    public ushort TemplateFlags { get; init; }
}

/// <summary>
///     Scanned RACE record data.
/// </summary>
internal sealed class RaceScanEntry
{
    public string? EditorId { get; init; }
    public uint? DefaultEyesFormId { get; init; }
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
    // Body mesh paths (from body parts section after NAM1)
    public string? MaleUpperBodyPath { get; init; }
    public string? FemaleUpperBodyPath { get; init; }
    public string? MaleLeftHandPath { get; init; }
    public string? FemaleLeftHandPath { get; init; }
    public string? MaleRightHandPath { get; init; }
    public string? FemaleRightHandPath { get; init; }
    public string? MaleBodyTexturePath { get; init; }
    public string? FemaleBodyTexturePath { get; init; }
}

/// <summary>
///     Scanned HAIR record data.
/// </summary>
internal sealed class HairScanEntry
{
    public string? EditorId { get; init; }
    public string? ModelPath { get; init; }
    public string? TexturePath { get; init; }
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

/// <summary>
///     Scanned ARMO record data (armor biped model paths for rendering).
/// </summary>
internal sealed class ArmoScanEntry
{
    public string? EditorId { get; init; }
    public uint BipedFlags { get; init; }
    public string? MaleBipedModelPath { get; init; }
    public string? FemaleBipedModelPath { get; init; }
}
