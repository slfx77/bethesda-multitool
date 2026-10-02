using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Semantic.LoadOrder;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;

/// <summary>Appearance records selected by the same physical winners as dialogue and terrain.</summary>
internal static class NpcAppearanceSelection
{
    internal static NpcAppearanceIndex Build(PluginLoadOrder order, LoadOrderRecordIndex selection,
        CancellationToken cancellationToken = default)
    {
        NpcAppearanceIndex? result = null;
        foreach (var entry in order.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = File.ReadAllBytes(entry.Path);
            var scan = EsmDescriptorScanner.Scan(bytes, _ => cancellationToken.ThrowIfCancellationRequested());
            var game = GameDetector.DetectFromBytes(bytes, entry.Name).Game;
            result ??= new NpcAppearanceIndex { Game = game };
            result.SourceHashes[entry.Path] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            var records = scan.ScanResult.MainRecords.Where(record =>
            {
                if (!NpcAppearanceIndexBuilder.IsAppearanceRecord(record.RecordType)) return false;
                var id = order.Map(entry.Path, record.FormId).LoadOrderFormId;
                return selection.Records.TryGetValue(id, out var identity) &&
                    !identity.DeletedByWinner && !identity.TypeConflict && !identity.HasAmbiguousWinningRecords &&
                    identity.Winner.FilePath.Equals(entry.Path, StringComparison.OrdinalIgnoreCase) &&
                    identity.Winner.Offset == record.Offset;
            }).ToArray();
            var local = NpcAppearanceIndexBuilder.Build(new ByteArrayMemoryAccessor(bytes), bytes.LongLength,
                records, EsmParser.IsBigEndian(bytes), game, cancellationToken: cancellationToken);
            uint Map(uint id) => order.Map(entry.Path, id).LoadOrderFormId;
            Append(result, local, Map);
            foreach (var record in records)
            {
                var globalId = Map(record.FormId);
                result.Sources[globalId] = selection.Records[globalId].Winner;
                if (result.Creatures.TryGetValue(globalId, out var creature))
                    result.Creatures[globalId] = creature with { AssetOwner = result.Owner(globalId, "CREA") };
            }
        }
        return result ?? new NpcAppearanceIndex();
    }

    internal static void Append(NpcAppearanceIndex target, NpcAppearanceIndex source, Func<uint, uint> map)
    {
        foreach (var (id, value) in source.Npcs) target.Npcs[map(id)] = Rebase(value, map);
        foreach (var (id, value) in source.Races) target.Races[map(id)] = Rebase(value, map);
        foreach (var (id, value) in source.Hairs) target.Hairs[map(id)] = Rebase(value, map);
        foreach (var (id, value) in source.Eyes) target.Eyes[map(id)] = Rebase(value, map);
        foreach (var (id, value) in source.HeadParts) target.HeadParts[map(id)] = Rebase(value, map);
        foreach (var (id, value) in source.Armors) target.Armors[map(id)] = Rebase(value, map);
        foreach (var (id, value) in source.ArmorAddons) target.ArmorAddons[map(id)] = Rebase(value, map);
        foreach (var (id, value) in source.Weapons) target.Weapons[map(id)] = Rebase(value, map);
        foreach (var (id, value) in source.Packages) target.Packages[map(id)] = Rebase(value, map);
        foreach (var (id, value) in source.Idles) target.Idles[map(id)] = Rebase(value, map);
        foreach (var (id, value) in source.LeveledItemRecords) target.LeveledItemRecords[map(id)] = Rebase(value, map);
        foreach (var (id, value) in source.LeveledNpcRecords) target.LeveledNpcRecords[map(id)] = Rebase(value, map);
        foreach (var (id, value) in source.Creatures)
            target.Creatures[map(id)] = value with
            {
                CombatStyleFormId = MapOptional(value.CombatStyleFormId, map),
                InventoryItems = value.InventoryItems?.Select(item => item with { ItemFormId = map(item.ItemFormId) }).ToList()
            };
        foreach (var (id, value) in source.CombatStyles) target.CombatStyles[map(id)] = value;
        target.IdleChildrenByParent.Clear();
        foreach (var (id, idle) in target.Idles)
        {
            if (idle.ParentIdleFormId is not { } parent) continue;
            if (!target.IdleChildrenByParent.TryGetValue(parent, out var children))
                target.IdleChildrenByParent[parent] = children = [];
            children.Add(id);
        }
        foreach (var (id, value) in source.FormLists) target.FormLists[map(id)] = value.Select(map).ToList();
        foreach (var (id, value) in source.LeveledItems) target.LeveledItems[map(id)] = value.Select(map).ToList();
        foreach (var (id, value) in source.LeveledNpcs) target.LeveledNpcs[map(id)] = value.Select(map).ToList();
    }

