using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class WeaponRecordScannerTests
{
    [Theory]
    [InlineData((byte)0, WeaponType.OneHandMelee, "onehandidle.kf")]
    [InlineData((byte)1, WeaponType.TwoHandMelee, "twohandidle.kf")]
    [InlineData((byte)2, WeaponType.OneHandMelee, "onehandidle.kf")]
    [InlineData((byte)3, WeaponType.TwoHandMelee, "twohandidle.kf")]
    [InlineData((byte)4, WeaponType.TwoHandHandle, "staffidle.kf")]
    [InlineData((byte)5, WeaponType.TwoHandRifle, "bowidle.kf")]
    public void Process_OblivionData_MapsNativeWeaponTypeAndRetailPose(
        byte rawWeaponType,
        WeaponType expectedType,
        string expectedPose)
    {
        var data = new byte[30];
        data[0] = rawWeaponType;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 1.25f);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 100);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 17);

        var (recordBytes, record) = EsmTestRecordBuilder.BuildAnalyzerRecord(
            0x00001233,
            "WEAP",
            false,
            ("EDID", EsmTestRecordBuilder.NullTermString("Tes4Weapon")),
            ("MODL", EsmTestRecordBuilder.NullTermString(@"weapons\test.nif")),
            ("DATA", data));

        var scanEntry = WeaponRecordScanner.Process(
            recordBytes,
            false,
            record,
            BethesdaGame.Oblivion);

        Assert.NotNull(scanEntry);
        Assert.Equal(expectedType, scanEntry.WeaponType);
        Assert.Equal(expectedPose, scanEntry.AttachmentPoseKfPath);
        Assert.Equal(100, scanEntry.Health);
        Assert.Equal(17, scanEntry.Damage);
        Assert.Equal(1.25f, scanEntry.ShotsPerSec);
    }

    [Theory]
    [InlineData((byte)4, WeaponType.OneHandPistolEnergy)]
    [InlineData((byte)7, WeaponType.TwoHandRifleEnergy)]
    [InlineData((byte)13, WeaponType.OneHandThrown)]
    public void Process_PreservesExtendedWeaponAnimationTypes(byte rawWeaponType, WeaponType expectedType)
    {
        var dnam = new byte[204];
        dnam[0] = rawWeaponType;

        var (recordBytes, record) = EsmTestRecordBuilder.BuildAnalyzerRecord(
            0x00001234,
            "WEAP",
            false,
            ("EDID", EsmTestRecordBuilder.NullTermString("TestWeapon")),
            ("MODL", EsmTestRecordBuilder.NullTermString(@"weapons\test.nif")),
            ("DNAM", dnam));

        var scanEntry = WeaponRecordScanner.Process(recordBytes, false, record);

        Assert.NotNull(scanEntry);
        Assert.Equal(expectedType, scanEntry.WeaponType);
        Assert.Equal(@"weapons\test.nif", scanEntry.ModelPath);
    }

    [Fact]
    public void Process_ReadsEmbeddedWeaponNodeMetadata()
    {
        var dnam = new byte[204];
        dnam[12] = 0x20;

        var (recordBytes, record) = EsmTestRecordBuilder.BuildAnalyzerRecord(
            0x00001235,
            "WEAP",
            false,
            ("EDID", EsmTestRecordBuilder.NullTermString("EmbeddedWeapon")),
            ("MODL", EsmTestRecordBuilder.NullTermString(@"weapons\embedded.nif")),
            ("NNAM", EsmTestRecordBuilder.NullTermString("Bip01 Spine2")),
            ("DNAM", dnam));

        var scanEntry = WeaponRecordScanner.Process(recordBytes, false, record);

        Assert.NotNull(scanEntry);
        Assert.Equal("Bip01 Spine2", scanEntry.EmbeddedWeaponNode);
        Assert.Equal(0x20, scanEntry.Flags);
    }

    [Fact]
    public void Process_PreservesMod2PathAndHandGripAnim()
    {
        var dnam = new byte[204];
        dnam[13] = 0x7B;

        var (recordBytes, record) = EsmTestRecordBuilder.BuildAnalyzerRecord(
            0x00001236,
            "WEAP",
            false,
            ("EDID", EsmTestRecordBuilder.NullTermString("WorldModelWeapon")),
            ("MODL", EsmTestRecordBuilder.NullTermString(@"weapons\firstperson.nif")),
            ("MOD2", EsmTestRecordBuilder.NullTermString(@"weapons\world.nif")),
            ("DNAM", dnam));

        var scanEntry = WeaponRecordScanner.Process(recordBytes, false, record);

        Assert.NotNull(scanEntry);
        Assert.Equal(@"weapons\firstperson.nif", scanEntry.ModelPath);
        Assert.Equal(@"weapons\world.nif", scanEntry.Mod2ModelPath);
        Assert.Equal(0x7B, scanEntry.HandGripAnim);
    }
}