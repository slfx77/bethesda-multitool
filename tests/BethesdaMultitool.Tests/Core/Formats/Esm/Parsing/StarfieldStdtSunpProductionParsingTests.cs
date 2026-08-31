using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.Reflection;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class StarfieldStdtSunpProductionParsingTests
{
    [Fact]
    public void ParseAll_DecodesStdtAndSunpWithoutCollapsingNullAndAuthoredZero()
    {
        const uint starFormId = 0x100;
        const uint rootSunPresetFormId = 0x200;
        const uint diffSunPresetFormId = 0x201;
        var parsed = ParseRecords(
            Stdt(
                starFormId,
                ("EDID", NullTermString("SolStarData")),
                ("DNAM", U32(0)),
                ("SNAM", U32(0)),
                ("PNAM", U32(diffSunPresetFormId))),
            Sunp(
                rootSunPresetFormId,
                ("EDID", NullTermString("SunPresetRoot")),
                ("REFL", StarfieldSunPresetTestStreamBuilder.BuildFull(
                    reflectedParent: 0,
                    diskTexture: "Data/Textures/Sky/SunDisk_color.dds"))),
            Sunp(
                diffSunPresetFormId,
                ("EDID", NullTermString("SunPresetSol")),
                ("RFDP", U32(rootSunPresetFormId)),
                ("RDIF", StarfieldSunPresetTestStreamBuilder.BuildDiff(
                    reflectedParent: rootSunPresetFormId,
                    diskTexture: string.Empty))));

        var star = Assert.Single(parsed.StarData);
        Assert.Equal("SolStarData", star.EditorId);
        Assert.Null(star.DecodeFailure);
        Assert.Equal(0u, star.Routing?.SystemId);
        Assert.Equal(0u, star.Routing?.BinaryStarFormId);
        Assert.Equal(diffSunPresetFormId, star.Routing?.SunPresetFormId);
        Assert.Null(star.Routing?.TimeOfDayDataFormId);

        Assert.Equal(2, parsed.SunPresets.Count);
        var root = parsed.SunPresets.Single(record => record.FormId == rootSunPresetFormId);
        Assert.Equal(StarfieldSunPresetPayloadKind.FullObject, root.PayloadKind);
        Assert.Null(root.ParentFormId);
        Assert.Equal(0u, root.Patch?.ParentFormId);
        Assert.Null(root.DecodeFailure);

        var diff = parsed.SunPresets.Single(record => record.FormId == diffSunPresetFormId);
        Assert.Equal(StarfieldSunPresetPayloadKind.Diff, diff.PayloadKind);
        Assert.Equal(rootSunPresetFormId, diff.ParentFormId);
        Assert.Equal(rootSunPresetFormId, diff.Patch?.ParentFormId);
        Assert.Equal(string.Empty, diff.Patch?.SunDiskTexture);
        Assert.Null(diff.DecodeFailure);

        var resolution = StarfieldSunPresetResolver.Resolve(
            diffSunPresetFormId,
            parsed.SunPresets.ToDictionary(record => record.FormId));
        Assert.True(resolution.IsResolved, resolution.FailureDetail);
        Assert.Equal([rootSunPresetFormId, diffSunPresetFormId], resolution.InheritanceChain);
        Assert.Equal(string.Empty, resolution.EffectivePatch?.SunDiskTexture);
        Assert.DoesNotContain("STDT", parsed.UnparsedTypeCounts.Keys);
        Assert.DoesNotContain("SUNP", parsed.UnparsedTypeCounts.Keys);
    }

    [Fact]
    public void LoadOrderMerge_LaterFailuresReplaceSameFormIdButPreserveDistinctSystemCandidates()
    {
        const uint replacedStarFormId = 0x100;
        const uint peerStarFormId = 0x101;
        const uint sunPresetFormId = 0x200;
        var primary = ParseRecords(
            Stdt(
                replacedStarFormId,
                ("EDID", NullTermString("ValidStarBase")),
                ("DNAM", U32(7))),
            Stdt(
                peerStarFormId,
                ("EDID", NullTermString("SameSystemPeer")),
                ("DNAM", U32(7))),
            Sunp(
                sunPresetFormId,
                ("EDID", NullTermString("ValidSunBase")),
                ("REFL", StarfieldSunPresetTestStreamBuilder.BuildFull())));
        var overlay = ParseRecords(
            Stdt(
                replacedStarFormId,
                ("EDID", NullTermString("BrokenStarOverride")),
                ("DNAM", U32(7)),
                ("DNAM", U32(8))),
            Sunp(
                sunPresetFormId,
                ("EDID", NullTermString("BrokenSunOverride")),
                ("XTRA", [1, 2, 3, 4])));

        var merged = primary.MergeWith(overlay);

        Assert.Equal(2, merged.StarData.Count);
        var retainedStar = merged.StarData.Single(record => record.FormId == replacedStarFormId);
        Assert.Same(overlay.StarData.Single(), retainedStar);
        Assert.Equal("BrokenStarOverride", retainedStar.EditorId);
        Assert.Contains("duplicate DNAM", retainedStar.DecodeFailure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Same(Assert.Single(overlay.SunPresets), Assert.Single(merged.SunPresets));
        Assert.Equal("BrokenSunOverride", merged.SunPresets[0].EditorId);
        Assert.Contains("unsupported", merged.SunPresets[0].DecodeFailure,
            StringComparison.OrdinalIgnoreCase);

        // The malformed override remains outside the system index because it has no valid routing,
        // while the distinct physical peer is still visible. No earlier definition leaks through.
        var index = StarfieldStarDataIndex.Build(merged.StarData);
        Assert.Single(index.RecordsBySystemId[7]);
        Assert.Same(primary.StarData.Single(record => record.FormId == peerStarFormId),
            index.RecordsBySystemId[7][0]);
        Assert.Contains(retainedStar, index.RecordsWithoutSystemId);
    }

    [Fact]
    public void SunpExactOuterEnvelope_RejectsDuplicateUnknownReorderedAndInvalidParents()
    {
        var duplicate = Assert.Single(ParseRecords(Sunp(
            0x300,
            ("EDID", NullTermString("Duplicate")),
            ("EDID", NullTermString("DuplicateAgain")),
            ("REFL", StarfieldSunPresetTestStreamBuilder.BuildFull()))).SunPresets);
        Assert.Contains("duplicate EDID", duplicate.DecodeFailure,
            StringComparison.OrdinalIgnoreCase);

        var unknown = Assert.Single(ParseRecords(Sunp(
            0x301,
            ("EDID", NullTermString("Unknown")),
            ("FULL", NullTermString("unsupported")),
            ("REFL", StarfieldSunPresetTestStreamBuilder.BuildFull()))).SunPresets);
        Assert.Contains("unsupported", unknown.DecodeFailure, StringComparison.OrdinalIgnoreCase);

        var reordered = Assert.Single(ParseRecords(Sunp(
            0x302,
            ("RFDP", U32(0x200)),
            ("EDID", NullTermString("Reordered")),
            ("RDIF", StarfieldSunPresetTestStreamBuilder.BuildDiff(0x200)))).SunPresets);
        Assert.Contains("exactly EDID", reordered.DecodeFailure,
            StringComparison.OrdinalIgnoreCase);

        var zeroParent = Assert.Single(ParseRecords(Sunp(
            0x303,
            ("EDID", NullTermString("ZeroParent")),
            ("RFDP", U32(0)),
            ("RDIF", StarfieldSunPresetTestStreamBuilder.BuildDiff(0)))).SunPresets);
        Assert.Equal(StarfieldSunPresetPayloadKind.Diff, zeroParent.PayloadKind);
        Assert.Equal(0u, zeroParent.ParentFormId);
        Assert.Contains("non-zero", zeroParent.DecodeFailure,
            StringComparison.OrdinalIgnoreCase);

        var omittedReflectedParent = Assert.Single(ParseRecords(Sunp(
            0x304,
            ("EDID", NullTermString("MissingReflectedParent")),
            ("RFDP", U32(0x200)),
            ("RDIF", StarfieldSunPresetTestStreamBuilder.BuildDiff(
                0x200,
                omitReflectedParent: true)))).SunPresets);
        Assert.Equal(StarfieldSunPresetPayloadKind.Diff, omittedReflectedParent.PayloadKind);
        Assert.Contains("explicitly authored", omittedReflectedParent.DecodeFailure,
            StringComparison.OrdinalIgnoreCase);

        var contradictoryParent = Assert.Single(ParseRecords(Sunp(
            0x305,
            ("EDID", NullTermString("ContradictoryParent")),
            ("RFDP", U32(0x200)),
            ("RDIF", StarfieldSunPresetTestStreamBuilder.BuildDiff(0x201)))).SunPresets);
        Assert.Equal(0x200u, contradictoryParent.ParentFormId);
        Assert.Contains("equal reflected", contradictoryParent.DecodeFailure,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccessorPath_RejectsBigEndianAndPartialRecordsWhileRetainingLeadingIdentity()
    {
        const uint starFormId = 0x400;
        const uint sunPresetFormId = 0x401;
        var starFields = new (string Signature, byte[] Data)[]
        {
            ("EDID", NullTermString("UnsafeStar")),
            ("DNAM", U32(0))
        };
        var sunFields = new (string Signature, byte[] Data)[]
        {
            ("EDID", NullTermString("UnsafeSun")),
            ("REFL", StarfieldSunPresetTestStreamBuilder.BuildFull())
        };
        var bigEndianStarBytes = BuildRecordBytes(starFormId, "STDT", true, starFields);
        var bigEndianSunBytes = BuildRecordBytes(sunPresetFormId, "SUNP", true, sunFields);
        var starBytes = Stdt(
            starFormId,
            starFields).Bytes;
        var sunBytes = Sunp(
            sunPresetFormId,
            sunFields).Bytes;

        var bigEndianStar = Assert.Single(ParseRecords(
            new Fixture("STDT", starFormId, bigEndianStarBytes, true)).StarData);
        Assert.Equal("UnsafeStar", bigEndianStar.EditorId);
        Assert.True(bigEndianStar.IsBigEndian);
        Assert.Contains("little-endian", bigEndianStar.DecodeFailure,
            StringComparison.OrdinalIgnoreCase);

        var bigEndianSun = Assert.Single(ParseRecords(
            new Fixture("SUNP", sunPresetFormId, bigEndianSunBytes, true)).SunPresets);
        Assert.Equal("UnsafeSun", bigEndianSun.EditorId);
        Assert.True(bigEndianSun.IsBigEndian);
        Assert.Contains("little-endian", bigEndianSun.DecodeFailure,
            StringComparison.OrdinalIgnoreCase);

        var scan = new EsmRecordScanResult
        {
            Game = BethesdaGame.Starfield,
            MainRecords =
            [
                Descriptor("STDT", starFormId, starBytes, 0, false),
                Descriptor("SUNP", sunPresetFormId, sunBytes, starBytes.Length, false)
            ]
        };
        var allBytes = starBytes.Concat(sunBytes).ToArray();
        var context = new RecordParserContext(
            scan,
            null,
            new ByteArrayMemoryAccessor(allBytes),
            allBytes.Length,
            null);
        context.PartiallyRecoveredFormIds.Add(starFormId);
        context.PartiallyRecoveredFormIds.Add(sunPresetFormId);
        var handler = new MiscEnvironmentHandler(context);

        var partialStar = Assert.Single(handler.ParseStarfieldStarData());
        Assert.Equal("UnsafeStar", partialStar.EditorId);
        Assert.Contains("partially recovered", partialStar.DecodeFailure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(partialStar.Routing);

        var partialSun = Assert.Single(handler.ParseStarfieldSunPresets());
        Assert.Equal("UnsafeSun", partialSun.EditorId);
        Assert.Contains("partially recovered", partialSun.DecodeFailure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(partialSun.Patch);
    }

    [Fact]
    public void ScanOnlyPath_RetainsOneFailureEnvelopePerDetectedRecord()
    {
        var scan = new EsmRecordScanResult
        {
            Game = BethesdaGame.Starfield,
            MainRecords =
            [
                new DetectedMainRecord("STDT", 64, 0, 0x500, 0, false),
                new DetectedMainRecord("SUNP", 64, 0, 0x501, 88, false)
            ]
        };

        var parsed = new RecordParser(scan).ParseAll();

        Assert.Contains("without record-byte access", Assert.Single(parsed.StarData).DecodeFailure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("without record-byte access", Assert.Single(parsed.SunPresets).DecodeFailure,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Handlers_DoNotClaimStdtOrSunpOutsideStarfield()
    {
        var scan = new EsmRecordScanResult
        {
            Game = BethesdaGame.Fallout76,
            MainRecords =
            [
                new DetectedMainRecord("STDT", 64, 0, 0x600, 0, false),
                new DetectedMainRecord("SUNP", 64, 0, 0x601, 88, false)
            ]
        };
        var handler = new MiscEnvironmentHandler(new RecordParserContext(scan));

        Assert.Empty(handler.ParseStarfieldStarData());
        Assert.Empty(handler.ParseStarfieldSunPresets());
    }

    private static RecordCollection ParseRecords(params Fixture[] fixtures)
    {
        var totalLength = fixtures.Sum(fixture => fixture.Bytes.Length);
        var allBytes = new byte[totalLength];
        var descriptors = new List<DetectedMainRecord>(fixtures.Length);
        var offset = 0;
        foreach (var fixture in fixtures)
        {
            fixture.Bytes.CopyTo(allBytes, offset);
            descriptors.Add(Descriptor(
                fixture.RecordType,
                fixture.FormId,
                fixture.Bytes,
                offset,
                fixture.IsBigEndian));
            offset += fixture.Bytes.Length;
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

    private static DetectedMainRecord Descriptor(
        string recordType,
        uint formId,
        byte[] bytes,
        long offset,
        bool isBigEndian) =>
        new(recordType, checked((uint)(bytes.Length - 24)), 0, formId, offset, isBigEndian);

    private static Fixture Stdt(
        uint formId,
        params (string Signature, byte[] Data)[] fields) =>
        new("STDT", formId, BuildRecordBytes(formId, "STDT", false, fields));

    private static Fixture Sunp(
        uint formId,
        params (string Signature, byte[] Data)[] fields) =>
        new("SUNP", formId, BuildRecordBytes(formId, "SUNP", false, fields));

    private static byte[] U32(uint value)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private readonly record struct Fixture(
        string RecordType,
        uint FormId,
        byte[] Bytes,
        bool IsBigEndian = false);
}
