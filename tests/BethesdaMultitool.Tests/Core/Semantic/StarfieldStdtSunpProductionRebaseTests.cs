using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Semantic;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Semantic;

public sealed class StarfieldStdtSunpProductionRebaseTests
{
    [Fact]
    public void Rebase_MapsOnlyEstablishedFormIdsAndDeepClonesSunPresetPatch()
    {
        var star = new StarfieldStarDataRecord
        {
            FormId = 0x10,
            Routing = new StarfieldStarDataRouting
            {
                SystemId = 0xDEADBEEF,
                BinaryStarFormId = 0x20,
                SunPresetFormId = 0x30,
                TimeOfDayDataFormId = 0x40
            }
        };
        var sunPatch = new StarfieldSunPresetPatch
        {
            ParentFormId = 0x60,
            SunColor = new StarfieldSunPresetFloat4Patch
            {
                X = 0f,
                Y = 1f,
                Z = null,
                W = 1f
            },
            SunIlluminance = 20_000f,
            SunDiskTexture = string.Empty,
            DuskDawnPreset = new StarfieldSunPresetDawnDuskPatch
            {
                DirectionalColor = new StarfieldSunPresetFloat4Patch { X = 0.25f },
                TransitionStartAngle = 0f
            },
            NightPreset = new StarfieldSunPresetNightPatch
            {
                DirectionalIlluminance = 0f,
                GlareColor = new StarfieldSunPresetFloat4Patch { W = 1f }
            }
        };
        var sun = new StarfieldSunPresetRecord
        {
            FormId = 0x50,
            ParentFormId = 0x60,
            PayloadKind = StarfieldSunPresetPayloadKind.Diff,
            Patch = sunPatch
        };
        var mappedValues = new List<uint>();

        var source = new RecordCollection { StarData = [star], SunPresets = [sun] };
        Assert.Equal(2, source.TotalRecordsParsed);

        var rebased = RecordCollectionFormIdRebaser.Rebase(
            source,
            formId =>
            {
                mappedValues.Add(formId);
                return formId + 0x1000;
            });

        Assert.Equal(7, mappedValues.Count);
        Assert.Equal(1, mappedValues.Count(value => value == 0x10));
        Assert.Equal(1, mappedValues.Count(value => value == 0x20));
        Assert.Equal(1, mappedValues.Count(value => value == 0x30));
        Assert.Equal(1, mappedValues.Count(value => value == 0x40));
        Assert.Equal(1, mappedValues.Count(value => value == 0x50));
        Assert.Equal(2, mappedValues.Count(value => value == 0x60));
        Assert.DoesNotContain(0xDEADBEEFu, mappedValues);
        var rebasedStar = Assert.Single(rebased.StarData);
        Assert.Equal(0x1010u, rebasedStar.FormId);
        Assert.Equal(0xDEADBEEFu, rebasedStar.Routing?.SystemId);
        Assert.Equal(0x1020u, rebasedStar.Routing?.BinaryStarFormId);
        Assert.Equal(0x1030u, rebasedStar.Routing?.SunPresetFormId);
        Assert.Equal(0x1040u, rebasedStar.Routing?.TimeOfDayDataFormId);

        var rebasedSun = Assert.Single(rebased.SunPresets);
        Assert.Equal(0x1050u, rebasedSun.FormId);
        Assert.Equal(0x1060u, rebasedSun.ParentFormId);
        Assert.Equal(0x1060u, rebasedSun.Patch?.ParentFormId);
        Assert.NotSame(sunPatch, rebasedSun.Patch);
        Assert.NotSame(sunPatch.SunColor, rebasedSun.Patch?.SunColor);
        Assert.NotSame(sunPatch.DuskDawnPreset, rebasedSun.Patch?.DuskDawnPreset);
        Assert.NotSame(
            sunPatch.DuskDawnPreset?.DirectionalColor,
            rebasedSun.Patch?.DuskDawnPreset?.DirectionalColor);
        Assert.NotSame(sunPatch.NightPreset, rebasedSun.Patch?.NightPreset);
        Assert.NotSame(sunPatch.NightPreset?.GlareColor, rebasedSun.Patch?.NightPreset?.GlareColor);
        Assert.Equal(string.Empty, rebasedSun.Patch?.SunDiskTexture);
        Assert.Equal(0f, rebasedSun.Patch?.DuskDawnPreset?.TransitionStartAngle);
        Assert.Null(rebasedSun.Patch?.SunColor?.Z);

        Assert.Equal(0x10u, star.FormId);
        Assert.Equal(0x60u, sunPatch.ParentFormId);
    }

    [Fact]
    public void Rebase_PreservesOmittedAndAuthoredZeroWithoutInvokingMapper()
    {
        var records = new RecordCollection
        {
            StarData =
            [
                new StarfieldStarDataRecord
                {
                    FormId = 0,
                    Routing = new StarfieldStarDataRouting
                    {
                        SystemId = 0,
                        BinaryStarFormId = 0,
                        SunPresetFormId = null,
                        TimeOfDayDataFormId = 0
                    }
                }
            ],
            SunPresets =
            [
                new StarfieldSunPresetRecord
                {
                    FormId = 0,
                    ParentFormId = null,
                    PayloadKind = StarfieldSunPresetPayloadKind.FullObject,
                    Patch = new StarfieldSunPresetPatch
                    {
                        ParentFormId = 0,
                        SunIlluminance = 0f,
                        SunDiskTexture = string.Empty
                    }
                }
            ]
        };

        var rebased = RecordCollectionFormIdRebaser.Rebase(
            records,
            _ => throw new InvalidOperationException("Null, zero, and scalar values must not be mapped."));

        var star = Assert.Single(rebased.StarData);
        Assert.Equal(0u, star.FormId);
        Assert.Equal(0u, star.Routing?.SystemId);
        Assert.Equal(0u, star.Routing?.BinaryStarFormId);
        Assert.Null(star.Routing?.SunPresetFormId);
        Assert.Equal(0u, star.Routing?.TimeOfDayDataFormId);

        var sun = Assert.Single(rebased.SunPresets);
        Assert.Equal(0u, sun.FormId);
        Assert.Null(sun.ParentFormId);
        Assert.Equal(0u, sun.Patch?.ParentFormId);
        Assert.Equal(0f, sun.Patch?.SunIlluminance);
        Assert.Equal(string.Empty, sun.Patch?.SunDiskTexture);
    }
}
