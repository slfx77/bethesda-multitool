using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class Fallout76VolumetricLightingParsingTests
{
    private const uint FormId = 0x0100_1234;

    [Fact]
    public void ClassicRetailSchema_DecodesEveryFloatWithoutClamping()
    {
        var parsed = ParseVoli(BuildValidVoli(includeLnam: false));

        Assert.Equal(FormId, parsed.FormId);
        Assert.Equal("VOLI_Test", parsed.EditorId);
        Assert.Null(parsed.DecodeFailure);
        Assert.False(parsed.IsBigEndian);
        var settings = Assert.IsType<Fallout76VolumetricLightingSettings>(parsed.Settings);
        Assert.Equal(100_000f, settings.Intensity);
        Assert.Equal(0.75f, settings.CustomColorContribution);
        Assert.Equal(0.1f, settings.ColorRed);
        Assert.Equal(0.2f, settings.ColorGreen);
        Assert.Equal(0.3f, settings.ColorBlue);
        Assert.Equal(0.7f, settings.DensityContribution);
        Assert.Equal(1f, settings.DensitySize);
        Assert.Equal(20f, settings.DensityWindSpeed);
        Assert.Equal(0.6f, settings.DensityFallingSpeed);
        Assert.Null(settings.PhaseFunctionContribution);
        Assert.Equal(0.995f, settings.PhaseFunctionScattering);
        Assert.Equal(50f, settings.SamplingRepartitionRangeFactor);
    }

    [Fact]
    public void OptionalLnam_InItsProvenSchemaPosition_IsPreserved()
    {
        var parsed = ParseVoli(BuildValidVoli(includeLnam: true));

        Assert.Null(parsed.DecodeFailure);
        Assert.Equal(0.42f, parsed.Settings!.PhaseFunctionContribution);
    }

    [Fact]
    public void RecordParser_PublishesFo76VoliOnlyToItsGameScopedCollection()
    {
        var bytes = BuildValidVoli(includeLnam: false);
        var descriptor = Descriptor(bytes, isBigEndian: false);
        var fixture = new ParserFixture(bytes, descriptor, BethesdaGame.Fallout76);

        var parsed = fixture.Parser.ParseAll();

        Assert.Single(parsed.Fallout76VolumetricLightingSettings);
        Assert.Empty(parsed.VolumetricLightingSettings);
        Assert.DoesNotContain("VOLI", parsed.UnparsedTypeCounts.Keys);
    }

    [Theory]
    [MemberData(nameof(MalformedClassicSchemas))]
    public void MalformedClassicSchema_FailsClosedAndPreservesSourceEnvelope(byte[] bytes, string expectedFailure)
    {
        var parsed = ParseVoli(bytes);

        Assert.Equal(FormId, parsed.FormId);
        Assert.Equal("VOLI_Test", parsed.EditorId);
        Assert.Null(parsed.Settings);
        Assert.Contains(expectedFailure, parsed.DecodeFailure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, parsed.Offset);
    }

    public static IEnumerable<object[]> MalformedClassicSchemas()
    {
        yield return
        [
            BuildVoli([
                ("EDID", NullTermString("VOLI_Test")),
                ("CNAM", Float(1f)),
                ("CNAM", Float(2f)),
                .. NumericTailAfterCnam()]),
            "expected DNAM"
        ];

        yield return
        [
            BuildVoli(
                ("EDID", NullTermString("VOLI_Test")),
                ("CNAM", Float(1f)),
                ("DNAM", Float(2f)),
                ("ENAM", Float(3f)),
                ("XXXX", new byte[4]),
                ("FNAM", Float(4f)),
                ("GNAM", Float(5f)),
                ("HNAM", Float(6f)),
                ("INAM", Float(7f)),
                ("JNAM", Float(8f)),
                ("KNAM", Float(9f)),
                ("MNAM", Float(10f)),
                ("NNAM", Float(11f))),
            "XXXX"
        ];

        yield return
        [
            BuildVoli([
                ("EDID", NullTermString("VOLI_Test")),
                ("CNAM", new byte[8]),
                .. NumericTailAfterCnam()]),
            "exactly four bytes"
        ];

        var nonFinite = ValidFields(includeLnam: false).ToArray();
        nonFinite[^1] = ("NNAM", Float(float.PositiveInfinity));
        yield return [BuildVoli(nonFinite), "non-finite"];

        var reordered = ValidFields(includeLnam: false).ToArray();
        (reordered[3], reordered[4]) = (reordered[4], reordered[3]);
        yield return [BuildVoli(reordered), "expected ENAM"];

        var unknown = ValidFields(includeLnam: false).ToArray();
        unknown[3] = ("XTRA", Float(0.1f));
        yield return [BuildVoli(unknown), "found XTRA"];

        var truncated = BuildValidVoli(includeLnam: false);
        Array.Resize(ref truncated, truncated.Length - 1);
        yield return [truncated, "extends past"];
    }

    [Fact]
    public void BigEndianClassicRecord_FailsClosed()
    {
        var fields = ValidFields(includeLnam: false).ToArray();
        var bytes = BuildRecordBytes(FormId, "VOLI", true, fields);
        var parsed = ParseVoli(bytes, isBigEndian: true);

        Assert.Equal("VOLI_Test", parsed.EditorId);
        Assert.Null(parsed.Settings);
        Assert.True(parsed.IsBigEndian);
        Assert.Contains("little-endian", parsed.DecodeFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveredOrNonContiguousClassicRecord_FailsClosed(bool partiallyRecovered)
    {
        var bytes = BuildValidVoli(includeLnam: false);
        var descriptor = Descriptor(bytes, isBigEndian: false);
        var context = Context(bytes, descriptor, BethesdaGame.Fallout76);
        if (partiallyRecovered)
        {
            context.PartiallyRecoveredFormIds.Add(FormId);
        }
        else
        {
            context.NonContiguousRecordFormIds.Add(FormId);
        }

        var parsed = Assert.Single(new MiscEnvironmentHandler(context)
            .ParseFallout76VolumetricLightingSettings());

        Assert.Null(parsed.Settings);
        Assert.Contains("non-contiguous or partially recovered", parsed.DecodeFailure);
    }

    [Fact]
    public void SameSignatureInStarfield_IsNotClaimedByClassicParser()
    {
        var bytes = BuildValidVoli(includeLnam: false);
        var descriptor = Descriptor(bytes, isBigEndian: false);
        var context = Context(bytes, descriptor, BethesdaGame.Starfield);

        Assert.Empty(new MiscEnvironmentHandler(context).ParseFallout76VolumetricLightingSettings());
    }

    [Fact]
    public void ScanOnlyFo76Voli_PreservesFailureEnvelopeInsteadOfInventingSettings()
    {
        var descriptor = new DetectedMainRecord("VOLI", 123, 0, FormId, 0x4567, false);
        var context = new RecordParserContext(
            new EsmRecordScanResult
            {
                Game = BethesdaGame.Fallout76,
                MainRecords = [descriptor]
            },
            null,
            // Cast: RecordParserContext overloads on MemoryMappedViewAccessor? and IMemoryAccessor?,
            // so a bare null is ambiguous. This is the scan-only path — no accessor either way.
            (IMemoryAccessor?)null,
            0,
            null);

        var parsed = Assert.Single(new MiscEnvironmentHandler(context)
            .ParseFallout76VolumetricLightingSettings());

        Assert.Equal(FormId, parsed.FormId);
        Assert.Equal(0x4567, parsed.Offset);
        Assert.Null(parsed.Settings);
        Assert.Contains("unavailable", parsed.DecodeFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fo76WeatherHnam_PreservesAllEightTimeBandReferences()
    {
        var refs = new uint[]
        {
            0x0100_0001, 0x0100_0002, 0x0100_0003, 0x0100_0004,
            0x0100_0005, 0, 0x0100_0007, 0x0100_0008
        };
        var hnam = new byte[32];
        for (var index = 0; index < refs.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(hnam.AsSpan(index * sizeof(uint)), refs[index]);
        }

        var weather = ParseWeather(BethesdaGame.Fallout76,
            ("EDID", NullTermString("WTHR_Test")),
            ("HNAM", hnam));

        var bands = Assert.IsType<WeatherTimeBands<uint>>(weather.VolumetricLightingFormIds);
        Assert.Equal(refs[0], bands.Sunrise);
        Assert.Equal(refs[1], bands.Day);
        Assert.Equal(refs[2], bands.Sunset);
        Assert.Equal(refs[3], bands.Night);
        Assert.Equal(refs[4], bands.EarlySunrise);
        Assert.Equal(0u, bands.LateSunrise);
        Assert.Equal(refs[6], bands.EarlySunset);
        Assert.Equal(refs[7], bands.LateSunset);
        Assert.Null(weather.Hdr);
    }

    [Fact]
    public void OblivionHnam_IsNeverInterpretedAsFo76VoliReferences()
    {
        var weather = ParseWeather(BethesdaGame.Oblivion,
            ("EDID", NullTermString("WTHR_Oblivion")),
            ("HNAM", new byte[56]));

        Assert.Null(weather.VolumetricLightingFormIds);
        Assert.NotNull(weather.Hdr);
    }

    private static Fallout76VolumetricLightingRecord ParseVoli(byte[] bytes, bool isBigEndian = false)
    {
        var descriptor = Descriptor(bytes, isBigEndian);
        var context = Context(bytes, descriptor, BethesdaGame.Fallout76);
        return Assert.Single(new MiscEnvironmentHandler(context)
            .ParseFallout76VolumetricLightingSettings());
    }

    private static WeatherRecord ParseWeather(
        BethesdaGame game,
        params (string sig, byte[] data)[] fields)
    {
        const uint weatherFormId = 0x0100_2000;
        var bytes = BuildRecordBytes(weatherFormId, "WTHR", false, fields);
        var descriptor = new DetectedMainRecord(
            "WTHR", (uint)(bytes.Length - 24), 0, weatherFormId, 0, false);
        var context = Context(bytes, descriptor, game);
        return Assert.Single(new MiscEnvironmentHandler(context).ParseWeather());
    }

    private static RecordParserContext Context(
        byte[] bytes,
        DetectedMainRecord descriptor,
        BethesdaGame game) =>
        new(
            new EsmRecordScanResult { Game = game, MainRecords = [descriptor] },
            null,
            new ByteArrayMemoryAccessor(bytes),
            bytes.Length,
            null);

    private static DetectedMainRecord Descriptor(byte[] bytes, bool isBigEndian) =>
        new("VOLI", (uint)(bytes.Length - 24), 0, FormId, 0, isBigEndian);

    private static byte[] BuildValidVoli(bool includeLnam) => BuildVoli(ValidFields(includeLnam).ToArray());

    private static byte[] BuildVoli(params (string sig, byte[] data)[] fields) =>
        BuildRecordBytes(FormId, "VOLI", false, fields);

    private static IEnumerable<(string sig, byte[] data)> ValidFields(bool includeLnam)
    {
        yield return ("EDID", NullTermString("VOLI_Test"));
        yield return ("CNAM", Float(100_000f));
        yield return ("DNAM", Float(0.75f));
        yield return ("ENAM", Float(0.1f));
        yield return ("FNAM", Float(0.2f));
        yield return ("GNAM", Float(0.3f));
        yield return ("HNAM", Float(0.7f));
        yield return ("INAM", Float(1f));
        yield return ("JNAM", Float(20f));
        yield return ("KNAM", Float(0.6f));
        if (includeLnam)
        {
            yield return ("LNAM", Float(0.42f));
        }

        yield return ("MNAM", Float(0.995f));
        yield return ("NNAM", Float(50f));
    }

    private static (string sig, byte[] data)[] NumericTailAfterCnam() =>
    [
        ("DNAM", Float(2f)),
        ("ENAM", Float(3f)),
        ("FNAM", Float(4f)),
        ("GNAM", Float(5f)),
        ("HNAM", Float(6f)),
        ("INAM", Float(7f)),
        ("JNAM", Float(8f)),
        ("KNAM", Float(9f)),
        ("MNAM", Float(10f)),
        ("NNAM", Float(11f))
    ];

    private static byte[] Float(float value)
    {
        var bytes = new byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        return bytes;
    }

    private sealed class ParserFixture
    {
        internal ParserFixture(byte[] bytes, DetectedMainRecord descriptor, BethesdaGame game)
        {
            Parser = new RecordParser(
                new EsmRecordScanResult { Game = game, MainRecords = [descriptor] },
                null,
                new ByteArrayMemoryAccessor(bytes),
                bytes.Length,
                null);
        }

        internal RecordParser Parser { get; }
    }
}
