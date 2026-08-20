using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class WorldMapExportManifestOwnershipTests
{
    [Fact]
    public void IsOwnedManifest_AcceptsMarkerBearingAndExactLegacyManifests()
    {
        const string marked = """
                              {
                                "format": "BethesdaMultitool.WorldMapTileManifest",
                                "version": 1,
                                "tiles": [ { "row": 0, "col": 1, "file": "Wasteland_r0_c1.png" } ]
                              }
                              """;
        const string legacy = """
                              {
                                "layer": "TerrainTextures",
                                "pixelsPerCell": 64,
                                "tilesWide": 1,
                                "tilesTall": 1,
                                "gridX0": 0,
                                "gridX1": 0,
                                "gridY0": 0,
                                "gridY1": 0,
                                "tiles": [ { "row": 0, "col": 0, "file": "Wasteland_r0_c0.png" } ]
                              }
                              """;

        Assert.True(WorldMapExportManifestOwnership.IsOwnedManifest(
            marked, "Wasteland", ".png"));
        Assert.True(WorldMapExportManifestOwnership.IsOwnedManifest(
            legacy, "Wasteland", ".png"));
    }

    [Theory]
    [InlineData("{ \"tiles\": [] }")]
    [InlineData("{ \"tiles\": [ { \"row\": 0, \"col\": 0, \"file\": \"other.png\" } ] }")]
    [InlineData(
        "{ \"format\": \"some.other.format\", \"version\": 1, \"tiles\": [ { \"row\": 0, \"col\": 0, \"file\": \"Wasteland_r0_c0.png\" } ] }")]
    [InlineData("not json")]
    public void IsOwnedManifest_RejectsUnrelatedOrMalformedJson(string json)
    {
        Assert.False(WorldMapExportManifestOwnership.IsOwnedManifest(
            json, "Wasteland", ".png"));
    }

    [Fact]
    public void EnsureExistingCompanionIsOwned_RefusesUnrelatedSameStemJson()
    {
        using var fixture = new TemporaryDirectory();
        var manifestPath = Path.Combine(fixture.Path, "Wasteland_manifest.json");
        File.WriteAllText(manifestPath, "{ \"application\": \"unrelated\" }");

        var exception = Assert.Throws<InvalidDataException>(() =>
            WorldMapExportManifestOwnership.EnsureExistingCompanionIsOwned(
                manifestPath, "Wasteland", ".png"));

        Assert.Contains("unrelated", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(manifestPath));
    }

    [Fact]
    public void EnsureSelectedPathIsNotAnOwnedTile_RejectsAChildOfAnExistingManifest()
    {
        using var fixture = new TemporaryDirectory();
        var manifestPath = Path.Combine(fixture.Path, "Wasteland_manifest.json");
        File.WriteAllText(manifestPath, """
                                        {
                                          "format": "BethesdaMultitool.WorldMapTileManifest",
                                          "version": 1,
                                          "tiles": [ { "row": 2, "col": 3, "file": "Wasteland_r2_c3.png" } ]
                                        }
                                        """);

        Assert.Throws<InvalidDataException>(() =>
            WorldMapExportManifestOwnership.EnsureSelectedPathIsNotAnOwnedTile(
                Path.Combine(fixture.Path, "Wasteland_r2_c3.png")));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"world-map-manifest-{Guid.NewGuid():N}")).FullName;
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}