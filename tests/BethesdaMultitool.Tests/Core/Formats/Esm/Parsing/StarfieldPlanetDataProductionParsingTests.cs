using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.WorldData;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using Xunit;
using static BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.StarfieldPlanetDataTestData;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class StarfieldPlanetDataProductionParsingTests
{
    [Fact]
    public void ParseAll_ThenLoadOrderMerge_RetainsAndFoldsOrderedPhysicalRecords()
    {
        const uint planetFormId = 0x500;
        const uint atmosphereFormId = 0x600;
        var removed = new StarfieldPlanetWorldspaceEntry(1d, 2d, 0x100);
        var retained = new StarfieldPlanetWorldspaceEntry(3d, 4d, 0x200);
        var added = new StarfieldPlanetWorldspaceEntry(5d, 6d, 0x300);
        var masterBody = Concat(
            Subrecord("EDID", [.. Encoding.ASCII.GetBytes("PlanetBase\0")]),
            ValidMasterData([removed, retained], atmosphereFormId: atmosphereFormId));
        var overrideBody = Concat(
            Subrecord("EDID", [.. Encoding.ASCII.GetBytes("PlanetOverride\0")]),
            ValidOverrideData(
                [
                    new(removed, StarfieldPlanetWorldspaceOperation.Removed),
                    new(added, StarfieldPlanetWorldspaceOperation.Added)
                ],
                systemId: 77,
                atmosphereFormId: atmosphereFormId));

        var primary = ParseRecords(PluginRecord(planetFormId, masterBody));
        var overlay = ParseRecords(PluginRecord(planetFormId, overrideBody));
        var merged = primary.MergeWith(overlay);

        Assert.Single(primary.PlanetData);
        Assert.Single(overlay.PlanetData);
        Assert.Equal(2, merged.PlanetData.Count);
        Assert.Equal(
            [StarfieldPlanetDataPayloadKind.Master, StarfieldPlanetDataPayloadKind.Override],
            merged.PlanetData.Select(static record => record.PayloadKind));
        Assert.DoesNotContain("PNDT", primary.UnparsedTypeCounts.Keys);

        var index = StarfieldPlanetWorldspaceIndex.Build(merged.PlanetData);
        Assert.Empty(index.Failures);
        var planet = Assert.Single(index.PlanetsByFormId).Value;
        Assert.Equal("PlanetOverride", planet.EditorId);
        Assert.Equal([retained, added], planet.Worldspaces);
        Assert.Equal(77u, planet.Body.SystemId);
        Assert.Equal(atmosphereFormId, planet.Body.Atmosphere.AtmosphereFormId);
    }

    [Fact]
    public void ParseAll_FailureEnvelopeRetainsSourceIdentity()
    {
        const uint planetFormId = 0x501;
        var malformedBody = Concat(
            Subrecord("EDID", [.. Encoding.ASCII.GetBytes("BrokenPlanet\0")]),
            Subrecord("CNAM", [1, 2, 3]));

        var parsed = ParseRecords(PluginRecord(planetFormId, malformedBody));

        var record = Assert.Single(parsed.PlanetData);
        Assert.Equal(planetFormId, record.FormId);
        Assert.Equal("BrokenPlanet", record.EditorId);
        Assert.Equal(StarfieldPlanetDataPayloadKind.Unknown, record.PayloadKind);
        Assert.Null(record.Body);
        Assert.Contains("multiple of 20", record.DecodeFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccessorPath_RejectsBigEndianAndPartiallyRecoveredRecords()
    {
        const uint planetFormId = 0x502;
        var body = Concat(
            Subrecord("EDID", [.. Encoding.ASCII.GetBytes("UnsafePlanet\0")]),
            ValidMasterData(
                [new StarfieldPlanetWorldspaceEntry(0d, 0d, 0x100)],
                atmosphereFormId: 0x600));
        var bytes = PluginRecord(planetFormId, body);

        var bigEndian = Assert.Single(ParseRecords(bytes, isBigEndian: true).PlanetData);
        Assert.True(bigEndian.IsBigEndian);
        Assert.Contains("little-endian", bigEndian.DecodeFailure, StringComparison.OrdinalIgnoreCase);

        var descriptor = Descriptor(bytes, 0, planetFormId, false);
        var context = new RecordParserContext(
            new EsmRecordScanResult
            {
                Game = BethesdaGame.Starfield,
                MainRecords = [descriptor]
            },
            null,
            new ByteArrayMemoryAccessor(bytes),
            bytes.Length,
            null);
        context.PartiallyRecoveredFormIds.Add(planetFormId);

        var recovered = Assert.Single(new MiscEnvironmentHandler(context).ParseStarfieldPlanetData());
        Assert.Equal(planetFormId, recovered.FormId);
        Assert.Contains("partially recovered", recovered.DecodeFailure, StringComparison.OrdinalIgnoreCase);
        Assert.Null(recovered.Body);
    }

    private static RecordCollection ParseRecords(params byte[][] recordBytes) =>
        ParseRecords(recordBytes, false);

    private static RecordCollection ParseRecords(byte[] recordBytes, bool isBigEndian) =>
        ParseRecords([recordBytes], isBigEndian);

    private static RecordCollection ParseRecords(byte[][] recordBytes, bool isBigEndian)
    {
        var totalLength = recordBytes.Sum(static bytes => bytes.Length);
        var allBytes = new byte[totalLength];
        var descriptors = new List<DetectedMainRecord>(recordBytes.Length);
        var offset = 0;
        foreach (var bytes in recordBytes)
        {
            Array.Copy(bytes, 0, allBytes, offset, bytes.Length);
            var formId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
            descriptors.Add(Descriptor(bytes, offset, formId, isBigEndian));
            offset += bytes.Length;
        }

        using var mmf = MemoryMappedFile.CreateNew(null, allBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, allBytes.Length);
        accessor.WriteArray(0, allBytes, 0, allBytes.Length);
        var scan = new EsmRecordScanResult
        {
            Game = BethesdaGame.Starfield,
            MainRecords = descriptors
        };
        return new RecordParser(scan, accessor: accessor, fileSize: allBytes.Length).ParseAll();
    }

    private static byte[] PluginRecord(uint formId, byte[] body)
    {
        var bytes = new byte[24 + body.Length];
        Encoding.ASCII.GetBytes("PNDT", bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint)body.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), formId);
        body.CopyTo(bytes, 24);
        return bytes;
    }

    private static DetectedMainRecord Descriptor(
        byte[] bytes,
        long offset,
        uint formId,
        bool isBigEndian) =>
        new("PNDT", (uint)(bytes.Length - 24), 0, formId, offset, isBigEndian);
}
