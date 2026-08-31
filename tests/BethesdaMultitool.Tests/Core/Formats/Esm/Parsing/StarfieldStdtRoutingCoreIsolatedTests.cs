using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class StarfieldStdtRoutingCoreIsolatedTests
{
    [Fact]
    public void Decoder_PreservesAuthoredZeroAndOmittedFields()
    {
        var data = Concat(
            Subrecord("EDID", NullTerminated("SolStar")),
            Subrecord("DNAM", U32(0)),
            Subrecord("ENAM", U32(0xDEADBEEF)),
            Subrecord("PNAM", U32(0x00134249)));

        Assert.True(StarfieldStarDataDecoder.TryDecode(
            data, false, out var record, out var error), error);

        Assert.Null(error);
        Assert.Equal("SolStar", record.EditorId);
        Assert.Null(record.DecodeFailure);
        var routing = Assert.IsType<StarfieldStarDataRouting>(record.Routing);
        Assert.True(routing.SystemId.HasValue);
        Assert.Equal(0u, routing.SystemId.Value);
        Assert.Null(routing.BinaryStarFormId);
        Assert.Equal(0x00134249u, routing.SunPresetFormId);
        Assert.Null(routing.TimeOfDayDataFormId);
    }

    [Fact]
    public void Decoder_PreservesExplicitZeroForEveryFormIdField()
    {
        var data = Concat(
            Subrecord("DNAM", U32(7)),
            Subrecord("SNAM", U32(0)),
            Subrecord("PNAM", U32(0)),
            Subrecord("HNAM", U32(0)));

        Assert.True(StarfieldStarDataDecoder.TryDecode(
            data, false, out var record, out var error), error);

        var routing = Assert.IsType<StarfieldStarDataRouting>(record.Routing);
        Assert.Equal(7u, routing.SystemId);
        Assert.True(routing.BinaryStarFormId.HasValue);
        Assert.True(routing.SunPresetFormId.HasValue);
        Assert.True(routing.TimeOfDayDataFormId.HasValue);
        Assert.Equal(0u, routing.BinaryStarFormId.Value);
        Assert.Equal(0u, routing.SunPresetFormId.Value);
        Assert.Equal(0u, routing.TimeOfDayDataFormId.Value);
    }

    [Fact]
    public void Decoder_AllowsMissingRoutingFieldsWithoutFabricatingZero()
    {
        var data = Concat(
            Subrecord("EDID", NullTerminated("_Test4Star")),
            Subrecord("ENAM", U32(1)));

        Assert.True(StarfieldStarDataDecoder.TryDecode(
            data, false, out var record, out var error), error);

        var routing = Assert.IsType<StarfieldStarDataRouting>(record.Routing);
        Assert.Null(routing.SystemId);
        Assert.Null(routing.BinaryStarFormId);
        Assert.Null(routing.SunPresetFormId);
        Assert.Null(routing.TimeOfDayDataFormId);
    }

    [Fact]
    public void Decoder_AllowsLargeOpaqueExtendedSubrecord()
    {
        var opaque = new byte[70_000];
        opaque[0] = 0x12;
        opaque[^1] = 0x34;
        var data = Concat(
            ExtendedSubrecord("PCCC", opaque),
            Subrecord("DNAM", U32(0x1234)),
            Subrecord("PNAM", U32(0x5678)));

        Assert.True(StarfieldStarDataDecoder.TryDecode(
            data, false, out var record, out var error), error);

        var routing = Assert.IsType<StarfieldStarDataRouting>(record.Routing);
        Assert.Equal(0x1234u, routing.SystemId);
        Assert.Equal(0x5678u, routing.SunPresetFormId);
    }

    [Theory]
    [InlineData("DNAM")]
    [InlineData("SNAM")]
    [InlineData("PNAM")]
    [InlineData("HNAM")]
    public void Decoder_RejectsDuplicateEstablishedUInt32Fields(string signature)
    {
        var data = Concat(
            Subrecord(signature, U32(0)),
            Subrecord(signature, U32(1)));

        AssertFailure(data, false, $"duplicate {signature}");
    }

    [Theory]
    [InlineData("DNAM", 0)]
    [InlineData("DNAM", 3)]
    [InlineData("DNAM", 5)]
    [InlineData("SNAM", 1)]
    [InlineData("PNAM", 8)]
    [InlineData("HNAM", 2)]
    public void Decoder_RejectsUnprovenEstablishedFieldWidths(string signature, int length)
    {
        AssertFailure(Subrecord(signature, new byte[length]), false, "exactly four bytes");
    }

    [Fact]
    public void Decoder_RejectsDuplicateOrMalformedEditorId()
    {
        AssertFailure(
            Concat(
                Subrecord("EDID", NullTerminated("One")),
                Subrecord("EDID", NullTerminated("Two"))),
            false,
            "duplicate EDID");
        AssertFailure(Subrecord("EDID", Encoding.ASCII.GetBytes("NoTerminator")), false, "null-terminated");
        AssertFailure(Subrecord("EDID", [0]), false, "null-terminated");
        AssertFailure(Subrecord("EDID", [.. Encoding.ASCII.GetBytes("A"), 0, (byte)'B', 0]), false, "null-terminated");
    }

    [Fact]
    public void Decoder_RejectsMalformedFramingAndBigEndian()
    {
        AssertFailure([1, 2], false, "truncated subrecord header");
        AssertFailure(Header("DNAM", 4), false, "overruns");
        AssertFailure(Subrecord("XXXX", [1, 2, 3]), false, "malformed XXXX");
        AssertFailure(Subrecord("XXXX", U32(4)), false, "unresolved XXXX");
        AssertFailure(
            Concat(Subrecord("XXXX", U32(4)), Subrecord("XXXX", U32(4))),
            false,
            "consecutive XXXX");
        AssertFailure(
            Concat(Subrecord("XXXX", U32(4)), Subrecord("DNAM", U32(1))),
            false,
            "nonzero short length");

        var nonAscii = Header("DNAM", 0);
        nonAscii[0] = 0xFF;
        AssertFailure(nonAscii, false, "non-ASCII");
        AssertFailure(Subrecord("DNAM", U32(0)), true, "little-endian");
    }

    [Fact]
    public void Index_RetainsSystemAndFormIdAmbiguityAndSnapshotsInput()
    {
        var first = Record(0x20, 9);
        var second = Record(0x10, 9);
        var duplicateFormId = Record(0x10, 10);
        var missingSystem = new StarfieldStarDataRecord
        {
            FormId = 0x30,
            Routing = new StarfieldStarDataRouting()
        };
        var source = new List<StarfieldStarDataRecord>
        {
            first, second, duplicateFormId, missingSystem
        };

        var index = StarfieldStarDataIndex.Build(source);
        source.Clear();

        Assert.Equal(4, index.Records.Count);
        Assert.Equal([0x20u, 0x10u], index.RecordsBySystemId[9].Select(record => record.FormId));
        Assert.Equal(2, index.RecordsByFormId[0x10].Count);
        Assert.Same(missingSystem, Assert.Single(index.RecordsWithoutSystemId));
    }

    [Fact]
    public void Resolver_ReportsEveryAmbiguousSystemCandidate()
    {
        var index = StarfieldStarDataIndex.Build(
            [Record(0x20, 9), Record(0x10, 9)]);

        var result = StarfieldStarDataResolver.ResolveSystem(9, index);

        Assert.False(result.IsResolved);
        Assert.Equal(StarfieldStarDataResolutionStatus.AmbiguousSystem, result.Status);
        Assert.Equal([0x20u, 0x10u], result.ConflictingFormIds);
        Assert.Null(result.Primary);
        Assert.Contains("2 STDT", result.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_IndexesAuthoredZeroSystemAndAllowsMissingSunOrTodd()
    {
        var sol = Record(0x0005E5CB, 0, sunPresetFormId: null);
        var index = StarfieldStarDataIndex.Build([sol]);

        var result = StarfieldStarDataResolver.ResolveSystem(0, index);

        Assert.True(result.IsResolved, result.FailureDetail);
        Assert.Same(sol, result.Primary);
        Assert.Null(result.BinaryStar);
        Assert.Empty(result.ConflictingFormIds);
        Assert.Null(result.Primary!.Routing!.SunPresetFormId);
        Assert.Null(result.Primary.Routing.TimeOfDayDataFormId);
    }

    [Fact]
    public void Resolver_FollowsNonzeroBinaryStarFormIdOnce()
    {
        var primary = Record(0x100, 7, binaryStarFormId: 0x200, sunPresetFormId: 0x300);
        var binary = Record(0x200, 8, sunPresetFormId: 0x301);
        var index = StarfieldStarDataIndex.Build([primary, binary]);

        var result = StarfieldStarDataResolver.ResolveSystem(7, index);

        Assert.True(result.IsResolved, result.FailureDetail);
        Assert.Same(primary, result.Primary);
        Assert.Same(binary, result.BinaryStar);
        Assert.Equal(0x300u, result.Primary!.Routing!.SunPresetFormId);
        Assert.Equal(0x301u, result.BinaryStar!.Routing!.SunPresetFormId);
    }

    [Fact]
    public void Resolver_FailsClosedForMissingOrAmbiguousBinaryStar()
    {
        var missing = StarfieldStarDataResolver.ResolveSystem(
            7,
            StarfieldStarDataIndex.Build([Record(0x100, 7, binaryStarFormId: 0x200)]));

        Assert.Equal(StarfieldStarDataResolutionStatus.BinaryStarNotFound, missing.Status);
        Assert.Equal(0x200u, missing.FailureFormId);
        Assert.NotNull(missing.Primary);

        var ambiguous = StarfieldStarDataResolver.ResolveSystem(
            7,
            StarfieldStarDataIndex.Build(
            [
                Record(0x100, 7, binaryStarFormId: 0x200),
                Record(0x200, 8),
                Record(0x200, 9)
            ]));

        Assert.Equal(StarfieldStarDataResolutionStatus.AmbiguousBinaryStar, ambiguous.Status);
        Assert.Equal([0x200u, 0x200u], ambiguous.ConflictingFormIds);
    }

    [Fact]
    public void Resolver_FailsClosedForDecodeFailureOnSelectedRecord()
    {
        var malformed = Record(0x100, 7) with { DecodeFailure = "synthetic malformed STDT" };
        var result = StarfieldStarDataResolver.ResolveSystem(
            7, StarfieldStarDataIndex.Build([malformed]));

        Assert.False(result.IsResolved);
        Assert.Equal(StarfieldStarDataResolutionStatus.PrimaryDecodeFailure, result.Status);
        Assert.Equal(0x100u, result.FailureFormId);
        Assert.Contains("malformed", result.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_ReportsMissingSystemWithoutSelectingAnotherRecord()
    {
        var result = StarfieldStarDataResolver.ResolveSystem(
            99, StarfieldStarDataIndex.Build([Record(0x100, 7)]));

        Assert.False(result.IsResolved);
        Assert.Equal(StarfieldStarDataResolutionStatus.SystemNotFound, result.Status);
        Assert.Empty(result.ConflictingFormIds);
        Assert.Null(result.Primary);
    }

    [Fact]
    public void Rebaser_MapsOnlyNonzeroFormIdsAndNeverMapsScalarSystemId()
    {
        var source = new StarfieldStarDataRecord
        {
            FormId = 0x10,
            EditorId = "Synthetic",
            Offset = 123,
            Routing = new StarfieldStarDataRouting
            {
                SystemId = 0xDEADBEEF,
                BinaryStarFormId = 0x20,
                SunPresetFormId = 0x30,
                TimeOfDayDataFormId = 0x40
            }
        };
        var mappedValues = new List<uint>();

        var rebased = StarfieldStarDataFormIdRebaser.Rebase(source, value =>
        {
            mappedValues.Add(value);
            return value + 0x1000;
        });

        Assert.Equal([0x10u, 0x20u, 0x30u, 0x40u], mappedValues);
        Assert.Equal(0x1010u, rebased.FormId);
        Assert.Equal(0xDEADBEEFu, rebased.Routing!.SystemId);
        Assert.Equal(0x1020u, rebased.Routing.BinaryStarFormId);
        Assert.Equal(0x1030u, rebased.Routing.SunPresetFormId);
        Assert.Equal(0x1040u, rebased.Routing.TimeOfDayDataFormId);
        Assert.Equal(0x10u, source.FormId);
        Assert.Equal(0x30u, source.Routing!.SunPresetFormId);
        Assert.Equal(source.EditorId, rebased.EditorId);
        Assert.Equal(source.Offset, rebased.Offset);
    }

    [Fact]
    public void Rebaser_PreservesMissingAndAuthoredZeroWithoutInvokingMapper()
    {
        var source = new StarfieldStarDataRecord
        {
            FormId = 0,
            Routing = new StarfieldStarDataRouting
            {
                SystemId = 0,
                BinaryStarFormId = 0,
                SunPresetFormId = null,
                TimeOfDayDataFormId = 0
            }
        };

        var rebased = StarfieldStarDataFormIdRebaser.Rebase(
            source,
            _ => throw new InvalidOperationException("Zero/null/scalar values must not be mapped."));

        Assert.Equal(0u, rebased.FormId);
        Assert.True(rebased.Routing!.SystemId.HasValue);
        Assert.Equal(0u, rebased.Routing.SystemId.Value);
        Assert.True(rebased.Routing.BinaryStarFormId.HasValue);
        Assert.Equal(0u, rebased.Routing.BinaryStarFormId.Value);
        Assert.Null(rebased.Routing.SunPresetFormId);
        Assert.True(rebased.Routing.TimeOfDayDataFormId.HasValue);
        Assert.Equal(0u, rebased.Routing.TimeOfDayDataFormId.Value);
    }

    private static StarfieldStarDataRecord Record(
        uint formId,
        uint systemId,
        uint? binaryStarFormId = null,
        uint? sunPresetFormId = null,
        uint? timeOfDayDataFormId = null)
    {
        return new StarfieldStarDataRecord
        {
            FormId = formId,
            Routing = new StarfieldStarDataRouting
            {
                SystemId = systemId,
                BinaryStarFormId = binaryStarFormId,
                SunPresetFormId = sunPresetFormId,
                TimeOfDayDataFormId = timeOfDayDataFormId
            }
        };
    }

    private static void AssertFailure(
        byte[] data,
        bool isBigEndian,
        string expectedError)
    {
        Assert.False(StarfieldStarDataDecoder.TryDecode(
            data, isBigEndian, out var record, out var error));
        Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(error, record.DecodeFailure);
        Assert.Null(record.Routing);
        Assert.Equal(isBigEndian, record.IsBigEndian);
    }

    private static byte[] NullTerminated(string value) =>
        [.. Encoding.ASCII.GetBytes(value), 0];

    private static byte[] U32(uint value)
    {
        var result = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(result, value);
        return result;
    }

    private static byte[] Subrecord(string signature, byte[] payload)
    {
        if (signature.Length != 4) throw new ArgumentException("Signature must be four characters.", nameof(signature));
        if (payload.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(payload));

        var result = new byte[6 + payload.Length];
        Encoding.ASCII.GetBytes(signature, result);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), checked((ushort)payload.Length));
        payload.CopyTo(result, 6);
        return result;
    }

    private static byte[] ExtendedSubrecord(string signature, byte[] payload)
    {
        return Concat(
            Subrecord("XXXX", U32(checked((uint)payload.Length))),
            Header(signature, 0),
            payload);
    }

    private static byte[] Header(string signature, ushort declaredLength)
    {
        var result = new byte[6];
        Encoding.ASCII.GetBytes(signature, result);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), declaredLength);
        return result;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
