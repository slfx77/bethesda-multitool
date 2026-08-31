using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class StarfieldClimateParsingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseAll_RetainsWsltDomainAndFiveByteTiming(bool bigEndian)
    {
        const uint climateFormId = 0x0024E982;
        var recordBytes = BuildRecordBytes(
            climateFormId,
            "CLMT",
            bigEndian,
            ("EDID", NullTermString("ClimateUniqueNewAtlantis")),
            ("WLST", ChoiceBytes([(0x0027CF9Bu, 100, 0u)], bigEndian)),
            ("WSLT", ChoiceBytes([
                (0x0002B544u, 75, 0u),
                (0x0002B545u, 25, 0x00000ABCu)
            ], bigEndian)),
            ("TNAM", [30, 54, 102, 126, 0]));

        var mainRecord = new DetectedMainRecord(
            "CLMT", (uint)(recordBytes.Length - 24), 0, climateFormId, 0, bigEndian);
        var scanResult = MakeScanResult([mainRecord]);
        scanResult.Game = BethesdaGame.Starfield;

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var climate = Assert.Single(
            new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length)
                .ParseAll()
                .Climate);

        var legacy = Assert.Single(climate.WeatherTypes);
        Assert.Equal(0x0027CF9Bu, legacy.WeatherFormId);
        Assert.Equal(100, legacy.Chance);

        Assert.Collection(
            climate.WeatherSettingsTypes,
            first =>
            {
                Assert.Equal(0x0002B544u, first.WeatherSettingsFormId);
                Assert.Equal(75, first.Chance);
                Assert.Equal(0u, first.GlobalFormId);
            },
            second =>
            {
                Assert.Equal(0x0002B545u, second.WeatherSettingsFormId);
                Assert.Equal(25, second.Chance);
                Assert.Equal(0x00000ABCu, second.GlobalFormId);
            });

        var timing = Assert.IsType<ClimateTimingData>(climate.Timing);
        Assert.Equal((byte)30, timing.SunriseBegin);
        Assert.Equal((byte)54, timing.SunriseEnd);
        Assert.Equal((byte)102, timing.SunsetBegin);
        Assert.Equal((byte)126, timing.SunsetEnd);
        Assert.Equal((byte)0, timing.Volatility);
        Assert.Equal((byte)0, timing.MoonPhaseLength);
        Assert.False(timing.HasMoonPhaseLength);
    }

    private static byte[] ChoiceBytes(
        IReadOnlyList<(uint FormId, int Chance, uint GlobalFormId)> entries,
        bool bigEndian)
    {
        var bytes = new byte[entries.Count * 12];
        for (var i = 0; i < entries.Count; i++)
        {
            var offset = i * 12;
            var entry = entries[i];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), entry.FormId);
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset + 4, 4), entry.Chance);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 8, 4), entry.GlobalFormId);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), entry.FormId);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4, 4), entry.Chance);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8, 4), entry.GlobalFormId);
            }
        }

        return bytes;
    }
}
