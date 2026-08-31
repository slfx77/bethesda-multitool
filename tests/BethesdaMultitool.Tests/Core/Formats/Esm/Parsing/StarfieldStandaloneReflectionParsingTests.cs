using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;
using static BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.Reflection.StarfieldReflectionTestStreamBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class StarfieldStandaloneReflectionParsingTests
{
    [Fact]
    public void ParseAll_DecodesValidVoliAndCldfBodyAboveFourKiBThroughRecordParser()
    {
        const uint voliFormId = 0x00064D26;
        const uint cldfFormId = 0x00117A28;
        const int cloudOpacityTextureLength = 5000;
        var voliBytes = BuildRecordBytes(
            voliFormId,
            "VOLI",
            false,
            ("EDID", NullTermString("VolumetricLightingEarth")),
            ("REFL", BuildValidVolumetricLightingStream()));
        var cldfBytes = BuildRecordBytes(
            cldfFormId,
            "CLDF",
            false,
            ("EDID", NullTermString("CloudsEarth")),
            ("REFL", BuildCloudFormStream(
                shadowOpacityTextureLength: cloudOpacityTextureLength)));
        Assert.True(
            cldfBytes.Length - 24 > 4096,
            "The CLDF fixture must keep its record body above the retail 4 KiB boundary.");

        var parsed = Parse(
            BethesdaGame.Starfield,
            ("VOLI", voliFormId, false, voliBytes),
            ("CLDF", cldfFormId, false, cldfBytes));

        var volumetric = Assert.Single(parsed.VolumetricLightingSettings);
        Assert.Null(volumetric.DecodeFailure);
        Assert.NotNull(volumetric.Settings);
        Assert.Equal(1f, volumetric.Settings.ExteriorAndInterior.ScatteringVolumeNear);
        Assert.Equal(32f, volumetric.Settings.DistantLighting.ScatteringFar);

        var clouds = Assert.Single(parsed.CloudForms);
        Assert.Null(clouds.DecodeFailure);
        Assert.NotNull(clouds.Definition);
        Assert.Equal(cloudOpacityTextureLength, clouds.Definition.Shadows.OpacityTexture.Length);
        Assert.Empty(clouds.Definition.Layers);
        Assert.Empty(clouds.Definition.Planes);
        Assert.Equal((long)voliBytes.Length, clouds.Offset);
        Assert.Equal(2, parsed.TotalRecordsParsed);
    }

    [Fact]
    public void ParseAll_RetainsFailedVoliAndCldfEnvelopesWithSourceMetadata()
    {
        const uint voliFormId = 0x00064D26;
        const uint cldfFormId = 0x00117A28;
        var voliBytes = BuildRecordBytes(
            voliFormId,
            "VOLI",
            false,
            ("EDID", NullTermString("VolumetricLightingEarth")),
            ("REFL", [0x01]));
        var cldfBytes = BuildRecordBytes(
            cldfFormId,
            "CLDF",
            false,
            ("EDID", NullTermString("CloudsEarth")),
            ("REFL", [0x02]));

        var parsed = Parse(
            BethesdaGame.Starfield,
            ("VOLI", voliFormId, false, voliBytes),
            ("CLDF", cldfFormId, false, cldfBytes));

        var volumetric = Assert.Single(parsed.VolumetricLightingSettings);
        Assert.Equal(voliFormId, volumetric.FormId);
        Assert.Equal("VolumetricLightingEarth", volumetric.EditorId);
        Assert.Null(volumetric.Settings);
        Assert.NotNull(volumetric.DecodeFailure);
        Assert.Equal(0L, volumetric.Offset);
        Assert.False(volumetric.IsBigEndian);

        var clouds = Assert.Single(parsed.CloudForms);
        Assert.Equal(cldfFormId, clouds.FormId);
        Assert.Equal("CloudsEarth", clouds.EditorId);
        Assert.Null(clouds.Definition);
        Assert.NotNull(clouds.DecodeFailure);
        Assert.Equal((long)voliBytes.Length, clouds.Offset);
        Assert.False(clouds.IsBigEndian);

        Assert.Equal(2, parsed.TotalRecordsParsed);
        Assert.Equal("VolumetricLightingEarth", parsed.FormIdToEditorId[voliFormId]);
        Assert.Equal("CloudsEarth", parsed.FormIdToEditorId[cldfFormId]);
        Assert.DoesNotContain("VOLI", parsed.UnparsedTypeCounts.Keys);
        Assert.DoesNotContain("CLDF", parsed.UnparsedTypeCounts.Keys);
    }

    [Fact]
    public void ParseAll_RejectsOuterInheritanceAndDuplicateReflWithoutDroppingRecords()
    {
        const uint voliFormId = 0x10;
        const uint cldfFormId = 0x20;
        var voliBytes = BuildRecordBytes(
            voliFormId,
            "VOLI",
            false,
            ("RFDP", U32(0x0F)),
            ("REFL", [0x01]));
        var cldfBytes = BuildRecordBytes(
            cldfFormId,
            "CLDF",
            false,
            ("REFL", [0x01]),
            ("REFL", [0x02]));

        var parsed = Parse(
            BethesdaGame.Starfield,
            ("VOLI", voliFormId, false, voliBytes),
            ("CLDF", cldfFormId, false, cldfBytes));

        var volumetric = Assert.Single(parsed.VolumetricLightingSettings);
        Assert.Null(volumetric.Settings);
        Assert.Contains("RFDP", volumetric.DecodeFailure, StringComparison.Ordinal);

        var clouds = Assert.Single(parsed.CloudForms);
        Assert.Null(clouds.Definition);
        Assert.Contains("exactly one REFL", clouds.DecodeFailure, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseAll_RejectsUnknownOuterSubrecordsAndRetainsFailureEnvelopes()
    {
        const uint voliFormId = 0x11;
        const uint cldfFormId = 0x21;
        var voliBytes = BuildRecordBytes(
            voliFormId,
            "VOLI",
            false,
            ("EDID", NullTermString("UnknownOuterVoli")),
            ("REFL", BuildValidVolumetricLightingStream()),
            ("XTRA", [0x01]));
        var cldfBytes = BuildRecordBytes(
            cldfFormId,
            "CLDF",
            false,
            ("EDID", NullTermString("UnknownOuterCldf")),
            ("XTRA", [0x02]),
            ("REFL", BuildCloudFormStream()));

        var parsed = Parse(
            BethesdaGame.Starfield,
            ("VOLI", voliFormId, false, voliBytes),
            ("CLDF", cldfFormId, false, cldfBytes));

        var volumetric = Assert.Single(parsed.VolumetricLightingSettings);
        Assert.Equal(voliFormId, volumetric.FormId);
        Assert.Equal("UnknownOuterVoli", volumetric.EditorId);
        Assert.Null(volumetric.Settings);
        Assert.Contains("XTRA", volumetric.DecodeFailure, StringComparison.Ordinal);
        Assert.Equal(0L, volumetric.Offset);

        var clouds = Assert.Single(parsed.CloudForms);
        Assert.Equal(cldfFormId, clouds.FormId);
        Assert.Equal("UnknownOuterCldf", clouds.EditorId);
        Assert.Null(clouds.Definition);
        Assert.Contains("XTRA", clouds.DecodeFailure, StringComparison.Ordinal);
        Assert.Equal((long)voliBytes.Length, clouds.Offset);
    }

    [Fact]
    public void ParseAll_RejectsRdifAndTruncatedOuterFramingWithoutDroppingRecords()
    {
        const uint voliFormId = 0x30;
        const uint cldfFormId = 0x40;
        var voliBytes = BuildRecordBytes(
            voliFormId,
            "VOLI",
            false,
            ("RDIF", [0x01]));
        var cloudEditorId = NullTermString("TruncatedClouds");
        var cldfBytes = BuildRecordBytes(
            cldfFormId,
            "CLDF",
            false,
            ("EDID", cloudEditorId),
            ("REFL", [0x01]));
        var reflLengthOffset = 24 + 6 + cloudEditorId.Length + 4;
        BinaryPrimitives.WriteUInt16LittleEndian(cldfBytes.AsSpan(reflLengthOffset), 2);

        var parsed = Parse(
            BethesdaGame.Starfield,
            ("VOLI", voliFormId, false, voliBytes),
            ("CLDF", cldfFormId, false, cldfBytes));

        var volumetric = Assert.Single(parsed.VolumetricLightingSettings);
        Assert.Contains("RDIF", volumetric.DecodeFailure, StringComparison.Ordinal);
        Assert.Null(volumetric.Settings);

        var clouds = Assert.Single(parsed.CloudForms);
        Assert.Contains("truncated or malformed", clouds.DecodeFailure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("TruncatedClouds", clouds.EditorId);
        Assert.Null(clouds.Definition);
    }

    [Fact]
    public void ParseAll_RejectsBigEndianStandaloneReflectionAndRetainsEndianFlag()
    {
        const uint formId = 0x50;
        var bytes = BuildRecordBytes(
            formId,
            "VOLI",
            true,
            ("EDID", NullTermString("BigEndianVOLI")),
            ("REFL", [0x01]));

        var parsed = Parse(BethesdaGame.Starfield, ("VOLI", formId, true, bytes));

        var volumetric = Assert.Single(parsed.VolumetricLightingSettings);
        Assert.True(volumetric.IsBigEndian);
        Assert.Equal("BigEndianVOLI", volumetric.EditorId);
        Assert.Contains("little-endian", volumetric.DecodeFailure, StringComparison.OrdinalIgnoreCase);
        Assert.Null(volumetric.Settings);
    }

    [Fact]
    public void ParseAll_DoesNotIngestVoliOrCldfOutsideStarfield()
    {
        var voliBytes = BuildRecordBytes(0x60, "VOLI", false, ("REFL", [0x01]));
        var cldfBytes = BuildRecordBytes(0x70, "CLDF", false, ("REFL", [0x01]));

        var parsed = Parse(
            BethesdaGame.Fallout76,
            ("VOLI", 0x60, false, voliBytes),
            ("CLDF", 0x70, false, cldfBytes));

        Assert.Empty(parsed.VolumetricLightingSettings);
        Assert.Empty(parsed.CloudForms);
    }

    [Fact]
    public void ParseAll_ScanOnlyRetainsFailureEnvelopesInsteadOfDroppingDefinitions()
    {
        var scanResult = MakeScanResult(
        [
            new DetectedMainRecord("VOLI", 1, 0, 0x71, 0, false),
            new DetectedMainRecord("CLDF", 1, 0, 0x72, 25, false)
        ]);
        scanResult.Game = BethesdaGame.Starfield;

        var parsed = new RecordParser(scanResult).ParseAll();

        var volumetric = Assert.Single(parsed.VolumetricLightingSettings);
        Assert.Equal(0x71u, volumetric.FormId);
        Assert.Contains("unavailable", volumetric.DecodeFailure, StringComparison.OrdinalIgnoreCase);
        var clouds = Assert.Single(parsed.CloudForms);
        Assert.Equal(0x72u, clouds.FormId);
        Assert.Contains("unavailable", clouds.DecodeFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MergeWith_MalformedLaterDefinitionsReplaceEarlierDefinitionsByFormId()
    {
        Assert.True(StarfieldVolumetricLightingDecoder.TryDecode(
            BuildValidVolumetricLightingStream(), out var earlierSettings, out var voliError), voliError);
        Assert.NotNull(earlierSettings);
        Assert.True(StarfieldCloudFormDecoder.TryDecode(
            BuildCloudFormStream(), out var earlierDefinition, out var cldfError), cldfError);
        Assert.NotNull(earlierDefinition);

        var earlierVolumetric = new StarfieldVolumetricLightingRecord
        {
            FormId = 0x80,
            Settings = earlierSettings
        };
        var malformedVolumetricOverride = new StarfieldVolumetricLightingRecord
        {
            FormId = 0x80,
            DecodeFailure = "malformed later override"
        };
        var earlierClouds = new StarfieldCloudFormRecord
        {
            FormId = 0x90,
            Definition = earlierDefinition
        };
        var malformedCloudOverride = new StarfieldCloudFormRecord
        {
            FormId = 0x90,
            DecodeFailure = "malformed later override"
        };

        var merged = new RecordCollection
        {
            VolumetricLightingSettings = [earlierVolumetric],
            CloudForms = [earlierClouds]
        }.MergeWith(new RecordCollection
        {
            VolumetricLightingSettings = [malformedVolumetricOverride],
            CloudForms = [malformedCloudOverride]
        });

        Assert.Same(malformedVolumetricOverride, Assert.Single(merged.VolumetricLightingSettings));
        Assert.Same(malformedCloudOverride, Assert.Single(merged.CloudForms));
        Assert.Null(merged.VolumetricLightingSettings[0].Settings);
        Assert.Null(merged.CloudForms[0].Definition);
        Assert.Equal(2, merged.TotalRecordsParsed);
    }

    private static RecordCollection Parse(
        BethesdaGame game,
        params (string Type, uint FormId, bool IsBigEndian, byte[] Bytes)[] records)
    {
        var totalSize = records.Sum(record => record.Bytes.Length);
        var allBytes = new byte[totalSize];
        var descriptors = new List<DetectedMainRecord>(records.Length);
        var offset = 0;
        foreach (var record in records)
        {
            Array.Copy(record.Bytes, 0, allBytes, offset, record.Bytes.Length);
            descriptors.Add(new DetectedMainRecord(
                record.Type,
                checked((uint)(record.Bytes.Length - 24)),
                0,
                record.FormId,
                offset,
                record.IsBigEndian));
            offset += record.Bytes.Length;
        }

        var scanResult = MakeScanResult(descriptors);
        scanResult.Game = game;
        using var mmf = MemoryMappedFile.CreateNew(null, allBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, allBytes.Length);
        accessor.WriteArray(0, allBytes, 0, allBytes.Length);
        return new RecordParser(scanResult, accessor: accessor, fileSize: allBytes.Length).ParseAll();
    }

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);
}
