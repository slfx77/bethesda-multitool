using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NpcAppearanceHelperTests
{
    private static readonly string MaleSampleCharacterRoot = FindMaleSampleCharacterRoot();

    [Fact]
    public void Build_UsesRaceDefaults_MergesGeometryAndTextureCoefficients()
    {
        var index = new NpcAppearanceIndex();
        index.Races[1] = new RaceScanEntry
        {
            FemaleHeadModelPath = @"characters\female\headfemale.nif",
            FemaleHeadTexturePath = @"characters\female\headfemale.dds",
            FemaleMouthModelPath = @"characters\female\mouthfemale.nif",
            FemaleLowerTeethModelPath = @"characters\female\teethlowerfemale.nif",
            FemaleUpperTeethModelPath = @"characters\female\teethupperfemale.nif",
            FemaleTongueModelPath = @"characters\female\tonguefemale.nif",
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
        index.Weapons[200] = BuildWeapon(
            @"weapons\rifle.nif",
            WeaponType.TwoHandRifle,
            25,
            2.5f);

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
            InventoryItems = [new InventoryItem(100, 1), new InventoryItem(200, 1)]
        };

        var factory = new NpcAppearanceFactory(index);
        var appearance = factory.Build(0x1234u, npc, "FalloutNV.esm");

        Assert.Equal(@"meshes\characters\female\headfemale.nif", appearance.BaseHeadNifPath);
        Assert.Equal(@"meshes\characters\female\headfemale.tri", appearance.BaseHeadTriPath);
        Assert.Equal(@"characters\female\headfemale.dds", appearance.HeadDiffuseOverride);
        Assert.Equal(@"meshes\hair\femalehair.nif", appearance.HairNifPath);
        Assert.Equal(@"textures\hair\femalehair.dds", appearance.HairTexturePath);
        Assert.Equal(@"textures\eyes\default.dds", appearance.EyeTexturePath);
        Assert.Equal(@"meshes\characters\female\mouthfemale.nif", appearance.MouthNifPath);
        Assert.Equal(@"meshes\characters\female\teethlowerfemale.nif", appearance.LowerTeethNifPath);
        Assert.Equal(@"meshes\characters\female\teethupperfemale.nif", appearance.UpperTeethNifPath);
        Assert.Equal(@"meshes\characters\female\tonguefemale.nif", appearance.TongueNifPath);
        Assert.NotNull(appearance.FaceGenSymmetricCoeffs);
        Assert.NotNull(appearance.FaceGenAsymmetricCoeffs);
        Assert.NotNull(appearance.FaceGenTextureCoeffs);
        Assert.NotNull(appearance.NpcFaceGenTextureCoeffs);
        Assert.NotNull(appearance.RaceFaceGenTextureCoeffs);
        Assert.Equal([1.1f, 2.2f], appearance.FaceGenSymmetricCoeffs);
        Assert.Equal([3.5f], appearance.FaceGenAsymmetricCoeffs);
        Assert.Equal([4.75f], appearance.FaceGenTextureCoeffs);
        Assert.Equal([4.0f], appearance.NpcFaceGenTextureCoeffs);
        Assert.Equal([0.75f], appearance.RaceFaceGenTextureCoeffs);
        Assert.Equal(@"textures\characters\female\HandFemale.dds", appearance.HandTexturePath);
        Assert.Equal(@"meshes\headparts\brow.nif", Assert.Single(appearance.HeadPartNifPaths!));
        Assert.Equal(@"meshes\armor\outfitf.nif", Assert.Single(appearance.EquippedItems!).MeshPath);
        Assert.NotNull(appearance.WeaponVisual);
        Assert.True(appearance.WeaponVisual!.IsVisible);
        Assert.Equal(WeaponVisualSourceKind.EsmBestWeapon, appearance.WeaponVisual.SourceKind);
        Assert.Equal(@"meshes\weapons\rifle.nif", appearance.WeaponVisual.MeshPath);
        Assert.Equal("2hr", appearance.WeaponVisual.HolsterProfileKey);
    }

    [Fact]
    public void Build_UsesRaceTextureCoefficientsWhenNpcTextureMissing()
    {
        var index = new NpcAppearanceIndex();
        index.Races[1] = new RaceScanEntry
        {
            MaleHeadModelPath = @"characters\male\headmale.nif",
            MaleFaceGenTexture = [0.75f]
        };

        var npc = new NpcScanEntry
        {
            EditorId = "TestNpc",
            RaceFormId = 1,
            IsFemale = false
        };

        var factory = new NpcAppearanceFactory(index);
        var appearance = factory.Build(0x1234u, npc, "FalloutNV.esm");

        Assert.NotNull(appearance.FaceGenTextureCoeffs);
        Assert.Null(appearance.NpcFaceGenTextureCoeffs);
        Assert.NotNull(appearance.RaceFaceGenTextureCoeffs);
        Assert.Equal([0.75f], appearance.FaceGenTextureCoeffs);
        Assert.Equal([0.75f], appearance.RaceFaceGenTextureCoeffs);
    }

    [Fact]
    public void Build_EquipmentResolver_IncludesArmorAddonMeshesFromBipedModelList()
    {
        var index = new NpcAppearanceIndex();
        index.Races[1] = new RaceScanEntry
        {
            FemaleHeadModelPath = @"characters\female\headfemale.nif",
            FemaleUpperBodyPath = @"characters\female\upperbody.nif",
            FemaleLeftHandPath = @"characters\female\lefthand.nif",
            FemaleRightHandPath = @"characters\female\righthand.nif",
            FemaleBodyTexturePath = @"characters\female\UpperBodyFemale.dds"
        };
        index.Armors[100] = new ArmoScanEntry
        {
            BipedFlags = 0x04 | 0x10,
            FemaleBipedModelPath = @"armor\outfitf.nif",
            BipedModelListFormId = 500
        };
        index.FormLists[500] = [600];
        index.ArmorAddons[600] = new ArmaAddonScanEntry
        {
            BipedFlags = 0x10,
            FemaleModelPath = @"armor\wristaddonf.nif"
        };

        var npc = new NpcScanEntry
        {
            EditorId = "TestNpc",
            RaceFormId = 1,
            IsFemale = true,
            InventoryItems = [new InventoryItem(100, 1)]
        };

        var factory = new NpcAppearanceFactory(index);
        var appearance = factory.Build(0x1234u, npc, "FalloutNV.esm");

        Assert.NotNull(appearance.EquippedItems);
        Assert.Equal(
            [@"meshes\armor\outfitf.nif", @"meshes\armor\wristaddonf.nif"],
            appearance.EquippedItems!.Select(item => item.MeshPath).ToArray());
        Assert.Equal(EquipmentAttachmentMode.None, appearance.EquippedItems[0].AttachmentMode);
        Assert.Equal(EquipmentAttachmentMode.RightWristRigid, appearance.EquippedItems[1].AttachmentMode);
    }

    [Fact]
    public void BuildFromDmpRecord_FallsBackToEsmHairColorAndHeadParts()
    {
        var index = new NpcAppearanceIndex();
        index.Races[1] = new RaceScanEntry
        {
            MaleHeadModelPath = @"characters\male\headhuman.nif",
            MaleMouthModelPath = @"characters\male\mouthhuman.nif",
            MaleLowerTeethModelPath = @"characters\male\teethlowerhuman.nif",
            MaleUpperTeethModelPath = @"characters\male\teethupperhuman.nif",
            MaleTongueModelPath = @"characters\male\tonguehuman.nif"
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
        Assert.Equal(@"meshes\characters\male\mouthhuman.nif", appearance.MouthNifPath);
        Assert.Equal(@"meshes\characters\male\teethlowerhuman.nif", appearance.LowerTeethNifPath);
        Assert.Equal(@"meshes\characters\male\teethupperhuman.nif", appearance.UpperTeethNifPath);
        Assert.Equal(@"meshes\characters\male\tonguehuman.nif", appearance.TongueNifPath);
    }

    [Fact]
    public void BuildFromDmpRecord_PrefersRuntimeInventoryForWeaponResolution()
    {
        var index = new NpcAppearanceIndex();
        index.Races[1] = new RaceScanEntry
        {
            MaleHeadModelPath = @"characters\male\headhuman.nif"
        };
        index.Weapons[200] = BuildWeapon(
            @"weapons\pistol.nif",
            WeaponType.OneHandPistol,
            12,
            1.5f);
        index.Weapons[201] = BuildWeapon(
            @"weapons\rifleauto.nif",
            WeaponType.TwoHandAutomatic,
            18,
            6f);
        index.Npcs[0x99u] = new NpcScanEntry
        {
            InventoryItems = [new InventoryItem(200, 1)]
        };

        var record = new NpcRecord
        {
            FormId = 0x99u,
            EditorId = "RuntimeNpc",
            Race = 1,
            Stats = new ActorBaseSubrecord(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false),
            Inventory = [new InventoryItem(201, 1)]
        };

        var factory = new NpcAppearanceFactory(index);
        var appearance = factory.BuildFromDmpRecord(record, "FalloutNV.esm");

        Assert.NotNull(appearance.WeaponVisual);
        Assert.True(appearance.WeaponVisual!.IsVisible);
        Assert.Equal(WeaponVisualSourceKind.EsmBestWeapon, appearance.WeaponVisual.SourceKind);
        Assert.Equal(@"meshes\weapons\rifleauto.nif", appearance.WeaponVisual.MeshPath);
        Assert.Equal("2ha", appearance.WeaponVisual.HolsterProfileKey);
    }

    [Fact]
    public void InventoryResolver_ResolvesTemplateInventoryThroughLeveledNpc()
    {
        var templateNpc = new NpcScanEntry
        {
            InventoryItems = [new InventoryItem(7, 2)]
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

        var inventory = resolver.ResolveInventoryItems(childNpc);

        Assert.NotNull(inventory);
        Assert.Equal([new InventoryItem(7, 2)], inventory);
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
            new Dictionary<uint, ArmaAddonScanEntry>(),
            new Dictionary<uint, List<uint>>(),
            new Dictionary<uint, List<uint>>());

        var equippedItems = resolver.Resolve(
            [new InventoryItem(1, 1), new InventoryItem(2, 1)],
            false);

        Assert.NotNull(equippedItems);
        var item = Assert.Single(equippedItems);
        Assert.Equal(@"meshes\armor\primary.nif", item.MeshPath);
        Assert.Equal(0x04u | 0x08u | 0x10u, item.BipedFlags);
    }

    [Fact]
    public void WeaponResolver_UsesFirstUseWeaponPackageBeforeBestWeaponScoring()
    {
        var resolver = CreateWeaponResolver(
            new Dictionary<uint, PackageScanEntry>
            {
                [10] = new()
                {
                    Type = 16,
                    UseWeaponFormId = 2
                }
            },
            new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(@"weapons\pistol.nif", WeaponType.OneHandPistol, 12, 3f),
                [3] = BuildWeapon(@"weapons\rifle.nif", WeaponType.TwoHandRifle, 40, 4f)
            });

        var visual = resolver.Resolve(
            new NpcScanEntry
            {
                PackageFormIds = [10]
            },
            [new InventoryItem(2, 1), new InventoryItem(3, 1)]);

        Assert.True(visual.IsVisible);
        Assert.Equal(WeaponVisualSourceKind.EsmPackage, visual.SourceKind);
        Assert.Equal(WeaponType.OneHandPistol, visual.WeaponType);
        Assert.Equal(@"meshes\weapons\pistol.nif", visual.MeshPath);
        Assert.Equal("1hp", visual.HolsterProfileKey);
    }

    [Fact]
    public void WeaponResolver_UnanimousWeaponsUnequipped_OmitsWeapon()
    {
        var resolver = CreateWeaponResolver(
            new Dictionary<uint, PackageScanEntry>
            {
                [10] = new() { GeneralFlags = 0x00200000 },
                [11] = new() { GeneralFlags = 0x00200000 }
            },
            new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(@"weapons\rifle.nif", WeaponType.TwoHandRifle, 40, 3f)
            });

        var visual = resolver.Resolve(
            new NpcScanEntry
            {
                PackageFormIds = [10, 11]
            },
            [new InventoryItem(2, 1)]);

        Assert.False(visual.IsVisible);
        Assert.Equal(WeaponVisualSourceKind.OmittedUnequipped, visual.SourceKind);
        Assert.Null(visual.MeshPath);
    }

    [Fact]
    public void WeaponResolver_PrefersHigherDpsOverHigherRawDamage()
    {
        var resolver = CreateWeaponResolver(
            weapons: new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(@"weapons\slowrifle.nif", WeaponType.TwoHandRifle, 45, 1f),
                [3] = BuildWeapon(@"weapons\autorifle.nif", WeaponType.TwoHandAutomatic, 20, 4f)
            });

        var visual = resolver.Resolve(
            new NpcScanEntry(),
            [new InventoryItem(2, 1), new InventoryItem(3, 1)]);

        Assert.True(visual.IsVisible);
        Assert.Equal(WeaponType.TwoHandAutomatic, visual.WeaponType);
        Assert.Equal(@"meshes\weapons\autorifle.nif", visual.MeshPath);
        Assert.Equal("2ha", visual.HolsterProfileKey);
    }

    [Fact]
    public void WeaponResolver_EqualScores_KeepFirstAuthoredCandidate()
    {
        var resolver = CreateWeaponResolver(
            weapons: new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(@"weapons\first.nif", WeaponType.OneHandPistol, 20, 2f),
                [3] = BuildWeapon(@"weapons\second.nif", WeaponType.OneHandPistol, 20, 2f)
            });

        var visual = resolver.Resolve(
            new NpcScanEntry(),
            [new InventoryItem(2, 1), new InventoryItem(3, 1)]);

        Assert.True(visual.IsVisible);
        Assert.Equal(@"meshes\weapons\first.nif", visual.MeshPath);
    }

    [Fact]
    public void WeaponResolver_NoAmmoCandidateLosesToUsableCandidate()
    {
        var resolver = CreateWeaponResolver(
            weapons: new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(@"weapons\rifle.nif", WeaponType.TwoHandRifle, 80, 5f, 90),
                [3] = BuildWeapon(@"weapons\pistol.nif", WeaponType.OneHandPistol, 10, 2f)
            });

        var visual = resolver.Resolve(
            new NpcScanEntry(),
            [new InventoryItem(2, 1), new InventoryItem(3, 1)]);

        Assert.True(visual.IsVisible);
        Assert.Equal(WeaponType.OneHandPistol, visual.WeaponType);
        Assert.Equal(@"meshes\weapons\pistol.nif", visual.MeshPath);
    }

    [Fact]
    public void WeaponResolver_NonRenderablePackageWeapon_OmitsInsteadOfGuessingFallback()
    {
        var resolver = CreateWeaponResolver(
            new Dictionary<uint, PackageScanEntry>
            {
                [10] = new()
                {
                    Type = 16,
                    UseWeaponFormId = 2
                }
            },
            new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(@"weapons\grenade.nif", WeaponType.OneHandGrenade, 100, 1f),
                [3] = BuildWeapon(@"weapons\rifle.nif", WeaponType.TwoHandRifle, 35, 2f)
            });

        var visual = resolver.Resolve(
            new NpcScanEntry
            {
                PackageFormIds = [10]
            },
            [new InventoryItem(2, 1), new InventoryItem(3, 1)]);

        Assert.False(visual.IsVisible);
        Assert.Equal(WeaponVisualSourceKind.OmittedUnresolved, visual.SourceKind);
    }

    [Fact]
    public void WeaponResolver_RuntimeTarget_UsesRuntimeWeaponSelection()
    {
        var resolver = CreateWeaponResolver(
            weapons: new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(@"weapons\pistol.nif", WeaponType.OneHandPistol, 10, 2f),
                [3] = BuildWeapon(@"weapons\rifle.nif", WeaponType.TwoHandRifle, 50, 1f)
            });

        var visual = resolver.Resolve(
            new NpcScanEntry(),
            [new InventoryItem(3, 1)],
            new NpcWeaponResolver.RuntimeWeaponSelection(true, 0x12345678, 2));

        Assert.True(visual.IsVisible);
        Assert.Equal(WeaponVisualSourceKind.DmpRuntimeCurrent, visual.SourceKind);
        Assert.Equal(0x12345678u, visual.RuntimeActorFormId);
        Assert.Equal(@"meshes\weapons\pistol.nif", visual.MeshPath);
    }

    [Fact]
    public void WeaponResolver_RuntimeTargetWithoutRenderableWeapon_OmitsAsUnequipped()
    {
        var resolver = CreateWeaponResolver();

        var visual = resolver.Resolve(
            new NpcScanEntry(),
            [new InventoryItem(3, 1)],
            new NpcWeaponResolver.RuntimeWeaponSelection(true, 0x12345678, null));

        Assert.False(visual.IsVisible);
        Assert.Equal(WeaponVisualSourceKind.OmittedUnequipped, visual.SourceKind);
    }

    [Fact]
    public void WeaponResolver_RuntimeTarget_TakesPrecedenceOverStaticInventory()
    {
        var resolver = CreateWeaponResolver(
            weapons: new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(@"weapons\pistol.nif", WeaponType.OneHandPistol, 10, 2f),
                [3] = BuildWeapon(@"weapons\rifle.nif", WeaponType.TwoHandRifle, 50, 1f)
            });

        var visual = resolver.Resolve(
            new NpcScanEntry(),
            [new InventoryItem(3, 1)],
            new NpcWeaponResolver.RuntimeWeaponSelection(true, 0x12345678, 2));

        Assert.True(visual.IsVisible);
        Assert.Equal(WeaponVisualSourceKind.DmpRuntimeCurrent, visual.SourceKind);
        Assert.Equal(@"meshes\weapons\pistol.nif", visual.MeshPath);
    }

    [Fact]
    public void WeaponResolver_EnergyPistol_UsesPistolHolsterProfile()
    {
        var resolver = CreateWeaponResolver(
            weapons: new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(@"weapons\\plasma.nif", WeaponType.OneHandPistolEnergy, 22, 1.6f)
            });

        var visual = resolver.Resolve(
            new NpcScanEntry(),
            [new InventoryItem(2, 1)]);

        Assert.True(visual.IsVisible);
        Assert.Equal(WeaponType.OneHandPistolEnergy, visual.WeaponType);
        Assert.Equal("1hp", visual.HolsterProfileKey);
    }

    [Fact]
    public void WeaponResolver_ThrownWeapon_UsesThrownHolsterProfile()
    {
        var resolver = CreateWeaponResolver(
            weapons: new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(@"weapons\\spear.nif", WeaponType.OneHandThrown, 18, 1.2f)
            });

        var visual = resolver.Resolve(
            new NpcScanEntry(),
            [new InventoryItem(2, 1)]);

        Assert.True(visual.IsVisible);
        Assert.Equal(WeaponType.OneHandThrown, visual.WeaponType);
        Assert.Equal("1gt", visual.HolsterProfileKey);
    }

    [Fact]
    public void WeaponResolver_PreservesEmbeddedWeaponNodeMetadata()
    {
        var resolver = CreateWeaponResolver(
            weapons: new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(
                    @"weapons\\embedded.nif",
                    WeaponType.TwoHandRifle,
                    22,
                    1.5f,
                    flags: 0x20,
                    embeddedWeaponNode: "Bip01 Spine2")
            });

        var visual = resolver.Resolve(
            new NpcScanEntry(),
            [new InventoryItem(2, 1)]);

        Assert.True(visual.IsVisible);
        Assert.True(visual.IsEmbeddedWeapon);
        Assert.Equal("Bip01 Spine2", visual.EmbeddedWeaponNode);
    }

    public void WeaponResolver_HandToHandRigidWeapon_UsesMatchingArmorAddonMesh()
    {
        var resolver = CreateWeaponResolver(
            weapons: new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(
                    @"weapons\hand2hand\powerfistrigid.nif",
                    WeaponType.HandToHandMelee,
                    22,
                    1.5f)
            },
            armorAddons: new Dictionary<uint, ArmaAddonScanEntry>
            {
                [10] = new()
                {
                    BipedFlags = 0x10,
                    MaleModelPath = @"weapons\hand2hand\powerfist.nif"
                }
            });

        var visual = resolver.Resolve(
            new NpcScanEntry { IsFemale = true },
            [new InventoryItem(2, 1)]);

        Assert.True(visual.IsVisible);
        Assert.Equal(WeaponAttachmentMode.EquippedHandMounted, visual.AttachmentMode);
        var addon = Assert.Single(visual.AddonMeshes!);
        Assert.Equal(0x10u, addon.BipedFlags);
        Assert.Equal(@"meshes\weapons\hand2hand\powerfist.nif", addon.MeshPath);
    }

    [Fact]
    public void WeaponResolver_HandToHandAddon_FallsBackToMaleMeshForFemaleNpc()
    {
        var resolver = CreateWeaponResolver(
            weapons: new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(
                    @"weapons\hand2hand\powerfistrigid.nif",
                    WeaponType.HandToHandMelee,
                    22,
                    1.5f)
            },
            armorAddons: new Dictionary<uint, ArmaAddonScanEntry>
            {
                [10] = new()
                {
                    BipedFlags = 0x10,
                    MaleModelPath = @"weapons\hand2hand\powerfist.nif"
                }
            });

        var visual = resolver.Resolve(
            new NpcScanEntry { IsFemale = true },
            [new InventoryItem(2, 1)]);

        Assert.NotNull(visual.AddonMeshes);
        Assert.Equal(@"meshes\weapons\hand2hand\powerfist.nif", visual.AddonMeshes![0].MeshPath);
    }

    [Fact]
    public void WeaponResolver_BoxingGloves_UsePairedHandAddonsAndSuppressStandaloneMesh()
    {
        var resolver = CreateWeaponResolver(
            weapons: new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(
                    @"weapons\hand2hand\boxinggoldlt.nif",
                    WeaponType.HandToHandMelee,
                    18,
                    1.3f)
            },
            armorAddons: new Dictionary<uint, ArmaAddonScanEntry>
            {
                [10] = new()
                {
                    EditorId = "ARMABoxingGoldGloves",
                    BipedFlags = 0x08,
                    MaleModelPath = @"weapons\hand2hand\boxinggoldlt.nif"
                },
                [11] = new()
                {
                    EditorId = "ARMABoxingGoldGlovesRT",
                    BipedFlags = 0x10,
                    MaleModelPath = @"weapons\hand2hand\boxinggoldrt.nif"
                }
            });

        var visual = resolver.Resolve(
            new NpcScanEntry { IsFemale = false },
            [new InventoryItem(2, 1)]);

        Assert.True(visual.IsVisible);
        Assert.False(visual.RenderStandaloneMesh);
        Assert.NotNull(visual.AddonMeshes);
        Assert.Equal(2, visual.AddonMeshes!.Count);
        Assert.Contains(visual.AddonMeshes, addon => addon.BipedFlags == 0x08u &&
                                                     string.Equals(addon.MeshPath,
                                                         @"meshes\weapons\hand2hand\boxinggoldlt.nif",
                                                         StringComparison.OrdinalIgnoreCase));
        Assert.Contains(visual.AddonMeshes, addon => addon.BipedFlags == 0x10u &&
                                                     string.Equals(addon.MeshPath,
                                                         @"meshes\weapons\hand2hand\boxinggoldrt.nif",
                                                         StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WeaponResolver_HandToHandWeapons_UseEquippedHandMountedAttachmentMode()
    {
        var resolver = CreateWeaponResolver(
            weapons: new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(
                    @"weapons\hand2hand\powerfistrigid.nif",
                    WeaponType.HandToHandMelee,
                    22,
                    1.5f)
            });

        var visual = resolver.Resolve(
            new NpcScanEntry { IsFemale = true },
            [new InventoryItem(2, 1)]);

        Assert.True(visual.IsVisible);
        Assert.Equal(WeaponAttachmentMode.EquippedHandMounted, visual.AttachmentMode);
        Assert.Equal("h2h", visual.HolsterProfileKey);
    }

    [Fact]
    public void WeaponResolver_PowerFistFamily_DoesNotReuseVatsSpecialIdlePoseForHeldAttachment()
    {
        var resolver = CreateWeaponResolver(
            weapons: new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(
                    @"weapons\hand2hand\powerfistrigid.nif",
                    WeaponType.HandToHandMelee,
                    22,
                    1.5f)
            },
            idles: new Dictionary<uint, IdleScanEntry>
            {
                [0x000C1105] = new()
                {
                    EditorId = "VATSPowerFistLow",
                    ModelPath = @"Characters\_Male\IdleAnims\VATSH2HAttackPower_PowerFistLow.kf"
                }
            });

        var visual = resolver.Resolve(
            new NpcScanEntry(),
            [new InventoryItem(2, 1)]);

        Assert.True(visual.IsVisible);
        Assert.Null(visual.EquippedPoseKfPath);
        Assert.True(visual.PreferEquippedForearmMount);
    }

    [Fact]
    public void WeaponResolver_BallisticFistFamily_DoesNotReuseVatsSpecialIdlePoseForHeldAttachment()
    {
        var resolver = CreateWeaponResolver(
            weapons: new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(
                    @"weapons\hand2hand\ballisticfistrigid.nif",
                    WeaponType.HandToHandMelee,
                    22,
                    1.5f)
            },
            idles: new Dictionary<uint, IdleScanEntry>
            {
                [0x000C1105] = new()
                {
                    EditorId = "VATSPowerFistLow",
                    ModelPath = @"Characters\_Male\IdleAnims\VATSH2HAttackPower_PowerFistLow.kf"
                }
            });

        var visual = resolver.Resolve(
            new NpcScanEntry(),
            [new InventoryItem(2, 1)]);

        Assert.True(visual.IsVisible);
        Assert.Null(visual.EquippedPoseKfPath);
        Assert.True(visual.PreferEquippedForearmMount);
    }

    [Fact]
    public void WeaponResolver_BoxingGloves_DoNotUsePowerFistSpecialIdlePose()
    {
        var resolver = CreateWeaponResolver(
            weapons: new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(
                    @"weapons\hand2hand\boxinggoldlt.nif",
                    WeaponType.HandToHandMelee,
                    18,
                    1.3f)
            },
            idles: new Dictionary<uint, IdleScanEntry>
            {
                [0x000C1105] = new()
                {
                    EditorId = "VATSPowerFistLow",
                    ModelPath = @"Characters\_Male\IdleAnims\VATSH2HAttackPower_PowerFistLow.kf"
                }
            });

        var visual = resolver.Resolve(
            new NpcScanEntry(),
            [new InventoryItem(2, 1)]);

        Assert.True(visual.IsVisible);
        Assert.Null(visual.EquippedPoseKfPath);
        Assert.False(visual.PreferEquippedForearmMount);
    }

    [Theory]
    [InlineData(WeaponType.OneHandMelee, "1hm")]
    [InlineData(WeaponType.TwoHandMelee, "2hm")]
    [InlineData(WeaponType.TwoHandHandle, "2hh")]
    public void WeaponResolver_MeleeWeaponsBeyondHandToHand_UseHolsterPoseAttachmentMode(
        WeaponType weaponType,
        string expectedHolsterProfileKey)
    {
        var resolver = CreateWeaponResolver(
            weapons: new Dictionary<uint, WeapScanEntry>
            {
                [2] = BuildWeapon(@"weapons\melee.nif", weaponType, 22, 1.5f)
            });

        var visual = resolver.Resolve(
            new NpcScanEntry(),
            [new InventoryItem(2, 1)]);

        Assert.True(visual.IsVisible);
        Assert.Equal(WeaponAttachmentMode.HolsterPose, visual.AttachmentMode);
        Assert.Equal(expectedHolsterProfileKey, visual.HolsterProfileKey);
    }

    [Fact]
    public void WeaponAttachmentNode_UsesExplicitEmbeddedNodeWhenPresent()
    {
        var result = NpcBodyBuilder.TryResolveWeaponAttachmentNode(
            new WeaponVisual
            {
                IsVisible = true,
                IsEmbeddedWeapon = true,
                EmbeddedWeaponNode = "Bip01 Spine2"
            },
            out var attachmentNodeName,
            out var omitReason);

        Assert.True(result);
        Assert.Equal("Bip01 Spine2", attachmentNodeName);
        Assert.Null(omitReason);
    }

    [Fact]
    public void WeaponAttachmentNode_EmbeddedWeaponWithoutNode_IsRejected()
    {
        var result = NpcBodyBuilder.TryResolveWeaponAttachmentNode(
            new WeaponVisual
            {
                IsVisible = true,
                EditorId = "EmbeddedTest",
                IsEmbeddedWeapon = true
            },
            out var attachmentNodeName,
            out var omitReason);

        Assert.False(result);
        Assert.Equal(string.Empty, attachmentNodeName);
        Assert.Contains("embedded weapon", omitReason);
    }

    [Fact]
    public void EquippedWeaponAttachment_UsesExplicitEmbeddedNodeWhenPresent()
    {
        var idleBoneTransforms = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bip01 Spine2"] = Matrix4x4.CreateTranslation(11f, 0f, 0f),
            ["Bip01 R ForeTwist"] = Matrix4x4.CreateTranslation(22f, 0f, 0f),
            ["Bip01 R Hand"] = Matrix4x4.CreateTranslation(33f, 0f, 0f),
            ["Weapon"] = Matrix4x4.CreateTranslation(44f, 0f, 0f)
        };

        var result = NpcBodyBuilder.TryResolveEquippedWeaponAttachmentTransform(
            new WeaponVisual
            {
                IsVisible = true,
                AttachmentMode = WeaponAttachmentMode.EquippedHandMounted,
                EmbeddedWeaponNode = "Bip01 Spine2"
            },
            idleBoneTransforms,
            out var attachmentNodeName,
            out var attachmentTransform,
            out var omitReason);

        Assert.True(result);
        Assert.Equal("Bip01 Spine2", attachmentNodeName);
        Assert.Equal(11f, attachmentTransform.Translation.X);
        Assert.Null(omitReason);
    }

    [Fact]
    public void EquippedWeaponAttachment_PrefersWeaponOverHandAndForeTwist()
    {
        var idleBoneTransforms = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Weapon"] = Matrix4x4.CreateTranslation(44f, 0f, 0f),
            ["Bip01 R Hand"] = Matrix4x4.CreateTranslation(33f, 0f, 0f),
            ["Bip01 R ForeTwist"] = Matrix4x4.CreateTranslation(22f, 0f, 0f)
        };

        var result = NpcBodyBuilder.TryResolveEquippedWeaponAttachmentTransform(
            new WeaponVisual
            {
                IsVisible = true,
                AttachmentMode = WeaponAttachmentMode.EquippedHandMounted
            },
            idleBoneTransforms,
            out var attachmentNodeName,
            out var attachmentTransform,
            out var omitReason);

        Assert.True(result);
        Assert.Equal("Weapon", attachmentNodeName);
        Assert.Equal(44f, attachmentTransform.Translation.X);
        Assert.Null(omitReason);
    }

    [Fact]
    public void EquippedWeaponAttachment_FallsBackToHandThenForeTwist()
    {
        var handOnlyTransforms = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bip01 R Hand"] = Matrix4x4.CreateTranslation(33f, 0f, 0f),
            ["Bip01 R ForeTwist"] = Matrix4x4.CreateTranslation(22f, 0f, 0f)
        };

        var handResult = NpcBodyBuilder.TryResolveEquippedWeaponAttachmentTransform(
            new WeaponVisual
            {
                IsVisible = true,
                AttachmentMode = WeaponAttachmentMode.EquippedHandMounted
            },
            handOnlyTransforms,
            out var handNodeName,
            out var handTransform,
            out var handOmitReason);

        Assert.True(handResult);
        Assert.Equal("Bip01 R Hand", handNodeName);
        Assert.Equal(33f, handTransform.Translation.X);
        Assert.Null(handOmitReason);

        var foreTwistOnlyTransforms = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bip01 R ForeTwist"] = Matrix4x4.CreateTranslation(22f, 0f, 0f)
        };

        var foreTwistResult = NpcBodyBuilder.TryResolveEquippedWeaponAttachmentTransform(
            new WeaponVisual
            {
                IsVisible = true,
                AttachmentMode = WeaponAttachmentMode.EquippedHandMounted
            },
            foreTwistOnlyTransforms,
            out var foreTwistNodeName,
            out var foreTwistTransform,
            out var foreTwistOmitReason);

        Assert.True(foreTwistResult);
        Assert.Equal("Bip01 R ForeTwist", foreTwistNodeName);
        Assert.Equal(22f, foreTwistTransform.Translation.X);
        Assert.Null(foreTwistOmitReason);
    }

    [Fact]
    public void EquippedWeaponAttachment_OmitsWhenNoCandidateBoneExists()
    {
        var result = NpcBodyBuilder.TryResolveEquippedWeaponAttachmentTransform(
            new WeaponVisual
            {
                IsVisible = true,
                AttachmentMode = WeaponAttachmentMode.EquippedHandMounted,
                MeshPath = @"meshes\weapons\hand2hand\powerfistrigid.nif"
            },
            new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase),
            out var attachmentNodeName,
            out _,
            out var omitReason);

        Assert.False(result);
        Assert.Equal(string.Empty, attachmentNodeName);
        Assert.Contains("no equipped attachment node", omitReason);
    }

    [Fact]
    public void EquipmentAttachment_PrefersLeftForeTwistThenForearmThenHand()
    {
        var leftForeTwistTransforms = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bip01 L ForeTwist"] = Matrix4x4.CreateTranslation(-21f, 2f, 60f),
            ["Bip01 L Forearm"] = Matrix4x4.CreateTranslation(-20f, 2f, 58f),
            ["Bip01 L Hand"] = Matrix4x4.CreateTranslation(-18f, 1f, 54f)
        };

        var foreTwistResult = NpcBodyBuilder.TryResolveEquipmentAttachmentTransform(
            new EquippedItem
            {
                MeshPath = @"meshes\pipboy3000\pipboyarmnpc.nif",
                BipedFlags = 0x40,
                AttachmentMode = EquipmentAttachmentMode.LeftWristRigid
            },
            leftForeTwistTransforms,
            out var foreTwistNodeName,
            out var foreTwistTransform,
            out var foreTwistOmitReason);

        Assert.True(foreTwistResult);
        Assert.Equal("Bip01 L ForeTwist", foreTwistNodeName);
        Assert.Equal(-21f, foreTwistTransform.Translation.X);
        Assert.Equal(60f, foreTwistTransform.Translation.Z);
        Assert.Null(foreTwistOmitReason);

        var handOnlyTransforms = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bip01 L Hand"] = Matrix4x4.CreateTranslation(-18f, 1f, 54f)
        };

        var handResult = NpcBodyBuilder.TryResolveEquipmentAttachmentTransform(
            new EquippedItem
            {
                MeshPath = @"meshes\pipboy3000\pipboyarmnpc.nif",
                BipedFlags = 0x40,
                AttachmentMode = EquipmentAttachmentMode.LeftWristRigid
            },
            handOnlyTransforms,
            out var handNodeName,
            out var handTransform,
            out var handOmitReason);

        Assert.True(handResult);
        Assert.Equal("Bip01 L Hand", handNodeName);
        Assert.Equal(-18f, handTransform.Translation.X);
        Assert.Equal(54f, handTransform.Translation.Z);
        Assert.Null(handOmitReason);
    }

    [Fact]
    public void HandToHandProcessAttachment_UsesWeaponLocalOnHeldIdleHandParentBeforeForeTwist()
    {
        var idleBoneTransforms = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bip01 R Hand"] = Matrix4x4.CreateTranslation(33f, 0f, 0f),
            ["Bip01 R ForeTwist"] = Matrix4x4.CreateTranslation(22f, 0f, 0f)
        };

        var result = NpcBodyBuilder.TryResolveProcessStyleWeaponAttachmentTransform(
            Matrix4x4.CreateTranslation(1f, 0f, 0f),
            idleBoneTransforms,
            ["Bip01 R Hand", "Bip01 R ForeTwist"],
            out var attachmentNodeName,
            out var attachmentTransform);

        Assert.True(result);
        Assert.Equal("Weapon via Bip01 R Hand", attachmentNodeName);
        Assert.Equal(34f, attachmentTransform.Translation.X);
    }

    [Fact]
    public void HandToHandProcessAttachment_FallsBackToForeTwistWhenHandParentMissing()
    {
        var idleBoneTransforms = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bip01 R ForeTwist"] = Matrix4x4.CreateTranslation(22f, 0f, 0f)
        };

        var result = NpcBodyBuilder.TryResolveProcessStyleWeaponAttachmentTransform(
            Matrix4x4.CreateTranslation(1f, 0f, 0f),
            idleBoneTransforms,
            ["Bip01 R Hand", "Bip01 R ForeTwist"],
            out var attachmentNodeName,
            out var attachmentTransform);

        Assert.True(result);
        Assert.Equal("Weapon via Bip01 R ForeTwist", attachmentNodeName);
        Assert.Equal(23f, attachmentTransform.Translation.X);
    }

    [Fact]
    public void HandToHandProcessAttachment_OmitsWhenNoProcessParentBoneExists()
    {
        var result = NpcBodyBuilder.TryResolveProcessStyleWeaponAttachmentTransform(
            Matrix4x4.Identity,
            new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase),
            ["Bip01 R Hand", "Bip01 R ForeTwist"],
            out var attachmentNodeName,
            out _);

        Assert.False(result);
        Assert.Equal(string.Empty, attachmentNodeName);
    }

    [Fact]
    public void HandToHandProcessAttachment_PrefersBindLocalOnHeldHandBeforeEquipLocalForeTwist()
    {
        var idleBoneTransforms = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bip01 R Hand"] = Matrix4x4.CreateTranslation(33f, 0f, 0f),
            ["Bip01 R ForeTwist"] = Matrix4x4.CreateTranslation(22f, 0f, 0f)
        };

        var result = NpcBodyBuilder.TryResolveHandToHandProcessStyleWeaponAttachmentTransform(
            Matrix4x4.CreateTranslation(1f, 0f, 0f),
            idleBoneTransforms,
            ["Bip01 R Hand", "Bip01 R ForeTwist"],
            null,
            "Bip01 R ForeTwist",
            [],
            null!,
            out var attachmentNodeName,
            out var attachmentTransform);

        Assert.True(result);
        Assert.Equal("Weapon via Bip01 R Hand", attachmentNodeName);
        Assert.Equal(34f, attachmentTransform.Translation.X);
    }

    [Fact]
    public void HandToHandProcessAttachment_PrefersPosedWeaponNodeOnlyWhenTrustedEquippedPoseIsPresent()
    {
        var meshesBsa = FindXboxFinalMeshesBsa();
        Assert.NotNull(meshesBsa);

        using var meshArchives = NpcMeshArchiveSet.Open(meshesBsa!, null);

        var idleBoneTransforms = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Weapon"] = Matrix4x4.CreateTranslation(44f, 1f, 2f),
            ["Bip01 R Hand"] = Matrix4x4.CreateTranslation(33f, 0f, 0f),
            ["Bip01 R ForeTwist"] = Matrix4x4.CreateTranslation(22f, 0f, 0f)
        };

        var result = NpcBodyBuilder.TryResolveHandToHandProcessWeaponAttachmentTransform(
            idleBoneTransforms,
            @"meshes\characters\_male\skeleton.nif",
            meshArchives,
            true,
            false,
            out var attachmentNodeName,
            out var attachmentTransform,
            out var omitReason);

        Assert.True(result, omitReason);
        Assert.Equal("Weapon (posed scene graph)", attachmentNodeName);
        Assert.Equal(new Vector3(44f, 1f, 2f), attachmentTransform.Translation);
    }

    [Fact]
    public void HandToHandProcessAttachment_RebuildsFromHeldHandBeforeUsingUntrustedWeaponNode()
    {
        var meshesBsa = FindXboxFinalMeshesBsa();
        Assert.NotNull(meshesBsa);

        using var meshArchives = NpcMeshArchiveSet.Open(meshesBsa!, null);

        var idleBoneTransforms = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Weapon"] = Matrix4x4.CreateTranslation(44f, 1f, 2f),
            ["Bip01 R Hand"] = Matrix4x4.CreateTranslation(33f, 0f, 0f),
            ["Bip01 R ForeTwist"] = Matrix4x4.CreateTranslation(22f, 0f, 0f)
        };

        var result = NpcBodyBuilder.TryResolveHandToHandProcessWeaponAttachmentTransform(
            idleBoneTransforms,
            @"meshes\characters\_male\skeleton.nif",
            meshArchives,
            false,
            false,
            out var attachmentNodeName,
            out var attachmentTransform,
            out var omitReason);

        Assert.True(result, omitReason);
        Assert.Equal("Weapon via Bip01 R Hand", attachmentNodeName);
        Assert.NotEqual(new Vector3(44f, 1f, 2f), attachmentTransform.Translation);
    }

    [Fact]
    public void HandToHandProcessAttachment_PowerFistHint_PrefersForeTwistEquipLocalBeforeHeldHand()
    {
        var meshesBsa = FindXboxFinalMeshesBsa();
        Assert.NotNull(meshesBsa);

        using var meshArchives = NpcMeshArchiveSet.Open(meshesBsa!, null);

        var idleBoneTransforms = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Weapon"] = Matrix4x4.CreateTranslation(44f, 1f, 2f),
            ["Bip01 R Hand"] = Matrix4x4.CreateTranslation(33f, 0f, 0f),
            ["Bip01 R ForeTwist"] = Matrix4x4.CreateTranslation(22f, 0f, 0f)
        };

        var result = NpcBodyBuilder.TryResolveHandToHandProcessWeaponAttachmentTransform(
            idleBoneTransforms,
            @"meshes\characters\_male\skeleton.nif",
            meshArchives,
            false,
            true,
            out var attachmentNodeName,
            out var attachmentTransform,
            out var omitReason);

        Assert.True(result, omitReason);
        Assert.Equal("Weapon via Bip01 R ForeTwist (equip local)", attachmentNodeName);
        Assert.True(attachmentTransform.Translation.X > 22f);
    }

    [Fact]
    public void H2hIdleSequence_ParsesHandParentOverride()
    {
        var idleKf = LoadNif(Path.Combine(MaleSampleCharacterRoot, "h2hidle.kf"));
        Assert.NotNull(idleKf);

        var parentOverride = NpcBodyBuilder.TryParseSequenceParentBoneName(idleKf.Value.Info);

        Assert.Equal("Bip01 R Hand", parentOverride);
    }

    [Fact]
    public void H2hEquipSequence_ParsesForeTwistParentOverride()
    {
        var equipKf = LoadNif(Path.Combine(MaleSampleCharacterRoot, "h2hequip.kf"));
        Assert.NotNull(equipKf);

        var parentOverride = NpcBodyBuilder.TryParseSequenceParentBoneName(equipKf.Value.Info);

        Assert.Equal("Bip01 R ForeTwist", parentOverride);
    }

    [Fact]
    public void H2hEquipSequence_ProvidesWeaponAndRightArmOverrides_WhileH2hIdleDoesNot()
    {
        var equipKf = LoadNif(Path.Combine(MaleSampleCharacterRoot, "h2hequip.kf"));
        var idleKf = LoadNif(Path.Combine(MaleSampleCharacterRoot, "h2hidle.kf"));
        Assert.NotNull(equipKf);
        Assert.NotNull(idleKf);

        var equipOverrides = NifAnimationParser.ParseIdlePoseOverrides(
            equipKf.Value.Data,
            equipKf.Value.Info,
            true);
        var idleOverrides = NifAnimationParser.ParseIdlePoseOverrides(
            idleKf.Value.Data,
            idleKf.Value.Info);

        Assert.NotNull(equipOverrides);
        Assert.Contains(equipOverrides!.Keys, k => string.Equals(k, "Weapon", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(equipOverrides.Keys, k => string.Equals(k, "Bip01 R Hand", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(equipOverrides.Keys,
            k => string.Equals(k, "Bip01 R Forearm", StringComparison.OrdinalIgnoreCase));
        Assert.Null(idleOverrides);
    }

    [Fact]
    public void ResolveAnimationAssetPath_UsesMeshesRootForFullIdleAssetPaths()
    {
        var resolved = NpcBodyBuilder.ResolveAnimationAssetPath(
            @"meshes\characters\_male\skeleton.nif",
            @"Characters\_Male\IdleAnims\VATSH2HAttackPower_PowerFistLow.kf");

        Assert.Equal(
            @"meshes\Characters\_Male\IdleAnims\VATSH2HAttackPower_PowerFistLow.kf",
            resolved);
    }

    [Fact]
    public void ResolveAnimationAssetPath_UsesSkeletonDirectoryForRelativeKfNames()
    {
        var resolved = NpcBodyBuilder.ResolveAnimationAssetPath(
            @"meshes\characters\_male\skeleton.nif",
            "h2hequip.kf");

        Assert.Equal(
            @"meshes\characters\_male\h2hequip.kf",
            resolved);
    }

    public void H2hEquipSequence_ParentOverrideChangesWeaponWorldTransform()
    {
        var skeleton = LoadNif(Path.Combine(MaleSampleCharacterRoot, "skeleton.nif"));
        var equipKf = LoadNif(Path.Combine(MaleSampleCharacterRoot, "h2hequip.kf"));
        Assert.NotNull(skeleton);
        Assert.NotNull(equipKf);

        var equipOverrides = NifAnimationParser.ParseIdlePoseOverrides(
            equipKf.Value.Data,
            equipKf.Value.Info,
            true);
        var parentOverride = NpcBodyBuilder.TryParseSequenceParentBoneName(equipKf.Value.Info);
        Assert.NotNull(equipOverrides);
        Assert.Equal("Bip01 R ForeTwist", parentOverride);

        var naiveWorldTransforms = NifGeometryExtractor.ExtractNamedBoneTransforms(
            skeleton.Value.Data,
            skeleton.Value.Info,
            equipOverrides);
        Assert.True(naiveWorldTransforms.TryGetValue("Weapon", out var naiveWeaponWorld));
        Assert.True(naiveWorldTransforms.ContainsKey(parentOverride!));

        var resolvedWeaponWorld = NpcBodyBuilder.ResolveWeaponHolsterAttachmentTransform(
            naiveWorldTransforms,
            equipOverrides!,
            skeleton.Value.Data,
            skeleton.Value.Info,
            "Weapon",
            parentOverride,
            naiveWorldTransforms);

        Assert.NotNull(resolvedWeaponWorld);
        Assert.True(Vector3.Distance(naiveWeaponWorld.Translation, resolvedWeaponWorld.Value.Translation) > 0.25f);
    }

    [Fact]
    public void H2hEquipSequence_AnimatedWeaponLocalIsOnlyUsedOnForeTwistParent()
    {
        var skeleton = LoadNif(Path.Combine(MaleSampleCharacterRoot, "skeleton.nif"));
        var equipKf = LoadNif(Path.Combine(MaleSampleCharacterRoot, "h2hequip.kf"));
        Assert.NotNull(skeleton);
        Assert.NotNull(equipKf);

        var equipOverrides = NifAnimationParser.ParseIdlePoseOverrides(
            equipKf.Value.Data,
            equipKf.Value.Info,
            true);
        Assert.NotNull(equipOverrides);
        Assert.Contains("Weapon", equipOverrides!.Keys, StringComparer.OrdinalIgnoreCase);

        var foreTwistWorldTransforms = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bip01 R ForeTwist"] = Matrix4x4.CreateTranslation(22f, 0f, 0f)
        };

        var result = NpcBodyBuilder.TryResolveHandToHandProcessStyleWeaponAttachmentTransform(
            Matrix4x4.CreateTranslation(1f, 0f, 0f),
            foreTwistWorldTransforms,
            ["Bip01 R Hand", "Bip01 R ForeTwist"],
            equipOverrides,
            "Bip01 R ForeTwist",
            skeleton.Value.Data,
            skeleton.Value.Info,
            out var attachmentNodeName,
            out var resolvedWeaponWorld);

        Assert.True(result);
        Assert.Equal("Weapon via Bip01 R ForeTwist (equip local)", attachmentNodeName);
        Assert.True(resolvedWeaponWorld.Translation.X > 22f);
    }

    [Fact]
    public void TwoHandMeleeHolsterSequence_PreservesInterpolatorBaseWeaponTranslation()
    {
        var holsterKf = LoadNif(Path.Combine(MaleSampleCharacterRoot, "2hmHolster.kf"));
        Assert.NotNull(holsterKf);

        var holsterOverrides = NifAnimationParser.ParseIdlePoseOverrides(
            holsterKf.Value.Data,
            holsterKf.Value.Info,
            true);
        var parentOverride = NpcBodyBuilder.TryParseSequenceParentBoneName(holsterKf.Value.Info);

        Assert.NotNull(holsterOverrides);
        Assert.Equal("Bip01 Spine2", parentOverride);

        var weaponPose = Assert.Contains("Weapon", holsterOverrides!);
        Assert.True(weaponPose.HasTranslation);
        Assert.Equal(16.985f, weaponPose.Tx, 3);
        Assert.Equal(-12.076f, weaponPose.Ty, 3);
        Assert.Equal(4.451f, weaponPose.Tz, 3);
    }

    [Fact]
    public void PowerFistRigidModel_DoesNotExposeInternalWeaponAnchorCompensation()
    {
        var rigid = LoadNif(FindPowerFistRigidPath());
        Assert.NotNull(rigid);

        var namedTransforms = NifGeometryExtractor.ExtractNamedBoneTransforms(
            rigid.Value.Data,
            rigid.Value.Info);
        Assert.False(namedTransforms.ContainsKey("Weapon"));

        var result = NpcBodyBuilder.TryResolveModelAttachmentCompensation(
            rigid.Value.Data,
            rigid.Value.Info,
            "Weapon",
            out _);

        Assert.False(result);
    }

    [Fact]
    public void BaseballBatModel_RotatedRoot_UsesRootAttachmentCompensationFallback()
    {
        var bat = LoadNif(FindBaseballBatPath());
        Assert.NotNull(bat);

        var result = NpcBodyBuilder.TryResolveModelAttachmentCompensation(
            bat.Value.Data,
            bat.Value.Info,
            "Weapon",
            out var compensation,
            out var compensationKind);

        Assert.True(result);
        Assert.Equal(NpcBodyBuilder.ModelAttachmentCompensationKind.RootFallback, compensationKind);
        Assert.False(compensation.Equals(Matrix4x4.Identity));
    }

    [Fact]
    public void RootFallbackWeaponCompensation_IsSkippedForHolsterPoseWeapons()
    {
        var shouldApply = NpcBodyBuilder.ShouldApplyWeaponModelAttachmentCompensation(
            WeaponAttachmentMode.HolsterPose,
            NpcBodyBuilder.ModelAttachmentCompensationKind.RootFallback);

        Assert.False(shouldApply);
    }

    [Fact]
    public void ExplicitWeaponNodeCompensation_IsPreservedForHolsterPoseWeapons()
    {
        var shouldApply = NpcBodyBuilder.ShouldApplyWeaponModelAttachmentCompensation(
            WeaponAttachmentMode.HolsterPose,
            NpcBodyBuilder.ModelAttachmentCompensationKind.ExplicitAttachmentNode);

        Assert.True(shouldApply);
    }

    [Fact]
    public void PowerFistRigidModel_IncludesBillboardAttachmentNodesForSprayMeshConnectShapes()
    {
        var rigid = LoadNif(FindPowerFistRigidPath());
        Assert.NotNull(rigid);

        var namedTransforms = NifGeometryExtractor.ExtractNamedBoneTransforms(
            rigid.Value.Data,
            rigid.Value.Info);

        Assert.Contains("##SprayMeshConnect03", namedTransforms.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("##SprayMeshConnect04", namedTransforms.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("##SprayMeshConnect05", namedTransforms.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("##SprayMeshConnect06", namedTransforms.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Flamer.NIF", 1)]
    [InlineData("Minigun.NIF", 4)]
    public void HeavyWeaponVisAnalysis_UsesBackpackUserPropertyBufferAttachment(
        string fileName,
        int expectedVisShapeCount)
    {
        var weapon = LoadNif(FindTwoHandHandleWeaponPath(fileName));
        Assert.NotNull(weapon);

        var analysis = NifSceneGraphWalker.AnalyzeVisControllers(weapon.Value.Data, weapon.Value.Info);

        Assert.Equal(expectedVisShapeCount, analysis.VisControlledShapeIndices.Count);
        var backpackGroup = Assert.Single(analysis.ParentBoneGroups);
        Assert.Equal("Backpack", backpackGroup.SourceNodeName);
        Assert.Equal("Bip01 Spine2", backpackGroup.BoneName);
        Assert.NotEmpty(backpackGroup.ShapeIndices);
    }

    [Fact]
    public void PathDeriver_DerivesHandTextureAndBodyEgtPaths()
    {
        var headTriPath = NpcAppearancePathDeriver.DeriveHeadTriPath(
            @"meshes\characters\ghoul\headghoul.nif");
        var handTexture = NpcAppearancePathDeriver.DeriveHandTexturePath(
            @"characters\ghoul\UpperBodyMale.dds",
            false);
        var bodyEgtPaths = NpcAppearancePathDeriver.DeriveBodyEgtPaths(
            @"characters\ghoul\headghoul.nif",
            false);

        Assert.Equal(@"meshes\characters\ghoul\headghoul.tri", headTriPath);
        Assert.Equal(@"textures\characters\ghoul\HandMale.dds", handTexture);
        Assert.Equal(@"meshes\characters\_male\upperbodyhumanghoul.egt", bodyEgtPaths.BodyEgt);
        Assert.Equal(@"meshes\characters\_male\lefthandghoul.egt", bodyEgtPaths.LeftHandEgt);
        Assert.Equal(@"meshes\characters\_male\righthandghoul.egt", bodyEgtPaths.RightHandEgt);
    }

    private static NpcWeaponResolver CreateWeaponResolver(
        IReadOnlyDictionary<uint, PackageScanEntry>? packages = null,
        IReadOnlyDictionary<uint, WeapScanEntry>? weapons = null,
        IReadOnlyDictionary<uint, ArmaAddonScanEntry>? armorAddons = null,
        IReadOnlyDictionary<uint, List<uint>>? leveledItems = null,
        IReadOnlyDictionary<uint, IdleScanEntry>? idles = null)
    {
        return new NpcWeaponResolver(
            packages ?? new Dictionary<uint, PackageScanEntry>(),
            weapons ?? new Dictionary<uint, WeapScanEntry>(),
            armorAddons ?? new Dictionary<uint, ArmaAddonScanEntry>(),
            leveledItems ?? new Dictionary<uint, List<uint>>(),
            idles ?? new Dictionary<uint, IdleScanEntry>());
    }

    private static WeapScanEntry BuildWeapon(
        string modelPath,
        WeaponType weaponType,
        short damage,
        float shotsPerSec,
        uint? ammoFormId = null,
        byte flags = 0,
        string? embeddedWeaponNode = null,
        byte handGripAnim = 0xff)
    {
        return new WeapScanEntry
        {
            ModelPath = modelPath,
            WeaponType = weaponType,
            Damage = damage,
            ShotsPerSec = shotsPerSec,
            AmmoFormId = ammoFormId,
            Health = 100,
            Flags = flags,
            HandGripAnim = handGripAnim,
            EmbeddedWeaponNode = embeddedWeaponNode
        };
    }

    private static (byte[] Data, NifInfo Info)? LoadNif(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var data = File.ReadAllBytes(fullPath);
        var nif = NifParser.Parse(data);
        return nif != null ? (data, nif) : null;
    }

    private static string FindMaleSampleCharacterRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(
                dir,
                "Sample",
                "Unpacked_Builds",
                "PC_Final_Unpacked",
                "Data",
                "meshes",
                "characters",
                "_male");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        return Path.Combine(
            "Sample",
            "Unpacked_Builds",
            "PC_Final_Unpacked",
            "Data",
            "meshes",
            "characters",
            "_male");
    }

    private static string? FindXboxFinalMeshesBsa()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(
                dir,
                "Sample",
                "Full_Builds",
                "Fallout New Vegas (360 Final)",
                "Data",
                "Fallout - Meshes.bsa");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        var fallback = Path.Combine(
            "Sample",
            "Full_Builds",
            "Fallout New Vegas (360 Final)",
            "Data",
            "Fallout - Meshes.bsa");
        return File.Exists(fallback) ? Path.GetFullPath(fallback) : null;
    }

    private static string FindPowerFistRigidPath()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(
                dir,
                "Sample",
                "Unpacked_Builds",
                "PC_Final_Unpacked",
                "Data",
                "meshes",
                "Weapons",
                "Hand2Hand",
                "PowerFistRigid.NIF");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        return Path.Combine(
            "Sample",
            "Unpacked_Builds",
            "PC_Final_Unpacked",
            "Data",
            "meshes",
            "Weapons",
            "Hand2Hand",
            "PowerFistRigid.NIF");
    }

    private static string FindBaseballBatPath()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(
                dir,
                "Sample",
                "Unpacked_Builds",
                "PC_Final_Unpacked",
                "Data",
                "meshes",
                "Weapons",
                "2HandMelee",
                "BaseballBat.NIF");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        return Path.Combine(
            "Sample",
            "Unpacked_Builds",
            "PC_Final_Unpacked",
            "Data",
            "meshes",
            "Weapons",
            "2HandMelee",
            "BaseballBat.NIF");
    }

    private static string FindTwoHandHandleWeaponPath(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(
                dir,
                "Sample",
                "Unpacked_Builds",
                "PC_Final_Unpacked",
                "Data",
                "meshes",
                "Weapons",
                "2HandHandle",
                fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        return Path.Combine(
            "Sample",
            "Unpacked_Builds",
            "PC_Final_Unpacked",
            "Data",
            "meshes",
            "Weapons",
            "2HandHandle",
            fileName);
    }
}
