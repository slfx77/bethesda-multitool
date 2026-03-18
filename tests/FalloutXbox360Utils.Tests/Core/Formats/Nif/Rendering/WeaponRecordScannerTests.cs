using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class WeaponRecordScannerTests
{
    [Theory]
    [InlineData((byte)4, WeaponType.OneHandPistolEnergy)]
    [InlineData((byte)7, WeaponType.TwoHandRifleEnergy)]
    [InlineData((byte)13, WeaponType.OneHandThrown)]
    public void Process_PreservesExtendedWeaponAnimationTypes(byte rawWeaponType, WeaponType expectedType)
    {
        var dnam = new byte[204];
        dnam[0] = rawWeaponType;

        var recordBytes = EsmTestRecordBuilder.BuildRecordBytes(
            0x00001234,
            "WEAP",
            false,
            ("EDID", EsmTestRecordBuilder.NullTermString("TestWeapon")),
            ("MODL", EsmTestRecordBuilder.NullTermString(@"weapons\test.nif")),
            ("DNAM", dnam));

        var record = new AnalyzerRecordInfo
        {
            Signature = "WEAP",
            FormId = 0x00001234,
            Flags = 0,
            DataSize = (uint)(recordBytes.Length - 24),
            Offset = 0,
            TotalSize = (uint)recordBytes.Length
        };

        var scanEntry = WeaponRecordScanner.Process(recordBytes, false, record);

        Assert.NotNull(scanEntry);
        Assert.Equal(expectedType, scanEntry!.WeaponType);
        Assert.Equal(@"weapons\test.nif", scanEntry.ModelPath);
    }

    [Fact]
    public void Process_ReadsEmbeddedWeaponNodeMetadata()
    {
        var dnam = new byte[204];
        dnam[12] = 0x20;

        var recordBytes = EsmTestRecordBuilder.BuildRecordBytes(
            0x00001235,
            "WEAP",
            false,
            ("EDID", EsmTestRecordBuilder.NullTermString("EmbeddedWeapon")),
            ("MODL", EsmTestRecordBuilder.NullTermString(@"weapons\embedded.nif")),
            ("NNAM", EsmTestRecordBuilder.NullTermString("Bip01 Spine2")),
            ("DNAM", dnam));

        var record = new AnalyzerRecordInfo
        {
            Signature = "WEAP",
            FormId = 0x00001235,
            Flags = 0,
            DataSize = (uint)(recordBytes.Length - 24),
            Offset = 0,
            TotalSize = (uint)recordBytes.Length
        };

        var scanEntry = WeaponRecordScanner.Process(recordBytes, false, record);

        Assert.NotNull(scanEntry);
        Assert.Equal("Bip01 Spine2", scanEntry!.EmbeddedWeaponNode);
        Assert.Equal(0x20, scanEntry.Flags);
    }

    [Fact]
    public void Process_PreservesMod2PathAndHandGripAnim()
    {
        var dnam = new byte[204];
        dnam[13] = 0x7B;

        var recordBytes = EsmTestRecordBuilder.BuildRecordBytes(
            0x00001236,
            "WEAP",
            false,
            ("EDID", EsmTestRecordBuilder.NullTermString("WorldModelWeapon")),
            ("MODL", EsmTestRecordBuilder.NullTermString(@"weapons\firstperson.nif")),
            ("MOD2", EsmTestRecordBuilder.NullTermString(@"weapons\world.nif")),
            ("DNAM", dnam));

        var record = new AnalyzerRecordInfo
        {
            Signature = "WEAP",
            FormId = 0x00001236,
            Flags = 0,
            DataSize = (uint)(recordBytes.Length - 24),
            Offset = 0,
            TotalSize = (uint)recordBytes.Length
        };

        var scanEntry = WeaponRecordScanner.Process(recordBytes, false, record);

        Assert.NotNull(scanEntry);
        Assert.Equal(@"weapons\firstperson.nif", scanEntry!.ModelPath);
        Assert.Equal(@"weapons\world.nif", scanEntry.Mod2ModelPath);
        Assert.Equal(0x7B, scanEntry.HandGripAnim);
    }
}