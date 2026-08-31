using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.WorldData;
using Xunit;
using static BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.StarfieldPlanetDataTestData;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class StarfieldPlanetWorldspaceIndexTests
{
    [Fact]
    public void Build_FoldsPhysicalOverridesAndUsesLatestCompleteBody()
    {
        var removed = new StarfieldPlanetWorldspaceEntry(1d, 2d, 0x100);
        var retained = new StarfieldPlanetWorldspaceEntry(3d, 4d, 0x200);
        var added = new StarfieldPlanetWorldspaceEntry(5d, 6d, 0x300);
        var master = DecodeMaster([removed, retained]) with
        {
            FormId = 0x500,
            EditorId = "PlanetBase"
        };
        var overlay = DecodeOverride(
            [
                new(removed, StarfieldPlanetWorldspaceOperation.Removed),
                new(added, StarfieldPlanetWorldspaceOperation.Added)
            ],
            systemId: 77,
            atmosphereFormId: 0xABC) with
        {
            FormId = 0x500,
            EditorId = "PlanetOverride"
        };

        var result = StarfieldPlanetWorldspaceIndex.Build([master, overlay]);

        Assert.Empty(result.Failures);
        var planet = Assert.Single(result.PlanetsByFormId).Value;
        Assert.Equal(0x500u, planet.FormId);
        Assert.Equal("PlanetOverride", planet.EditorId);
        Assert.Equal([retained, added], planet.Worldspaces);
        Assert.Equal(77u, planet.Body.SystemId);
        Assert.Equal(0xABCu, planet.Body.Atmosphere.AtmosphereFormId);
        Assert.Equal(2, planet.SourceRecordCount);
        Assert.False(result.CandidatesByWorldspaceFormId.ContainsKey(removed.WorldspaceFormId));
        Assert.True(result.TryResolveUnique(retained.WorldspaceFormId, out var retainedCandidate));
        Assert.Same(planet, retainedCandidate!.Planet);
        Assert.Equal(retained, retainedCandidate.Worldspace);
    }

    [Fact]
    public void Build_PreservesAmbiguousWorldspaceCandidates()
    {
        const uint sharedWorldspace = 0x100;
        var north = new StarfieldPlanetWorldspaceEntry(10d, 20d, sharedWorldspace);
        var south = new StarfieldPlanetWorldspaceEntry(-10d, -20d, sharedWorldspace);

        var result = StarfieldPlanetWorldspaceIndex.Build(
        [
            DecodeMaster([north]) with { FormId = 0x501, EditorId = "PlanetA" },
            DecodeMaster([south]) with { FormId = 0x502, EditorId = "PlanetB" }
        ]);

        Assert.Empty(result.Failures);
        var candidates = Assert.Single(result.CandidatesByWorldspaceFormId).Value;
        Assert.Equal(2, candidates.Count);
        Assert.Equal([0x501u, 0x502u], candidates.Select(static item => item.Planet.FormId));
        Assert.Equal([north, south], candidates.Select(static item => item.Worldspace));
        Assert.False(result.TryResolveUnique(sharedWorldspace, out var candidate));
        Assert.Null(candidate);
    }

    [Fact]
    public void Build_ReportsMalformedPlanetWithoutHidingValidPeer()
    {
        var validWorldspace = new StarfieldPlanetWorldspaceEntry(1d, 1d, 0x100);
        var valid = DecodeMaster([validWorldspace]) with { FormId = 0x501 };
        var deltaOnly = DecodeOverride(
            [new(validWorldspace, StarfieldPlanetWorldspaceOperation.Added)]) with
        {
            FormId = 0x502
        };
        var zeroFormId = DecodeMaster([]);

        var result = StarfieldPlanetWorldspaceIndex.Build([deltaOnly, zeroFormId, valid]);

        Assert.Single(result.PlanetsByFormId);
        Assert.True(result.PlanetsByFormId.ContainsKey(0x501));
        Assert.Equal(2, result.Failures.Count);
        Assert.Contains(result.Failures, static failure =>
            failure.Kind == StarfieldPlanetWorldspaceIndexFailureKind.InvalidPlanetFormId &&
            failure.PlanetFormId == 0);
        Assert.Contains(result.Failures, static failure =>
            failure.Kind == StarfieldPlanetWorldspaceIndexFailureKind.MergeFailed &&
            failure.PlanetFormId == 0x502 &&
            failure.MergeStatus == StarfieldPlanetDataMergeStatus.DeltaWithoutBase);
    }

    private static StarfieldPlanetDataRecord DecodeMaster(
        IReadOnlyList<StarfieldPlanetWorldspaceEntry> entries)
    {
        Assert.True(StarfieldPlanetDataDecoder.TryDecode(
            ValidMasterData(entries), false, out var record, out var error), error);
        return record;
    }

    private static StarfieldPlanetDataRecord DecodeOverride(
        IReadOnlyList<StarfieldPlanetWorldspaceDelta> deltas,
        uint systemId = 10,
        uint atmosphereFormId = 0x00123456)
    {
        Assert.True(StarfieldPlanetDataDecoder.TryDecode(
            ValidOverrideData(deltas, systemId: systemId, atmosphereFormId: atmosphereFormId),
            false,
            out var record,
            out var error), error);
        return record;
    }
}
