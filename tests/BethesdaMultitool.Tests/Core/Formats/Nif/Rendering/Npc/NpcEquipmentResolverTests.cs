using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

[Collection(SequentialIntegrationGroup.Name)]
public sealed class NpcEquipmentResolverTests
{
    [Fact]
    public void Resolve_OblivionLexInventory_PrefersImperialWatchArmorOverEarlierClothing()
    {
        var resolver = CreateResolver(
            new Dictionary<uint, ArmoScanEntry>
            {
                [0x000229AF] = new()
                {
                    IsClothing = true,
                    BipedFlags = 0x20,
                    MaleBipedModelPath = @"Clothes\MiddleClass\01\M\Shoes.NIF"
                },
                [0x000229AD] = new()
                {
                    IsClothing = true,
                    BipedFlags = 0x04,
                    MaleBipedModelPath = @"Clothes\MiddleClass\01\M\Shirt.NIF"
                },
                [0x000229AE] = new()
                {
                    IsClothing = true,
                    BipedFlags = 0x08,
                    MaleBipedModelPath = @"Clothes\MiddleClass\01\M\Pants.NIF"
                },
                [0x0018AE4B] = new()
                {
                    BaseArmorRating = 450,
                    BipedFlags = 0x20,
                    MaleBipedModelPath = @"Armor\ImperialWatch\M\Boots.NIF"
                },
                [0x0018AE4C] = new()
                {
                    BaseArmorRating = 1125,
                    BipedFlags = 0x04,
                    MaleBipedModelPath = @"Armor\ImperialWatch\M\Cuirass.NIF"
                },
                [0x0018AE4D] = new()
                {
                    BaseArmorRating = 450,
                    BipedFlags = 0x10,
                    MaleBipedModelPath = @"Armor\ImperialWatch\M\Gauntlets.NIF"
                },
                [0x0018AE4E] = new()
                {
                    BaseArmorRating = 675,
                    BipedFlags = 0x08,
                    MaleBipedModelPath = @"Armor\ImperialWatch\M\Greaves.NIF"
                }
            },
            BethesdaGame.Oblivion);

        var equippedItems = resolver.Resolve(
            [
                new InventoryItem(0x000229AF, 1),
                new InventoryItem(0x000229AD, 1),
                new InventoryItem(0x000229AE, 1),
                new InventoryItem(0x0003E4C8, 1),
                new InventoryItem(0x00053FF2, 1),
                new InventoryItem(0x00015EAF, 1),
                new InventoryItem(0x0018AE4B, 1),
                new InventoryItem(0x0018AE4C, 1),
                new InventoryItem(0x0018AE4D, 1),
                new InventoryItem(0x0018AE4E, 1),
                new InventoryItem(0x00025226, 1)
            ],
            false);

        Assert.NotNull(equippedItems);
        Assert.Equal(
            [
                @"meshes\Armor\ImperialWatch\M\Boots.NIF",
                @"meshes\Armor\ImperialWatch\M\Cuirass.NIF",
                @"meshes\Armor\ImperialWatch\M\Gauntlets.NIF",
                @"meshes\Armor\ImperialWatch\M\Greaves.NIF"
            ],
            equippedItems!.Select(item => item.MeshPath).ToArray());
    }

    [Fact]
    public void Resolve_OblivionArmor_PrefersHigherBaseRatingForSameSlot()
    {
        var resolver = CreateResolver(
            new Dictionary<uint, ArmoScanEntry>
            {
                [1] = BuildArmor(@"armor\lower.nif", 0x04, 100),
                [2] = BuildArmor(@"armor\higher.nif", 0x04, 200)
            },
            BethesdaGame.Oblivion);

        var equippedItems = resolver.Resolve(
            [new InventoryItem(1, 1), new InventoryItem(2, 1)],
            false);

        var item = Assert.Single(equippedItems!);
        Assert.Equal(@"meshes\armor\higher.nif", item.MeshPath);
    }

