using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NpcAppearanceHelperTests
{
    [Fact]
    public void Build_UsesRaceDefaultsAndMergesCoefficients()
    {
        var index = new NpcAppearanceIndex();
        index.Races[1] = new RaceScanEntry
        {
            FemaleHeadModelPath = @"characters\female\headfemale.nif",
            FemaleHeadTexturePath = @"characters\female\headfemale.dds",
            FemaleEyeLeftModelPath = @"characters\female\eyeleft.nif",
            FemaleEyeRightModelPath = @"characters\female\eyeright.nif",
            FemaleFaceGenSymmetric = [0.1f, 0.2f],
            FemaleFaceGenAsymmetric = [0.5f],
            FemaleFaceGenTexture = [0.75f],
            FemaleUpperBodyPath = @"characters\female\upperbody.nif",
            FemaleLeftHandPath = @"characters\female\lefthand.nif",
            FemaleRightHandPath = @"characters\female\righthand.nif",
            FemaleBodyTexturePath = @"characters\female\UpperBodyFemale.dds",
            DefaultEyesFormId = 20
        };
        index.Hairs[10] = new HairScanEntry
        {
            ModelPath = @"hair\femalehair.nif",
            TexturePath = @"hair\femalehair.dds"
        };
        index.Eyes[20] = new EyesScanEntry
        {
            TexturePath = @"eyes\default.dds"
        };
        index.HeadParts[30] = new HdptScanEntry
        {
            ModelPath = @"headparts\brow.nif"
        };
        index.Armors[100] = new ArmoScanEntry
        {
            BipedFlags = 0x04 | 0x08 | 0x10,
            FemaleBipedModelPath = @"armor\outfitf.nif"
        };
        index.Weapons[200] = new WeapScanEntry
        {
            ModelPath = @"weapons\rifle.nif",
            WeaponType = WeaponType.Rifle,
            Damage = 25
        };

        var npc = new NpcScanEntry
        {
            EditorId = "TestNpc",
            FullName = "Test NPC",
            RaceFormId = 1,
            HairFormId = 10,
            HeadPartFormIds = [30],
            IsFemale = true,
            FaceGenSymmetric = [1.0f, 2.0f],
            FaceGenAsymmetric = [3.0f],
            FaceGenTexture = [4.0f],
            InventoryFormIds = [100, 200]
        };

        var factory = new NpcAppearanceFactory(index);
        var appearance = factory.Build(0x1234u, npc, "FalloutNV.esm");

        Assert.Equal(@"meshes\characters\female\headfemale.nif", appearance.BaseHeadNifPath);
        Assert.Equal(@"characters\female\headfemale.dds", appearance.HeadDiffuseOverride);
        Assert.Equal(@"meshes\hair\femalehair.nif", appearance.HairNifPath);
        Assert.Equal(@"textures\hair\femalehair.dds", appearance.HairTexturePath);
        Assert.Equal(@"textures\eyes\default.dds", appearance.EyeTexturePath);
        Assert.NotNull(appearance.FaceGenSymmetricCoeffs);
        Assert.NotNull(appearance.FaceGenAsymmetricCoeffs);
        Assert.NotNull(appearance.FaceGenTextureCoeffs);
        Assert.Equal([1.1f, 2.2f], appearance.FaceGenSymmetricCoeffs);
        Assert.Equal([3.5f], appearance.FaceGenAsymmetricCoeffs);
        Assert.Equal([4.75f], appearance.FaceGenTextureCoeffs);
        Assert.Equal(@"textures\characters\female\HandFemale.dds", appearance.HandTexturePath);
        Assert.Equal(@"meshes\headparts\brow.nif", Assert.Single(appearance.HeadPartNifPaths!));
        Assert.Equal(@"meshes\armor\outfitf.nif", Assert.Single(appearance.EquippedItems!).MeshPath);
        Assert.Equal(@"meshes\weapons\rifle.nif", appearance.EquippedWeapon!.MeshPath);
    }

    [Fact]
    public void BuildFromDmpRecord_FallsBackToEsmHairColorAndHeadParts()
    {
        var index = new NpcAppearanceIndex();
        index.Races[1] = new RaceScanEntry
        {
            MaleHeadModelPath = @"characters\male\headhuman.nif"
        };
        index.HeadParts[42] = new HdptScanEntry
        {
            ModelPath = @"headparts\beard.nif"
        };
        index.Npcs[0x99u] = new NpcScanEntry
        {
            HairColor = 0x00112233,
            HeadPartFormIds = [42]
        };

        var record = new NpcRecord
        {
            FormId = 0x99u,
            EditorId = "FallbackNpc",
            Race = 1,
            Stats = new ActorBaseSubrecord(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false)
        };

        var factory = new NpcAppearanceFactory(index);
        var appearance = factory.BuildFromDmpRecord(record, "FalloutNV.esm");

        Assert.Equal(0x00112233u, appearance.HairColor);
        Assert.Equal(@"meshes\headparts\beard.nif", Assert.Single(appearance.HeadPartNifPaths!));
    }

    [Fact]
    public void InventoryResolver_ResolvesTemplateInventoryThroughLeveledNpc()
    {
        var templateNpc = new NpcScanEntry
        {
            InventoryFormIds = [7]
        };
        var childNpc = new NpcScanEntry
        {
            TemplateFormId = 100,
            TemplateFlags = 0x0100
        };

        var resolver = new NpcInventoryResolver(
            new Dictionary<uint, NpcScanEntry>
            {
                [200] = templateNpc
            },
            new Dictionary<uint, List<uint>>
            {
                [100] = [200]
            });

        var inventory = resolver.ResolveInventoryFormIds(childNpc);

        Assert.Equal([7u], inventory);
    }

    [Fact]
    public void EquipmentResolver_DeduplicatesFirstArmorAcrossCoveredSlots()
    {
        var resolver = new NpcEquipmentResolver(
            new Dictionary<uint, ArmoScanEntry>
            {
                [1] = new()
                {
                    BipedFlags = 0x04 | 0x08 | 0x10,
                    MaleBipedModelPath = @"armor\primary.nif"
                },
                [2] = new()
                {
                    BipedFlags = 0x04,
                    MaleBipedModelPath = @"armor\secondary.nif"
                }
            },
            new Dictionary<uint, List<uint>>());

        var equippedItems = resolver.Resolve([1, 2], isFemale: false);

        Assert.NotNull(equippedItems);
        var item = Assert.Single(equippedItems);
        Assert.Equal(@"meshes\armor\primary.nif", item.MeshPath);
        Assert.Equal(0x04u | 0x08u | 0x10u, item.BipedFlags);
    }

    [Fact]
    public void WeaponResolver_PicksHighestDamageRenderableWeaponFromLeveledList()
    {
        var resolver = new NpcWeaponResolver(
            new Dictionary<uint, WeapScanEntry>
            {
                [2] = new()
                {
                    ModelPath = @"weapons\grenade.nif",
                    WeaponType = WeaponType.GrenadeThrow,
                    Damage = 100
                },
                [3] = new()
                {
                    ModelPath = @"weapons\rifle.nif",
                    WeaponType = WeaponType.Rifle,
                    Damage = 35
                }
            },
            new Dictionary<uint, List<uint>>
            {
                [1] = [2, 3]
            });

        var equippedWeapon = resolver.Resolve([1]);

        Assert.NotNull(equippedWeapon);
        Assert.Equal(WeaponType.Rifle, equippedWeapon.WeaponType);
        Assert.Equal(@"meshes\weapons\rifle.nif", equippedWeapon.MeshPath);
    }

    [Fact]
    public void PathDeriver_DerivesHandTextureAndBodyEgtPaths()
    {
        var handTexture = NpcAppearancePathDeriver.DeriveHandTexturePath(
            @"characters\ghoul\UpperBodyMale.dds",
            isFemale: false);
        var bodyEgtPaths = NpcAppearancePathDeriver.DeriveBodyEgtPaths(
            @"characters\ghoul\headghoul.nif",
            isFemale: false);

        Assert.Equal(@"textures\characters\ghoul\HandMale.dds", handTexture);
        Assert.Equal(@"meshes\characters\_male\upperbodyhumanghoul.egt", bodyEgtPaths.BodyEgt);
        Assert.Equal(@"meshes\characters\_male\lefthandghoul.egt", bodyEgtPaths.LeftHandEgt);
        Assert.Equal(@"meshes\characters\_male\righthandghoul.egt", bodyEgtPaths.RightHandEgt);
    }
}
