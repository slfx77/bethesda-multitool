using BethesdaMultitool.Core.Actors;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

/// <summary>Guards the boundary between a generated inventory and the existing visual equipment selection.</summary>
public sealed class NpcGeneratedInventoryEquipmentTests
{
    private const uint ActorId = 1;
    private const uint LowWeapon = 10;
    private const uint HighWeapon = 11;
    private const uint GeneratedArmor = 20;
    private const uint StaticArmor = 21;
    private const uint WeaponList = 30;
    private const uint ArmorList = 31;
    private const uint PackageId = 40;
    private const uint TemplateId = 50;

    /// <summary>Both supported Fallout families equip exactly the concrete leaves instead of rerolling authored lists.</summary>
    [Theory]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    public void GeneratedLeaves_ReplaceStaticWeaponsAndArmor(BethesdaGame game)
    {
        var index = CreateIndex(game);
        var factory = new NpcAppearanceFactory(index);
        var generation = new ActorInventoryGeneration(42, 1,
            [new InventoryItem(LowWeapon, 1), new InventoryItem(GeneratedArmor, 1)], true);

        var appearance = factory.Build(ActorId, index.Npcs[ActorId], "Fallout.esm", generation: generation);

        Assert.Equal(LowWeapon, appearance.WeaponVisual?.WeaponFormId);
        Assert.Equal(WeaponVisualSourceKind.GeneratedPreviewInventory, appearance.WeaponVisual?.SourceKind);
        Assert.Equal(@"meshes\armor\generated.nif", Assert.Single(appearance.EquippedItems!).MeshPath);
        Assert.Null(appearance.WeaponVisual?.LeveledListTrace);
        Assert.Equal(2, generation.Items.Count);
    }

    /// <summary>An empty generated result must clear equipment even when the NPC inherits an equipped template.</summary>
    [Fact]
    public void EmptyGeneration_DoesNotFallBackToTemplateInventory()
    {
        var index = CreateIndex(BethesdaGame.FalloutNewVegas);
        index.Npcs[TemplateId] = index.Npcs[ActorId];
        var npc = new NpcScanEntry { TemplateFormId = TemplateId, TemplateFlags = 0x0100 };
        var factory = new NpcAppearanceFactory(index);

        var legacy = factory.Build(ActorId, npc, "FalloutNV.esm");
        var empty = factory.Build(ActorId, npc, "FalloutNV.esm",
            generation: new ActorInventoryGeneration(42, 1, [], true));

        Assert.True(legacy.WeaponVisual?.IsVisible);
        Assert.NotEmpty(legacy.EquippedItems!);
        Assert.False(empty.WeaponVisual?.IsVisible);
        Assert.Null(empty.EquippedItems);
    }

    /// <summary>A malformed residual list in the concrete-only boundary cannot expand into an ungenerated mesh.</summary>
    [Fact]
    public void ConcreteMode_RejectsResidualLeveledReferences()
    {
        var index = CreateIndex(BethesdaGame.FalloutNewVegas);
        var factory = new NpcAppearanceFactory(index);
        var generation = new ActorInventoryGeneration(42, 100,
            [new InventoryItem(WeaponList, 1), new InventoryItem(ArmorList, 1)], false);

        var appearance = factory.Build(ActorId, index.Npcs[ActorId], "FalloutNV.esm", generation: generation);

        Assert.False(appearance.WeaponVisual?.IsVisible);
        Assert.Null(appearance.EquippedItems);
    }

    /// <summary>A package may prefer an owned generated weapon, but cannot inject absent or nonpositive-count equipment.</summary>
    [Theory]
    [InlineData(-1, LowWeapon)]
    [InlineData(0, LowWeapon)]
    [InlineData(1, HighWeapon)]
    public void PackageWeapon_RequiresPositiveGeneratedMembership(int count, uint expectedWeapon)
    {
        var index = CreateIndex(BethesdaGame.FalloutNewVegas);
        index.Packages[PackageId] = new PackageScanEntry { Type = 16, UseWeaponFormId = HighWeapon };
        var npc = new NpcScanEntry { PackageFormIds = [PackageId] };
        var factory = new NpcAppearanceFactory(index);
        var generation = new ActorInventoryGeneration(42, 1,
            [new InventoryItem(LowWeapon, 1), new InventoryItem(HighWeapon, count)], true);

        var appearance = factory.Build(ActorId, npc, "FalloutNV.esm", generation: generation);

        Assert.Equal(expectedWeapon, appearance.WeaponVisual?.WeaponFormId);
        Assert.Equal(WeaponVisualSourceKind.GeneratedPreviewInventory, appearance.WeaponVisual?.SourceKind);
    }

