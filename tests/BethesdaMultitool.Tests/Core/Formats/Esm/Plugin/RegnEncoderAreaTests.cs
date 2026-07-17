using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class RegnEncoderAreaTests
{
    [Fact]
    public void EncodeNew_EmitsTypedAreasBeforeDataBlocks()
    {
        var region = new RegionRecord
        {
            FormId = 0x01001000,
            EditorId = "TestRegion",
            WorldspaceFormId = 0x01002000,
            Areas =
            [
                new RegionArea(
                    1024,
                    [
                        new RegionPoint(10f, 20f),
                        new RegionPoint(30f, 20f),
                        new RegionPoint(30f, 40f),
                    ]),
            ],
            DataBlocks = [new RegionDataBlock(3, 0x00006401, [])],
        };

        var encoded = RegnEncoder.EncodeNew(region);
        var signatures = encoded.Subrecords.Select(subrecord => subrecord.Signature).ToArray();

        Assert.Equal(["EDID", "RCLR", "WNAM", "RPLI", "RPLD", "RDAT"], signatures);
        var rpli = Assert.Single(encoded.Subrecords, subrecord => subrecord.Signature == "RPLI");
        Assert.Equal(1024u, BinaryPrimitives.ReadUInt32LittleEndian(rpli.Bytes));
        var rpld = Assert.Single(encoded.Subrecords, subrecord => subrecord.Signature == "RPLD");
        Assert.Equal(24, rpld.Bytes.Length);
        Assert.Equal(10f, BinaryPrimitives.ReadSingleLittleEndian(rpld.Bytes.AsSpan(0, 4)));
        Assert.Equal(20f, BinaryPrimitives.ReadSingleLittleEndian(rpld.Bytes.AsSpan(4, 4)));
        Assert.Equal(30f, BinaryPrimitives.ReadSingleLittleEndian(rpld.Bytes.AsSpan(16, 4)));
        Assert.Equal(40f, BinaryPrimitives.ReadSingleLittleEndian(rpld.Bytes.AsSpan(20, 4)));
    }
}
