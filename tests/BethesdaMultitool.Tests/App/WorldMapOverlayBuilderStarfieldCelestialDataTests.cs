using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.SaveGame.Models;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class WorldMapOverlayBuilderStarfieldCelestialDataTests
{
    [Fact]
    public void BuildFromRecords_IndexesMergedStdtAndSunpWhileRetainingSystemAmbiguity()
    {
        var retainedStar = Star(0x01, 7);
        var replacedStar = Star(0x02, 8);
        var overrideStar = Star(0x02, 7);
        var addedStar = Star(0x03, 7);
        var retainedSun = Sun(0x11, "RetainedSun");
        var replacedSun = Sun(0x12, "BaseSun");
        var overrideSun = Sun(0x12, "OverrideSun");
        var addedSun = Sun(0x13, "AddedSun");
        var merged = new RecordCollection
        {
            StarData = [retainedStar, replacedStar],
            SunPresets = [retainedSun, replacedSun]
        }.MergeWith(new RecordCollection
        {
            StarData = [overrideStar, addedStar],
            SunPresets = [overrideSun, addedSun]
        });

        var world = WorldMapOverlayBuilder.BuildFromRecords(merged, null);

        Assert.Equal(3, world.StarDataIndex.Records.Count);
        Assert.False(world.StarDataIndex.RecordsBySystemId.ContainsKey(8));
        Assert.Equal(
            [retainedStar, overrideStar, addedStar],
            world.StarDataIndex.RecordsBySystemId[7]);
        var ambiguous = StarfieldStarDataResolver.ResolveSystem(7, world.StarDataIndex);
        Assert.Equal(StarfieldStarDataResolutionStatus.AmbiguousSystem, ambiguous.Status);
        Assert.Equal([0x01u, 0x02u, 0x03u], ambiguous.ConflictingFormIds);

        Assert.Equal(3, world.SunPresetsByFormId.Count);
        Assert.Same(retainedSun, world.SunPresetsByFormId[retainedSun.FormId]);
        Assert.Same(overrideSun, world.SunPresetsByFormId[overrideSun.FormId]);
        Assert.Same(addedSun, world.SunPresetsByFormId[addedSun.FormId]);
    }

    [Fact]
    public void BuildFromSave_IndexesSupplementaryStdtAndSunp()
    {
        var star = Star(0x21, 0);
        var sun = Sun(0x22, "SupplementarySun");
        var supplementary = new RecordCollection
        {
            StarData = [star],
            SunPresets = [sun]
        };
        var save = new SaveFile
        {
            Header = new SaveFileHeader(),
            Statistics = new SaveStatistics(),
            LocationTable = new FileLocationTable()
        };

        var world = WorldMapOverlayBuilder.BuildFromSave(
            save,
            supplementary,
            FormIdResolver.Empty,
            null);

        Assert.Same(star, Assert.Single(world.StarDataIndex.Records));
        Assert.Same(star, Assert.Single(world.StarDataIndex.RecordsBySystemId[0]));
        Assert.Same(sun, world.SunPresetsByFormId[sun.FormId]);
    }

    private static StarfieldStarDataRecord Star(uint formId, uint systemId) =>
        new()
        {
            FormId = formId,
            Routing = new StarfieldStarDataRouting { SystemId = systemId }
        };

    private static StarfieldSunPresetRecord Sun(uint formId, string editorId) =>
        new()
        {
            FormId = formId,
            EditorId = editorId
        };
}
