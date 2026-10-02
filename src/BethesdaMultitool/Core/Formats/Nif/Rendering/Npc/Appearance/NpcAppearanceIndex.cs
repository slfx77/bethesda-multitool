using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Semantic.LoadOrder;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;

/// <summary>
///     The scanned ESM record tables (NPCs, creatures, races, head parts, etc.) keyed by FormID that the appearance
///     resolver looks up.
/// </summary>
internal sealed class NpcAppearanceIndex
{
    public BethesdaGame Game { get; init; } = BethesdaGame.Unknown;

    internal Dictionary<uint, LoadOrderRecordVersion> Sources { get; } = [];
    internal Dictionary<string, string> SourceHashes { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<uint, BethesdaMultitool.Core.Assets.AssetRecordOwner> StoredOwners { get; } = [];

    internal BethesdaMultitool.Core.Assets.AssetRecordOwner Owner(uint? id, string signature)
    {
        if (id is { } value && Sources.TryGetValue(value, out var source))
            return BethesdaMultitool.Core.Assets.AssetRecordOwner.Selected(source, SourceHashes.GetValueOrDefault(source.FilePath));
        if (id is { } local && StoredOwners.TryGetValue(local, out var stored)) return stored;
        return BethesdaMultitool.Core.Assets.AssetRecordOwner.Unavailable(signature, id);
    }

    public Dictionary<uint, NpcScanEntry> Npcs { get; } =
        new();

    public Dictionary<uint, CreatureScanEntry> Creatures { get; } =
        new();

    public Dictionary<uint, RaceScanEntry> Races { get; } =
        new();

    public Dictionary<uint, HairScanEntry> Hairs { get; } =
        new();

    public Dictionary<uint, EyesScanEntry> Eyes { get; } =
        new();

    public Dictionary<uint, HdptScanEntry> HeadParts { get; } =
        new();

    public Dictionary<uint, ArmoScanEntry> Armors { get; } =
        new();

    public Dictionary<uint, ArmaAddonScanEntry> ArmorAddons { get; } =
        new();

    public Dictionary<uint, WeapScanEntry> Weapons { get; } =
        new();

    public Dictionary<uint, PackageScanEntry> Packages { get; } =
        new();

    public Dictionary<uint, IdleScanEntry> Idles { get; } =
        new();

    public Dictionary<uint, List<uint>> IdleChildrenByParent { get; } =
        new();

    public Dictionary<uint, List<uint>> FormLists { get; } =
        new();

    public Dictionary<uint, List<uint>> LeveledItems { get; } =
        new();

    /// <summary>Full LVLI metadata used when an engine-family-specific preview context is available.</summary>
    public Dictionary<uint, LeveledListScanEntry> LeveledItemRecords { get; } =
        new();

    public Dictionary<uint, List<uint>> LeveledNpcs { get; } =
        new();

    /// <summary>Full LVLN metadata retained alongside the legacy flattened template lookup.</summary>
    public Dictionary<uint, LeveledListScanEntry> LeveledNpcRecords { get; } =
        new();

    public Dictionary<uint, CstyEntry> CombatStyles { get; } =
        new();
}
