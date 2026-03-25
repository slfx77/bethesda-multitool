using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;

internal sealed class NpcWeaponResolver
{
    private const uint PackageWeaponsUnequippedFlag = 0x00200000;
    private const uint WeaponEmbeddedFlag = 0x20;
    private const uint WeaponNotUsedInNormalCombatFlag = 0x00000040;

    private static readonly HashSet<WeaponType> NonRenderableWeaponTypes =
    [
        WeaponType.OneHandGrenade,
        WeaponType.OneHandMine,
        WeaponType.OneHandLunchboxMine
    ];

    private static readonly (string From, string To)[] OppositeHandSuffixPairs =
    [
        ("_fl", "_fr"),
        ("_fr", "_fl"),
        ("_ml", "_mr"),
        ("_mr", "_ml"),
        ("_l", "_r"),
        ("_r", "_l"),
        ("lt", "rt"),
        ("rt", "lt")
    ];

    private readonly Dictionary<string, List<ArmaAddonScanEntry>> _handToHandAddonsByPath;
    private readonly Dictionary<string, IdleScanEntry> _idlesByEditorId;
    private readonly IReadOnlyDictionary<uint, List<uint>> _leveledItems;

    private readonly IReadOnlyDictionary<uint, PackageScanEntry> _packages;
    private readonly IReadOnlyDictionary<uint, WeapScanEntry> _weapons;

    internal NpcWeaponResolver(
        IReadOnlyDictionary<uint, PackageScanEntry> packages,
        IReadOnlyDictionary<uint, WeapScanEntry> weapons,
        IReadOnlyDictionary<uint, ArmaAddonScanEntry> armorAddons,
        IReadOnlyDictionary<uint, List<uint>> leveledItems,
        IReadOnlyDictionary<uint, IdleScanEntry> idles)
    {
        _packages = packages;
        _weapons = weapons;
        _leveledItems = leveledItems;
        _idlesByEditorId = BuildIdleEditorLookup(idles);
        _handToHandAddonsByPath = BuildHandToHandAddonLookup(armorAddons);
    }

    internal WeaponVisual Resolve(
        NpcScanEntry npc,
        List<InventoryItem>? inventoryItems,
        RuntimeWeaponSelection? runtimeSelection = null)
    {
        if (runtimeSelection is { HasRuntimeTarget: true })
        {
            if (runtimeSelection.Value.WeaponFormId.HasValue &&
                TryBuildVisual(
                    runtimeSelection.Value.WeaponFormId.Value,
                    WeaponVisualSourceKind.DmpRuntimeCurrent,
                    runtimeSelection.Value.ActorRefFormId,
                    npc.IsFemale,
                    out var runtimeVisual))
            {
                return runtimeVisual;
            }

            return BuildOmitted(WeaponVisualSourceKind.OmittedUnequipped);
        }

        var resolvedPackages = ResolvePackages(npc.PackageFormIds);
        if (resolvedPackages.Count > 0 &&
            resolvedPackages.All(package => (package.GeneralFlags & PackageWeaponsUnequippedFlag) != 0))
        {
            return BuildOmitted(WeaponVisualSourceKind.OmittedUnequipped);
        }

        foreach (var package in resolvedPackages)
        {
            if (package.Type != 16 || !package.UseWeaponFormId.HasValue)
            {
                continue;
            }

            if (TryBuildVisual(
                    package.UseWeaponFormId.Value,
                    WeaponVisualSourceKind.EsmPackage,
                    null,
                    npc.IsFemale,
                    out var packageVisual))
            {
                return packageVisual;
            }

            return BuildOmitted(WeaponVisualSourceKind.OmittedUnresolved);
        }

        var bestWeapon = SelectBestWeapon(npc, inventoryItems);
        if (bestWeapon == null)
        {
            return BuildOmitted(WeaponVisualSourceKind.OmittedUnresolved);
        }

        return bestWeapon;
    }

    private List<PackageScanEntry> ResolvePackages(List<uint>? packageFormIds)
    {
        var packages = new List<PackageScanEntry>();
        if (packageFormIds is not { Count: > 0 })
        {
            return packages;
        }

        foreach (var packageFormId in packageFormIds)
        {
            if (_packages.TryGetValue(packageFormId, out var package))
            {
                packages.Add(package);
            }
        }

        return packages;
    }

