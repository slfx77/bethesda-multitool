using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Core.Formats.Esm;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Retail-data authority for the FNV ONAM/DATA inputs that exercise cloud-transition motion.
///     The synthetic midpoint test pins the recovered polynomial; this fixture proves that shipped
///     weather pairs contain the correlated speed/wind values for which blend order is observable.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class FnvCloudMotionRetailTests(SampleFileFixture samples)
{
    private const uint WastelandClearFormId = 0x000FFC88;
    private const uint HooverFinalBattleFormId = 0x0017407F;

    [Fact]
    public void PcFinalWeatherCensus_HooverTransitionUsesProductOfBlendedOnamAndWind()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final FalloutNV.esm not available");

        var fileData = File.ReadAllBytes(samples.PcFinalEsm!);
        var weathers = PcFinalEsmPipelineCache.GetOrBuild(samples.PcFinalEsm!).ParsedRecords
            .Where(record => record.Header.Signature == "WTHR")
            .GroupBy(record => record.Header.FormId)
            .ToDictionary(group => group.Key, group => group.Last());

        Assert.Equal(63, weathers.Count);
        Assert.All(weathers.Values, weather =>
            Assert.Equal(4, ReadSubrecord(fileData, weather, "ONAM").Length));

        var outgoingRaw = weathers[WastelandClearFormId];
        var currentRaw = weathers[HooverFinalBattleFormId];
        var outgoing = BuildWeather(fileData, outgoingRaw);
        var current = BuildWeather(fileData, currentRaw);
        Assert.Equal("NVWastelandClear", outgoing.EditorId);
        Assert.Equal("NVHooverFinalBattle", current.EditorId);
        Assert.Equal(52f / 255f, outgoing.CloudSpeedsX[0], 7);
        Assert.Equal(72f / 255f, current.CloudSpeedsX[0], 7);
        Assert.NotNull(outgoing.Data);
        Assert.NotNull(current.Data);
        Assert.Equal((byte)50, outgoing.Data.WindSpeed);
        Assert.Equal((byte)130, current.Data.WindSpeed);
        Assert.Equal(@"sky\alpha.dds", outgoing.FindCloudLayerBySourceIndex(0)?.Texture,
            ignoreCase: true);
        Assert.Equal(@"sky\wastelandcloudcloudyupper01.dds",
            current.FindCloudLayerBySourceIndex(0)?.Texture,
            ignoreCase: true);

        var transition = WeatherCloudTransitionResolver.Resolve(
            current,
            outgoing,
            sourceLayerIndex: 0,
            currentWeatherWeight: 0.5f,
            game: BethesdaGame.FalloutNewVegas);

        var expectedEngine = 0.1f
                             * (((52f / 255f) + (72f / 255f)) * 0.5f)
                             * (((50f / 255f) + (130f / 255f)) * 0.5f);
        var oldBlendOfProducts = 0.5f
                                 * ((0.1f * (52f / 255f) * (50f / 255f))
                                    + (0.1f * (72f / 255f) * (130f / 255f)));

        Assert.Equal(0.008581315f, expectedEngine, 7);
        Assert.Equal(expectedEngine, transition.ScrollVelocity.X, 7);
        Assert.Equal(0f, transition.ScrollVelocity.Y);
        Assert.True(oldBlendOfProducts > transition.ScrollVelocity.X);
        Assert.InRange(
            (oldBlendOfProducts / transition.ScrollVelocity.X) - 1f,
            0.071f,
            0.072f);
    }

    private static WeatherRecord BuildWeather(byte[] fileData, ParsedMainRecord record)
    {
        var onam = ReadSubrecord(fileData, record, "ONAM");
        var data = ReadSubrecord(fileData, record, "DATA");
        var texture = Encoding.ASCII.GetString(ReadSubrecord(fileData, record, "DNAM")).TrimEnd('\0');
        var speeds = MiscEnvironmentHandler.ReadCloudSpeeds(
            onam,
            isBigEndian: false,
            game: BethesdaGame.FalloutNewVegas);

        return new WeatherRecord
        {
            FormId = record.Header.FormId,
            EditorId = record.EditorId,
            Data = new WeatherData { WindSpeed = data[0] },
            CloudSpeedsX = speeds,
            CloudLayers =
            [
                new WeatherCloudLayer
                {
                    SourceIndex = 0,
                    Texture = texture,
                    SpeedU = speeds[0],
                },
            ],
        };
    }

    private static byte[] ReadSubrecord(byte[] fileData, ParsedMainRecord record, string wantedSignature)
    {
        Assert.False(record.Header.IsCompressed);
        var offset = checked((int)record.Offset + EsmParser.MainRecordHeaderSize);
        var end = checked(offset + (int)record.Header.DataSize);
        int? extendedSize = null;

        while (offset + EsmParser.SubrecordHeaderSize <= end)
        {
            var signature = Encoding.ASCII.GetString(fileData, offset, 4);
            var storedSize = BinaryPrimitives.ReadUInt16LittleEndian(
                fileData.AsSpan(offset + 4, sizeof(ushort)));
            offset += EsmParser.SubrecordHeaderSize;

            if (signature == "XXXX" && storedSize == sizeof(uint))
            {
                extendedSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                    fileData.AsSpan(offset, sizeof(uint))));
                offset += sizeof(uint);
                continue;
            }

            var size = extendedSize ?? storedSize;
            extendedSize = null;
            Assert.True(offset + size <= end, $"{record.EditorId} {signature} overruns WTHR data.");
            if (signature == wantedSignature)
            {
                return fileData.AsSpan(offset, size).ToArray();
            }

            offset += size;
        }

        Assert.Fail($"{record.EditorId} has no {wantedSignature} subrecord.");
        return [];
    }
}
