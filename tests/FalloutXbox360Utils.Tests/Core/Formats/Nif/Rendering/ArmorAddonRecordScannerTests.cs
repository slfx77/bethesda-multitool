using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class ArmorAddonRecordScannerTests
{
    [Fact]
    public void Process_ReadsBipedFlagsFromBmdt_NotData()
    {
        var bmdt = new byte[8];
        bmdt[0] = 0x10;

        var data = new byte[12];
        data[0] = 0x78;
        data[1] = 0x56;
        data[2] = 0x34;
        data[3] = 0x12;

        var recordBytes = EsmTestRecordBuilder.BuildRecordBytes(
            0x00044947,
            "ARMA",
            false,
            ("EDID", EsmTestRecordBuilder.NullTermString("ARMAPowerfist")),
            ("BMDT", bmdt),
            ("MODL", EsmTestRecordBuilder.NullTermString(@"weapons\hand2hand\powerfist.nif")),
            ("DATA", data));

        var record = new AnalyzerRecordInfo
        {
            Signature = "ARMA",
            FormId = 0x00044947,
            Flags = 0,
            DataSize = (uint)(recordBytes.Length - 24),
            Offset = 0,
            TotalSize = (uint)recordBytes.Length
        };

        var scanEntry = ArmorAddonRecordScanner.Process(recordBytes, false, record);

        Assert.NotNull(scanEntry);
        Assert.Equal(0x10u, scanEntry!.BipedFlags);
        Assert.Equal(@"weapons\hand2hand\powerfist.nif", scanEntry.MaleModelPath);
    }
}