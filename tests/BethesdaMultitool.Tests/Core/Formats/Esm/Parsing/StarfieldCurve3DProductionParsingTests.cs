using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.Reflection;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class StarfieldCurve3DProductionParsingTests
{
    [Fact]
    public void ParseAll_DecodesExactEdidReflEnvelopeAndClaimsCur3()
    {
        var parsed = ParseRecords(Cur3(
            0x100,
            ("EDID", NullTermString("WaterCurve")),
            ("REFL", StarfieldCurve3DTestStreamBuilder.Build(
                serializedControlListMarker: 0xA1B2C3D4))));

        var record = Assert.Single(parsed.Curves3D);
        Assert.Equal(0x100u, record.FormId);
        Assert.Equal("WaterCurve", record.EditorId);
        Assert.Null(record.DecodeFailure);
        Assert.NotNull(record.Definition);
        Assert.Equal(0xA1B2C3D4u, record.Definition.XCurve.SerializedControlListMarker);
        Assert.Equal(3, record.Definition.XCurve.Controls.Count);
        Assert.Equal(-2f, record.Definition.XCurve.Controls[0].Input);
        Assert.Equal(4, record.Definition.YCurve.Controls.Count);
        Assert.Equal(2, record.Definition.ZCurve.Controls.Count);
        Assert.DoesNotContain("CUR3", parsed.UnparsedTypeCounts.Keys);
        Assert.Equal(1, parsed.TotalRecordsParsed);
    }

    [Fact]
    public void LoadOrderMerge_LaterCur3FailureReplacesEarlierDefinition()
    {
        const uint formId = 0x200;
        var primary = ParseRecords(Cur3(
            formId,
            ("EDID", NullTermString("BaseCurve")),
            ("REFL", StarfieldCurve3DTestStreamBuilder.Build())));
        var overlay = ParseRecords(Cur3(
            formId,
            ("EDID", NullTermString("BrokenOverride")),
            ("REFL", StarfieldCurve3DTestStreamBuilder.Build(
                layoutMutation: StarfieldCurve3DLayoutMutation.TrailingByte))));

        var merged = primary.MergeWith(overlay);

        var retained = Assert.Single(merged.Curves3D);
        Assert.Same(Assert.Single(overlay.Curves3D), retained);
        Assert.Equal("BrokenOverride", retained.EditorId);
        Assert.Null(retained.Definition);
        Assert.False(string.IsNullOrWhiteSpace(retained.DecodeFailure));
        Assert.Equal(1, merged.TotalRecordsParsed);
    }

    [Fact]
    public void ExactOuterEnvelope_RejectsDuplicateUnknownReorderedXxxxAndMalformedEdid()
    {
        var reflection = StarfieldCurve3DTestStreamBuilder.Build();

        var duplicate = Assert.Single(ParseRecords(Cur3(
            0x301,
            ("EDID", NullTermString("Duplicate")),
            ("EDID", NullTermString("DuplicateAgain")),
            ("REFL", reflection))).Curves3D);
        Assert.Contains("duplicate EDID", duplicate.DecodeFailure,
            StringComparison.OrdinalIgnoreCase);

        var unknown = Assert.Single(ParseRecords(Cur3(
            0x302,
            ("EDID", NullTermString("Unknown")),
            ("FULL", NullTermString("unsupported")),
            ("REFL", reflection))).Curves3D);
        Assert.Contains("unsupported", unknown.DecodeFailure, StringComparison.OrdinalIgnoreCase);

        var reordered = Assert.Single(ParseRecords(Cur3(
            0x303,
            ("REFL", reflection),
            ("EDID", NullTermString("Reordered")))).Curves3D);
        Assert.Contains("exactly EDID+REFL", reordered.DecodeFailure,
            StringComparison.OrdinalIgnoreCase);

        var extended = Assert.Single(ParseRecords(Cur3(
            0x304,
            ("EDID", NullTermString("Extended")),
            ("XXXX", [1, 0, 0, 0]),
            ("REFL", reflection))).Curves3D);
        Assert.Contains("XXXX", extended.DecodeFailure, StringComparison.Ordinal);

        var malformedEditorId = Assert.Single(ParseRecords(Cur3(
            0x305,
            ("EDID", "NoTerminator"u8.ToArray()),
            ("REFL", reflection))).Curves3D);
        Assert.Contains("null-terminated", malformedEditorId.DecodeFailure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(malformedEditorId.Definition);
    }

    [Fact]
    public void AccessorPath_RejectsBigEndianAndPartialRecordsWhileRetainingLeadingIdentity()
    {
        const uint bigEndianFormId = 0x400;
        const uint partialFormId = 0x401;
        var fields = new (string Signature, byte[] Data)[]
        {
            ("EDID", NullTermString("UnsafeCurve")),
            ("REFL", StarfieldCurve3DTestStreamBuilder.Build())
        };
        var bigEndianBytes = BuildRecordBytes(bigEndianFormId, "CUR3", true, fields);
        var bigEndian = Assert.Single(ParseRecords(
            new Fixture("CUR3", bigEndianFormId, bigEndianBytes, true)).Curves3D);
        Assert.Equal("UnsafeCurve", bigEndian.EditorId);
        Assert.True(bigEndian.IsBigEndian);
        Assert.Contains("little-endian", bigEndian.DecodeFailure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(bigEndian.Definition);

        var partialBytes = Cur3(partialFormId, fields).Bytes;
        var scan = new EsmRecordScanResult
        {
            Game = BethesdaGame.Starfield,
            MainRecords = [Descriptor("CUR3", partialFormId, partialBytes, 0, false)]
        };
        var context = new RecordParserContext(
            scan,
            null,
            new ByteArrayMemoryAccessor(partialBytes),
            partialBytes.Length,
            null);
        context.PartiallyRecoveredFormIds.Add(partialFormId);

        var partial = Assert.Single(new MiscEnvironmentHandler(context).ParseStarfieldCurves3D());
        Assert.Equal("UnsafeCurve", partial.EditorId);
        Assert.Contains("partially recovered", partial.DecodeFailure,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(partial.Definition);
    }

    [Fact]
    public void ScanOnlyPath_RetainsFailureEnvelopeAndNonStarfieldHandlerDoesNotClaimCur3()
    {
        var starfieldScan = new EsmRecordScanResult
        {
            Game = BethesdaGame.Starfield,
            MainRecords = [new DetectedMainRecord("CUR3", 64, 0, 0x500, 0, false)]
        };

        var parsed = new RecordParser(starfieldScan).ParseAll();

        Assert.Contains("without record-byte access", Assert.Single(parsed.Curves3D).DecodeFailure,
            StringComparison.OrdinalIgnoreCase);

        var fallout76Scan = new EsmRecordScanResult
        {
            Game = BethesdaGame.Fallout76,
            MainRecords = [new DetectedMainRecord("CUR3", 64, 0, 0x501, 0, false)]
        };
        var handler = new MiscEnvironmentHandler(new RecordParserContext(fallout76Scan));
        Assert.Empty(handler.ParseStarfieldCurves3D());
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

    private static Fixture Cur3(
        uint formId,
        params (string Signature, byte[] Data)[] fields) =>
        new("CUR3", formId, BuildRecordBytes(formId, "CUR3", false, fields));

    private readonly record struct Fixture(
        string RecordType,
        uint FormId,
        byte[] Bytes,
        bool IsBigEndian = false);
}
