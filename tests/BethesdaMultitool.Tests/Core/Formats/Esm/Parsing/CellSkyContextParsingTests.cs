using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class CellSkyContextParsingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClassicCell_RetainsXccmClimateAndBehavesLikeExteriorFlag(bool bigEndian)
    {
        const uint cellFormId = 0x01001000;
        const uint climateFormId = 0x01002000;
        var bytes = BuildRecordBytes(
            cellFormId,
            "CELL",
            bigEndian,
            ("DATA", new byte[] { 0x81 }),
            ("XCCM", FormIdBytes(climateFormId, bigEndian)));
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

        Assert.True(cell.IsInterior);
        Assert.Equal(CellDataFlagSemantics.ClassicBit7, cell.DataFlagSemantics);
        Assert.True(cell.BehavesLikeExterior);
        Assert.False(cell.ShowsSky);
        Assert.False(cell.UsesSkyLighting);
        Assert.Equal(climateFormId, cell.ClimateFormId);
        Assert.Equal(bigEndian, cell.IsBigEndian);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fallout3Cell_UsesBitSixForBehaveLikeExterior(bool bigEndian)
    {
        const uint cellFormId = 0x01001000;
        var bytes = BuildRecordBytes(
            cellFormId,
            "CELL",
            bigEndian,
            ("DATA", new byte[] { 0x41 }));
        var record = new DetectedMainRecord(
            "CELL", (uint)(bytes.Length - 24), 0, cellFormId, 0, bigEndian);
        var context = new RecordParserContext(
            new EsmRecordScanResult
            {
                Game = BethesdaGame.Fallout3,
                MainRecords = [record]
            },
            null,
            new ByteArrayMemoryAccessor(bytes),
            bytes.Length,
            null);

        var cell = Assert.Single(new CellRecordHandler(context).ParseCells());

        Assert.Equal(CellDataFlagSemantics.Fallout3, cell.DataFlagSemantics);
        Assert.True(cell.IsInterior);
        Assert.True(cell.BehavesLikeExterior);
        Assert.False(cell.ShowsSky);
        Assert.False(cell.UsesSkyLighting);
        Assert.False((cell with { Flags = 0x81 }).BehavesLikeExterior);
    }

    [Theory]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    public void CreationCell_DoesNotMisclassifyXccmRegionAsClimate(BethesdaGame game)
    {
        const uint cellFormId = 0x01001000;
        var bytes = BuildRecordBytes(
            cellFormId,
            "CELL",
            false,
            ("DATA", FlagBytes(0x0081, false)),
            ("XCCM", FormIdBytes(0x01002000, false)));
        var record = new DetectedMainRecord(
            "CELL", (uint)(bytes.Length - 24), 0, cellFormId, 0, false);
        var context = new RecordParserContext(
            new EsmRecordScanResult { Game = game, MainRecords = [record] },
            null,
            new ByteArrayMemoryAccessor(bytes),
            bytes.Length,
            null);

        var cell = Assert.Single(new CellRecordHandler(context).ParseCells());

        Assert.Equal(CellDataFlagSemantics.Creation, cell.DataFlagSemantics);
        Assert.False(cell.BehavesLikeExterior);
        Assert.True(cell.ShowsSky);
        Assert.Null(cell.ClimateFormId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreationCell_PreservesTwoByteShowSkyAndUseSkyLightingFlags(bool bigEndian)
    {
        const uint cellFormId = 0x01001000;
        const ushort flags = 0x0181;
        var bytes = BuildRecordBytes(
            cellFormId,
            "CELL",
            bigEndian,
            ("DATA", FlagBytes(flags, bigEndian)));
        var record = new DetectedMainRecord(
            "CELL", (uint)(bytes.Length - 24), 0, cellFormId, 0, bigEndian);
        var context = new RecordParserContext(
            new EsmRecordScanResult
            {
                Game = BethesdaGame.Skyrim,
                MainRecords = [record]
            },
            null,
            new ByteArrayMemoryAccessor(bytes),
            bytes.Length,
            null);

        var cell = Assert.Single(new CellRecordHandler(context).ParseCells());

        Assert.Equal(flags, cell.Flags);
        Assert.Equal(CellDataFlagSemantics.Creation, cell.DataFlagSemantics);
        Assert.True(cell.IsInterior);
        Assert.False(cell.BehavesLikeExterior);
        Assert.True(cell.ShowsSky);
        Assert.True(cell.UsesSkyLighting);
    }

    [Fact]
    public void DragonsreachRetailFlags_ShowSkyWithoutUsingSkyLighting()
    {
        // Exact decompressed CELL 0x000165A3 DATA payload from retail Skyrim LE:
        // A1 00 = interior + public + Show Sky. Bit 8 (Use Sky Lighting) is clear.
        const uint cellFormId = 0x000165A3;
        var bytes = BuildRecordBytes(
            cellFormId,
            "CELL",
            false,
            ("EDID", "WhiterunDragonsreach\0"u8.ToArray()),
            ("DATA", FlagBytes(0x00A1, false)));
        var record = new DetectedMainRecord(
            "CELL", (uint)(bytes.Length - 24), 0, cellFormId, 0, false);
        var context = new RecordParserContext(
            new EsmRecordScanResult
            {
                Game = BethesdaGame.Skyrim,
                MainRecords = [record]
            },
            null,
            new ByteArrayMemoryAccessor(bytes),
            bytes.Length,
            null);

        var cell = Assert.Single(new CellRecordHandler(context).ParseCells());

        Assert.Equal(0xA1u, cell.Flags);
        Assert.True(cell.ShowsSky);
        Assert.False(cell.UsesSkyLighting);
        Assert.False(cell.BehavesLikeExterior);
    }

    private static byte[] FlagBytes(ushort value, bool bigEndian)
    {
        var bytes = new byte[2];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        }

        return bytes;
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