using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;

/// <summary>Assembles an <see cref="NpcAppearance" /> from a scanned NPC record by resolving its head parts, equipment, and FaceGen coefficients.</summary>
internal sealed class NpcAppearanceFactory
{
    /// <summary>The player's base NPC_ record ("PlayerBase", engine-reserved FormID).</summary>
    private const uint PlayerBaseFormId = 0x7;

    private readonly NpcEquipmentResolver _equipmentResolver;
    private readonly NpcHeadPartPathResolver _headPartPathResolver;
    private readonly NpcAppearanceIndex _index;
    private readonly NpcInventoryResolver _inventoryResolver;
    private readonly NpcWeaponResolver _weaponResolver;

    internal NpcAppearanceFactory(NpcAppearanceIndex index)
    {
        _index = index;
        _headPartPathResolver = new NpcHeadPartPathResolver(index.HeadParts);
        _inventoryResolver = new NpcInventoryResolver(index.Npcs, index.LeveledNpcs);
        _equipmentResolver = new NpcEquipmentResolver(
            index.Armors,
            index.ArmorAddons,
            index.FormLists,
            index.LeveledItems);
        _weaponResolver = new NpcWeaponResolver(
            index.Packages,
            index.Weapons,
            index.ArmorAddons,
            index.LeveledItems,
            index.Idles,
            index.CombatStyles);
    }