    private WeaponVisual? SelectBestWeapon(
        NpcScanEntry npc,
        List<InventoryItem>? inventoryItems)
    {
        if (inventoryItems is not { Count: > 0 })
        {
            return null;
        }

        var expandedInventory = ExpandInventory(inventoryItems);
        if (expandedInventory.Count == 0)
        {
            return null;
        }

        var ammoCounts = new Dictionary<uint, int>();
        foreach (var item in expandedInventory)
        {
            ammoCounts.TryGetValue(item.ItemFormId, out var count);
            ammoCounts[item.ItemFormId] = count + item.Count;
        }

        WeaponVisual? bestVisual = null;
        var bestScore = float.MinValue;

        foreach (var item in expandedInventory)
        {
            if (item.Count <= 0 ||
                !_weapons.TryGetValue(item.ItemFormId, out var weapon) ||
                !IsRenderableCombatWeapon(weapon) ||
                string.IsNullOrWhiteSpace(weapon.ModelPath) ||
                !HasAmmo(weapon, ammoCounts))
            {
                continue;
            }

            if (!TryBuildVisual(
                    item.ItemFormId,
                    WeaponVisualSourceKind.EsmBestWeapon,
                    null,
                    npc.IsFemale,
                    out var visual))
            {
                continue;
            }

            var score = ScoreWeapon(npc, weapon);
            if (score > bestScore)
            {
                bestScore = score;
                bestVisual = visual;
            }
        }

        return bestVisual;
    }

    private List<InventoryItem> ExpandInventory(List<InventoryItem> inventoryItems)
    {
        var expanded = new List<InventoryItem>();
        foreach (var inventoryItem in inventoryItems)
        {
            ExpandInventoryItem(
                inventoryItem.ItemFormId,
                inventoryItem.Count,
                0,
                expanded);
        }

        return expanded;
    }

    private void ExpandInventoryItem(
        uint formId,
        int count,
        int depth,
        List<InventoryItem> expanded)
    {
        if (count <= 0 || depth > 5)
        {
            return;
        }

        if (_leveledItems.TryGetValue(formId, out var entries))
        {
            foreach (var entryFormId in entries)
            {
                ExpandInventoryItem(entryFormId, count, depth + 1, expanded);
            }

            return;
        }

        expanded.Add(new InventoryItem(formId, count));
    }

    private bool TryBuildVisual(
        uint weaponFormId,
        WeaponVisualSourceKind sourceKind,
        uint? runtimeActorFormId,
        bool isFemale,
        out WeaponVisual weaponVisual)
    {
        weaponVisual = BuildOmitted(WeaponVisualSourceKind.OmittedUnresolved);

        if (!_weapons.TryGetValue(weaponFormId, out var weapon) ||
            string.IsNullOrWhiteSpace(weapon.ModelPath) ||
            !IsRenderableCombatWeapon(weapon))
        {
            return false;
        }

        var attachmentMode = ResolveAttachmentMode(weapon.WeaponType);
        string? holsterProfileKey = null;
        if (TryResolveHolsterProfileKey(weapon.WeaponType, out var resolvedHolsterProfileKey))
        {
            holsterProfileKey = resolvedHolsterProfileKey;
        }

        if (attachmentMode == WeaponAttachmentMode.HolsterPose && holsterProfileKey == null)
        {
            return false;
        }

        var meshPath = NpcAppearancePathDeriver.AsMeshPath(weapon.ModelPath);
        if (meshPath == null)
        {
            return false;
        }

        var addonMeshes = BuildWeaponAddons(weapon, isFemale, out var suppressStandaloneMesh);
        var equippedPoseKfPath = ResolveEquippedPoseKfPath(weapon);
        var preferEquippedForearmMount = IsPowerFistFamilyModelPath(weapon.ModelPath);

        weaponVisual = new WeaponVisual
        {
            WeaponFormId = weaponFormId,
            EditorId = weapon.EditorId,
            SourceKind = sourceKind,
            IsVisible = true,
            WeaponType = weapon.WeaponType,
            AttachmentMode = attachmentMode,
            MeshPath = meshPath,
            HolsterProfileKey = holsterProfileKey,
            RuntimeActorFormId = runtimeActorFormId,
            AmmoFormId = weapon.AmmoFormId,
            IsEmbeddedWeapon = (weapon.Flags & WeaponEmbeddedFlag) != 0,
            EmbeddedWeaponNode = weapon.EmbeddedWeaponNode,
            EquippedPoseKfPath = equippedPoseKfPath,
            PreferEquippedForearmMount = preferEquippedForearmMount,
            RenderStandaloneMesh = !suppressStandaloneMesh,
            AddonMeshes = addonMeshes
        };
        return true;
    }