    /// <summary>A package weapon omitted by the generator cannot reappear through a static package override.</summary>
    [Fact]
    public void PackageWeapon_AbsentFromGenerationDoesNotLeakIntoPreview()
    {
        var index = CreateIndex(BethesdaGame.FalloutNewVegas);
        index.Packages[PackageId] = new PackageScanEntry { Type = 16, UseWeaponFormId = HighWeapon };
        var npc = new NpcScanEntry { PackageFormIds = [PackageId] };
        var factory = new NpcAppearanceFactory(index);

        var appearance = factory.Build(ActorId, npc, "FalloutNV.esm",
            generation: new ActorInventoryGeneration(42, 1, [new InventoryItem(LowWeapon, 1)], true));

        Assert.Equal(LowWeapon, appearance.WeaponVisual?.WeaponFormId);
    }

    /// <summary>Partial generation uses its known leaves and cannot pollute a later unseeded static appearance.</summary>
    [Fact]
    public void IncompleteGeneration_UsesKnownLeavesAndLeavesLegacyResolutionUnchanged()
    {
        var index = CreateIndex(BethesdaGame.FalloutNewVegas);
        var factory = new NpcAppearanceFactory(index);
        var npc = index.Npcs[ActorId];

        var generated = factory.Build(ActorId, npc, "FalloutNV.esm",
            generation: new ActorInventoryGeneration(42, 1, [new InventoryItem(LowWeapon, 1)], false));
        var legacy = factory.Build(ActorId, npc, "FalloutNV.esm");

        Assert.Equal(LowWeapon, generated.WeaponVisual?.WeaponFormId);
        Assert.Null(generated.EquippedItems);
        Assert.Equal(HighWeapon, legacy.WeaponVisual?.WeaponFormId);
        Assert.Equal(WeaponVisualSourceKind.EsmBestWeapon, legacy.WeaponVisual?.SourceKind);
        Assert.Equal(@"meshes\armor\static.nif", Assert.Single(legacy.EquippedItems!).MeshPath);
        Assert.Equal(WeaponList, npc.InventoryItems![0].ItemFormId);
    }

    /// <summary>Creates authored lists with a stronger ungenerated weapon and different static armor to expose accidental fallback.</summary>
    private static NpcAppearanceIndex CreateIndex(BethesdaGame game)
    {
        var index = new NpcAppearanceIndex { Game = game };
        index.Npcs[ActorId] = new NpcScanEntry
        {
            InventoryItems = [new InventoryItem(WeaponList, 1), new InventoryItem(ArmorList, 1)]
        };
        index.Weapons[LowWeapon] = CreateWeapon("low", 10);
        index.Weapons[HighWeapon] = CreateWeapon("high", 100);
        index.Armors[GeneratedArmor] = new ArmoScanEntry
        {
            BipedFlags = 0x04, MaleBipedModelPath = @"armor\generated.nif"
        };
        index.Armors[StaticArmor] = new ArmoScanEntry
        {
            BipedFlags = 0x04, MaleBipedModelPath = @"armor\static.nif"
        };
        index.LeveledItems[WeaponList] = [LowWeapon, HighWeapon];
        index.LeveledItems[ArmorList] = [StaticArmor];
        return index;
    }

    /// <summary>Supplies a renderable melee weapon whose damage cleanly distinguishes the static ranking.</summary>
    private static WeapScanEntry CreateWeapon(string name, short damage)
    {
        return new WeapScanEntry
        {
            EditorId = name,
            ModelPath = $@"weapons\{name}.nif",
            WeaponType = WeaponType.OneHandMelee,
            Damage = damage,
            Health = 100,
            ShotsPerSec = 1f,
            AttachmentPoseKfPath = "onehandidle.kf"
        };
    }
}