    [Fact]
    public void Resolve_OblivionArmor_EqualRatingKeepsEarlierInventoryItem()
    {
        var resolver = CreateResolver(
            new Dictionary<uint, ArmoScanEntry>
            {
                [1] = BuildArmor(@"armor\earlier.nif", 0x04, 200),
                [2] = BuildArmor(@"armor\later.nif", 0x04, 200)
            },
            BethesdaGame.Oblivion);

        var equippedItems = resolver.Resolve(
            [new InventoryItem(1, 1), new InventoryItem(2, 1)],
            false);

        var item = Assert.Single(equippedItems!);
        Assert.Equal(@"meshes\armor\earlier.nif", item.MeshPath);
    }

    [Fact]
    public void Resolve_OblivionClothing_DoesNotEmitWholeMultiSlotMeshAfterArmorWinsOneSlot()
    {
        var resolver = CreateResolver(
            new Dictionary<uint, ArmoScanEntry>
            {
                [1] = new()
                {
                    IsClothing = true,
                    BipedFlags = 0x04 | 0x08,
                    MaleBipedModelPath = @"clothes\robe.nif"
                },
                [2] = BuildArmor(@"armor\cuirass.nif", 0x04, 100)
            },
            BethesdaGame.Oblivion);

        var equippedItems = resolver.Resolve(
            [new InventoryItem(1, 1), new InventoryItem(2, 1)],
            false);

        var item = Assert.Single(equippedItems!);
        Assert.Equal(@"meshes\armor\cuirass.nif", item.MeshPath);
    }

    [Fact]
    public void Resolve_FalloutNewVegas_IgnoresTes4ArmorRatingAndKeepsFirstClaim()
    {
        var resolver = CreateResolver(
            new Dictionary<uint, ArmoScanEntry>
            {
                [1] = BuildArmor(@"armor\earlier.nif", 0x04, 100),
                [2] = BuildArmor(@"armor\later.nif", 0x04, 200)
            },
            BethesdaGame.FalloutNewVegas);

        var equippedItems = resolver.Resolve(
            [new InventoryItem(1, 1), new InventoryItem(2, 1)],
            false);

        var item = Assert.Single(equippedItems!);
        Assert.Equal(@"meshes\armor\earlier.nif", item.MeshPath);
    }

    [Fact]
    public void Resolve_OblivionRenderableZeroSlotItem_RemainsUnequipped()
    {
        var resolver = CreateResolver(
            new Dictionary<uint, ArmoScanEntry>
            {
                [1] = BuildArmor(@"armor\zeroflags.nif", 0, 200)
            },
            BethesdaGame.Oblivion);

        var equippedItems = resolver.Resolve([new InventoryItem(1, 1)], false);

        Assert.Null(equippedItems);
    }

