using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class ArmorRecordScannerTests
{
    [Fact]
    public void Process_ReadsBipedModelListFormId()
    {
        var bmdt = new byte[8];
        bmdt[0] = 0x04;

        var recordBytes = EsmTestRecordBuilder.BuildRecordBytes(
            0x00012345,
            "ARMO",
            false,
            ("EDID", EsmTestRecordBuilder.NullTermString("ArmorBoomerWrist")),
            ("BMDT", bmdt),
            ("BIPL", BitConverter.GetBytes(0x00054321u)),
            ("MODL", EsmTestRecordBuilder.NullTermString(@"armor\boomeroutfit.nif")));

        var record = new AnalyzerRecordInfo
        {
            Signature = "ARMO",
            FormId = 0x00012345,
            Flags = 0,
            DataSize = (uint)(recordBytes.Length - 24),
            Offset = 0,
            TotalSize = (uint)recordBytes.Length
        };

        var scanEntry = ArmorRecordScanner.Process(recordBytes, false, record);

        Assert.NotNull(scanEntry);
        Assert.Equal(0x04u, scanEntry!.BipedFlags);
        Assert.Equal(0x00054321u, scanEntry.BipedModelListFormId);
    }
}