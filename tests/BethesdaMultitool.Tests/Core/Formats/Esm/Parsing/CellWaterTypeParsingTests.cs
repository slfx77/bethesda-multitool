using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class CellWaterTypeParsingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cell_RetainsXcwtWaterFormId(bool bigEndian)
    {
        const uint cellFormId = 0x01001000;
        const uint waterFormId = 0x01002000;
        var bytes = BuildRecordBytes(
            cellFormId,
            "CELL",
            bigEndian,
            ("DATA", new byte[] { 0x02 }),
            ("XCWT", FormIdBytes(waterFormId, bigEndian)));
        var record = new DetectedMainRecord(
            "CELL", (uint)(bytes.Length - 24), 0, cellFormId, 0, bigEndian);
        var context = new RecordParserContext(
            new EsmRecordScanResult
            {
                Game = BethesdaGame.FalloutNewVegas,
                MainRecords = [record]
            },
            null,
            new ByteArrayMemoryAccessor(bytes),
            bytes.Length,
            null);

        var cell = Assert.Single(new CellRecordHandler(context).ParseCells());

        Assert.True(cell.HasWater);
        Assert.Equal(waterFormId, cell.WaterFormId);
        Assert.Equal(bigEndian, cell.IsBigEndian);
    }

    private static byte[] FormIdBytes(uint value, bool bigEndian)
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
}