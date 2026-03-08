using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;

internal sealed class NpcAppearanceFactory
{
    private readonly NpcAppearanceIndex _index;
    private readonly NpcHeadPartPathResolver _headPartPathResolver;
    private readonly NpcInventoryResolver _inventoryResolver;
    private readonly NpcEquipmentResolver _equipmentResolver;
    private readonly NpcWeaponResolver _weaponResolver;

    internal NpcAppearanceFactory(NpcAppearanceIndex index)
    {
        _index = index;
        _headPartPathResolver = new NpcHeadPartPathResolver(index.HeadParts);
        _inventoryResolver = new NpcInventoryResolver(index.Npcs, index.LeveledNpcs);
        _equipmentResolver = new NpcEquipmentResolver(index.Armors, index.LeveledItems);
        _weaponResolver = new NpcWeaponResolver(index.Weapons, index.LeveledItems);
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
        var eyeTexturePath = ResolveEyeTexture(npc.EyesFormId, race);
        var inventoryFormIds = _inventoryResolver.ResolveInventoryFormIds(npc);
        var equippedItems = _equipmentResolver.Resolve(
            inventoryFormIds,
            npc.IsFemale);
        var equippedWeapon = _weaponResolver.Resolve(inventoryFormIds);
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
        var textureCoefficients = NpcFaceGenCoefficientMerger.Merge(
            npc.FaceGenTexture,
            SelectGenderValue(
                npc.IsFemale,
                race?.MaleFaceGenTexture,
                race?.FemaleFaceGenTexture));
        var handTexturePath = NpcAppearancePathDeriver.DeriveHandTexturePath(
            bodyTexturePath,
            npc.IsFemale);
        var bodyEgtPaths = NpcAppearancePathDeriver.DeriveBodyEgtPaths(
            headModelPath,
            npc.IsFemale);

        return new NpcAppearance
        {
            NpcFormId = formId,
            EditorId = npc.EditorId,
            FullName = npc.FullName,
            IsFemale = npc.IsFemale,
            BaseHeadNifPath = NpcAppearancePathDeriver.AsMeshPath(headModelPath),
            HeadDiffuseOverride = headTexturePath,
            FaceGenNifPath = NpcAppearancePathDeriver.BuildFaceGenNifPath(
                pluginName,
                formId),
            HairNifPath = NpcAppearancePathDeriver.AsMeshPath(hair?.ModelPath),
            HairTexturePath = NpcAppearancePathDeriver.AsTexturePath(hair?.TexturePath),
            LeftEyeNifPath = NpcAppearancePathDeriver.AsMeshPath(leftEyeModelPath),
            RightEyeNifPath = NpcAppearancePathDeriver.AsMeshPath(rightEyeModelPath),
            EyeTexturePath = NpcAppearancePathDeriver.AsTexturePath(eyeTexturePath),
            HeadPartNifPaths = _headPartPathResolver.Resolve(npc.HeadPartFormIds),
            HairColor = npc.HairColor,
            FaceGenSymmetricCoeffs = symmetricCoefficients,
            FaceGenAsymmetricCoeffs = asymmetricCoefficients,
            FaceGenTextureCoeffs = textureCoefficients,
            EquippedItems = equippedItems,
            EquippedWeapon = equippedWeapon,
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
        string pluginName)
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
        var eyeFormId = npcRecord.EyesFormId ?? race?.DefaultEyesFormId;
        var eyeTexturePath = ResolveEyeTexture(eyeFormId, race, useRaceDefault: false);
        var (hairColor, headPartIds) = ResolveDmpFallbacks(npcRecord);
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
        var textureCoefficients = NpcFaceGenCoefficientMerger.Merge(
            npcRecord.FaceGenTextureSymmetric,
            SelectGenderValue(
                isFemale,
                race?.MaleFaceGenTexture,
                race?.FemaleFaceGenTexture));
        var equippedItems = ResolveDmpEquipment(npcRecord.FormId, isFemale);
        var equippedWeapon = ResolveDmpWeapon(npcRecord.FormId);
        var handTexturePath = NpcAppearancePathDeriver.DeriveHandTexturePath(
            bodyTexturePath,
            isFemale);
        var bodyEgtPaths = NpcAppearancePathDeriver.DeriveBodyEgtPaths(
            headModelPath,
            isFemale);

        return new NpcAppearance
        {
            NpcFormId = npcRecord.FormId,
            EditorId = npcRecord.EditorId,
            FullName = npcRecord.FullName,
            IsFemale = isFemale,
            BaseHeadNifPath = NpcAppearancePathDeriver.AsMeshPath(headModelPath),
            HeadDiffuseOverride = headTexturePath,
            FaceGenNifPath = NpcAppearancePathDeriver.BuildFaceGenNifPath(
                pluginName,
                npcRecord.FormId),
            HairNifPath = NpcAppearancePathDeriver.AsMeshPath(hair?.ModelPath),
            HairTexturePath = NpcAppearancePathDeriver.AsTexturePath(hair?.TexturePath),
            LeftEyeNifPath = NpcAppearancePathDeriver.AsMeshPath(leftEyeModelPath),
            RightEyeNifPath = NpcAppearancePathDeriver.AsMeshPath(rightEyeModelPath),
            EyeTexturePath = NpcAppearancePathDeriver.AsTexturePath(eyeTexturePath),
            HeadPartNifPaths = _headPartPathResolver.Resolve(headPartIds),
            HairColor = hairColor,
            FaceGenSymmetricCoeffs = symmetricCoefficients,
            FaceGenAsymmetricCoeffs = asymmetricCoefficients,
            FaceGenTextureCoeffs = textureCoefficients,
            EquippedItems = equippedItems,
            EquippedWeapon = equippedWeapon,
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

    private List<EquippedItem>? ResolveDmpEquipment(uint formId, bool isFemale)
    {
        if (!_index.Npcs.TryGetValue(formId, out var esmNpc))
        {
            return null;
        }

        var inventory = _inventoryResolver.ResolveInventoryFormIds(esmNpc);
        return _equipmentResolver.Resolve(inventory, isFemale);
    }

    private EquippedWeapon? ResolveDmpWeapon(uint formId)
    {
        if (!_index.Npcs.TryGetValue(formId, out var esmNpc))
        {
            return null;
        }

        var inventory = _inventoryResolver.ResolveInventoryFormIds(esmNpc);
        return _weaponResolver.Resolve(inventory);
    }

    private static T? SelectGenderValue<T>(
        bool isFemale,
        T? maleValue,
        T? femaleValue)
    {
        return isFemale ? femaleValue : maleValue;
    }
}
