using System.IO.MemoryMappedFiles;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class StarfieldWeatherSettingsParsingTests
{
    private const uint TypeRef = 0xFFFFFF05;
    private const uint TypeUInt32 = 0xFFFFFF0D;

    [Fact]
    public void ParseAll_DecodesRetailShapedRdifAndRetainsItsOuterParent()
    {
        const uint formId = 0x0002B517;
        const uint parentFormId = 0x000E8396;

        var parsed = ParseSingle(formId,
            ("EDID", NullTermString("Weather_Cloudy_C3_MistLight")),
            ("RFDP", U32(parentFormId)),
            ("RDIF", BuildParentOnlyDiff(parentFormId)));

        var weather = Assert.Single(parsed.WeatherSettings);
        Assert.Equal(formId, weather.FormId);
        Assert.Equal("Weather_Cloudy_C3_MistLight", weather.EditorId);
        Assert.Equal(StarfieldWeatherSettingsPayloadKind.Diff, weather.PayloadKind);
        Assert.Equal(parentFormId, weather.ParentFormId);
        Assert.Equal(parentFormId, weather.Patch?.ParentFormId);
        Assert.Null(weather.DecodeFailure);
    }

    [Fact]
    public void ParseAll_RetainsInvalidOverrideWhenRfdpDisagreesWithReflectedParent()
    {
        var parsed = ParseSingle(0x0002B517,
            ("RFDP", U32(0x000E8396)),
            ("RDIF", BuildParentOnlyDiff(0x000E8397)));

        var weather = Assert.Single(parsed.WeatherSettings);
        Assert.Null(weather.Patch);
        Assert.Contains("disagrees", weather.DecodeFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseAll_RejectsRdifWithoutNonZeroRfdp()
    {
        var parsed = ParseSingle(0x0002B517,
            ("RDIF", BuildParentOnlyDiff(0x000E8396)));

        var weather = Assert.Single(parsed.WeatherSettings);
        Assert.Null(weather.Patch);
        Assert.Contains("non-zero RFDP", weather.DecodeFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseAll_RejectsMixedRootAndDiffPayloads()
    {
        var payload = BuildParentOnlyDiff(0x000E8396);
        var parsed = ParseSingle(0x0002B517,
            ("RFDP", U32(0x000E8396)),
            ("REFL", payload),
            ("RDIF", payload));

        var weather = Assert.Single(parsed.WeatherSettings);
        Assert.Equal(StarfieldWeatherSettingsPayloadKind.Unknown, weather.PayloadKind);
        Assert.Null(weather.Patch);
        Assert.Contains("exactly one", weather.DecodeFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseAll_ClassifiesOnlyAnEstablishedSingularPayloadKind()
    {
        var full = Assert.Single(ParseSingle(
            0x0002B518,
            ("EDID", NullTermString("EstablishedFullEnvelope")),
            ("REFL", [0x01])).WeatherSettings);
        Assert.Equal(StarfieldWeatherSettingsPayloadKind.FullObject, full.PayloadKind);
        Assert.NotNull(full.DecodeFailure);

        var missing = Assert.Single(ParseSingle(
            0x0002B519,
            ("EDID", NullTermString("MissingPayload"))).WeatherSettings);
        Assert.Equal(StarfieldWeatherSettingsPayloadKind.Unknown, missing.PayloadKind);
        Assert.Contains("exactly one", missing.DecodeFailure, StringComparison.OrdinalIgnoreCase);

        var bigEndian = Assert.Single(ParseSingle(
            0x0002B51A,
            true,
            ("EDID", NullTermString("BigEndianPayload")),
            ("REFL", [0x01])).WeatherSettings);
        Assert.Equal(StarfieldWeatherSettingsPayloadKind.Unknown, bigEndian.PayloadKind);
        Assert.Contains("little-endian", bigEndian.DecodeFailure, StringComparison.OrdinalIgnoreCase);

        var scanOnly = MakeScanResult(
        [
            new DetectedMainRecord("WTHS", 16, 0, 0x0002B51B, 0, false)
        ]);
        scanOnly.Game = BethesdaGame.Starfield;
        var unavailable = Assert.Single(new RecordParser(scanOnly).ParseAll().WeatherSettings);
        Assert.Equal(StarfieldWeatherSettingsPayloadKind.Unknown, unavailable.PayloadKind);
        Assert.Contains("unavailable", unavailable.DecodeFailure, StringComparison.OrdinalIgnoreCase);

        const uint partialFormId = 0x0002B51C;
        var partialBytes = BuildRecordBytes(
            partialFormId,
            "WTHS",
            false,
            ("EDID", NullTermString("PartiallyRecoveredPayload")),
            ("REFL", [0x01]));
        var partialDescriptor = new DetectedMainRecord(
            "WTHS", (uint)(partialBytes.Length - 24), 0, partialFormId, 0, false);
        var partialScan = MakeScanResult([partialDescriptor]);
        partialScan.Game = BethesdaGame.Starfield;
        using var partialMmf = MemoryMappedFile.CreateNew(null, partialBytes.Length);
        using var partialAccessor = partialMmf.CreateViewAccessor(0, partialBytes.Length);
        partialAccessor.WriteArray(0, partialBytes, 0, partialBytes.Length);
        var partialContext = new RecordParserContext(
            partialScan, null, partialAccessor, partialBytes.Length, null);
        partialContext.PartiallyRecoveredFormIds.Add(partialFormId);
        var partial = Assert.Single(
            new MiscEnvironmentHandler(partialContext).ParseStarfieldWeatherSettings());
        Assert.Equal(StarfieldWeatherSettingsPayloadKind.Unknown, partial.PayloadKind);
        Assert.Contains("partially recovered", partial.DecodeFailure, StringComparison.OrdinalIgnoreCase);
    }

    private static RecordCollection ParseSingle(
        uint formId,
        params (string Signature, byte[] Data)[] subrecords) =>
        ParseSingle(formId, false, subrecords);

    private static RecordCollection ParseSingle(
        uint formId,
        bool isBigEndian,
        params (string Signature, byte[] Data)[] subrecords)
    {
        var recordBytes = BuildRecordBytes(formId, "WTHS", isBigEndian, subrecords);
        var mainRecord = new DetectedMainRecord(
            "WTHS", (uint)(recordBytes.Length - 24), 0, formId, 0, isBigEndian);
        var scanResult = MakeScanResult([mainRecord]);
        scanResult.Game = BethesdaGame.Starfield;

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);
        return new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length).ParseAll();
    }

    private static byte[] BuildParentOnlyDiff(uint parentFormId)
    {
        var strings = new List<byte>();
        var rootOffset = AddString(strings, "BGSWeatherSettingsForm");
        var parentOffset = AddString(strings, "pParent");
        var chunks = new[]
        {
            Chunk("TYPE", U32(1)),
            Chunk("CLAS", Concat(
                U32(rootOffset), U32(0), U16(0), U16(1),
                U32(parentOffset), U32(TypeRef), U16(0), U16(0))),
            Chunk("DIFF", Concat(
                U32(rootOffset), U16(0), U32(TypeUInt32), U32(parentFormId),
                U16(ushort.MaxValue)))
        };

        return Concat(
            Encoding.ASCII.GetBytes("BETH"), U32(8), U32(4), U32((uint)chunks.Length + 2),
            Encoding.ASCII.GetBytes("STRT"), U32((uint)strings.Count), [.. strings],
            Concat(chunks));
    }

    private static uint AddString(List<byte> strings, string value)
    {
        var offset = checked((uint)strings.Count);
        strings.AddRange(Encoding.ASCII.GetBytes(value));
        strings.Add(0);
        return offset;
    }

    private static byte[] Chunk(string signature, byte[] body)
    {
        return Concat(Encoding.ASCII.GetBytes(signature), U32((uint)body.Length), body);
    }

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);

    private static byte[] U16(ushort value) => BitConverter.GetBytes(value);

    private static byte[] Concat(params byte[][] parts)
    {
        var bytes = new List<byte>();
        foreach (var part in parts) bytes.AddRange(part);
        return [.. bytes];
    }
}
