using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class ArmorRecordScannerTests
{
    [Fact]
    public void Process_OblivionBmdt_SeparatesUInt16SlotsFromGeneralFlags()
    {
        // Mazoga's iron shield uses slot 0x2000 and Heavy (0x80). Interpreting TES4 BMDT
        // as Fallout's uint32 mask turns these bytes into the bogus slot value 0x00802000.
        byte[] bmdt = [0x00, 0x20, 0x80, 0x00];
        var (recordBytes, record) = EsmTestRecordBuilder.BuildAnalyzerRecord(
            0x00012344,
            "ARMO",
            false,
            ("EDID", EsmTestRecordBuilder.NullTermString("ArmorIronShield")),
            ("BMDT", bmdt),
            ("MODL", EsmTestRecordBuilder.NullTermString(@"Armor\Iron\Shield.NIF")));

        var scanEntry = ArmorRecordScanner.Process(
            recordBytes,
            false,
            record,
            BethesdaGame.Oblivion);

        Assert.NotNull(scanEntry);
        Assert.Equal(0x2000u, scanEntry.BipedFlags);
        Assert.Equal((byte)0x80, scanEntry.GeneralFlags);
    }

    [Fact]
    public void Process_ReadsBipedModelListFormId()
    {
        var bmdt = new byte[8];
        bmdt[0] = 0x04;

        var (recordBytes, record) = EsmTestRecordBuilder.BuildAnalyzerRecord(
            0x00012345,
            "ARMO",
            false,
            ("EDID", EsmTestRecordBuilder.NullTermString("ArmorBoomerWrist")),
            ("BMDT", bmdt),
            ("BIPL", BitConverter.GetBytes(0x00054321u)),
            ("MODL", EsmTestRecordBuilder.NullTermString(@"armor\boomeroutfit.nif")));

        var scanEntry = ArmorRecordScanner.Process(recordBytes, false, record);

        Assert.NotNull(scanEntry);
        Assert.Equal(0x04u, scanEntry.BipedFlags);
        Assert.Equal(0x00054321u, scanEntry.BipedModelListFormId);
    }

    [Fact]
    public void Process_OblivionArmor_ReadsBaseRatingAndRecordKind()
    {
        byte[] bmdt = [0x04, 0x00, 0x00, 0x00];
        byte[] data =
        [
            0x65, 0x04, 0xC8, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ];
        var (recordBytes, record) = EsmTestRecordBuilder.BuildAnalyzerRecord(
            0x0018AE4C,
            "ARMO",
            false,
            ("EDID", EsmTestRecordBuilder.NullTermString("ImperialPalaceCuirass")),
            ("BMDT", bmdt),
            ("MODL", EsmTestRecordBuilder.NullTermString(@"Armor\ImperialWatch\M\Cuirass.NIF")),
            ("DATA", data));

        var scanEntry = ArmorRecordScanner.Process(
            recordBytes,
            false,
            record,
            BethesdaGame.Oblivion);

        Assert.NotNull(scanEntry);
        Assert.False(scanEntry.IsClothing);
        Assert.Equal((ushort)1125, scanEntry.BaseArmorRating);
        Assert.Equal(200u, scanEntry.BaseValue);
    }

    [Fact]
    public void Process_OblivionClothing_PreservesKindWithoutTreatingDataAsArmorRating()
    {
        byte[] bmdt = [0x04, 0x00, 0x00, 0x00];
        byte[] data = [0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x3F];
        var (recordBytes, record) = EsmTestRecordBuilder.BuildAnalyzerRecord(
            0x000229AD,
            "CLOT",
            false,
            ("EDID", EsmTestRecordBuilder.NullTermString("MiddleShirt01")),
            ("BMDT", bmdt),
            ("MODL", EsmTestRecordBuilder.NullTermString(@"Clothes\MiddleClass\01\M\Shirt.NIF")),
            ("DATA", data));

        var scanEntry = ArmorRecordScanner.Process(
            recordBytes,
            false,
            record,
            BethesdaGame.Oblivion);

        Assert.NotNull(scanEntry);
        Assert.True(scanEntry.IsClothing);
        Assert.Equal((ushort)0, scanEntry.BaseArmorRating);
        Assert.Equal(2u, scanEntry.BaseValue);
    }

    [Fact]
    public void Process_OblivionBigEndianArmor_ReadsBaseRating()
    {
        byte[] bmdt = [0x00, 0x04, 0x00, 0x00];
        byte[] data =
        [
            0x04, 0x65, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ];
        var (recordBytes, record) = EsmTestRecordBuilder.BuildAnalyzerRecord(
            0x0018AE4C,
            "ARMO",
            true,
            ("BMDT", bmdt),
            ("MODL", EsmTestRecordBuilder.NullTermString(@"Armor\ImperialWatch\M\Cuirass.NIF")),
            ("DATA", data));

        var scanEntry = ArmorRecordScanner.Process(
            recordBytes,
            true,
            record,
            BethesdaGame.Oblivion);

        Assert.NotNull(scanEntry);
        Assert.Equal(0x04u, scanEntry.BipedFlags);
        Assert.Equal((ushort)1125, scanEntry.BaseArmorRating);
    }
}