    private List<WeaponAddonVisual>? BuildWeaponAddons(
        WeapScanEntry weapon,
        bool isFemale,
        out bool suppressStandaloneMesh)
    {
        suppressStandaloneMesh = false;
        if (weapon.WeaponType != WeaponType.HandToHandMelee)
        {
            return null;
        }

        var candidateKeys = BuildHandToHandAddonCandidateKeys(weapon.ModelPath);
        if (candidateKeys.Count == 0)
        {
            return null;
        }

        var addons = new List<ArmaAddonScanEntry>();
        var seenAddonKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidateKey in candidateKeys)
        {
            if (!_handToHandAddonsByPath.TryGetValue(candidateKey, out var matchedAddons))
            {
                continue;
            }

            foreach (var addon in matchedAddons)
            {
                var addonKey = $"{addon.EditorId}|{addon.BipedFlags:X}|{addon.MaleModelPath}|{addon.FemaleModelPath}";
                if (seenAddonKeys.Add(addonKey))
                {
                    addons.Add(addon);
                }
            }
        }

        if (addons.Count == 0)
        {
            return null;
        }

        var visuals = new List<WeaponAddonVisual>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primaryMeshPath = NpcAppearancePathDeriver.AsMeshPath(weapon.ModelPath);

        foreach (var addon in addons)
        {
            var meshPath = SelectAddonMeshPath(addon, isFemale);
            var derivedMeshPath = NpcAppearancePathDeriver.AsMeshPath(meshPath);
            if (derivedMeshPath == null || !seenPaths.Add(derivedMeshPath))
            {
                continue;
            }

            visuals.Add(new WeaponAddonVisual
            {
                BipedFlags = addon.BipedFlags,
                MeshPath = derivedMeshPath
            });

            if (primaryMeshPath != null &&
                string.Equals(primaryMeshPath, derivedMeshPath, StringComparison.OrdinalIgnoreCase))
            {
                suppressStandaloneMesh = true;
            }
        }