    private static uint? MapOptional(uint? id, Func<uint, uint> map) => id is null ? null : map(id.Value);

    private static NpcScanEntry Rebase(NpcScanEntry value, Func<uint, uint> map) => new()
    {
        EditorId = value.EditorId,
        FullName = value.FullName,
        RaceFormId = MapOptional(value.RaceFormId, map),
        IsFemale = value.IsFemale,
        HairFormId = MapOptional(value.HairFormId, map),
        EyesFormId = MapOptional(value.EyesFormId, map),
        HeadPartFormIds = value.HeadPartFormIds?.Select(map).ToList(),
        HairColor = value.HairColor,
        HairLength = value.HairLength,
        FaceGenSymmetric = value.FaceGenSymmetric,
        FaceGenAsymmetric = value.FaceGenAsymmetric,
        FaceGenTexture = value.FaceGenTexture,
        SpecialStats = value.SpecialStats,
        Skills = value.Skills,
        InventoryItems = value.InventoryItems?.Select(item => item with { ItemFormId = map(item.ItemFormId) }).ToList(),
        PackageFormIds = value.PackageFormIds?.Select(map).ToList(),
        TemplateFormId = MapOptional(value.TemplateFormId, map),
        TemplateFlags = value.TemplateFlags,
        CombatStyleFormId = MapOptional(value.CombatStyleFormId, map),
    };

    private static RaceScanEntry Rebase(RaceScanEntry value, Func<uint, uint> map) => new()
    {
        EditorId = value.EditorId,
        OlderRaceFormId = MapOptional(value.OlderRaceFormId, map),
        YoungerRaceFormId = MapOptional(value.YoungerRaceFormId, map),
        DefaultEyesFormId = MapOptional(value.DefaultEyesFormId, map),
        MaleHeadModelPath = value.MaleHeadModelPath,
        FemaleHeadModelPath = value.FemaleHeadModelPath,
        MaleHeadTexturePath = value.MaleHeadTexturePath,
        FemaleHeadTexturePath = value.FemaleHeadTexturePath,
        MaleEarModelPath = value.MaleEarModelPath,
        FemaleEarModelPath = value.FemaleEarModelPath,
        MaleEarTexturePath = value.MaleEarTexturePath,
        FemaleEarTexturePath = value.FemaleEarTexturePath,
        MaleMouthModelPath = value.MaleMouthModelPath,
        FemaleMouthModelPath = value.FemaleMouthModelPath,
        MaleLowerTeethModelPath = value.MaleLowerTeethModelPath,
        FemaleLowerTeethModelPath = value.FemaleLowerTeethModelPath,
        MaleUpperTeethModelPath = value.MaleUpperTeethModelPath,
        FemaleUpperTeethModelPath = value.FemaleUpperTeethModelPath,
        MaleTongueModelPath = value.MaleTongueModelPath,
        FemaleTongueModelPath = value.FemaleTongueModelPath,
        MaleEyeLeftModelPath = value.MaleEyeLeftModelPath,
        FemaleEyeLeftModelPath = value.FemaleEyeLeftModelPath,
        MaleEyeRightModelPath = value.MaleEyeRightModelPath,
        FemaleEyeRightModelPath = value.FemaleEyeRightModelPath,
        MaleFaceGenSymmetric = value.MaleFaceGenSymmetric,
        FemaleFaceGenSymmetric = value.FemaleFaceGenSymmetric,
        MaleFaceGenAsymmetric = value.MaleFaceGenAsymmetric,
        FemaleFaceGenAsymmetric = value.FemaleFaceGenAsymmetric,
        MaleFaceGenTexture = value.MaleFaceGenTexture,
        FemaleFaceGenTexture = value.FemaleFaceGenTexture,
        MaleUpperBodyPath = value.MaleUpperBodyPath,
        FemaleUpperBodyPath = value.FemaleUpperBodyPath,
        MaleLowerBodyPath = value.MaleLowerBodyPath,
        FemaleLowerBodyPath = value.FemaleLowerBodyPath,
        MaleHandPath = value.MaleHandPath,
        FemaleHandPath = value.FemaleHandPath,
        MaleFootPath = value.MaleFootPath,
        FemaleFootPath = value.FemaleFootPath,
        MaleTailPath = value.MaleTailPath,
        FemaleTailPath = value.FemaleTailPath,
        MaleLeftHandPath = value.MaleLeftHandPath,
        FemaleLeftHandPath = value.FemaleLeftHandPath,
        MaleRightHandPath = value.MaleRightHandPath,
        FemaleRightHandPath = value.FemaleRightHandPath,
        MaleBodyTexturePath = value.MaleBodyTexturePath,
        FemaleBodyTexturePath = value.FemaleBodyTexturePath,
        MaleLowerBodyTexturePath = value.MaleLowerBodyTexturePath,
        FemaleLowerBodyTexturePath = value.FemaleLowerBodyTexturePath,
        MaleHandTexturePath = value.MaleHandTexturePath,
        FemaleHandTexturePath = value.FemaleHandTexturePath,
        MaleFootTexturePath = value.MaleFootTexturePath,
        FemaleFootTexturePath = value.FemaleFootTexturePath,
        MaleTailTexturePath = value.MaleTailTexturePath,
        FemaleTailTexturePath = value.FemaleTailTexturePath,
    };

