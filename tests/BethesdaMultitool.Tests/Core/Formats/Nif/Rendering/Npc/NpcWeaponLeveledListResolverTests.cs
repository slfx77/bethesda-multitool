using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcWeaponLeveledListResolverTests
{
    private const uint LongswordListFormId = 0x0003ABC0;
    private const uint ClothingListFormId = 0x0004B916;

    [Theory]
    [InlineData((ushort)1, 0x00000C0Cu, (ushort)1)]
    [InlineData((ushort)10, 0x000229B3u, (ushort)9)]
    [InlineData((ushort)20, 0x00035E76u, (ushort)20)]
    public void OblivionClosestEligibleTier_UsesExplicitPreviewPlayerLevel(
        ushort previewPlayerLevel,
        uint expectedWeaponFormId,
        ushort expectedTier)
    {
        var resolver = CreateResolver(BethesdaGame.Oblivion, 0x02);

        var visual = resolver.Resolve(
            new NpcScanEntry { EditorId = "MazogatheOrc" },
            [new InventoryItem(LongswordListFormId, 1)],
            previewPlayerLevel: previewPlayerLevel);

        Assert.True(visual.IsVisible);
        Assert.Equal(expectedWeaponFormId, visual.WeaponFormId);
        var trace = Assert.IsType<WeaponLeveledListTrace>(visual.LeveledListTrace);
        Assert.Equal(LongswordListFormId, trace.ListFormId);
        Assert.Equal(previewPlayerLevel, trace.PreviewPlayerLevel);
        Assert.Equal(expectedTier, trace.SelectedEntryLevel);
        Assert.Equal(expectedWeaponFormId, trace.SelectedEntryFormId);
        Assert.Equal((ushort)1, trace.SelectedEntryCount);
        Assert.Equal(0x02, trace.Flags);
    }

    [Fact]
    public void OblivionLeveledWeaponWithoutPreviewLevel_FailsClosedWithTrace()
    {
        var resolver = CreateResolver(BethesdaGame.Oblivion, 0x02);

        var visual = resolver.Resolve(
            new NpcScanEntry { EditorId = "MazogatheOrc" },
            [new InventoryItem(LongswordListFormId, 1)]);

        Assert.False(visual.IsVisible);
        Assert.Equal(WeaponVisualSourceKind.OmittedLeveledContextRequired, visual.SourceKind);
        var trace = Assert.IsType<WeaponLeveledListTrace>(visual.LeveledListTrace);
        Assert.Equal(LongswordListFormId, trace.ListFormId);
        Assert.Null(trace.PreviewPlayerLevel);
        Assert.Null(trace.SelectedEntryLevel);
        Assert.Null(trace.SelectedEntryFormId);
    }

    [Fact]
    public void OblivionNoContextTrace_SkipsEarlierNonWeaponLeveledInventory()
    {
        var resolver = CreateResolver(
            BethesdaGame.Oblivion,
            0x02,
            includeEarlierClothingList: true);

        var visual = resolver.Resolve(
            new NpcScanEntry { EditorId = "MazogatheOrc" },
            [
                new InventoryItem(ClothingListFormId, 1),
                new InventoryItem(LongswordListFormId, 1)
            ]);

        Assert.False(visual.IsVisible);
        Assert.Equal(WeaponVisualSourceKind.OmittedLeveledContextRequired, visual.SourceKind);
        Assert.Equal(LongswordListFormId, visual.LeveledListTrace?.ListFormId);
    }

    [Fact]
    public void OblivionNoContext_DoesNotPretendDirectWeaponWinsOverUnresolvedList()
    {
        var resolver = CreateResolver(BethesdaGame.Oblivion, 0x02);

        var visual = resolver.Resolve(
            new NpcScanEntry { EditorId = "MixedInventoryNpc" },
            [
                new InventoryItem(0x00000C0C, 1),
                new InventoryItem(LongswordListFormId, 1)
            ]);

        Assert.False(visual.IsVisible);
        Assert.Equal(WeaponVisualSourceKind.OmittedLeveledContextRequired, visual.SourceKind);
        Assert.Equal(LongswordListFormId, visual.LeveledListTrace?.ListFormId);
    }

    [Fact]
    public void OblivionAllEligibleLevelsFlag_RetainsAllEligibleCandidatesForStaticScoring()
    {
        var resolver = CreateResolver(
            BethesdaGame.Oblivion,
            0x03,
            new Dictionary<uint, short>
            {
                [0x00000C0C] = 100,
                [0x000229B3] = 1
            });

        var visual = resolver.Resolve(
            new NpcScanEntry(),
            [new InventoryItem(LongswordListFormId, 1)],
            previewPlayerLevel: 10);

        Assert.Equal(0x00000C0Cu, visual.WeaponFormId);
        Assert.Equal((ushort)1, visual.LeveledListTrace?.SelectedEntryLevel);
    }

    [Fact]
    public void FalloutNewVegasWithoutPreviewLevel_PreservesLegacyStaticExpansion()
    {
        var resolver = CreateResolver(BethesdaGame.FalloutNewVegas, 0x02);

        var visual = resolver.Resolve(
            new NpcScanEntry(),
            [new InventoryItem(LongswordListFormId, 1)]);

        Assert.True(visual.IsVisible);
        Assert.Equal(0x00035E76u, visual.WeaponFormId);
        Assert.Null(visual.LeveledListTrace);
    }

    private static NpcWeaponResolver CreateResolver(
        BethesdaGame game,
        byte flags,
        IReadOnlyDictionary<uint, short>? weaponDamageOverrides = null,
        bool includeEarlierClothingList = false)
    {
        var entries = new List<LeveledEntry>
        {
            new(1, 0x00000C0C, 1),
            new(2, 0x000229BA, 1),
            new(4, 0x0002521F, 1),
            new(6, 0x00035DD1, 1),
            new(9, 0x000229B3, 1),
            new(12, 0x00035E5F, 1),
            new(16, 0x00035E6E, 1),
            new(20, 0x00035E76, 1)
        };
        var weapons = new Dictionary<uint, WeapScanEntry>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            weapons[entry.FormId] = new WeapScanEntry
            {
                EditorId = $"Tier{entry.Level}Longsword",
                ModelPath = $@"weapons\tier{entry.Level}\longsword.nif",
                WeaponType = WeaponType.OneHandMelee,
                Damage = weaponDamageOverrides?.GetValueOrDefault(entry.FormId) ?? (short)(10 + i * 2),
                Health = 100,
                ShotsPerSec = 1f,
                AttachmentPoseKfPath = "onehandidle.kf"
            };
        }

        var flattenedLists = new Dictionary<uint, List<uint>>
        {
            [LongswordListFormId] = entries.Select(static entry => entry.FormId).ToList()
        };
        var detailedLists = new Dictionary<uint, LeveledListScanEntry>
        {
            [LongswordListFormId] = new()
            {
                EditorId = "LL0NPCWeaponLongswordLvl100",
                ChanceNone = 0,
                Flags = flags,
                Entries = entries
            }
        };
        if (includeEarlierClothingList)
        {
            const uint lowerBodyFormId = 0x000229A7;
            flattenedLists[ClothingListFormId] = [lowerBodyFormId];
            detailedLists[ClothingListFormId] = new LeveledListScanEntry
            {
                EditorId = "LL0NPCClothingPantsLower",
                ChanceNone = 0,
                Flags = 0x02,
                Entries = [new LeveledEntry(1, lowerBodyFormId, 1)]
            };
        }

        return new NpcWeaponResolver(
            new Dictionary<uint, PackageScanEntry>(),
            weapons,
            new Dictionary<uint, ArmaAddonScanEntry>(),
            flattenedLists,
            new Dictionary<uint, IdleScanEntry>(),
            game: game,
            leveledItemRecords: detailedLists);
    }
}