    [Fact]
    public void Resolve_OblivionReynaldInventory_PrefersUpperAndLowerOutfitOverOverlappingPants()
    {
        var resolver = CreateResolver(
            new Dictionary<uint, ArmoScanEntry>
            {
                [0x0001C830] = new()
                {
                    IsClothing = true,
                    BaseValue = 2,
                    BipedFlags = 0x08,
                    MaleBipedModelPath = @"Clothes\MiddleClass\02\M\Pants.NIF"
                },
                [0x0001C884] = new()
                {
                    IsClothing = true,
                    BaseValue = 2,
                    BipedFlags = 0x0C,
                    MaleBipedModelPath = @"Clothes\MiddleClass\03\M\Shirt.NIF"
                },
                [0x0001C883] = new()
                {
                    IsClothing = true,
                    BaseValue = 1,
                    BipedFlags = 0x20,
                    MaleBipedModelPath = @"Clothes\MiddleClass\02\M\Shoes.NIF"
                }
            },
            BethesdaGame.Oblivion);

        var equippedItems = resolver.Resolve(
            [
                new InventoryItem(0x0001C830, 1),
                new InventoryItem(0x0001C884, 1),
                new InventoryItem(0x0001C883, 1)
            ],
            false);

        Assert.NotNull(equippedItems);
        Assert.Equal(2, equippedItems!.Count);
        Assert.Equal(
            @"meshes\clothes\middleclass\03\m\shirt.nif",
            Assert.Single(equippedItems, item => (item.BipedFlags & 0x04) != 0).MeshPath,
            ignoreCase: true);
        Assert.Equal(
            @"meshes\clothes\middleclass\03\m\shirt.nif",
            Assert.Single(equippedItems, item => (item.BipedFlags & 0x08) != 0).MeshPath,
            ignoreCase: true);
        Assert.Equal(
            @"meshes\clothes\middleclass\02\m\shoes.nif",
            Assert.Single(equippedItems, item => (item.BipedFlags & 0x20) != 0).MeshPath,
            ignoreCase: true);
        Assert.DoesNotContain(
            equippedItems,
            static item => item.MeshPath.EndsWith(
                @"middleclass\02\m\pants.nif",
                StringComparison.OrdinalIgnoreCase));

        var appearance = new NpcAppearance
        {
            NpcFormId = 0x000222A8,
            LowerBodyNifPath = @"meshes\characters\_male\lowerbody.nif"
        };
        var coveredSlots = equippedItems.Aggregate(
            0u,
            static (slots, item) => slots | item.BipedFlags);
        var bodyParts = NpcCompositionPlanner.BuildBodyParts(
            appearance,
            new NpcCompositionOptions(),
            coveredSlots,
            effectiveBodyTex: null,
            effectiveHandTex: null);

        Assert.Empty(bodyParts);
    }

    [Fact]
    public void Resolve_OblivionClothing_PrefersHigherValueWithinSlot()
    {
        var resolver = CreateResolver(
            new Dictionary<uint, ArmoScanEntry>
            {
                [1] = new()
                {
                    IsClothing = true,
                    BaseValue = 1,
                    BipedFlags = 0x04,
                    MaleBipedModelPath = @"clothes\low-value-shirt.nif"
                },
                [2] = new()
                {
                    IsClothing = true,
                    BaseValue = 2,
                    BipedFlags = 0x04,
                    MaleBipedModelPath = @"clothes\high-value-shirt.nif"
                }
            },
            BethesdaGame.Oblivion);

        var equippedItem = Assert.Single(
            resolver.Resolve(
                [new InventoryItem(1, 1), new InventoryItem(2, 1)],
                false)!);

        Assert.Equal(
            @"meshes\clothes\high-value-shirt.nif",
            equippedItem.MeshPath,
            ignoreCase: true);
    }

    [Fact]
    [Trait("Category", TestCategories.BucketB)]
    public void RetailOblivion_ReynaldComposition_HasOneLowerGarmentAndNoBaseLowerBody()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esmPath = RealAssetPaths.Masters.Oblivion();
        Assert.SkipWhen(esmPath is null, RealAssetPaths.SkipMessage("Oblivion.esm"));

        var esm = File.ReadAllBytes(esmPath!);
        var index = NpcAppearanceIndexBuilder.Build(
            esm,
            bigEndian: false,
            cancellationToken: TestContext.Current.CancellationToken);
        var npcRecord = Assert.Contains(0x000222A8u, index.Npcs);
        var appearance = new NpcAppearanceFactory(index).Build(
            0x000222A8,
            npcRecord,
            "Oblivion.esm");

        var equippedItems = Assert.IsAssignableFrom<IReadOnlyList<EquippedItem>>(
            appearance.EquippedItems);
        var lowerGarment = Assert.Single(
            equippedItems,
            static item => (item.BipedFlags & 0x08) != 0);
        Assert.Equal(
            @"meshes\clothes\middleclass\03\m\shirt.nif",
            lowerGarment.MeshPath,
            ignoreCase: true);
        Assert.Equal(0x0Cu, lowerGarment.BipedFlags);
        Assert.DoesNotContain(
            equippedItems,
            static item => item.MeshPath.EndsWith(
                @"middleclass\02\m\pants.nif",
                StringComparison.OrdinalIgnoreCase));

