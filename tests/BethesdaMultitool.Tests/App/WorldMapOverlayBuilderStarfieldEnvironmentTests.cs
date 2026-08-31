using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.SaveGame.Models;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class WorldMapOverlayBuilderStarfieldEnvironmentTests
{
    [Fact]
    public void BuildFromRecords_IndexesMergedVolumetricLightingCloudFormsAndAtmospheres()
    {
        var retainedVolumetric = Volumetric(0x00000001, "RetainedVolumetric");
        var replacedVolumetric = Volumetric(0x00000002, "BaseVolumetric");
        var overrideVolumetric = Volumetric(0x00000002, "OverrideVolumetric");
        var addedVolumetric = Volumetric(0x00000003, "AddedVolumetric");
        var retainedCloud = Cloud(0x00000011, "RetainedCloud");
        var replacedCloud = Cloud(0x00000012, "BaseCloud");
        var overrideCloud = Cloud(0x00000012, "OverrideCloud");
        var addedCloud = Cloud(0x00000013, "AddedCloud");
        var retainedAtmosphere = Atmosphere(0x00000021, "RetainedAtmosphere");
        var replacedAtmosphere = Atmosphere(0x00000022, "BaseAtmosphere");
        var overrideAtmosphere = Atmosphere(0x00000022, "OverrideAtmosphere");
        var addedAtmosphere = Atmosphere(0x00000023, "AddedAtmosphere");

        var merged = new RecordCollection
        {
            VolumetricLightingSettings = [retainedVolumetric, replacedVolumetric],
            CloudForms = [retainedCloud, replacedCloud],
            Atmospheres = [retainedAtmosphere, replacedAtmosphere]
        }.MergeWith(new RecordCollection
        {
            VolumetricLightingSettings = [overrideVolumetric, addedVolumetric],
            CloudForms = [overrideCloud, addedCloud],
            Atmospheres = [overrideAtmosphere, addedAtmosphere]
        });

        var world = WorldMapOverlayBuilder.BuildFromRecords(merged, null);

        Assert.Equal(3, world.VolumetricLightingByFormId.Count);
        Assert.Same(retainedVolumetric, world.VolumetricLightingByFormId[retainedVolumetric.FormId]);
        Assert.Same(overrideVolumetric, world.VolumetricLightingByFormId[overrideVolumetric.FormId]);
        Assert.Same(addedVolumetric, world.VolumetricLightingByFormId[addedVolumetric.FormId]);

        Assert.Equal(3, world.CloudFormsByFormId.Count);
        Assert.Same(retainedCloud, world.CloudFormsByFormId[retainedCloud.FormId]);
        Assert.Same(overrideCloud, world.CloudFormsByFormId[overrideCloud.FormId]);
        Assert.Same(addedCloud, world.CloudFormsByFormId[addedCloud.FormId]);

        Assert.Equal(3, world.AtmospheresByFormId.Count);
        Assert.Same(retainedAtmosphere, world.AtmospheresByFormId[retainedAtmosphere.FormId]);
        Assert.Same(overrideAtmosphere, world.AtmospheresByFormId[overrideAtmosphere.FormId]);
        Assert.Same(addedAtmosphere, world.AtmospheresByFormId[addedAtmosphere.FormId]);
    }

    [Fact]
    public void BuildFromSave_WithSupplementaryRecords_IndexesVolumetricLightingCloudFormsAndAtmospheres()
    {
        var volumetric = Volumetric(0x00000021, "SupplementaryVolumetric");
        var cloud = Cloud(0x00000022, "SupplementaryCloud");
        var atmosphere = Atmosphere(0x00000023, "SupplementaryAtmosphere");
        var supplementary = new RecordCollection
        {
            VolumetricLightingSettings = [volumetric],
            CloudForms = [cloud],
            Atmospheres = [atmosphere]
        };
        var save = new SaveFile
        {
            Header = new SaveFileHeader(),
            Statistics = new SaveStatistics(),
            LocationTable = new FileLocationTable()
        };

        var world = WorldMapOverlayBuilder.BuildFromSave(
            save, supplementary, FormIdResolver.Empty, null);

        Assert.Same(volumetric, world.VolumetricLightingByFormId[volumetric.FormId]);
        Assert.Same(cloud, world.CloudFormsByFormId[cloud.FormId]);
        Assert.Same(atmosphere, world.AtmospheresByFormId[atmosphere.FormId]);
    }

    private static StarfieldVolumetricLightingRecord Volumetric(uint formId, string editorId) =>
        new()
        {
            FormId = formId,
            EditorId = editorId
        };

    private static StarfieldCloudFormRecord Cloud(uint formId, string editorId) =>
        new()
        {
            FormId = formId,
            EditorId = editorId
        };

    private static StarfieldAtmosphereRecord Atmosphere(uint formId, string editorId) =>
        new()
        {
            FormId = formId,
            EditorId = editorId
        };
}
