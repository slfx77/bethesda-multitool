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

public sealed class WeatherImageSpaceParsingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fallout3FourBandRecord_LeavesOptionalHighNoonAndMidnightAbsent(bool bigEndian)
    {
        var weather = ParseWeather(BethesdaGame.Fallout3, bigEndian,
            (0, 0x0100_0100u),
            (1, 0x0100_0101u),
            (2, 0x0100_0102u),
            (3, 0x0100_0103u));

        var bands = Assert.IsType<WeatherTimeBands<uint>>(weather.ImageSpaceModifiers);
        Assert.Equal(0x0100_0100u, bands.Sunrise);
        Assert.Equal(0x0100_0101u, bands.Day);
        Assert.Equal(0x0100_0102u, bands.Sunset);
        Assert.Equal(0x0100_0103u, bands.Night);
        Assert.Null(bands.HighNoon);
        Assert.Null(bands.Midnight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FalloutNewVegasAuthoredNullOptionalBand_PreservesPresence(bool bigEndian)
    {
        var weather = ParseWeather(BethesdaGame.FalloutNewVegas, bigEndian,
            (0, 0x0100_0100u),
            (1, 0x0100_0101u),
            (2, 0x0100_0102u),
            (3, 0x0100_0103u),
            (4, 0u),
            (5, 0x0100_0105u));

        var bands = Assert.IsType<WeatherTimeBands<uint>>(weather.ImageSpaceModifiers);
        Assert.True(bands.HighNoon.HasValue);
        Assert.Equal(0u, bands.HighNoon.Value);
        Assert.Equal(0x0100_0105u, bands.Midnight);
    }

    private static WeatherRecord ParseWeather(
        BethesdaGame game,
        bool bigEndian,
        params (int Band, uint FormId)[] imageSpaces)
    {
        var subrecords = imageSpaces.Select(entry =>
            (new string([(char)entry.Band, 'I', 'A', 'D']), FormIdBytes(entry.FormId, bigEndian)))
            .ToArray();
        const uint weatherFormId = 0x0100_1000;
        var bytes = BuildRecordBytes(weatherFormId, "WTHR", bigEndian, subrecords);
        var record = new DetectedMainRecord(
            "WTHR", (uint)(bytes.Length - 24), 0, weatherFormId, 0, bigEndian);
        var context = new RecordParserContext(
            new EsmRecordScanResult { Game = game, MainRecords = [record] },
            formIdCorrelations: null,
            accessor: new ByteArrayMemoryAccessor(bytes),
            fileSize: bytes.Length,
            minidumpInfo: null);

        return Assert.Single(new MiscEnvironmentHandler(context).ParseWeather());
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