    internal NpcAppearance Build(uint formId, NpcScanEntry npc, string pluginName)
    {
        var race = ResolveRace(npc.RaceFormId);
        var headModelPath = SelectGenderValue(
            npc.IsFemale,
            race?.MaleHeadModelPath,
            race?.FemaleHeadModelPath);
        var headTexturePath = SelectGenderValue(
            npc.IsFemale,
            race?.MaleHeadTexturePath,
            race?.FemaleHeadTexturePath);
        var leftEyeModelPath = SelectGenderValue(
            npc.IsFemale,
            race?.MaleEyeLeftModelPath,
            race?.FemaleEyeLeftModelPath);
        var rightEyeModelPath = SelectGenderValue(
            npc.IsFemale,
            race?.MaleEyeRightModelPath,
            race?.FemaleEyeRightModelPath);
        var mouthModelPath = SelectGenderValue(
            npc.IsFemale,
            race?.MaleMouthModelPath,
            race?.FemaleMouthModelPath);
        var lowerTeethModelPath = SelectGenderValue(
            npc.IsFemale,
            race?.MaleLowerTeethModelPath,
            race?.FemaleLowerTeethModelPath);
        var upperTeethModelPath = SelectGenderValue(
            npc.IsFemale,
            race?.MaleUpperTeethModelPath,
            race?.FemaleUpperTeethModelPath);
        var tongueModelPath = SelectGenderValue(
            npc.IsFemale,
            race?.MaleTongueModelPath,
            race?.FemaleTongueModelPath);
        var upperBodyPath = SelectGenderValue(
            npc.IsFemale,
            race?.MaleUpperBodyPath,
            race?.FemaleUpperBodyPath);
        var leftHandPath = SelectGenderValue(
            npc.IsFemale,
            race?.MaleLeftHandPath,
            race?.FemaleLeftHandPath);
        var rightHandPath = SelectGenderValue(
            npc.IsFemale,
            race?.MaleRightHandPath,
            race?.FemaleRightHandPath);
        var bodyTexturePath = SelectGenderValue(
            npc.IsFemale,
            race?.MaleBodyTexturePath,
            race?.FemaleBodyTexturePath);
        var hair = ResolveHair(npc.HairFormId);
        var eyeTexturePath = ResolveEyeTexture(
            ResolveEffectiveEyesFormId(npc.EyesFormId, race, headModelPath), race);
        var inventoryItems = _inventoryResolver.ResolveInventoryItems(npc);
        var equippedItems = _equipmentResolver.Resolve(
            inventoryItems,
            npc.IsFemale);
        var weaponVisual = _weaponResolver.Resolve(npc, inventoryItems);
        var symmetricCoefficients = NpcFaceGenCoefficientMerger.Merge(
            npc.FaceGenSymmetric,
            SelectGenderValue(
                npc.IsFemale,
                race?.MaleFaceGenSymmetric,
                race?.FemaleFaceGenSymmetric));
        var asymmetricCoefficients = NpcFaceGenCoefficientMerger.Merge(
            npc.FaceGenAsymmetric,
            SelectGenderValue(
                npc.IsFemale,
                race?.MaleFaceGenAsymmetric,
                race?.FemaleFaceGenAsymmetric));
        var raceTextureCoefficients = SelectGenderValue(
            npc.IsFemale,
            race?.MaleFaceGenTexture,
            race?.FemaleFaceGenTexture);
        var textureCoefficients = NpcFaceGenCoefficientMerger.Merge(
            npc.FaceGenTexture,
            raceTextureCoefficients);
        var handTexturePath = NpcAppearancePathDeriver.DeriveHandTexturePath(
            bodyTexturePath,
            npc.IsFemale);
        var bodyEgtPaths = NpcAppearancePathDeriver.DeriveBodyEgtPaths(
            headModelPath,
            npc.IsFemale);
        var baseHeadNifPath = NpcAppearancePathDeriver.AsMeshPath(headModelPath);

        return new NpcAppearance
        {
            NpcFormId = formId,
            EditorId = npc.EditorId,
            FullName = npc.FullName,
            IsFemale = npc.IsFemale,
            BaseHeadNifPath = baseHeadNifPath,
            BaseHeadTriPath = NpcAppearancePathDeriver.DeriveHeadTriPath(baseHeadNifPath),
            HeadDiffuseOverride = headTexturePath,
            FaceGenNifPath = NpcAppearancePathDeriver.BuildFaceGenNifPath(
                pluginName,
                formId),
            HairNifPath = NpcAppearancePathDeriver.AsMeshPath(hair?.ModelPath),
            HairTexturePath = NpcAppearancePathDeriver.AsTexturePath(hair?.TexturePath),
            LeftEyeNifPath = NpcAppearancePathDeriver.AsMeshPath(leftEyeModelPath),
            RightEyeNifPath = NpcAppearancePathDeriver.AsMeshPath(rightEyeModelPath),
            EyeTexturePath = NpcAppearancePathDeriver.AsTexturePath(eyeTexturePath),
            MouthNifPath = NpcAppearancePathDeriver.AsMeshPath(mouthModelPath),
            LowerTeethNifPath = NpcAppearancePathDeriver.AsMeshPath(lowerTeethModelPath),
            UpperTeethNifPath = NpcAppearancePathDeriver.AsMeshPath(upperTeethModelPath),
            TongueNifPath = NpcAppearancePathDeriver.AsMeshPath(tongueModelPath),
            HeadPartNifPaths = _headPartPathResolver.Resolve(npc.HeadPartFormIds),
            HairColor = npc.HairColor,
            HairLength = npc.HairLength,
            FaceGenSymmetricCoeffs = symmetricCoefficients,
            FaceGenAsymmetricCoeffs = asymmetricCoefficients,
            FaceGenTextureCoeffs = textureCoefficients,
            NpcFaceGenTextureCoeffs = npc.FaceGenTexture,
            RaceFaceGenTextureCoeffs = raceTextureCoefficients,
            EquippedItems = equippedItems,
            WeaponVisual = weaponVisual,
            UpperBodyNifPath = NpcAppearancePathDeriver.AsMeshPath(upperBodyPath),
            LeftHandNifPath = NpcAppearancePathDeriver.AsMeshPath(leftHandPath),
            RightHandNifPath = NpcAppearancePathDeriver.AsMeshPath(rightHandPath),
            BodyTexturePath = NpcAppearancePathDeriver.AsTexturePath(bodyTexturePath),
            HandTexturePath = handTexturePath,
            SkeletonNifPath = "meshes\\characters\\_Male\\skeleton.nif",
            BodyEgtPath = bodyEgtPaths.BodyEgt,
            LeftHandEgtPath = bodyEgtPaths.LeftHandEgt,
            RightHandEgtPath = bodyEgtPaths.RightHandEgt
        };
    }