    private static HairScanEntry Rebase(HairScanEntry value, Func<uint, uint> map) => new()
    {
        EditorId = value.EditorId,
        ModelPath = value.ModelPath,
        TexturePath = value.TexturePath,
    };

    private static EyesScanEntry Rebase(EyesScanEntry value, Func<uint, uint> map) => new()
    {
        EditorId = value.EditorId,
        TexturePath = value.TexturePath,
    };

    private static HdptScanEntry Rebase(HdptScanEntry value, Func<uint, uint> map) => new()
    {
        EditorId = value.EditorId,
        ModelPath = value.ModelPath,
    };

    private static ArmoScanEntry Rebase(ArmoScanEntry value, Func<uint, uint> map) => new()
    {
        EditorId = value.EditorId,
        IsClothing = value.IsClothing,
        BaseArmorRating = value.BaseArmorRating,
        BaseValue = value.BaseValue,
        BipedFlags = value.BipedFlags,
        GeneralFlags = value.GeneralFlags,
        MaleBipedModelPath = value.MaleBipedModelPath,
        FemaleBipedModelPath = value.FemaleBipedModelPath,
        BipedModelListFormId = MapOptional(value.BipedModelListFormId, map),
    };

    private static ArmaAddonScanEntry Rebase(ArmaAddonScanEntry value, Func<uint, uint> map) => new()
    {
        EditorId = value.EditorId,
        BipedFlags = value.BipedFlags,
        MaleModelPath = value.MaleModelPath,
        FemaleModelPath = value.FemaleModelPath,
    };

    private static WeapScanEntry Rebase(WeapScanEntry value, Func<uint, uint> map) => new()
    {
        EditorId = value.EditorId,
        ModelPath = value.ModelPath,
        Mod2ModelPath = value.Mod2ModelPath,
        WeaponType = value.WeaponType,
        Damage = value.Damage,
        Health = value.Health,
        ShotsPerSec = value.ShotsPerSec,
        Spread = value.Spread,
        MinRange = value.MinRange,
        MaxRange = value.MaxRange,
        Flags = value.Flags,
        FlagsEx = value.FlagsEx,
        AmmoFormId = MapOptional(value.AmmoFormId, map),
        SkillActorValue = value.SkillActorValue,
        SkillRequirement = value.SkillRequirement,
        StrengthRequirement = value.StrengthRequirement,
        HandGripAnim = value.HandGripAnim,
        EmbeddedWeaponNode = value.EmbeddedWeaponNode,
        AttachmentPoseKfPath = value.AttachmentPoseKfPath,
    };

    private static PackageScanEntry Rebase(PackageScanEntry value, Func<uint, uint> map) => new()
    {
        EditorId = value.EditorId,
        Type = value.Type,
        GeneralFlags = value.GeneralFlags,
        UseWeaponFormId = MapOptional(value.UseWeaponFormId, map),
    };

    private static IdleScanEntry Rebase(IdleScanEntry value, Func<uint, uint> map) => new()
    {
        EditorId = value.EditorId,
        ModelPath = value.ModelPath,
        ParentIdleFormId = MapOptional(value.ParentIdleFormId, map),
        PreviousIdleFormId = MapOptional(value.PreviousIdleFormId, map),
        AnimData = value.AnimData,
        LoopMin = value.LoopMin,
        LoopMax = value.LoopMax,
        ReplayDelay = value.ReplayDelay,
        FlagsEx = value.FlagsEx,
    };

    private static LeveledListScanEntry Rebase(LeveledListScanEntry value, Func<uint, uint> map) => new()
    {
        EditorId = value.EditorId,
        ChanceNone = value.ChanceNone,
        Flags = value.Flags,
        Entries = value.Entries.Select(item => item with { FormId = map(item.FormId) }).ToList(),
    };
}
