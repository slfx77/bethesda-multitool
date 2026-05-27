using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Tests for simple-type new-record encoders (GMST/GLOB/MISC/KEYM/ALCH/BOOK/AMMO),
///     INFO CIS1/CIS2 string-parameter emission, WEAP ModelVariants (WNAM/WNM*/MWD*),
///     standalone new-record encoders for PROJ/EXPL/IMOD, and the trivial+small encoder
///     batch (EYES/HAIR/REPU/AVIF/MUSC/MESG/NOTE/FLST/LVLI).
/// </summary>
public class NewTopLevelEncoderTests
{
    [Fact]
    public void GmstEncoder_EncodeNew_FloatGmst_EmitsEdidAndDataAsFloat()
    {
        var gmst = new GameSettingRecord
        {
            FormId = 0x800,
            EditorId = "fNewSetting",
            ValueType = GameSettingType.Float,
            FloatValue = 3.14f
        };

        var encoded = GmstEncoder.EncodeNew(gmst);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "DATA"], sigs);

        var data = encoded.Subrecords[1];
        Assert.Equal(3.14f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes));
    }

    [Fact]
    public void GmstEncoder_EncodeNew_StringGmst_EmitsEdidOnlyWithWarning()
    {
        var gmst = new GameSettingRecord
        {
            FormId = 0x800,
            EditorId = "sNewSetting",
            ValueType = GameSettingType.String,
            StringValue = "hello"
        };

        var encoded = GmstEncoder.EncodeNew(gmst);

        Assert.Equal(["EDID"], encoded.Subrecords.Select(s => s.Signature));
        Assert.Contains(encoded.Warnings, w => w.Contains("string"));
    }

    [Fact]
    public void GlobEncoder_EncodeNew_EmitsEdidFnamFltvInOrder()
    {
        var glob = new GlobalRecord
        {
            FormId = 0x800,
            EditorId = "NewGlobal",
            ValueType = 'f',
            Value = 42.5f
        };

        var encoded = GlobEncoder.EncodeNew(glob);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "FNAM", "FLTV"], sigs);
        Assert.Equal((byte)'f', encoded.Subrecords[1].Bytes[0]);
        Assert.Equal(42.5f, BinaryPrimitives.ReadSingleLittleEndian(encoded.Subrecords[2].Bytes));
    }

    [Fact]
    public void MiscEncoder_EncodeNew_EmitsCanonicalOrder()
    {
        var misc = new MiscItemRecord
        {
            FormId = 0x800,
            EditorId = "NewMisc",
            FullName = "New Item",
            ModelPath = "Items/Misc/NewItem.NIF",
            Bounds = new ObjectBounds { X1 = -5, Y1 = -5, Z1 = 0, X2 = 5, Y2 = 5, Z2 = 10 },
            Value = 25,
            Weight = 1.5f
        };

        var encoded = MiscEncoder.EncodeNew(misc);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "OBND", "FULL", "MODL", "DATA"], sigs);
    }

    [Fact]
    public void MiscEncoder_EncodeNew_NoModel_WarnsAboutMissingModel()
    {
        var misc = new MiscItemRecord { FormId = 0x800, EditorId = "NoModel", Value = 5, Weight = 1.0f };
        var encoded = MiscEncoder.EncodeNew(misc);
        Assert.Contains(encoded.Warnings, w => w.Contains("model"));
    }

    [Fact]
    public void KeymEncoder_EncodeNew_OmitsObndWhenAbsent()
    {
        var key = new KeyRecord { FormId = 0x800, EditorId = "NewKey", Value = 0, Weight = 0.1f };
        var encoded = KeymEncoder.EncodeNew(key);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.DoesNotContain("OBND", sigs);
        Assert.Contains("EDID", sigs);
        Assert.Contains("DATA", sigs);
    }

    [Fact]
    public void AlchEncoder_EncodeNew_EmitsDataWeight()
    {
        var alch = new ConsumableRecord { FormId = 0x800, EditorId = "Stim", Weight = 0.25f };
        var encoded = AlchEncoder.EncodeNew(alch);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes));
    }

    [Fact]
    public void BookEncoder_EncodeNew_EmitsDescAndEnamWhenPresent()
    {
        var book = new BookRecord
        {
            FormId = 0x800,
            EditorId = "NewBook",
            FullName = "Book Title",
            Text = "Once upon a time...",
            Value = 5,
            Weight = 0.5f,
            EnchantmentFormId = 0x1234
        };

        var encoded = BookEncoder.EncodeNew(book);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Contains("DESC", sigs);
        Assert.Contains("ENAM", sigs);
        // DESC must come before DATA per fopdoc.
        Assert.True(sigs.IndexOf("DESC") < sigs.IndexOf("DATA"));
        // ENAM comes after DATA per fopdoc.
        Assert.True(sigs.IndexOf("DATA") < sigs.IndexOf("ENAM"));
    }

    [Fact]
    public void AmmoEncoder_EncodeNew_DataIsThirteenBytes()
    {
        var ammo = new AmmoRecord
        {
            FormId = 0x800,
            EditorId = "NewAmmo",
            Speed = 5000f,
            Flags = 0x01,
            Value = 1u,
            ClipRounds = 6
        };

        var encoded = AmmoEncoder.EncodeNew(ammo);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(13, data.Bytes.Length);
    }

    [Fact]
    public void AmmoEncoder_EncodeNew_ProjectileData_EmitsWarning()
    {
        var ammo = new AmmoRecord
        {
            FormId = 0x800,
            EditorId = "NewAmmo",
            ProjectileFormId = 0xABC
        };

        var encoded = AmmoEncoder.EncodeNew(ammo);
        Assert.Contains(encoded.Warnings, w => w.Contains("DAT2"));
    }

    [Fact]
    public void DebrEncoder_EncodeNew_Variants_PreservesDataFields()
    {
        var debr = new DebrisRecord
        {
            FormId = 0x900,
            EditorId = "TestDebris",
            Variants =
            [
                new DebrisVariantData(42, @"meshes\clutter\test.nif", 0x05)
            ]
        };

        var encoded = DebrEncoder.EncodeNew(debr);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        var pathBytes = Encoding.Latin1.GetBytes(@"meshes\clutter\test.nif");
        Assert.Equal(42, data.Bytes[0]);
        Assert.Equal(pathBytes, data.Bytes[1..(1 + pathBytes.Length)]);
        Assert.Equal(0, data.Bytes[1 + pathBytes.Length]);
        Assert.Equal(0x05, data.Bytes[^1]);
    }

    [Fact]
    public void AllNewRecordEncoders_HandleMissingEditorIdWithWarning()
    {
        var gmst = GmstEncoder.EncodeNew(new GameSettingRecord { FormId = 1, EditorId = null });
        Assert.Contains(gmst.Warnings, w => w.Contains("EditorId"));

        var glob = GlobEncoder.EncodeNew(new GlobalRecord { FormId = 1, EditorId = null });
        Assert.Contains(glob.Warnings, w => w.Contains("EditorId"));

        var misc = MiscEncoder.EncodeNew(new MiscItemRecord { FormId = 1, EditorId = null });
        Assert.Contains(misc.Warnings, w => w.Contains("EditorId"));
    }

    // ====================================================================================
    // INFO CIS1/CIS2 emission — mirrors the v10 TERM pattern (CIS1/CIS2 trail their CTDA
    // when string parameters are set on the condition).
    // ====================================================================================

    [Fact]
    public void InfoEncoder_EncodeNew_EmitsCis1AfterCtdaWhenParameter1StringSet()
    {
        var info = new DialogueRecord
        {
            FormId = 0x600,
            Conditions =
            [
                new DialogueCondition
                {
                    Type = 0x20,
                    FunctionIndex = 449, // GetVariable
                    Parameter1String = "MyScriptVar"
                }
            ]
        };

        var encoded = InfoEncoder.EncodeNew(info);

        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "CTDA" or "CIS1" or "CIS2")
            .Select(s => s.Signature)
            .ToList();
        Assert.Equal(["CTDA", "CIS1"], sigs);

        var cis1 = Assert.Single(encoded.Subrecords, s => s.Signature == "CIS1");
        Assert.Equal("MyScriptVar".Length + 1, cis1.Bytes.Length);
        Assert.Equal(0, cis1.Bytes[^1]);
    }

    [Fact]
    public void InfoEncoder_EncodeNew_EmitsCis1AndCis2WhenBothSet()
    {
        var info = new DialogueRecord
        {
            FormId = 0x600,
            Conditions =
            [
                new DialogueCondition
                {
                    Type = 0x20,
                    FunctionIndex = 449,
                    Parameter1String = "P1",
                    Parameter2String = "P2"
                }
            ]
        };

        var encoded = InfoEncoder.EncodeNew(info);

        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "CTDA" or "CIS1" or "CIS2")
            .Select(s => s.Signature)
            .ToList();
        Assert.Equal(["CTDA", "CIS1", "CIS2"], sigs);
    }

    [Fact]
    public void InfoEncoder_EncodeNew_OmitsCis1Cis2WhenStringsNull()
    {
        var info = new DialogueRecord
        {
            FormId = 0x600,
            Conditions =
            [
                new DialogueCondition { Type = 0x20, FunctionIndex = 1 }
            ]
        };

        var encoded = InfoEncoder.EncodeNew(info);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "CIS1");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "CIS2");
        Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA");
    }

    // ====================================================================================
    // WEAP ModelVariants emission (WNAM/WNM*/MWD*)
    // ====================================================================================

    [Fact]
    public void WeapEncoder_EncodeNew_EmitsWnamForBaseVariant()
    {
        var weap = new WeaponRecord
        {
            FormId = 0x700,
            EditorId = "TestGun",
            ModelPath = "testgun.nif",
            ModelVariants =
            [
                new WeaponModelVariant
                {
                    Combination = WeaponModCombination.None,
                    FirstPersonObjectFormId = 0xABCDEFu
                }
            ]
        };

        var encoded = WeapEncoder.EncodeNew(weap);

        var wnam = Assert.Single(encoded.Subrecords, s => s.Signature == "WNAM");
        Assert.Equal(0xABCDEFu, BinaryPrimitives.ReadUInt32LittleEndian(wnam.Bytes));
    }

    [Fact]
    public void WeapEncoder_EncodeNew_EmitsPerCombinationWnmAndMwd()
    {
        var weap = new WeaponRecord
        {
            FormId = 0x700,
            EditorId = "TestGun",
            ModelPath = "testgun.nif",
            ModelVariants =
            [
                new WeaponModelVariant
                {
                    Combination = WeaponModCombination.Mod1,
                    FirstPersonObjectFormId = 0x101u,
                    ThirdPersonModelPath = "mod1_world.nif"
                },
                new WeaponModelVariant
                {
                    Combination = WeaponModCombination.Mod12,
                    FirstPersonObjectFormId = 0x404u,
                    ThirdPersonModelPath = "mod12_world.nif"
                }
            ]
        };

        var encoded = WeapEncoder.EncodeNew(weap);

        var wnm1 = Assert.Single(encoded.Subrecords, s => s.Signature == "WNM1");
        Assert.Equal(0x101u, BinaryPrimitives.ReadUInt32LittleEndian(wnm1.Bytes));
        var wnm4 = Assert.Single(encoded.Subrecords, s => s.Signature == "WNM4");
        Assert.Equal(0x404u, BinaryPrimitives.ReadUInt32LittleEndian(wnm4.Bytes));

        var mwd1 = Assert.Single(encoded.Subrecords, s => s.Signature == "MWD1");
        Assert.Equal("mod1_world.nif".Length + 1, mwd1.Bytes.Length);
        var mwd4 = Assert.Single(encoded.Subrecords, s => s.Signature == "MWD4");
        Assert.Equal("mod12_world.nif".Length + 1, mwd4.Bytes.Length);
    }

    [Fact]
    public void WeapEncoder_EncodeNew_OmitsWnamMwdWhenNoVariants()
    {
        var weap = new WeaponRecord
        {
            FormId = 0x700,
            EditorId = "TestGun",
            ModelPath = "testgun.nif"
        };

        var encoded = WeapEncoder.EncodeNew(weap);

        Assert.DoesNotContain(encoded.Subrecords,
            s => s.Signature is "WNAM" or "WNM1" or "WNM2" or "WNM3" or "WNM4" or "WNM5" or "WNM6" or "WNM7");
        Assert.DoesNotContain(encoded.Subrecords,
            s => s.Signature is "MWD1" or "MWD2" or "MWD3" or "MWD4" or "MWD5" or "MWD6" or "MWD7");
        // The pre-v11 deferred-warning should be gone.
        Assert.DoesNotContain(encoded.Warnings, w => w.Contains("MOD2/MOD3/MOD4"));
    }

    [Fact]
    public void WeapEncoder_EncodeNew_AllSevenCombinationsMapToCorrectIndex()
    {
        var combos = new (WeaponModCombination Combination, int ExpectedIndex)[]
        {
            (WeaponModCombination.Mod1, 1),
            (WeaponModCombination.Mod2, 2),
            (WeaponModCombination.Mod3, 3),
            (WeaponModCombination.Mod12, 4),
            (WeaponModCombination.Mod13, 5),
            (WeaponModCombination.Mod23, 6),
            (WeaponModCombination.Mod123, 7)
        };

        foreach (var (combination, expectedIndex) in combos)
        {
            var weap = new WeaponRecord
            {
                FormId = 0x700,
                EditorId = "TestGun",
                ModelPath = "testgun.nif",
                ModelVariants =
                [
                    new WeaponModelVariant
                    {
                        Combination = combination,
                        FirstPersonObjectFormId = 0x1u,
                        ThirdPersonModelPath = "mesh.nif"
                    }
                ]
            };

            var encoded = WeapEncoder.EncodeNew(weap);

            Assert.Contains(encoded.Subrecords, s => s.Signature == $"WNM{expectedIndex}");
            Assert.Contains(encoded.Subrecords, s => s.Signature == $"MWD{expectedIndex}");
        }
    }

    // ====================================================================================
    // PROJ encoder (84-byte DATA)
    // ====================================================================================

    [Fact]
    public void ProjEncoder_EncodeNew_DataIs84Bytes()
    {
        var proj = new ProjectileRecord { FormId = 0x800, EditorId = "Bullet" };
        var encoded = ProjEncoder.EncodeNew(proj);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(84, data.Bytes.Length);
    }

    [Fact]
    public void ProjEncoder_EncodeNew_DataLayoutMatchesPdb()
    {
        var proj = new ProjectileRecord
        {
            FormId = 0x800,
            EditorId = "Bullet",
            Flags = 0x1234,
            ProjectileType = 0x0001, // Missile
            Gravity = 1.5f,
            Speed = 8000.0f,
            Range = 30000.0f,
            Light = 0x111u,
            MuzzleFlashLight = 0x222u,
            TracerChance = 0.25f,
            ExplosionProximity = 50.0f,
            ExplosionTimer = 3.0f,
            Explosion = 0x333u,
            Sound = 0x444u,
            MuzzleFlashDuration = 0.1f,
            FadeDuration = 0.5f,
            ImpactForce = 100.0f,
            CountdownSound = 0x555u,
            DeactivateSound = 0x666u,
            DefaultWeaponSource = 0x777u,
            RotationX = 1.0f,
            RotationY = 2.0f,
            RotationZ = 3.0f,
            BounceMultiplier = 0.8f
        };

        var encoded = ProjEncoder.EncodeNew(proj);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;

        Assert.Equal((ushort)0x1234, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0, 2)));
        Assert.Equal((ushort)0x0001, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2, 2)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(4, 4)));
        Assert.Equal(8000.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(8, 4)));
        Assert.Equal(30000.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(12, 4)));
        Assert.Equal(0x111u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16, 4)));
        Assert.Equal(0x222u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20, 4)));
        Assert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(24, 4)));
        Assert.Equal(0x333u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(36, 4)));
        Assert.Equal(0x444u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40, 4)));
        Assert.Equal(0x555u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(56, 4)));
        Assert.Equal(0x666u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(60, 4)));
        Assert.Equal(0x777u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(64, 4)));
        Assert.Equal(0.8f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(80, 4)));
    }

    [Fact]
    public void ProjEncoder_EncodeNew_CanonicalSubrecordOrder()
    {
        var proj = new ProjectileRecord
        {
            FormId = 0x800,
            EditorId = "Bullet",
            FullName = "9mm Round",
            ModelPath = "9mm.nif",
            SoundLevel = 3
        };

        var encoded = ProjEncoder.EncodeNew(proj);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FULL", "MODL", "DATA", "VNAM"], sigs);
    }

    [Fact]
    public void ProjEncoder_EncodeNew_OmitsVnamWhenSoundLevelZero()
    {
        var proj = new ProjectileRecord { FormId = 0x800, EditorId = "Bullet", SoundLevel = 0 };
        var encoded = ProjEncoder.EncodeNew(proj);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "VNAM");
    }

    // ====================================================================================
    // EXPL encoder (52-byte DATA)
    // ====================================================================================

    [Fact]
    public void ExplEncoder_EncodeNew_DataIs52Bytes()
    {
        var expl = new ExplosionRecord { FormId = 0x900, EditorId = "Boom" };
        var encoded = ExplEncoder.EncodeNew(expl);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(52, data.Bytes.Length);
    }

    [Fact]
    public void ExplEncoder_EncodeNew_DataLayoutMatchesPdb()
    {
        var expl = new ExplosionRecord
        {
            FormId = 0x900,
            EditorId = "Boom",
            Force = 500.0f,
            Damage = 100.0f,
            Radius = 256.0f,
            Light = 0x111u,
            Sound1 = 0x222u,
            Flags = 0x0F,
            IsRadius = 512.0f,
            ImpactDataSet = 0x333u,
            Sound2 = 0x444u,
            RadiationLevel = 10.0f,
            RadiationDissipationTime = 5.0f,
            RadiationRadius = 1024.0f,
            SoundLevel = 3
        };

        var encoded = ExplEncoder.EncodeNew(expl);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;

        Assert.Equal(500.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(0, 4)));
        Assert.Equal(100.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(4, 4)));
        Assert.Equal(256.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(8, 4)));
        Assert.Equal(0x111u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12, 4)));
        Assert.Equal(0x222u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16, 4)));
        Assert.Equal(0x0Fu, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20, 4)));
        Assert.Equal(512.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(24, 4)));
        Assert.Equal(0x333u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28, 4)));
        Assert.Equal(0x444u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(32, 4)));
        Assert.Equal(10.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(36, 4)));
        Assert.Equal(5.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(40, 4)));
        Assert.Equal(1024.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(44, 4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(48, 4)));
    }

    [Fact]
    public void ExplEncoder_EncodeNew_CanonicalSubrecordOrder()
    {
        var expl = new ExplosionRecord
        {
            FormId = 0x900,
            EditorId = "Boom",
            FullName = "Explosion",
            ModelPath = "explosion.nif",
            Enchantment = 0x12345u
        };

        var encoded = ExplEncoder.EncodeNew(expl);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FULL", "MODL", "EITM", "DATA"], sigs);
    }

    [Fact]
    public void ExplEncoder_EncodeNew_OmitsEitmWhenZero()
    {
        var expl = new ExplosionRecord { FormId = 0x900, EditorId = "Boom" };
        var encoded = ExplEncoder.EncodeNew(expl);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "EITM");
    }

    // ====================================================================================
    // IMOD encoder (8-byte DATA)
    // ====================================================================================

    [Fact]
    public void ImodEncoder_EncodeNew_DataIs8Bytes()
    {
        var imod = new WeaponModRecord { FormId = 0xA00, EditorId = "Suppressor", Value = 150, Weight = 0.5f };
        var encoded = ImodEncoder.EncodeNew(imod);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(8, data.Bytes.Length);
        Assert.Equal(150, BinaryPrimitives.ReadInt32LittleEndian(data.Bytes.AsSpan(0, 4)));
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void ImodEncoder_EncodeNew_CanonicalSubrecordOrder()
    {
        var imod = new WeaponModRecord
        {
            FormId = 0xA00,
            EditorId = "Suppressor",
            FullName = "9mm Suppressor",
            Description = "Reduces sound",
            ModelPath = "suppressor.nif",
            Icon = "icons/suppressor.dds",
            Value = 200,
            Weight = 0.4f
        };

        var encoded = ImodEncoder.EncodeNew(imod);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FULL", "DESC", "MODL", "ICON", "DATA"], sigs);
    }

    [Fact]
    public void ImodEncoder_EncodeNew_OmitsOptionalsWhenNull()
    {
        var imod = new WeaponModRecord { FormId = 0xA00, EditorId = "Suppressor" };
        var encoded = ImodEncoder.EncodeNew(imod);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "DATA"], sigs);
    }

    // ====================================================================================
    // Trivial + small new-record encoder batch:
    // EYES, HAIR, REPU, AVIF, MUSC, MESG, NOTE, FLST, and LVLI/LVLN/LVLC
    // ====================================================================================

    // ====================================================================================
    // EyesEncoder
    // ====================================================================================

    [Fact]
    public void EyesEncoder_EncodeNew_CanonicalOrder()
    {
        var eyes = new EyesRecord
        {
            FormId = 0x1100,
            EditorId = "BlueEyes",
            FullName = "Blue",
            TexturePath = "characters/eyes/blue.dds",
            Flags = 0x01
        };

        var encoded = EyesEncoder.EncodeNew(eyes);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FULL", "ICON", "DATA"], sigs);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Single(data.Bytes);
        Assert.Equal(0x01, data.Bytes[0]);
    }

    [Fact]
    public void EyesEncoder_EncodeNew_OmitsOptionalsWhenNull()
    {
        var eyes = new EyesRecord { FormId = 0x1100, EditorId = "E", Flags = 0 };
        var encoded = EyesEncoder.EncodeNew(eyes);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "DATA"], sigs);
    }

    // ====================================================================================
    // HairEncoder
    // ====================================================================================

    [Fact]
    public void HairEncoder_EncodeNew_CanonicalOrder()
    {
        var hair = new HairRecord
        {
            FormId = 0x1200,
            EditorId = "BrownHair",
            FullName = "Brown",
            ModelPath = "characters/hair/brown.nif",
            TexturePath = "characters/hair/brown.dds",
            Flags = 0x01
        };

        var encoded = HairEncoder.EncodeNew(hair);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FULL", "MODL", "ICON", "DATA"], sigs);
    }

    // ====================================================================================
    // RepuEncoder
    // ====================================================================================

    [Fact]
    public void RepuEncoder_EncodeNew_DataLayout()
    {
        var repu = new ReputationRecord
        {
            FormId = 0x1300,
            EditorId = "NCRRep",
            FullName = "NCR Reputation",
            PositiveValue = 100.0f,
            NegativeValue = -50.0f
        };

        var encoded = RepuEncoder.EncodeNew(repu);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;

        Assert.Equal(8, data.Length);
        Assert.Equal(100.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(0, 4)));
        Assert.Equal(-50.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(4, 4)));
    }

    [Fact]
    public void RepuEncoder_EncodeNew_OmitsFullWhenNull()
    {
        var repu = new ReputationRecord { FormId = 0x1300, EditorId = "R" };
        var encoded = RepuEncoder.EncodeNew(repu);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "DATA"], sigs);
    }

    // ====================================================================================
    // AvifEncoder
    // ====================================================================================

    [Fact]
    public void AvifEncoder_EncodeNew_SkipsAllAvifEmission()
    {
        // v20.13: new AVIF emission was disabled — actor values are engine-hardcoded
        // and emitting any new AVIF crashes FNV at startup (~17 iterations of bisection
        // pinned the crash to this encoder). The encoder now always returns an empty
        // subrecord list with a "skipping" warning, regardless of the input fields.
        var avif = new ActorValueInfoRecord
        {
            FormId = 0x1400,
            EditorId = "Strength",
            FullName = "Strength",
            Description = "Raw physical power.",
            Icon = "icons/special/strength.dds",
            Abbreviation = "STR"
        };

        var encoded = AvifEncoder.EncodeNew(avif);

        Assert.Empty(encoded.Subrecords);
        Assert.Contains(encoded.Warnings, w => w.Contains("engine-hardcoded"));
    }

    // ====================================================================================
    // MuscEncoder
    // ====================================================================================

    [Fact]
    public void MuscEncoder_EncodeNew_FnamAndAnam()
    {
        var musc = new MusicTypeRecord
        {
            FormId = 0x1500,
            EditorId = "MusCombat",
            FileName = "music/special/dnbattle.mp3",
            Attenuation = -12.0f
        };

        var encoded = MuscEncoder.EncodeNew(musc);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FNAM", "ANAM"], sigs);
        var anam = Assert.Single(encoded.Subrecords, s => s.Signature == "ANAM").Bytes;
        Assert.Equal(-12.0f, BinaryPrimitives.ReadSingleLittleEndian(anam));
    }

    // ====================================================================================
    // MesgEncoder
    // ====================================================================================

    [Fact]
    public void MesgEncoder_EncodeNew_AllFieldsCanonicalOrder()
    {
        var mesg = new MessageRecord
        {
            FormId = 0x1600,
            EditorId = "MsgPickLock",
            FullName = "Pick Lock?",
            Description = "Attempt to pick this lock?",
            Icon = "icons/lockpick.dds",
            QuestFormId = 0xABCD,
            Flags = 0x03, // MessageBox + AutoDisplay
            DisplayTime = 5,
            Buttons = ["Yes", "No"]
        };

        var encoded = MesgEncoder.EncodeNew(mesg);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FULL", "DESC", "ICON", "QNAM", "DNAM", "TNAM", "ITXT", "ITXT"], sigs);
    }

    [Fact]
    public void MesgEncoder_EncodeNew_OmitsQnamWhenZero()
    {
        var mesg = new MessageRecord { FormId = 0x1600, EditorId = "M", QuestFormId = 0 };
        var encoded = MesgEncoder.EncodeNew(mesg);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "QNAM");
    }

    // ====================================================================================
    // NoteEncoder
    // ====================================================================================

    [Fact]
    public void NoteEncoder_EncodeNew_TextNoteEmitsTnamAsString()
    {
        var note = new NoteRecord
        {
            FormId = 0x1700,
            EditorId = "NoteEx",
            FullName = "Sample Note",
            NoteType = 1, // Text
            Text = "Hello, Wasteland."
        };

        var encoded = NoteEncoder.EncodeNew(note);
        var tnam = Assert.Single(encoded.Subrecords, s => s.Signature == "TNAM").Bytes;
        // Null-terminated Latin-1 string body.
        Assert.Equal(note.Text.Length + 1, tnam.Length);
        Assert.Equal(0, tnam[^1]);
    }

    [Fact]
    public void NoteEncoder_EncodeNew_VoiceNoteEmitsTnamAsFormId()
    {
        var note = new NoteRecord
        {
            FormId = 0x1700,
            EditorId = "V",
            NoteType = 3, // Voice
            TopicFormId = 0xDEADBEEF
        };

        var encoded = NoteEncoder.EncodeNew(note);
        var tnam = Assert.Single(encoded.Subrecords, s => s.Signature == "TNAM").Bytes;
        Assert.Equal(4, tnam.Length);
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(tnam));
    }

    [Fact]
    public void NoteEncoder_EncodeNew_CanonicalOrder()
    {
        var note = new NoteRecord
        {
            FormId = 0x1700,
            EditorId = "F",
            FullName = "Full Note",
            ModelPath = "note.nif",
            IconPath = "icons/note.dds",
            TexturePath = "icons/note_mico.dds",
            NoteType = 1,
            Text = "Body",
            SoundFormId = 0x300,
            ObjectFormId = 0x400
        };

        var encoded = NoteEncoder.EncodeNew(note);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FULL", "MODL", "ICON", "MICO", "DATA", "TNAM", "SNAM", "ONAM"], sigs);
    }

    // ====================================================================================
    // FlstEncoder
    // ====================================================================================

    [Fact]
    public void FlstEncoder_EncodeNew_EmitsEachFormIdAsLnam()
    {
        var flst = new FormListRecord
        {
            FormId = 0x1800,
            EditorId = "MyList",
            FormIds = [0x101u, 0x202u, 0x303u]
        };

        var encoded = FlstEncoder.EncodeNew(flst);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "LNAM", "LNAM", "LNAM"], sigs);

        var lnams = encoded.Subrecords.Where(s => s.Signature == "LNAM").ToList();
        Assert.Equal(0x101u, BinaryPrimitives.ReadUInt32LittleEndian(lnams[0].Bytes));
        Assert.Equal(0x202u, BinaryPrimitives.ReadUInt32LittleEndian(lnams[1].Bytes));
        Assert.Equal(0x303u, BinaryPrimitives.ReadUInt32LittleEndian(lnams[2].Bytes));
    }

    [Fact]
    public void FlstEncoder_EncodeNew_EmptyListEmitsOnlyEdid()
    {
        var flst = new FormListRecord { FormId = 0x1800, EditorId = "E" };
        var encoded = FlstEncoder.EncodeNew(flst);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID"], sigs);
    }

    // ====================================================================================
    // LvliEncoder (handles LVLI/LVLN/LVLC)
    // ====================================================================================

    [Fact]
    public void LvliEncoder_EncodeNew_LvloLayout()
    {
        var lvli = new LeveledListRecord
        {
            FormId = 0x1900,
            EditorId = "LL1",
            ListType = "LVLI",
            ChanceNone = 25,
            Flags = 0x01,
            GlobalFormId = 0xAA,
            Entries =
            [
                new LeveledEntry(Level: 10, FormId: 0x111, Count: 1),
                new LeveledEntry(Level: 20, FormId: 0x222, Count: 3)
            ]
        };

        var encoded = LvliEncoder.EncodeNew(lvli);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "LVLD", "LVLF", "LVLG", "LVLO", "LVLO"], sigs);

        var lvlos = encoded.Subrecords.Where(s => s.Signature == "LVLO").ToList();
        Assert.Equal(12, lvlos[0].Bytes.Length);
        Assert.Equal(10, BinaryPrimitives.ReadUInt16LittleEndian(lvlos[0].Bytes.AsSpan(0, 2)));
        Assert.Equal(0x111u, BinaryPrimitives.ReadUInt32LittleEndian(lvlos[0].Bytes.AsSpan(4, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(lvlos[0].Bytes.AsSpan(8, 2)));
    }

    [Fact]
    public void LvliEncoder_EncodeNew_OmitsOptionalsWhenDefault()
    {
        var lvli = new LeveledListRecord
        {
            FormId = 0x1900,
            EditorId = "E",
            ChanceNone = 0,
            Flags = 0,
            GlobalFormId = null,
            Entries = []
        };

        var encoded = LvliEncoder.EncodeNew(lvli);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID"], sigs);
    }

    [Fact]
    public void LvliEncoder_EncodeNew_HandlesAllThreeListTypes()
    {
        foreach (var listType in new[] { "LVLI", "LVLN", "LVLC" })
        {
            var lvl = new LeveledListRecord
            {
                FormId = 0x1900,
                EditorId = "T",
                ListType = listType,
                Entries = [new LeveledEntry(Level: 1, FormId: 0x1, Count: 1)]
            };

            var encoded = LvliEncoder.EncodeNew(lvl);
            Assert.Contains(encoded.Subrecords, s => s.Signature == "EDID");
            Assert.Contains(encoded.Subrecords, s => s.Signature == "LVLO");
        }
    }

    // ====================================================================================
    // Cross-encoder warning check (trivial + small batch)
    // ====================================================================================

    [Fact]
    public void AllEncoders_EmitWarningWhenEditorIdMissing()
    {
        Assert.Contains(EyesEncoder.EncodeNew(new EyesRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(HairEncoder.EncodeNew(new HairRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(RepuEncoder.EncodeNew(new ReputationRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        // AVIF excluded — post-v20, new AVIF emission is always skipped (regardless of
        // EditorId presence) because the actor-value table is engine-hardcoded.
        Assert.Contains(MuscEncoder.EncodeNew(new MusicTypeRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(MesgEncoder.EncodeNew(new MessageRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(NoteEncoder.EncodeNew(new NoteRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(FlstEncoder.EncodeNew(new FormListRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(LvliEncoder.EncodeNew(new LeveledListRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
    }
}
