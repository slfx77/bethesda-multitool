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

public sealed class StarfieldWaterMaterialParsingTests
{
    [Fact]
    public void StarfieldRecords_RetainWorldNam7AndCellXcwmVerbatim()
    {
        const uint worldspaceFormId = 0x0024E982;
        const uint cellFormId = 0x0024E983;

        var worldspace = ParseWorldspace(
            BethesdaGame.Starfield,
            worldspaceFormId,
            ("NAM7", NullTermString("materials/water/new_atlantis.mat")));
        var cell = ParseCell(
            BethesdaGame.Starfield,
            cellFormId,
            ("XCWM", NullTermString("materials/water/new_atlantis_cell.mat")));

        Assert.Equal("materials/water/new_atlantis.mat", worldspace.StarfieldWaterMaterial);
        Assert.Equal("materials/water/new_atlantis_cell.mat", cell.StarfieldWaterType);
    }

    [Fact]
    public void StarfieldRecords_RetainAuthoredEmptyStringsDistinctFromAbsentSubrecords()
    {
        var worldspace = ParseWorldspace(BethesdaGame.Starfield, 0x0024E982, ("NAM7", []));
        var cell = ParseCell(BethesdaGame.Starfield, 0x0024E983, ("XCWM", []));
        var absentWorldspace = ParseWorldspace(BethesdaGame.Starfield, 0x0024E984);
        var absentCell = ParseCell(BethesdaGame.Starfield, 0x0024E985);

        Assert.Equal(string.Empty, worldspace.StarfieldWaterMaterial);
        Assert.Equal(string.Empty, cell.StarfieldWaterType);
        Assert.Null(absentWorldspace.StarfieldWaterMaterial);
        Assert.Null(absentCell.StarfieldWaterType);
    }

    [Fact]
    public void NonStarfieldRecords_DoNotClassifyNam7OrXcwmAsWaterMaterialStrings()
    {
        var worldspace = ParseWorldspace(
            BethesdaGame.Fallout4,
            0x0024E982,
            ("NAM7", NullTermString("not-starfield")));
        var cell = ParseCell(
            BethesdaGame.Fallout4,
            0x0024E983,
            ("XCWM", NullTermString("not-starfield")));

        Assert.Null(worldspace.StarfieldWaterMaterial);
        Assert.Null(cell.StarfieldWaterType);
    }

    private static WorldspaceRecord ParseWorldspace(
        BethesdaGame game,
        uint formId,
        params (string sig, byte[] data)[] subrecords)
    {
        var bytes = BuildRecordBytes(formId, "WRLD", false, subrecords);
        var record = new DetectedMainRecord("WRLD", (uint)(bytes.Length - 24), 0, formId, 0, false);
        var context = new RecordParserContext(
            new EsmRecordScanResult { Game = game, MainRecords = [record] },
            null,
            new ByteArrayMemoryAccessor(bytes),
            bytes.Length,
            null);

        return Assert.Single(new WorldspaceRecordHandler(context).ParseWorldspaces());
    }

    private static CellRecord ParseCell(
        BethesdaGame game,
        uint formId,
        params (string sig, byte[] data)[] subrecords)
    {
        var bytes = BuildRecordBytes(formId, "CELL", false, subrecords);
        var record = new DetectedMainRecord("CELL", (uint)(bytes.Length - 24), 0, formId, 0, false);
        var context = new RecordParserContext(
            new EsmRecordScanResult { Game = game, MainRecords = [record] },
            null,
            new ByteArrayMemoryAccessor(bytes),
            bytes.Length,
            null);

        return Assert.Single(new CellRecordHandler(context).ParseCells());
    }
}
