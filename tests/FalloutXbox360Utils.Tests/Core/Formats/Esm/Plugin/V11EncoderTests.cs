using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v11 tests covering the parity-completion + new-record-type encoders:
///     - INFO CIS1/CIS2 emission (mirrors v10 TERM pattern)
///     - WEAP ModelVariants (WNAM/WNM*/MWD*) emission
///     - PROJ new-record encoder (84-byte DATA)
///     - EXPL new-record encoder (52-byte DATA)
///     - IMOD new-record encoder (8-byte DATA)
/// </summary>
public class V11EncoderTests
{
    // ====================================================================================
    // INFO CIS1/CIS2 emission
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
}
