using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class StarfieldPlanetDataFormIdRebaserTests
{
    [Fact]
    public void Rebase_MapsOnlyRecordWorldspaceAndAtmosphereFormIds()
    {
        var masterEntry = new StarfieldPlanetWorldspaceEntry(10d, -20d, 0x01000100);
        var deltaEntry = new StarfieldPlanetWorldspaceEntry(-0d, 30d, 0x01000200);
        var source = new StarfieldPlanetDataRecord
        {
            FormId = 0x01000300,
            EditorId = "Planet",
            PayloadKind = StarfieldPlanetDataPayloadKind.Master,
            MasterWorldspaces = [masterEntry],
            WorldspaceOverrides =
            [
                new StarfieldPlanetWorldspaceDelta(
                    deltaEntry,
                    StarfieldPlanetWorldspaceOperation.Added)
            ],
            TopLevelGnamRawBits = 0x3FC00000,
            Body = new StarfieldPlanetBodyData(
                2,
                0,
                0x01000400,
                0x01000500,
                new StarfieldPlanetAtmosphereData(0x01000600, 0.25f, 0.0025f, 0.001f)),
            Offset = 1234
        };

        var rebased = StarfieldPlanetDataFormIdRebaser.Rebase(
            source,
            static formId => formId + 0x01000000);

        Assert.Equal(0x02000300u, rebased.FormId);
        Assert.Equal(0x02000100u, Assert.Single(rebased.MasterWorldspaces).WorldspaceFormId);
        Assert.Equal(
            0x02000200u,
            Assert.Single(rebased.WorldspaceOverrides).Entry.WorldspaceFormId);
        Assert.Equal(0x02000600u, rebased.Body!.Atmosphere.AtmosphereFormId);

        Assert.Equal(masterEntry.LatitudeRawBits, rebased.MasterWorldspaces[0].LatitudeRawBits);
        Assert.Equal(masterEntry.LongitudeRawBits, rebased.MasterWorldspaces[0].LongitudeRawBits);
        Assert.Equal(deltaEntry.LatitudeRawBits, rebased.WorldspaceOverrides[0].Entry.LatitudeRawBits);
        Assert.Equal(0u, rebased.Body.SystemId);
        Assert.Equal(0x01000400u, rebased.Body.ParentPlanetId);
        Assert.Equal(0x01000500u, rebased.Body.PlanetId);
        Assert.Equal(0x3FC00000u, rebased.TopLevelGnamRawBits);
        Assert.Equal("Planet", rebased.EditorId);
        Assert.Equal(1234, rebased.Offset);

        Assert.Equal(0x01000300u, source.FormId);
        Assert.Equal(0x01000100u, source.MasterWorldspaces[0].WorldspaceFormId);
        Assert.Equal(0x01000600u, source.Body!.Atmosphere.AtmosphereFormId);
        Assert.NotSame(source.MasterWorldspaces, rebased.MasterWorldspaces);
        Assert.NotSame(source.WorldspaceOverrides, rebased.WorldspaceOverrides);
        Assert.NotSame(source.Body, rebased.Body);
    }

    [Fact]
    public void Rebase_PreservesZeroAndNeverOffersItToMapper()
    {
        var calls = new List<uint>();
        var source = new StarfieldPlanetDataRecord
        {
            FormId = 0,
            PayloadKind = StarfieldPlanetDataPayloadKind.Master,
            MasterWorldspaces = [new StarfieldPlanetWorldspaceEntry(0d, 0d, 0)],
            Body = new StarfieldPlanetBodyData(
                0,
                0,
                0,
                0,
                new StarfieldPlanetAtmosphereData(0, 0f, 0f, 0f))
        };

        var rebased = StarfieldPlanetDataFormIdRebaser.Rebase(source, value =>
        {
            calls.Add(value);
            return value + 1;
        });

        Assert.Empty(calls);
        Assert.Equal(0u, rebased.FormId);
        Assert.Equal(0u, Assert.Single(rebased.MasterWorldspaces).WorldspaceFormId);
        Assert.Equal(0u, rebased.Body!.Atmosphere.AtmosphereFormId);
    }
}
