using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using Xunit;
using static BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.StarfieldPlanetDataTestData;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class StarfieldPlanetDataDecoderTests
{
    [Fact]
    public void TryDecode_MasterEarthShapedData_DisambiguatesTopAndBodyFields()
    {
        var earth = new StarfieldPlanetWorldspaceEntry(29.7604, -95.3698, 0x0010ABCD);
        var data = Concat(
            Subrecord("EDID", [0x45, 0x61, 0x72, 0x74, 0x68, 0]),
            Subrecord("CNAM", MasterPayload([earth])),
            Subrecord("GNAM", U32(0x3FC00000)),
            MarkerBody(
                ("ZZZZ", [0xCA, 0xFE]),
                ("CNAM", [0x7F]),
                ("GNAM", Identifiers(0x11, 0x22, 0x33)),
                ("INAM", Atmosphere(0x00123456, 1.25f, -2.5f, 0f))));

        var ok = StarfieldPlanetDataDecoder.TryDecode(data, false, out var record, out var error);

        Assert.True(ok, error);
        Assert.Null(record.DecodeFailure);
        Assert.Equal("Earth", record.EditorId);
        Assert.Equal(StarfieldPlanetDataPayloadKind.Master, record.PayloadKind);
        Assert.Equal(earth, Assert.Single(record.MasterWorldspaces));
        Assert.Empty(record.WorldspaceOverrides);
        Assert.True(record.TopLevelGnamRawBits.HasValue);
        Assert.Equal(0x3FC00000u, record.TopLevelGnamRawBits.Value);

        var body = Assert.IsType<StarfieldPlanetBodyData>(record.Body);
        Assert.Equal((byte)0x7F, body.CnamRawValue);
        Assert.Equal(0x11u, body.SystemId);
        Assert.Equal(0x22u, body.ParentPlanetId);
        Assert.Equal(0x33u, body.PlanetId);
        Assert.NotEqual(record.TopLevelGnamRawBits.GetValueOrDefault(), body.SystemId);
        Assert.Equal(0x00123456u, body.Atmosphere.AtmosphereFormId);
        Assert.Equal(1.25f, body.Atmosphere.UnknownFloat0);
        Assert.Equal(-2.5f, body.Atmosphere.UnknownFloat1);
        Assert.Equal(0f, body.Atmosphere.UnknownFloat2);
    }

    [Fact]
    public void TryDecode_Override_RetainsAuthoredOperationOrder()
    {
        var earth = new StarfieldPlanetWorldspaceEntry(0d, 0d, 0x100);
        var mars = new StarfieldPlanetWorldspaceEntry(-4.5d, 137.4d, 0x200);
        StarfieldPlanetWorldspaceDelta[] authored =
        [
            new(earth, StarfieldPlanetWorldspaceOperation.Removed),
            new(mars, StarfieldPlanetWorldspaceOperation.Added),
            new(earth, StarfieldPlanetWorldspaceOperation.Added)
        ];

        var ok = StarfieldPlanetDataDecoder.TryDecode(
            ValidOverrideData(authored), false, out var record, out var error);

        Assert.True(ok, error);
        Assert.Equal(StarfieldPlanetDataPayloadKind.Override, record.PayloadKind);
        Assert.Empty(record.MasterWorldspaces);
        Assert.Equal(authored, record.WorldspaceOverrides);
    }

    [Fact]
    public void TryDecode_PreservesExactDoubleCoordinateBits()
    {
        var signedZero = unchecked((long)0x8000000000000000UL);
        var customNan = unchecked((long)0x7FF8000000001234UL);
        var authored = new StarfieldPlanetWorldspaceEntry(signedZero, customNan, 0x01020304);

        Assert.True(StarfieldPlanetDataDecoder.TryDecode(
            ValidMasterData([authored]), false, out var record, out var error), error);

        var decoded = Assert.Single(record.MasterWorldspaces);
        Assert.Equal(signedZero, decoded.LatitudeRawBits);
        Assert.Equal(customNan, decoded.LongitudeRawBits);
        Assert.Equal(BitConverter.Int64BitsToDouble(signedZero), decoded.Latitude);
        Assert.True(double.IsNaN(decoded.Longitude));
    }

    [Fact]
    public void TryDecode_AcceptsUnmodeledFieldsAndValidExtendedCnamFraming()
    {
        var entry = new StarfieldPlanetWorldspaceEntry(51.5072d, -0.1276d, 0x44556677);
        var data = Concat(
            Subrecord("ZZZZ", [1, 2, 3]),
            ExtendedSubrecord("CNAM", MasterPayload([entry])),
            MarkerBody(
                ("CNAM", [4]),
                ("DATA", [5, 6]),
                ("GNAM", Identifiers(1, 2, 3)),
                ("INAM", Atmosphere(4, 5f, 6f, 7f))));

        Assert.True(StarfieldPlanetDataDecoder.TryDecode(
            data, false, out var record, out var error), error);
        Assert.Equal(entry, Assert.Single(record.MasterWorldspaces));
    }

    [Fact]
    public void TryDecode_AcceptsFramedUnmodeledFieldsAfterBded()
    {
        var entry = new StarfieldPlanetWorldspaceEntry(29.7604d, -95.3698d, 0x0010ABCD);
        var data = Concat(
            Subrecord("CNAM", MasterPayload([entry])),
            ValidBody(),
            Subrecord("TEMP", [1, 2, 3, 4]),
            Subrecord("DENS", U32(5)),
            Subrecord("PHLA", []),
            Subrecord("RSCS", [6, 7]));

        Assert.True(StarfieldPlanetDataDecoder.TryDecode(
            data, false, out var record, out var error), error);
        Assert.Equal(entry, Assert.Single(record.MasterWorldspaces));
    }

    [Theory]
    [MemberData(nameof(KnownTopLevelFieldsAfterBody))]
    public void TryDecode_RejectsKnownTopLevelFieldsAfterBded(
        byte[] data,
        string signature)
    {
        Assert.False(StarfieldPlanetDataDecoder.TryDecode(
            data, false, out var record, out var error));
        Assert.Equal(error, record.DecodeFailure);
        Assert.Contains(signature, error, StringComparison.Ordinal);
        Assert.Contains("only before BDST", error, StringComparison.Ordinal);
        Assert.Null(record.Body);
    }

    public static IEnumerable<object[]> KnownTopLevelFieldsAfterBody()
    {
        var entry = new StarfieldPlanetWorldspaceEntry(29.7604d, -95.3698d, 0x0010ABCD);
        yield return
        [
            Concat(ValidBody(), Subrecord("CNAM", MasterPayload([entry]))),
            "CNAM"
        ];
        yield return
        [
            Concat(ValidBody(), Subrecord("EOVR", OverridePayload([]))),
            "EOVR"
        ];
        yield return
        [
            Concat(
                Subrecord("CNAM", MasterPayload([entry])),
                ValidBody(),
                Subrecord("GNAM", U32(0x3FC00000))),
            "GNAM"
        ];
        yield return
        [
            Concat(
                Subrecord("CNAM", MasterPayload([entry])),
                ValidBody(),
                Subrecord("EDID", [0x4C, 0x61, 0x74, 0x65, 0])),
            "EDID"
        ];
    }

    [Theory]
    [MemberData(nameof(KnownScopeAndLengthFailures))]
    public void TryDecode_RejectsKnownFieldsAtWrongScopeOrLength(
        byte[] data,
        string expectedError)
    {
        Assert.False(StarfieldPlanetDataDecoder.TryDecode(
            data, false, out var record, out var error));
        Assert.Equal(error, record.DecodeFailure);
        Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(record.Body);
    }

    public static IEnumerable<object[]> KnownScopeAndLengthFailures()
    {
        var entry = new StarfieldPlanetWorldspaceEntry(1d, 2d, 3);
        yield return
        [
            Concat(Subrecord("CNAM", [1]), ValidBody()),
            "multiple of 20"
        ];
        yield return
        [
            Concat(
                Subrecord("CNAM", MasterPayload([entry])),
                MarkerBody(
                    ("CNAM", MasterPayload([entry])),
                    ("GNAM", Identifiers(1, 2, 3)),
                    ("INAM", Atmosphere(4, 5f, 6f, 7f)))),
            "body CNAM"
        ];
        yield return
        [
            Concat(
                Subrecord("CNAM", MasterPayload([entry])),
                Subrecord("GNAM", Identifiers(1, 2, 3)),
                ValidBody()),
            "top-level GNAM"
        ];
        yield return
        [
            Concat(
                Subrecord("CNAM", MasterPayload([entry])),
                MarkerBody(
                    ("CNAM", [1]),
                    ("GNAM", U32(2)),
                    ("INAM", Atmosphere(4, 5f, 6f, 7f)))),
            "body GNAM"
        ];
        yield return
        [
            Concat(
                Subrecord("CNAM", MasterPayload([entry])),
                Subrecord("INAM", Atmosphere(4, 5f, 6f, 7f)),
                ValidBody()),
            "only inside"
        ];
        yield return
        [
            Concat(
                Subrecord("CNAM", MasterPayload([entry])),
                MarkerBody(
                    ("CNAM", [1]),
                    ("EOVR", OverridePayload([])),
                    ("GNAM", Identifiers(1, 2, 3)),
                    ("INAM", Atmosphere(4, 5f, 6f, 7f)))),
            "not valid inside"
        ];
        yield return
        [
            Concat(Subrecord("EOVR", new byte[20]), ValidBody()),
            "multiple of 21"
        ];
        yield return
        [
            Concat(
                Subrecord("CNAM", MasterPayload([entry])),
                MarkerBody(
                    ("CNAM", [1]),
                    ("GNAM", Identifiers(1, 2, 3)),
                    ("INAM", new byte[15]))),
            "body INAM"
        ];
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TryDecode_RejectsEveryNonFiniteInamFloat(int invalidIndex)
    {
        var values = new[] { 1f, 2f, 3f };
        values[invalidIndex] = invalidIndex switch
        {
            0 => float.NaN,
            1 => float.PositiveInfinity,
            _ => float.NegativeInfinity
        };

        Assert.False(StarfieldPlanetDataDecoder.TryDecode(
            ValidMasterData(
                [new StarfieldPlanetWorldspaceEntry(1d, 2d, 3)],
                unknownFloat0: values[0],
                unknownFloat1: values[1],
                unknownFloat2: values[2]),
            false,
            out var record,
            out var error));
        Assert.Contains("non-finite", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(error, record.DecodeFailure);
    }

    [Fact]
    public void TryDecode_RejectsUnknownOverrideOperation()
    {
        var payload = OverridePayload(
        [
            new StarfieldPlanetWorldspaceDelta(
                new StarfieldPlanetWorldspaceEntry(1d, 2d, 3),
                StarfieldPlanetWorldspaceOperation.Added)
        ]);
        payload[20] = 2;

        Assert.False(StarfieldPlanetDataDecoder.TryDecode(
            Concat(Subrecord("EOVR", payload), ValidBody()),
            false,
            out var record,
            out var error));
        Assert.Contains("unknown operation 2", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(error, record.DecodeFailure);
    }

    [Theory]
    [MemberData(nameof(DuplicateFieldFailures))]
    public void TryDecode_RejectsDuplicateOrMixedProvenFields(byte[] data, string expectedError)
    {
        Assert.False(StarfieldPlanetDataDecoder.TryDecode(
            data, false, out var record, out var error));
        Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(error, record.DecodeFailure);
    }

    public static IEnumerable<object[]> DuplicateFieldFailures()
    {
        var entry = new StarfieldPlanetWorldspaceEntry(1d, 2d, 3);
        var master = Subrecord("CNAM", MasterPayload([entry]));
        var eovr = Subrecord("EOVR", OverridePayload([]));
        yield return [Concat(master, master, ValidBody()), "duplicate or mixed"];
        yield return [Concat(master, eovr, ValidBody()), "duplicate or mixed"];
        yield return
        [
            Concat(master, Subrecord("GNAM", U32(1)), Subrecord("GNAM", U32(2)), ValidBody()),
            "top-level GNAM"
        ];
        yield return
        [
            Concat(
                Subrecord("EDID", [0x4F, 0x6E, 0x65, 0]),
                Subrecord("EDID", [0x54, 0x77, 0x6F, 0]),
                master,
                ValidBody()),
            "duplicate EDID"
        ];
        yield return
        [
            Concat(master, MarkerBody(
                ("CNAM", [1]),
                ("CNAM", [2]),
                ("GNAM", Identifiers(1, 2, 3)),
                ("INAM", Atmosphere(4, 5f, 6f, 7f)))),
            "body CNAM"
        ];
        yield return
        [
            Concat(master, MarkerBody(
                ("CNAM", [1]),
                ("GNAM", Identifiers(1, 2, 3)),
                ("GNAM", Identifiers(4, 5, 6)),
                ("INAM", Atmosphere(4, 5f, 6f, 7f)))),
            "body GNAM"
        ];
        yield return
        [
            Concat(master, MarkerBody(
                ("CNAM", [1]),
                ("GNAM", Identifiers(1, 2, 3)),
                ("INAM", Atmosphere(4, 5f, 6f, 7f)),
                ("INAM", Atmosphere(8, 9f, 10f, 11f)))),
            "body INAM"
        ];
    }

    [Theory]
    [MemberData(nameof(MarkerFailures))]
    public void TryDecode_RejectsMalformedMarkerState(byte[] data, string expectedError)
    {
        Assert.False(StarfieldPlanetDataDecoder.TryDecode(
            data, false, out var record, out var error));
        Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(error, record.DecodeFailure);
    }

    public static IEnumerable<object[]> MarkerFailures()
    {
        var master = Subrecord("CNAM", MasterPayload(
            [new StarfieldPlanetWorldspaceEntry(1d, 2d, 3)]));
        yield return [Concat(master, Subrecord("BDED", [])), "unmatched"];
        yield return [Concat(master, Subrecord("BDST", [1])), "zero length"];
        yield return
        [
            Concat(master, Subrecord("BDST", []), Subrecord("BDST", [])),
            "nested or duplicate"
        ];
        yield return
        [
            Concat(master, Subrecord("BDST", []),
                Subrecord("CNAM", [1]),
                Subrecord("GNAM", Identifiers(1, 2, 3)),
                Subrecord("INAM", Atmosphere(4, 5f, 6f, 7f))),
            "missing its BDED"
        ];
        yield return [Concat(master, ValidBody(), Subrecord("BDED", [])), "unmatched"];
        yield return [Concat(master, ValidBody(), Subrecord("BDST", [])), "nested or duplicate"];
        yield return
        [
            Concat(master, Subrecord("BDST", []), Subrecord("BDED", [1])),
            "zero length"
        ];
    }

    [Theory]
    [MemberData(nameof(MissingBodyFailures))]
    public void TryDecode_RejectsMissingRequiredBodyMembers(byte[] data, string expectedError)
    {
        Assert.False(StarfieldPlanetDataDecoder.TryDecode(
            data, false, out var record, out var error));
        Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(error, record.DecodeFailure);
    }

    public static IEnumerable<object[]> MissingBodyFailures()
    {
        var master = Subrecord("CNAM", MasterPayload(
            [new StarfieldPlanetWorldspaceEntry(1d, 2d, 3)]));
        yield return [master, "missing its BDST/BDED"];
        yield return
        [
            Concat(master, MarkerBody(
                ("GNAM", Identifiers(1, 2, 3)),
                ("INAM", Atmosphere(4, 5f, 6f, 7f)))),
            "requires one CNAM"
        ];
        yield return
        [
            Concat(master, MarkerBody(
                ("CNAM", [1]),
                ("INAM", Atmosphere(4, 5f, 6f, 7f)))),
            "requires one CNAM"
        ];
        yield return
        [
            Concat(master, MarkerBody(
                ("CNAM", [1]),
                ("GNAM", Identifiers(1, 2, 3)))),
            "requires one CNAM"
        ];
        yield return [ValidBody(), "top-level CNAM or EOVR"];
    }

    [Fact]
    public void TryDecode_RejectsMalformedSubrecordFramingAndBigEndian()
    {
        AssertFailure([0x01, 0x02], false, "truncated subrecord header");
        AssertFailure(Header("CNAM", 20), false, "overruns");
        AssertFailure(Subrecord("XXXX", U32(20)), false, "unresolved XXXX");
        AssertFailure(
            Concat(Subrecord("XXXX", U32(20)), Subrecord("XXXX", U32(20))),
            false,
            "consecutive XXXX");
        AssertFailure(
            Concat(
                Subrecord("XXXX", U32(20)),
                Subrecord("CNAM", MasterPayload(
                    [new StarfieldPlanetWorldspaceEntry(1d, 2d, 3)]))),
            false,
            "nonzero short length");

        var nonAscii = Header("CNAM", 0);
        nonAscii[0] = 0xFF;
        AssertFailure(nonAscii, false, "non-ASCII");
        AssertFailure(
            ValidMasterData([new StarfieldPlanetWorldspaceEntry(1d, 2d, 3)]),
            true,
            "little-endian");
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 0x41 })]
    [InlineData(new byte[] { 0x41, 0, 0 })]
    [InlineData(new byte[] { 0xFF, 0 })]
    public void TryDecode_RejectsMalformedEdid(byte[] edid)
    {
        var entry = new StarfieldPlanetWorldspaceEntry(1d, 2d, 3);
        AssertFailure(
            Concat(
                Subrecord("EDID", edid),
                Subrecord("CNAM", MasterPayload([entry])),
                ValidBody()),
            false,
            "EDID");
    }

    [Fact]
    public void TryDecode_FailureEnvelopeRetainsOnlySuccessfullyParsedLeadingEdid()
    {
        var entry = new StarfieldPlanetWorldspaceEntry(1d, 2d, 3);
        var malformedAfterEdid = Concat(
            Subrecord("EDID", [.. Encoding.ASCII.GetBytes("BrokenPlanet\0")]),
            Subrecord("CNAM", [1, 2, 3]));

        Assert.False(StarfieldPlanetDataDecoder.TryDecode(
            malformedAfterEdid, false, out var identifiedFailure, out var identifiedError));
        Assert.Contains("multiple of 20", identifiedError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("BrokenPlanet", identifiedFailure.EditorId);
        Assert.Equal(identifiedError, identifiedFailure.DecodeFailure);

        var malformedBeforeEdid = Concat(
            Subrecord("CNAM", [1, 2, 3]),
            Subrecord("EDID", [.. Encoding.ASCII.GetBytes("NeverReached\0")]));
        Assert.False(StarfieldPlanetDataDecoder.TryDecode(
            malformedBeforeEdid, false, out var unidentifiedFailure, out _));
        Assert.Null(unidentifiedFailure.EditorId);

        Assert.False(StarfieldPlanetDataDecoder.TryDecode(
            Concat(
                Subrecord("EDID", [.. Encoding.ASCII.GetBytes("BigEndianIdentity\0")]),
                Subrecord("CNAM", MasterPayload([entry])),
                ValidBody()),
            true,
            out var bigEndianFailure,
            out _));
        Assert.Null(bigEndianFailure.EditorId);
    }

    private static void AssertFailure(byte[] data, bool isBigEndian, string expectedError)
    {
        Assert.False(StarfieldPlanetDataDecoder.TryDecode(
            data, isBigEndian, out var record, out var error));
        Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(error, record.DecodeFailure);
        Assert.Null(record.Body);
    }
}