        return visuals.Count > 0 ? visuals : null;
    }

    private static List<string> BuildHandToHandAddonCandidateKeys(string? modelPath)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddKey(string? candidatePath)
        {
            var exactNormalized = NormalizeHandToHandAddonPath(candidatePath, false);
            if (exactNormalized != null && seen.Add(exactNormalized))
            {
                keys.Add(exactNormalized);
            }

            var strippedNormalized = NormalizeHandToHandAddonPath(candidatePath, true);
            if (strippedNormalized != null && seen.Add(strippedNormalized))
            {
                keys.Add(strippedNormalized);
            }
        }

        AddKey(modelPath);
        foreach (var siblingPath in DeriveOppositeHandVariantPaths(modelPath))
        {
            AddKey(siblingPath);
        }

        return keys;
    }

    private static IEnumerable<string> DeriveOppositeHandVariantPaths(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            yield break;
        }

        var normalized = modelPath.Trim().Replace('/', '\\');
        var extension = Path.GetExtension(normalized);
        if (extension.Length == 0)
        {
            yield break;
        }

        var stem = normalized[..^extension.Length];
        foreach (var (fromSuffix, toSuffix) in OppositeHandSuffixPairs)
        {
            if (!stem.EndsWith(fromSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return stem[..^fromSuffix.Length] + toSuffix + extension;
        }
    }

    private static Dictionary<string, List<ArmaAddonScanEntry>> BuildHandToHandAddonLookup(
        IReadOnlyDictionary<uint, ArmaAddonScanEntry> armorAddons)
    {
        var lookup = new Dictionary<string, List<ArmaAddonScanEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var addon in armorAddons.Values)
        {
            AddLookupEntry(addon.MaleModelPath, addon);
            AddLookupEntry(addon.FemaleModelPath, addon);
        }

        return lookup;

        void AddLookupEntry(string? modelPath, ArmaAddonScanEntry addon)
        {
            var key = NormalizeHandToHandAddonPath(modelPath, false);
            if (key == null)
            {
                return;
            }

            if (!lookup.TryGetValue(key, out var entries))
            {
                entries = [];
                lookup[key] = entries;
            }

            entries.Add(addon);
        }
    }

    private static Dictionary<string, IdleScanEntry> BuildIdleEditorLookup(
        IReadOnlyDictionary<uint, IdleScanEntry> idles)
    {
        var lookup = new Dictionary<string, IdleScanEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var idle in idles.Values)
        {
            if (!string.IsNullOrWhiteSpace(idle.EditorId))
            {
                lookup[idle.EditorId] = idle;
            }
        }

        return lookup;
    }

    private string? ResolveEquippedPoseKfPath(WeapScanEntry weapon)
    {
        if (weapon.WeaponType != WeaponType.HandToHandMelee ||
            string.IsNullOrWhiteSpace(weapon.ModelPath))
        {
            return null;
        }

        var normalizedModelPath = weapon.ModelPath
            .Trim()
            .Replace('/', '\\')
            .ToLowerInvariant();

        if (!IsPowerFistFamilyModelPath(normalizedModelPath))
        {
            return null;
        }

        // The scanned Power Fist-specific IDLEs in the shipping data are VATS attack poses.
        // They visibly misplace held fist weapons when reused as a normal equipped idle.
        // Only accept a non-VATS fist pose here.
        var nonVatsFistIdle = _idlesByEditorId.Values
            .Where(idle =>
                !string.IsNullOrWhiteSpace(idle.EditorId) &&
                !string.IsNullOrWhiteSpace(idle.ModelPath) &&
                idle.EditorId.Contains("PowerFist", StringComparison.OrdinalIgnoreCase) &&
                !idle.EditorId.Contains("VATS", StringComparison.OrdinalIgnoreCase) &&
                !idle.ModelPath.Contains("VATS", StringComparison.OrdinalIgnoreCase))
            .OrderBy(idle => idle.EditorId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return nonVatsFistIdle != null
            ? NpcAppearancePathDeriver.AsMeshPath(nonVatsFistIdle.ModelPath)
            : null;
    }

    private static string? NormalizeHandToHandAddonPath(string? modelPath, bool stripVariantSuffix)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return null;
        }

        var normalized = modelPath.Trim()
            .Replace('/', '\\')
            .ToLowerInvariant();

        if (!normalized.Contains(@"weapons\hand2hand\", StringComparison.Ordinal))
        {
            return null;
        }

        if (!stripVariantSuffix)
        {
            return normalized;
        }

        if (normalized.EndsWith("rigid.nif", StringComparison.Ordinal))
        {
            return normalized[..^"rigid.nif".Length] + ".nif";
        }

        if (normalized.EndsWith("worldobject.nif", StringComparison.Ordinal))
        {
            return normalized[..^"worldobject.nif".Length] + ".nif";
        }

        return normalized;
    }

    private static string? SelectAddonMeshPath(ArmaAddonScanEntry addon, bool isFemale)
    {
        if (isFemale)
        {
            return addon.FemaleModelPath ?? addon.MaleModelPath;
        }

        return addon.MaleModelPath ?? addon.FemaleModelPath;
    }

    private static bool IsPowerFistFamilyModelPath(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return false;
        }

        var normalizedModelPath = modelPath
            .Trim()
            .Replace('/', '\\')
            .ToLowerInvariant();

        return normalizedModelPath.Contains("powerfist", StringComparison.Ordinal) ||
               normalizedModelPath.Contains("ballisticfist", StringComparison.Ordinal);
    }

    private static WeaponVisual BuildOmitted(WeaponVisualSourceKind sourceKind)
    {
        return new WeaponVisual
        {
            SourceKind = sourceKind,
            IsVisible = false,
            AttachmentMode = WeaponAttachmentMode.HolsterPose,
            MeshPath = null,
            HolsterProfileKey = null
        };
    }

    private static bool IsRenderableCombatWeapon(WeapScanEntry weapon)
    {
        return !NonRenderableWeaponTypes.Contains(weapon.WeaponType) &&
               (weapon.FlagsEx & WeaponNotUsedInNormalCombatFlag) == 0;
    }

    private static bool HasAmmo(WeapScanEntry weapon, Dictionary<uint, int> ammoCounts)
    {
        if (!weapon.AmmoFormId.HasValue)
        {
            return true;
        }

        return ammoCounts.TryGetValue(weapon.AmmoFormId.Value, out var count) && count > 0;
    }

    private static float ScoreWeapon(NpcScanEntry npc, WeapScanEntry weapon)
    {
        var baseDps = MathF.Max(weapon.Damage, 0) * MathF.Max(weapon.ShotsPerSec, 0.1f);
        var effectiveRange = weapon.MaxRange > 0f
            ? MathF.Max(weapon.MaxRange - weapon.MinRange, weapon.MaxRange)
            : 0f;
        var rangeFactor = 1f + MathF.Min(effectiveRange / 2000f, 0.75f);
        var spreadPenalty = 1f / (1f + MathF.Max(weapon.Spread, 0f) * 0.5f);
        var skill = ResolveActorSkill(npc.Skills, weapon.SkillActorValue);
        var skillFactor = 0.5f + Math.Clamp(skill / 100f, 0f, 1f);
        var skillRequirementPenalty = weapon.SkillRequirement > 0 && skill < weapon.SkillRequirement
            ? 0.35f
            : 1f;
        var strength = npc.SpecialStats is { Length: > 0 }
            ? npc.SpecialStats[0]
            : (byte)10;
        var strengthPenalty = weapon.StrengthRequirement > 0 && strength < weapon.StrengthRequirement
            ? 0.65f
            : 1f;
        var conditionFactor = weapon.Health > 0 ? 1f : 0.85f;

        return baseDps * rangeFactor * spreadPenalty * skillFactor * skillRequirementPenalty * strengthPenalty *
               conditionFactor;
    }

    private static float ResolveActorSkill(byte[]? skills, uint actorValueId)
    {
        if (skills == null || skills.Length == 0)
        {
            return 50f;
        }

        var index = actorValueId switch
        {
            32 => 0, // Barter
            33 => 1, // Big Guns
            34 => 2, // Energy Weapons
            35 => 3, // Explosives
            38 => 6, // Melee Weapons
            41 => 9, // Guns
            44 => 12, // Survival/Thrown
            45 => 13, // Unarmed
            _ => -1
        };

        if (index < 0 || index >= skills.Length)
        {
            return 50f;
        }

        return skills[index];
    }

    private static bool TryResolveHolsterProfileKey(WeaponType weaponType, out string holsterProfileKey)
    {
        holsterProfileKey = weaponType switch
        {
            WeaponType.OneHandPistol => "1hp",
            WeaponType.OneHandPistolEnergy => "1hp",
            WeaponType.TwoHandRifle => "2hr",
            WeaponType.TwoHandRifleEnergy => "2hr",
            WeaponType.TwoHandAutomatic => "2ha",
            WeaponType.OneHandMelee => "1hm",
            WeaponType.TwoHandMelee => "2hm",
            WeaponType.TwoHandHandle => "2hh",
            WeaponType.TwoHandLauncher => "2hl",
            WeaponType.HandToHandMelee => "h2h",
            WeaponType.OneHandGrenade => "1gt",
            WeaponType.OneHandMine => "1lm",
            WeaponType.OneHandLunchboxMine => "1md",
            WeaponType.OneHandThrown => "1gt",
            _ => ""
        };

        return holsterProfileKey.Length > 0;
    }

    private static WeaponAttachmentMode ResolveAttachmentMode(WeaponType weaponType)
    {
        return weaponType switch
        {
            WeaponType.HandToHandMelee => WeaponAttachmentMode.EquippedHandMounted,
            _ => WeaponAttachmentMode.HolsterPose
        };
    }

    internal readonly record struct RuntimeWeaponSelection(
        bool HasRuntimeTarget,
        uint? ActorRefFormId,
        uint? WeaponFormId);
}