        var coveredSlots = equippedItems.Aggregate(
            0u,
            static (slots, item) => slots | item.BipedFlags);
        var bodyParts = NpcCompositionPlanner.BuildBodyParts(
            appearance,
            new NpcCompositionOptions(),
            coveredSlots,
            effectiveBodyTex: null,
            effectiveHandTex: null);

        Assert.DoesNotContain(
            bodyParts,
            static part => part.MeshPath.EndsWith(
                @"characters\_male\lowerbody.nif",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildFromDmpRecord_AuthoritativeWornEquipment_PreservesSuppliedOrder()
    {
        var index = new NpcAppearanceIndex
        {
            Game = BethesdaGame.Oblivion
        };
        index.Armors[1] = new ArmoScanEntry
        {
            IsClothing = true,
            BipedFlags = 0x04,
            MaleBipedModelPath = @"clothes\runtime-first.nif"
        };
        index.Armors[2] = BuildArmor(@"armor\runtime-second.nif", 0x04, 1125);
        var npcRecord = new NpcRecord
        {
            FormId = 0x0003486E,
            EditorId = "RuntimeLex",
            Stats = new ActorBaseSubrecord(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false)
        };
        var runtimeEquipment = new NpcEquipmentResolver.RuntimeEquipmentSelection(
            true,
            0x00123456,
            [1, 2]);

        var appearance = new NpcAppearanceFactory(index).BuildFromDmpRecord(
            npcRecord,
            "Oblivion.esm",
            runtimeEquipmentSelection: runtimeEquipment);

        Assert.NotNull(appearance.EquippedItems);
        Assert.Equal(
            [@"meshes\clothes\runtime-first.nif", @"meshes\armor\runtime-second.nif"],
            appearance.EquippedItems!.Select(item => item.MeshPath).ToArray());
    }

    [Fact]
    public void BuildFromDmpRecord_AuthoritativeEmptyWornEquipment_DoesNotFallBackToBaseInventory()
    {
        var index = new NpcAppearanceIndex
        {
            Game = BethesdaGame.Oblivion
        };
        index.Armors[1] = BuildArmor(@"armor\base-inventory.nif", 0x04, 1125);
        var npcRecord = new NpcRecord
        {
            FormId = 0x0003486E,
            EditorId = "RuntimeNakedLex",
            Stats = new ActorBaseSubrecord(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false),
            Inventory = [new InventoryItem(1, 1)]
        };
        var runtimeEquipment = new NpcEquipmentResolver.RuntimeEquipmentSelection(
            true,
            0x00123456,
            []);

        var factory = new NpcAppearanceFactory(index);
        var appearance = factory.BuildFromDmpRecord(
            npcRecord,
            "Oblivion.esm",
            runtimeEquipmentSelection: runtimeEquipment);
        var unavailableAppearance = factory.BuildFromDmpRecord(
            npcRecord,
            "Oblivion.esm",
            runtimeEquipmentSelection: new NpcEquipmentResolver.RuntimeEquipmentSelection(
                true,
                0x00123456,
                null));

        Assert.Null(appearance.EquippedItems);
        var fallbackItem = Assert.Single(unavailableAppearance.EquippedItems!);
        Assert.Equal(@"meshes\armor\base-inventory.nif", fallbackItem.MeshPath);
    }

    private static NpcEquipmentResolver CreateResolver(
        IReadOnlyDictionary<uint, ArmoScanEntry> armors,
        BethesdaGame game)
    {
        return new NpcEquipmentResolver(
            armors,
            new Dictionary<uint, ArmaAddonScanEntry>(),
            new Dictionary<uint, List<uint>>(),
            new Dictionary<uint, List<uint>>(),
            game);
    }

    private static ArmoScanEntry BuildArmor(string meshPath, uint bipedFlags, ushort rating)
    {
        return new ArmoScanEntry
        {
            BaseArmorRating = rating,
            BipedFlags = bipedFlags,
            MaleBipedModelPath = meshPath
        };
    }
}
