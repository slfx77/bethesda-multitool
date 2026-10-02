using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;

/// <summary>
///     Picks which weapon an NPC visibly carries (from AI package, best-weapon heuristic, or live DMP state) and how
///     it attaches.
/// </summary>
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

    private static readonly Logger Log = Logger.Instance;

    private readonly IReadOnlyDictionary<uint, CstyEntry> _combatStyles;
    private readonly BethesdaGame _game;

    private readonly Dictionary<string, List<ArmaAddonScanEntry>> _handToHandAddonsByPath;
    private readonly Dictionary<string, IdleScanEntry> _idlesByEditorId;
    private readonly IReadOnlyDictionary<uint, LeveledListScanEntry> _leveledItemRecords;
    private readonly IReadOnlyDictionary<uint, List<uint>> _leveledItems;

    private readonly IReadOnlyDictionary<uint, PackageScanEntry> _packages;
    private readonly IReadOnlyDictionary<uint, WeapScanEntry> _weapons;
    private readonly IReadOnlyDictionary<uint, ArmaAddonScanEntry> _assetAddons;

    internal NpcWeaponResolver(
        IReadOnlyDictionary<uint, PackageScanEntry> packages,
        IReadOnlyDictionary<uint, WeapScanEntry> weapons,
        IReadOnlyDictionary<uint, ArmaAddonScanEntry> armorAddons,
        IReadOnlyDictionary<uint, List<uint>> leveledItems,
        IReadOnlyDictionary<uint, IdleScanEntry> idles,
        IReadOnlyDictionary<uint, CstyEntry>? combatStyles = null,
        BethesdaGame game = BethesdaGame.Unknown,
        IReadOnlyDictionary<uint, LeveledListScanEntry>? leveledItemRecords = null)
    {
        _packages = packages;
        _weapons = weapons;
        _assetAddons = armorAddons;
        _leveledItems = leveledItems;
        _leveledItemRecords = leveledItemRecords ?? new Dictionary<uint, LeveledListScanEntry>();
        _combatStyles = combatStyles ?? new Dictionary<uint, CstyEntry>();
        _game = game;
        _idlesByEditorId = BuildIdleEditorLookup(idles);
        _handToHandAddonsByPath = BuildHandToHandAddonLookup(armorAddons);
    }

    /// <summary>Selects a visible weapon, restricting generated previews to positive-count concrete inventory members.</summary>
    internal WeaponVisual Resolve(
        NpcScanEntry npc,
        List<InventoryItem>? inventoryItems,
        RuntimeWeaponSelection? runtimeSelection = null,
        ushort? previewPlayerLevel = null,
        bool concreteInventoryOnly = false)
    {
        if (runtimeSelection is { HasRuntimeTarget: true })
        {
            if (runtimeSelection.Value.WeaponFormId.HasValue &&
                TryBuildVisual(
                    runtimeSelection.Value.WeaponFormId.Value,
                    WeaponVisualSourceKind.DmpRuntimeCurrent,
                    runtimeSelection.Value.ActorRefFormId,
                    npc.IsFemale,
                    null,
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
            if (package.Type != 16 || !package.UseWeaponFormId.HasValue ||
                (concreteInventoryOnly && inventoryItems?.Any(item =>
                    item.Count > 0 && item.ItemFormId == package.UseWeaponFormId.Value) != true))
            {
                continue;
            }

            if (TryBuildVisual(
                    package.UseWeaponFormId.Value,
                    concreteInventoryOnly
                        ? WeaponVisualSourceKind.GeneratedPreviewInventory
                        : WeaponVisualSourceKind.EsmPackage,
                    null,
                    npc.IsFemale,
                    null,
                    out var packageVisual))
            {
                return packageVisual;
            }

            return BuildOmitted(WeaponVisualSourceKind.OmittedUnresolved);
        }

        var bestWeapon = SelectBestWeapon(
            npc,
            inventoryItems,
            previewPlayerLevel,
            concreteInventoryOnly,
            out var leveledResolutionFailure);
        if (bestWeapon == null)
        {
            return leveledResolutionFailure ?? BuildOmitted(WeaponVisualSourceKind.OmittedUnresolved);
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

    /// <summary>Ranks renderable weapons using the existing heuristic, optionally bypassing all leveled-list expansion.</summary>
    private WeaponVisual? SelectBestWeapon(
        NpcScanEntry npc,
        List<InventoryItem>? inventoryItems,
        ushort? previewPlayerLevel,
        bool concreteInventoryOnly,
        out WeaponVisual? leveledResolutionFailure)
    {
        leveledResolutionFailure = null;
        if (inventoryItems is not { Count: > 0 })
        {
            return null;
        }

        var expandedInventory = ExpandInventory(
            inventoryItems,
            previewPlayerLevel,
            concreteInventoryOnly,
            out var leveledTrace);
        if (!previewPlayerLevel.HasValue && leveledTrace != null)
        {
            leveledResolutionFailure = BuildOmitted(
                WeaponVisualSourceKind.OmittedLeveledContextRequired,
                leveledTrace);
            Log.Debug(
                "NPC {0} weapon LVLI 0x{1:X8} ({2}) omitted: explicit preview player level required; LVLD={3}, LVLF=0x{4:X2}",
                npc.EditorId ?? npc.FullName ?? "?",
                leveledTrace.ListFormId,
                leveledTrace.ListEditorId ?? "?",
                leveledTrace.ChanceNone,
                leveledTrace.Flags);
            return null;
        }

        if (expandedInventory.Count == 0)
        {
            if (leveledTrace != null)
            {
                var sourceKind = leveledTrace.PreviewPlayerLevel.HasValue
                    ? WeaponVisualSourceKind.OmittedUnresolved
                    : WeaponVisualSourceKind.OmittedLeveledContextRequired;
                leveledResolutionFailure = BuildOmitted(sourceKind, leveledTrace);
                Log.Debug(
                    "NPC {0} weapon LVLI 0x{1:X8} ({2}) omitted: {3}; LVLD={4}, LVLF=0x{5:X2}",
                    npc.EditorId ?? npc.FullName ?? "?",
                    leveledTrace.ListFormId,
                    leveledTrace.ListEditorId ?? "?",
                    leveledTrace.PreviewPlayerLevel.HasValue
                        ? $"no eligible weapon at preview player level {leveledTrace.PreviewPlayerLevel.Value}"
                        : "explicit preview player level required",
                    leveledTrace.ChanceNone,
                    leveledTrace.Flags);
            }

            return null;
        }

        // Collect renderable candidates with their inventory FormId, then apply
        // CSTY Weapon Restrictions filtering and score the survivors. The scorer
        // applies a heavy penalty to weapons that require ammo (AmmoFormId set) since
        // NPC static inventories rarely include ammo entries — companions get ammo via
        // scripts and leveled lists at runtime.
        var candidates = new List<(uint FormId, WeapScanEntry Weapon, WeaponLeveledListTrace? Trace)>();
        foreach (var item in expandedInventory)
        {
            if (item.Count <= 0 ||
                !_weapons.TryGetValue(item.ItemFormId, out var weapon) ||
                !IsRenderableCombatWeapon(weapon) ||
                string.IsNullOrWhiteSpace(weapon.ModelPath))
            {
                continue;
            }

            candidates.Add((item.ItemFormId, weapon, item.LeveledListTrace));
        }

        if (candidates.Count == 0)
        {
            if (leveledTrace != null)
            {
                leveledResolutionFailure = BuildOmitted(
                    leveledTrace.PreviewPlayerLevel.HasValue
                        ? WeaponVisualSourceKind.OmittedUnresolved
                        : WeaponVisualSourceKind.OmittedLeveledContextRequired,
                    leveledTrace);
            }

            return null;
        }

        var restriction = ResolveWeaponRestriction(npc.CombatStyleFormId);
        var pool = candidates;
        if (restriction != WeaponRestriction.None)
        {
            var filtered = candidates
                .Where(c => WeaponSelectionScorer.MatchesRestriction(c.Weapon.WeaponType, restriction))
                .ToList();
            if (filtered.Count > 0)
            {
                pool = filtered;
            }
        }

        var strength = npc.SpecialStats is { Length: > 0 } ? npc.SpecialStats[0] : (byte)10;

        WeaponVisual? bestVisual = null;
        var bestScore = float.MinValue;
        foreach (var (formId, weapon, trace) in pool)
        {
            if (!TryBuildVisual(
                    formId,
                    concreteInventoryOnly
                        ? WeaponVisualSourceKind.GeneratedPreviewInventory
                        : WeaponVisualSourceKind.EsmBestWeapon,
                    null,
                    npc.IsFemale,
                    trace,
                    out var visual))
            {
                continue;
            }

            var score = WeaponSelectionScorer.Score(weapon, npc.Skills, null, strength);
            if (score > bestScore)
            {
                bestScore = score;
                bestVisual = visual;
            }
        }

        if (bestVisual?.LeveledListTrace is { } selectedTrace)
        {
            Log.Debug(
                "NPC {0} weapon LVLI 0x{1:X8} ({2}): previewLevel={3}, selectedTier={4}, entry=0x{5:X8}, weapon=0x{6:X8}, LVLD={7}, LVLF=0x{8:X2}",
                npc.EditorId ?? npc.FullName ?? "?",
                selectedTrace.ListFormId,
                selectedTrace.ListEditorId ?? "?",
                selectedTrace.PreviewPlayerLevel,
                selectedTrace.SelectedEntryLevel,
                selectedTrace.SelectedEntryFormId,
                bestVisual.WeaponFormId,
                selectedTrace.ChanceNone,
                selectedTrace.Flags);
        }

        return bestVisual;
    }

    private WeaponRestriction ResolveWeaponRestriction(uint? combatStyleFormId)
    {
        if (combatStyleFormId is { } id && _combatStyles.TryGetValue(id, out var csty))
        {
            return csty.Restriction;
        }

        return WeaponRestriction.None;
    }

    /// <summary>Adapts concrete generated leaves directly or expands legacy authored lists with the existing game policy.</summary>
    private List<ExpandedInventoryItem> ExpandInventory(
        List<InventoryItem> inventoryItems,
        ushort? previewPlayerLevel,
        bool concreteInventoryOnly,
        out WeaponLeveledListTrace? leveledTrace)
    {
        var expanded = new List<ExpandedInventoryItem>();
        leveledTrace = null;
        foreach (var inventoryItem in inventoryItems)
        {
            if (concreteInventoryOnly)
            {
                expanded.Add(new ExpandedInventoryItem(inventoryItem.ItemFormId, inventoryItem.Count, null));
                continue;
            }

            ExpandInventoryItem(
                inventoryItem.ItemFormId,
                inventoryItem.Count,
                0,
                previewPlayerLevel,
                null,
                expanded,
                ref leveledTrace);
        }

        return expanded;
    }

    private void ExpandInventoryItem(
        uint formId,
        int count,
        int depth,
        ushort? previewPlayerLevel,
        WeaponLeveledListTrace? inheritedTrace,
        List<ExpandedInventoryItem> expanded,
        ref WeaponLeveledListTrace? leveledTrace)
    {
        if (count <= 0 || depth > 5)
        {
            return;
        }

        if (_game == BethesdaGame.Oblivion &&
            _leveledItemRecords.TryGetValue(formId, out var leveledList))
        {
            var canYieldRenderableWeapon = CanYieldRenderableWeapon(formId, depth);
            if (!previewPlayerLevel.HasValue)
            {
                if (canYieldRenderableWeapon)
                {
                    leveledTrace ??= BuildLeveledTrace(formId, leveledList, null, null);
                }

                return;
            }

            var eligibleEntries = leveledList.Entries
                .Where(entry => entry.Level <= previewPlayerLevel.Value)
                .ToList();
            if (!leveledList.CalculateFromAllLevelsAtOrBelowPlayer && eligibleEntries.Count > 0)
            {
                var selectedLevel = eligibleEntries.Max(static entry => entry.Level);
                eligibleEntries.RemoveAll(entry => entry.Level != selectedLevel);
            }

            if (eligibleEntries.Count == 0)
            {
                if (canYieldRenderableWeapon)
                {
                    leveledTrace ??= BuildLeveledTrace(
                        formId,
                        leveledList,
                        previewPlayerLevel,
                        null);
                }

                return;
            }

            foreach (var entry in eligibleEntries)
            {
                var entryCanYieldRenderableWeapon = CanYieldRenderableWeapon(entry.FormId, depth + 1);
                var entryTrace = entryCanYieldRenderableWeapon
                    ? inheritedTrace ?? BuildLeveledTrace(
                        formId,
                        leveledList,
                        previewPlayerLevel,
                        entry)
                    : null;
                if (entryTrace != null)
                {
                    leveledTrace ??= entryTrace;
                }

                ExpandInventoryItem(
                    entry.FormId,
                    MultiplyCounts(count, entry.Count),
                    depth + 1,
                    previewPlayerLevel,
                    entryTrace,
                    expanded,
                    ref leveledTrace);
            }

            return;
        }

        if (_leveledItems.TryGetValue(formId, out var entries))
        {
            foreach (var entryFormId in entries)
            {
                ExpandInventoryItem(
                    entryFormId,
                    count,
                    depth + 1,
                    previewPlayerLevel,
                    inheritedTrace,
                    expanded,
                    ref leveledTrace);
            }

            return;
        }

        expanded.Add(new ExpandedInventoryItem(formId, count, inheritedTrace));
    }

    /// <summary>
    ///     Determines whether a leveled-list branch can ever produce a visible combat weapon.
    ///     This is deliberately independent of preview level: when no level was supplied, it lets
    ///     omission telemetry identify the relevant weapon LVLI instead of whichever clothing or
    ///     armor list happened to appear first in CNTO order.
    /// </summary>
    private bool CanYieldRenderableWeapon(uint formId, int depth, HashSet<uint>? visited = null)
    {
        if (depth > 5)
        {
            return false;
        }

        if (_weapons.TryGetValue(formId, out var weapon))
        {
            return IsRenderableCombatWeapon(weapon) &&
                   !string.IsNullOrWhiteSpace(weapon.ModelPath);
        }

        visited ??= [];
        if (!visited.Add(formId))
        {
            return false;
        }

        var result = _leveledItemRecords.TryGetValue(formId, out var leveledList)
            ? leveledList.Entries.Any(entry =>
                CanYieldRenderableWeapon(entry.FormId, depth + 1, visited))
            : _leveledItems.TryGetValue(formId, out var entries) &&
              entries.Any(entry => CanYieldRenderableWeapon(entry, depth + 1, visited));
        visited.Remove(formId);
        return result;
    }

    private static WeaponLeveledListTrace BuildLeveledTrace(
        uint listFormId,
        LeveledListScanEntry leveledList,
        ushort? previewPlayerLevel,
        LeveledEntry? selectedEntry)
    {
        return new WeaponLeveledListTrace(
            listFormId,
            leveledList.EditorId,
            leveledList.ChanceNone,
            leveledList.Flags,
            previewPlayerLevel,
            selectedEntry?.Level,
            selectedEntry?.FormId,
            selectedEntry?.Count);
    }

    private static int MultiplyCounts(int parentCount, ushort entryCount)
    {
        return (int)Math.Min((long)parentCount * entryCount, int.MaxValue);
    }

    private bool TryBuildVisual(
        uint weaponFormId,
        WeaponVisualSourceKind sourceKind,
        uint? runtimeActorFormId,
        bool isFemale,
        WeaponLeveledListTrace? leveledListTrace,
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
            AttachmentPoseKfPath = weapon.AttachmentPoseKfPath,
            RuntimeActorFormId = runtimeActorFormId,
            AmmoFormId = weapon.AmmoFormId,
            IsEmbeddedWeapon = (weapon.Flags & WeaponEmbeddedFlag) != 0,
            EmbeddedWeaponNode = weapon.EmbeddedWeaponNode,
            EquippedPoseKfPath = equippedPoseKfPath,
            PreferEquippedForearmMount = preferEquippedForearmMount,
            RenderStandaloneMesh = !suppressStandaloneMesh,
            AddonMeshes = addonMeshes,
            LeveledListTrace = leveledListTrace
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
        var seenAddons = new HashSet<ArmaAddonScanEntry>(ReferenceEqualityComparer.Instance);
        foreach (var candidateKey in candidateKeys)
        {
            if (!_handToHandAddonsByPath.TryGetValue(candidateKey, out var matchedAddons))
            {
                continue;
            }

            foreach (var addon in matchedAddons)
            {
                if (seenAddons.Add(addon))
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
            if (derivedMeshPath == null)
            {
                continue;
            }

            var visual = visuals.FirstOrDefault(item => string.Equals(item.MeshPath, derivedMeshPath, StringComparison.OrdinalIgnoreCase));
            if (seenPaths.Add(derivedMeshPath))
            {
                visual = new WeaponAddonVisual { BipedFlags = addon.BipedFlags, MeshPath = derivedMeshPath };
                visuals.Add(visual);
            }
            foreach (var id in _assetAddons.Where(pair => ReferenceEquals(pair.Value, addon)).Select(pair => pair.Key))
                visual!.AssetOwners.Add(new(id, isFemale && !string.IsNullOrEmpty(addon.FemaleModelPath) ? "FemaleModelPath" : "MaleModelPath"));

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

    private static WeaponVisual BuildOmitted(
        WeaponVisualSourceKind sourceKind,
        WeaponLeveledListTrace? leveledListTrace = null)
    {
        return new WeaponVisual
        {
            SourceKind = sourceKind,
            IsVisible = false,
            AttachmentMode = WeaponAttachmentMode.HolsterPose,
            MeshPath = null,
            HolsterProfileKey = null,
            LeveledListTrace = leveledListTrace
        };
    }

    private static bool IsRenderableCombatWeapon(WeapScanEntry weapon)
    {
        return !NonRenderableWeaponTypes.Contains(weapon.WeaponType) &&
               (weapon.FlagsEx & WeaponNotUsedInNormalCombatFlag) == 0;
    }


    internal static bool TryResolveHolsterProfileKey(WeaponType weaponType, out string holsterProfileKey)
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

    /// <summary>
    ///     A weapon selection read from a DMP's live actor state: the target actor reference and its equipped weapon
    ///     FormID.
    /// </summary>
    internal readonly record struct RuntimeWeaponSelection(
        bool HasRuntimeTarget,
        uint? ActorRefFormId,
        uint? WeaponFormId);

    private readonly record struct ExpandedInventoryItem(
        uint ItemFormId,
        int Count,
        WeaponLeveledListTrace? LeveledListTrace);
}
