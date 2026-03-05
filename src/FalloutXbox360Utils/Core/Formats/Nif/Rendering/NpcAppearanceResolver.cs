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
                    var entry = NpcEsmRecordParsers.ProcessNpcRecord(esmData, bigEndian, record);
                    if (entry != null)
                        npcs[record.FormId] = entry;
                    break;
                }
                case "RACE":
                {
                    var entry = NpcEsmRecordParsers.ProcessRaceRecord(esmData, bigEndian, record);
                    if (entry != null)
                        races[record.FormId] = entry;
                    break;
                }
                case "HAIR":
                {
                    var entry = NpcEsmRecordParsers.ProcessHairRecord(esmData, bigEndian, record);
                    if (entry != null)
                        hairs[record.FormId] = entry;
                    break;
                }
                case "EYES":
                {
                    var entry = NpcEsmRecordParsers.ProcessEyesRecord(esmData, bigEndian, record);
                    if (entry != null)
                        eyes[record.FormId] = entry;
                    break;
                }
                case "HDPT":
                {
                    var entry = NpcEsmRecordParsers.ProcessHdptRecord(esmData, bigEndian, record);
                    if (entry != null)
                        headParts[record.FormId] = entry;
                    break;
                }
                case "ARMO":
                {
                    var entry = NpcEsmRecordParsers.ProcessArmoRecord(esmData, bigEndian, record);
                    if (entry != null)
                        armors[record.FormId] = entry;
                    break;
                }
                case "LVLI":
                {
                    var entries = NpcEsmRecordParsers.ProcessLvliRecord(esmData, bigEndian, record);
                    if (entries != null)
                        leveledItems[record.FormId] = entries;
                    break;
                }
                case "LVLN":
                {
                    // LVLN uses same LVLO subrecord format as LVLI
                    var entries = NpcEsmRecordParsers.ProcessLvliRecord(esmData, bigEndian, record);
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

        // Head parts from PNAM -> HDPT (eyebrows, beards, etc.)
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

        // Equipment from NPC_ CNTO inventory -> ARMO biped models
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

    /// <summary>
    ///     Derives the hand texture path from the RACE body texture path.
    ///     FNV convention: same directory, replace UpperBody{Male|Female} with Hand{Male|Female}.
    ///     E.g., Characters\Ghoul\UpperBodyMale.dds -> textures\Characters\Ghoul\HandMale.dds
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
    ///       headhuman -> body.egt (default), headghoul -> upperbodyhumanghoul.egt, etc.
    ///     Hand EGTs follow: lefthand{variant}.egt / righthand{variant}.egt.
    /// </summary>
    private static (string? bodyEgt, string? leftHandEgt, string? rightHandEgt) DeriveBodyEgtPaths(
        string? headNifPath, bool isFemale)
    {
        if (headNifPath == null)
            return (null, null, null);

        // Extract the head variant from the NIF filename (e.g., "headhuman" -> "human")
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

            // Try LVLN (leveled NPC list) -> resolve first NPC_ entry with inventory
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

        // Track first armor per biped slot (first in inventory order wins -- matches game behavior)
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

            // An armor can cover multiple slots -- assign to each slot it occupies (first wins)
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
    ///     (deterministic for rendering -- we just need representative equipment).
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
}
