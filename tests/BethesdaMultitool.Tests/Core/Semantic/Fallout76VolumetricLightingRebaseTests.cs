using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Semantic;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Semantic;

public sealed class Fallout76VolumetricLightingRebaseTests
{
    [Fact]
    public void Rebase_MapsClassicVoliRecordAndWeatherReferencesWithoutMappingZero()
    {
        var sourceBands = new WeatherTimeBands<uint>(0x100, 0x101, 0x102, 0)
        {
            EarlySunrise = 0x104,
            LateSunrise = 0,
            EarlySunset = 0x106,
            LateSunset = 0x107
        };
        var source = new RecordCollection
        {
            Fallout76VolumetricLightingSettings =
            [
                new Fallout76VolumetricLightingRecord
                {
                    FormId = 0x100,
                    EditorId = "VOLI_Source",
                    Settings = new Fallout76VolumetricLightingSettings
                    {
                        Intensity = 12f,
                        SamplingRepartitionRangeFactor = 50f
                    }
                }
            ],
            Weather =
            [
                new WeatherRecord
                {
                    FormId = 0x200,
                    EditorId = "WTHR_Source",
                    VolumetricLightingFormIds = sourceBands
                }
            ]
        };

        var mappedInputs = new List<uint>();
        var rebased = RecordCollectionFormIdRebaser.Rebase(source, value =>
        {
            Assert.NotEqual(0u, value);
            mappedInputs.Add(value);
            return value + 0x0100_0000;
        });

        var volumetric = Assert.Single(rebased.Fallout76VolumetricLightingSettings);
        Assert.Equal(0x0100_0100u, volumetric.FormId);
        Assert.Equal("VOLI_Source", volumetric.EditorId);
        Assert.Equal(50f, volumetric.Settings!.SamplingRepartitionRangeFactor);

        var bands = Assert.IsType<WeatherTimeBands<uint>>(Assert.Single(rebased.Weather)
            .VolumetricLightingFormIds);
        Assert.NotSame(sourceBands, bands);
        Assert.Equal(0x0100_0100u, bands.Sunrise);
        Assert.Equal(0x0100_0101u, bands.Day);
        Assert.Equal(0x0100_0102u, bands.Sunset);
        Assert.Equal(0u, bands.Night);
        Assert.Equal(0x0100_0104u, bands.EarlySunrise);
        Assert.Equal(0u, bands.LateSunrise);
        Assert.Equal(0x0100_0106u, bands.EarlySunset);
        Assert.Equal(0x0100_0107u, bands.LateSunset);

        Assert.Same(sourceBands, source.Weather[0].VolumetricLightingFormIds);
        Assert.Equal(0x100u, sourceBands.Sunrise);
        Assert.Contains(0x100u, mappedInputs);
        Assert.DoesNotContain(0u, mappedInputs);
        Assert.True(EsmFormIdPropertyRegistry.IsFormIdProperty("VolumetricLightingFormIds"));
    }
}
