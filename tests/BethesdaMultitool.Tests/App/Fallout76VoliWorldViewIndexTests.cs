using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.SaveGame.Models;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class Fallout76VoliWorldViewIndexTests
{
    [Fact]
    public void BuildFromRecords_IndexesOverlayLastClassicVoliIncludingFailureEnvelope()
    {
        var retained = Volumetric(0x0000_0001, "Retained");
        var replaced = Volumetric(0x0000_0002, "Base");
        var invalidOverride = new Fallout76VolumetricLightingRecord
        {
            FormId = replaced.FormId,
            EditorId = "InvalidOverride",
            DecodeFailure = "synthetic malformed later definition"
        };
        var added = Volumetric(0x0000_0003, "Added");

        var merged = new RecordCollection
        {
            Fallout76VolumetricLightingSettings = [retained, replaced]
        }.MergeWith(new RecordCollection
        {
            Fallout76VolumetricLightingSettings = [invalidOverride, added]
        });

        var world = WorldMapOverlayBuilder.BuildFromRecords(merged, null);

        Assert.Equal(3, world.Fallout76VolumetricLightingByFormId.Count);
        Assert.Same(retained, world.Fallout76VolumetricLightingByFormId[retained.FormId]);
        Assert.Same(invalidOverride, world.Fallout76VolumetricLightingByFormId[replaced.FormId]);
        Assert.Same(added, world.Fallout76VolumetricLightingByFormId[added.FormId]);
        Assert.Equal(3, merged.TotalRecordsParsed);
    }

    [Fact]
    public void BuildFromSave_WithSupplementaryRecords_IndexesClassicVoli()
    {
        var volumetric = Volumetric(0x0000_0021, "Supplementary");
        var supplementary = new RecordCollection
        {
            Fallout76VolumetricLightingSettings = [volumetric]
        };
        var save = new SaveFile
        {
            Header = new SaveFileHeader(),
            Statistics = new SaveStatistics(),
            LocationTable = new FileLocationTable()
        };

        var world = WorldMapOverlayBuilder.BuildFromSave(
            save, supplementary, FormIdResolver.Empty, null);

        Assert.Same(volumetric, world.Fallout76VolumetricLightingByFormId[volumetric.FormId]);
    }

    private static Fallout76VolumetricLightingRecord Volumetric(uint formId, string editorId) =>
        new()
        {
            FormId = formId,
            EditorId = editorId,
            Settings = new Fallout76VolumetricLightingSettings
            {
                SamplingRepartitionRangeFactor = 50f
            }
        };
}
