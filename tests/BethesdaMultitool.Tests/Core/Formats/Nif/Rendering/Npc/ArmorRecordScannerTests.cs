using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class ArmorRecordScannerTests
{
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
        Assert.Equal(0x04u, scanEntry!.BipedFlags);
        Assert.Equal(0x00054321u, scanEntry.BipedModelListFormId);
    }
}