    internal NpcAppearance BuildFromDmpRecord(
        NpcRecord npcRecord,
        string pluginName,
        NpcWeaponResolver.RuntimeWeaponSelection? runtimeWeaponSelection = null,
        NpcEquipmentResolver.RuntimeEquipmentSelection? runtimeEquipmentSelection = null)
    {
        var isFemale = npcRecord.Stats != null && (npcRecord.Stats.Flags & 1) != 0;
        var race = ResolveRace(npcRecord.Race);
        var headModelPath = SelectGenderValue(
            isFemale,
            race?.MaleHeadModelPath,
            race?.FemaleHeadModelPath);
        var headTexturePath = SelectGenderValue(
            isFemale,
            race?.MaleHeadTexturePath,
            race?.FemaleHeadTexturePath);
        var leftEyeModelPath = SelectGenderValue(
            isFemale,
            race?.MaleEyeLeftModelPath,
            race?.FemaleEyeLeftModelPath);
        var rightEyeModelPath = SelectGenderValue(
            isFemale,
            race?.MaleEyeRightModelPath,
            race?.FemaleEyeRightModelPath);
        var mouthModelPath = SelectGenderValue(
            isFemale,
            race?.MaleMouthModelPath,
            race?.FemaleMouthModelPath);
        var lowerTeethModelPath = SelectGenderValue(
            isFemale,
            race?.MaleLowerTeethModelPath,
            race?.FemaleLowerTeethModelPath);
        var upperTeethModelPath = SelectGenderValue(
            isFemale,
            race?.MaleUpperTeethModelPath,
            race?.FemaleUpperTeethModelPath);
        var tongueModelPath = SelectGenderValue(
            isFemale,
            race?.MaleTongueModelPath,
            race?.FemaleTongueModelPath);
        var upperBodyPath = SelectGenderValue(
            isFemale,
            race?.MaleUpperBodyPath,
            race?.FemaleUpperBodyPath);
        var leftHandPath = SelectGenderValue(
            isFemale,
            race?.MaleLeftHandPath,
            race?.FemaleLeftHandPath);
        var rightHandPath = SelectGenderValue(
            isFemale,
            race?.MaleRightHandPath,
            race?.FemaleRightHandPath);
        var bodyTexturePath = SelectGenderValue(
            isFemale,
            race?.MaleBodyTexturePath,
            race?.FemaleBodyTexturePath);
        var hair = ResolveHair(npcRecord.HairFormId);
        var eyeFormId = ResolveEffectiveEyesFormId(
            npcRecord.EyesFormId ?? race?.DefaultEyesFormId, race, headModelPath);
        var eyeTexturePath = ResolveEyeTexture(eyeFormId, race, false);
        var (hairColor, headPartIds) = ResolveDmpFallbacks(npcRecord);
        _index.Npcs.TryGetValue(npcRecord.FormId, out var esmNpc);
        var weaponResolutionNpc = BuildWeaponResolutionNpc(npcRecord, esmNpc, isFemale);
        var inventoryItems = ResolveDmpInventoryItems(weaponResolutionNpc, npcRecord);
        var symmetricCoefficients = NpcFaceGenCoefficientMerger.Merge(
            npcRecord.FaceGenGeometrySymmetric,
            SelectGenderValue(
                isFemale,
                race?.MaleFaceGenSymmetric,
                race?.FemaleFaceGenSymmetric));
        var asymmetricCoefficients = NpcFaceGenCoefficientMerger.Merge(
            npcRecord.FaceGenGeometryAsymmetric,
            SelectGenderValue(
                isFemale,
                race?.MaleFaceGenAsymmetric,
                race?.FemaleFaceGenAsymmetric));
        var raceTextureCoefficients = SelectGenderValue(
            isFemale,
            race?.MaleFaceGenTexture,
            race?.FemaleFaceGenTexture);
        // In-game, an ESM NPC's face TEXTURE comes from the GECK prebake of the
        // AUTHORED coefficients (bLoadFaceGenHeadEGTFiles=0 → FaceMods\<id>_0.dds);
        // the engine never re-bakes ESM NPCs from the live TESNPC arrays, and those
        // live arrays drift at runtime (per-NPC materialization/reseeding — see
        // docs/facegen_head_rendering_pipeline.md "runtime FGTS drift"). Baking from
        // the drifted live values produces face textures the game never displays
        // (displaced nose-corner shading, tinted lips, brow bands). Prefer authored
        // FGTS when the NPC exists in the ESM; keep live values for the player
        // (FormID 0x7), whose in-game texture IS runtime-baked from live state, and
        // for runtime-only actors with no authored record.
        var npcTextureCoefficients = npcRecord.FaceGenTextureSymmetric;
        if (npcRecord.FormId != PlayerBaseFormId &&
            esmNpc?.FaceGenTexture is { Length: > 0 } authoredTextureCoefficients)
        {
            npcTextureCoefficients = authoredTextureCoefficients;
        }

        var textureCoefficients = NpcFaceGenCoefficientMerger.Merge(
            npcTextureCoefficients,
            raceTextureCoefficients);
        // Runtime worn armor (from the dump's BipedAnim slots) replaces the base
        // record's inventory for equipment when present; the weapon path below
        // keeps using the base inventory regardless.
        var equipmentSource = runtimeEquipmentSelection is { WornArmorFormIds.Count: > 0 }
            ? runtimeEquipmentSelection.Value.WornArmorFormIds!
                .Select(id => new InventoryItem(id, 1))
                .ToList()
            : inventoryItems;
        var equippedItems = _equipmentResolver.Resolve(equipmentSource, isFemale);
        var weaponVisual = _weaponResolver.Resolve(
            weaponResolutionNpc,
            inventoryItems,
            runtimeWeaponSelection);
        var handTexturePath = NpcAppearancePathDeriver.DeriveHandTexturePath(
            bodyTexturePath,
            isFemale);
        var bodyEgtPaths = NpcAppearancePathDeriver.DeriveBodyEgtPaths(
            headModelPath,
            isFemale);
        var baseHeadNifPath = NpcAppearancePathDeriver.AsMeshPath(headModelPath);

        return new NpcAppearance
        {
            NpcFormId = npcRecord.FormId,
            EditorId = npcRecord.EditorId,
            FullName = npcRecord.FullName,
            IsFemale = isFemale,
            BaseHeadNifPath = baseHeadNifPath,
            BaseHeadTriPath = NpcAppearancePathDeriver.DeriveHeadTriPath(baseHeadNifPath),
            HeadDiffuseOverride = headTexturePath,
            FaceGenNifPath = NpcAppearancePathDeriver.BuildFaceGenNifPath(
                pluginName,
                npcRecord.FormId),
            HairNifPath = NpcAppearancePathDeriver.AsMeshPath(hair?.ModelPath),
            HairTexturePath = NpcAppearancePathDeriver.AsTexturePath(hair?.TexturePath),
            LeftEyeNifPath = NpcAppearancePathDeriver.AsMeshPath(leftEyeModelPath),
            RightEyeNifPath = NpcAppearancePathDeriver.AsMeshPath(rightEyeModelPath),
            EyeTexturePath = NpcAppearancePathDeriver.AsTexturePath(eyeTexturePath),
            MouthNifPath = NpcAppearancePathDeriver.AsMeshPath(mouthModelPath),
            LowerTeethNifPath = NpcAppearancePathDeriver.AsMeshPath(lowerTeethModelPath),
            UpperTeethNifPath = NpcAppearancePathDeriver.AsMeshPath(upperTeethModelPath),
            TongueNifPath = NpcAppearancePathDeriver.AsMeshPath(tongueModelPath),
            HeadPartNifPaths = _headPartPathResolver.Resolve(headPartIds),
            HairColor = hairColor,
            HairLength = npcRecord.HairLength,
            FaceGenSymmetricCoeffs = symmetricCoefficients,
            FaceGenAsymmetricCoeffs = asymmetricCoefficients,
            FaceGenTextureCoeffs = textureCoefficients,
            NpcFaceGenTextureCoeffs = npcTextureCoefficients,
            RaceFaceGenTextureCoeffs = raceTextureCoefficients,
            EquippedItems = equippedItems,
            WeaponVisual = weaponVisual,
            UpperBodyNifPath = NpcAppearancePathDeriver.AsMeshPath(upperBodyPath),
            LeftHandNifPath = NpcAppearancePathDeriver.AsMeshPath(leftHandPath),
            RightHandNifPath = NpcAppearancePathDeriver.AsMeshPath(rightHandPath),
            BodyTexturePath = NpcAppearancePathDeriver.AsTexturePath(bodyTexturePath),
            HandTexturePath = handTexturePath,
            SkeletonNifPath = "meshes\\characters\\_Male\\skeleton.nif",
            BodyEgtPath = bodyEgtPaths.BodyEgt,
            LeftHandEgtPath = bodyEgtPaths.LeftHandEgt,
            RightHandEgtPath = bodyEgtPaths.RightHandEgt
        };
    }

