using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class RegionAreaParsingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Region_RetainsRpliRpldPolygonAlongsideRdwt(bool bigEndian)
    {
        const uint regionFormId = 0x01001000;
        const uint worldspaceFormId = 0x01002000;
        const uint weatherFormId = 0x01003000;
        var bytes = BuildRecordBytes(
            regionFormId,
            "REGN",
            bigEndian,
            ("EDID", NullTermString("TestWeatherRegion")),
            ("WNAM", UInt32(worldspaceFormId, bigEndian)),
            ("RPLI", UInt32(1024, bigEndian)),
            ("RPLD", Points(bigEndian,
                (10f, 20f), (30f, 20f), (30f, 40f), (10f, 40f))),
            ("RDAT", Concat(UInt32(3, bigEndian), UInt32(0x00006401, bigEndian))),
            ("RDWT", Concat(
                UInt32(weatherFormId, bigEndian),
                UInt32(100, bigEndian),
                UInt32(0, bigEndian))));
        var record = new DetectedMainRecord(
            "REGN", (uint)(bytes.Length - 24), 0, regionFormId, 0, bigEndian);
        var context = new RecordParserContext(
            new EsmRecordScanResult
            {
                Game = BethesdaGame.FalloutNewVegas,
                MainRecords = [record],
            },
            formIdCorrelations: null,
            accessor: new ByteArrayMemoryAccessor(bytes),
            fileSize: bytes.Length,
            minidumpInfo: null);

        var region = Assert.Single(new WorldRecordHandler(context).ParseRegions());

        Assert.Equal(worldspaceFormId, region.WorldspaceFormId);
        var area = Assert.Single(region.Areas);
        Assert.Equal(1024u, area.EdgeFalloff);
        Assert.Equal(
            [
                new RegionPoint(10f, 20f),
                new RegionPoint(30f, 20f),
                new RegionPoint(30f, 40f),
                new RegionPoint(10f, 40f),
            ],
            area.Points);
        Assert.Equal(
            new RegionWeatherType(weatherFormId, 100, 0),
            Assert.Single(region.WeatherTypes));
        Assert.DoesNotContain(
            region.DataBlocks.SelectMany(block => block.Payload),
            payload => payload.Signature is "RPLI" or "RPLD");
    }

    [Fact]
    public void DecodeRegionPoints_TruncatedPayload_IsRejectedAtomically()
    {
        var malformed = new byte[25];

        var points = WorldRecordHandler.DecodeRegionPoints(malformed, isBigEndian: false);

        Assert.Empty(points);
    }

    private static byte[] Points(bool bigEndian, params (float X, float Y)[] points)
    {
        var bytes = new byte[points.Length * 8];
        for (var i = 0; i < points.Length; i++)
        {
            WriteSingle(bytes.AsSpan(i * 8), points[i].X, bigEndian);
            WriteSingle(bytes.AsSpan((i * 8) + 4), points[i].Y, bigEndian);
        }

        return bytes;
    }

    private static byte[] UInt32(uint value, bool bigEndian)
    {
        var bytes = new byte[4];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        }

        return bytes;
    }

    private static void WriteSingle(Span<byte> destination, float value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteSingleBigEndian(destination, value);
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(destination, value);
        }
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
