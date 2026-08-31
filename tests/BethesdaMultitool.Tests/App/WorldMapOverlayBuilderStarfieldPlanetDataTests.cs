using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.SaveGame.Models;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class WorldMapOverlayBuilderStarfieldPlanetDataTests
{
    [Fact]
    public void BuildFromRecords_FoldsOrderedPndtPhysicalRecordsIntoInverseIndex()
    {
        const uint planetFormId = 0x500;
        var removed = new StarfieldPlanetWorldspaceEntry(1d, 2d, 0x100);
        var retained = new StarfieldPlanetWorldspaceEntry(3d, 4d, 0x200);
        var added = new StarfieldPlanetWorldspaceEntry(5d, 6d, 0x300);
        var primary = new RecordCollection
        {
            PlanetData = [Master(planetFormId, removed, retained)]
        };
        var overlay = new RecordCollection
        {
            PlanetData =
            [
                Override(
                    planetFormId,
                    new(removed, StarfieldPlanetWorldspaceOperation.Removed),
                    new(added, StarfieldPlanetWorldspaceOperation.Added))
            ]
        };

        var world = WorldMapOverlayBuilder.BuildFromRecords(primary.MergeWith(overlay), null);

        Assert.Empty(world.PlanetWorldspaceIndex.Failures);
        var planet = Assert.Single(world.PlanetWorldspaceIndex.PlanetsByFormId).Value;
        Assert.Equal([retained, added], planet.Worldspaces);
        Assert.False(world.PlanetWorldspaceIndex.CandidatesByWorldspaceFormId
            .ContainsKey(removed.WorldspaceFormId));
        Assert.True(world.PlanetWorldspaceIndex.TryResolveUnique(
            retained.WorldspaceFormId,
            out var candidate));
        Assert.Equal(planetFormId, candidate!.Planet.FormId);
    }

    [Fact]
    public void BuildFromSave_IndexesSupplementaryPndtRecords()
    {
        const uint planetFormId = 0x501;
        var worldspace = new StarfieldPlanetWorldspaceEntry(0d, 0d, 0x101);
        var supplementary = new RecordCollection
        {
            PlanetData = [Master(planetFormId, worldspace)]
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

        Assert.True(world.PlanetWorldspaceIndex.TryResolveUnique(
            worldspace.WorldspaceFormId,
            out var candidate));
        Assert.Equal(planetFormId, candidate!.Planet.FormId);
    }

    private static StarfieldPlanetDataRecord Master(
        uint planetFormId,
        params StarfieldPlanetWorldspaceEntry[] worldspaces) =>
        new()
        {
            FormId = planetFormId,
            PayloadKind = StarfieldPlanetDataPayloadKind.Master,
            MasterWorldspaces = worldspaces,
            Body = Body()
        };

    private static StarfieldPlanetDataRecord Override(
        uint planetFormId,
        params StarfieldPlanetWorldspaceDelta[] deltas) =>
        new()
        {
            FormId = planetFormId,
            PayloadKind = StarfieldPlanetDataPayloadKind.Override,
            WorldspaceOverrides = deltas,
            Body = Body()
        };

    private static StarfieldPlanetBodyData Body() =>
        new(
            2,
            0,
            0,
            3,
            new StarfieldPlanetAtmosphereData(0x600, 0.25f, 0.0025f, 0.001f));
}