    private RaceScanEntry? ResolveRace(uint? raceFormId)
    {
        if (raceFormId.HasValue &&
            _index.Races.TryGetValue(raceFormId.Value, out var race))
        {
            return race;
        }

        return null;
    }

    private HairScanEntry? ResolveHair(uint? hairFormId)
    {
        if (hairFormId.HasValue &&
            _index.Hairs.TryGetValue(hairFormId.Value, out var hair))
        {
            return hair;
        }

        return null;
    }

    /// <summary>
    ///     For non-human races (e.g. ghouls), always prefer the race's default EYES record.
    ///     Many ghoul NPCs have a human-type EYES FormID in their NPC_ data, but at runtime
    ///     the game renders race-appropriate eyes. Without this override, ghoul NPCs would
    ///     get human iris textures applied to their eye meshes.
    /// </summary>
    private static uint? ResolveEffectiveEyesFormId(
        uint? npcEyesFormId,
        RaceScanEntry? race,
        string? headModelPath)
    {
        if (race?.DefaultEyesFormId != null &&
            headModelPath != null &&
            headModelPath.Contains("ghoul", StringComparison.OrdinalIgnoreCase))
        {
            return race.DefaultEyesFormId;
        }

        return npcEyesFormId;
    }

    private string? ResolveEyeTexture(
        uint? eyesFormId,
        RaceScanEntry? race,
        bool useRaceDefault = true)
    {
        var effectiveEyesFormId = eyesFormId;
        if (!effectiveEyesFormId.HasValue && useRaceDefault)
        {
            effectiveEyesFormId = race?.DefaultEyesFormId;
        }

        if (effectiveEyesFormId.HasValue &&
            _index.Eyes.TryGetValue(effectiveEyesFormId.Value, out var eyesRecord))
        {
            return eyesRecord.TexturePath;
        }

        return null;
    }

    private (uint? HairColor, List<uint>? HeadPartIds) ResolveDmpFallbacks(
        NpcRecord npcRecord)
    {
        var hairColor = npcRecord.HairColor;
        var headPartIds = npcRecord.HeadPartFormIds;

        if ((hairColor != null && headPartIds != null) ||
            !_index.Npcs.TryGetValue(npcRecord.FormId, out var esmEntry))
        {
            return (hairColor, headPartIds);
        }

        hairColor ??= esmEntry.HairColor;
        headPartIds ??= esmEntry.HeadPartFormIds;
        return (hairColor, headPartIds);
    }

    private static NpcScanEntry BuildWeaponResolutionNpc(
        NpcRecord npcRecord,
        NpcScanEntry? esmNpc,
        bool isFemale)
    {
        return new NpcScanEntry
        {
            EditorId = esmNpc?.EditorId ?? npcRecord.EditorId,
            FullName = esmNpc?.FullName ?? npcRecord.FullName,
            RaceFormId = esmNpc?.RaceFormId ?? npcRecord.Race,
            IsFemale = isFemale,
            SpecialStats = npcRecord.SpecialStats ?? esmNpc?.SpecialStats,
            Skills = npcRecord.Skills ?? esmNpc?.Skills,
            PackageFormIds = npcRecord.Packages.Count > 0
                ? npcRecord.Packages
                : esmNpc?.PackageFormIds,
            InventoryItems = npcRecord.Inventory.Count > 0
                ? npcRecord.Inventory
                : esmNpc?.InventoryItems,
            TemplateFormId = esmNpc?.TemplateFormId,
            TemplateFlags = esmNpc?.TemplateFlags ?? 0
        };
    }

    private List<InventoryItem>? ResolveDmpInventoryItems(
        NpcScanEntry weaponResolutionNpc,
        NpcRecord npcRecord)
    {
        var inventory = _inventoryResolver.ResolveInventoryItems(weaponResolutionNpc);
        if (inventory is { Count: > 0 })
        {
            return inventory;
        }

        return npcRecord.Inventory.Count > 0 ? npcRecord.Inventory : null;
    }

    private static T? SelectGenderValue<T>(
        bool isFemale,
        T? maleValue,
        T? femaleValue)
    {
        return isFemale ? femaleValue : maleValue;
    }
}

