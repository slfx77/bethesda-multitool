using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.Reflection;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.Reflection;

public sealed class StarfieldSunPresetProjectorTests
{
    [Fact]
    public void Decoder_projects_complete_exact_schema_and_preserves_authored_zero_and_empty()
    {
        var data = StarfieldSunPresetTestStreamBuilder.BuildFull();

        var success = StarfieldSunPresetDecoder.TryDecode(
            data, StarfieldSunPresetPayloadKind.FullObject, out var patch, out var error);

        Assert.True(success, error);
        Assert.NotNull(patch);
        Assert.Equal(0u, patch.ParentFormId);
        Assert.Equal(0f, patch.SunColor?.X);
        Assert.Equal(1f, patch.SunColor?.Y);
        Assert.Equal(2f, patch.SunColor?.Z);
        Assert.Equal(3f, patch.SunColor?.W);
        Assert.Equal(20_000f, patch.SunIlluminance);
        Assert.Equal(4f, patch.SunGlareColor?.X);
        Assert.Equal(1f, patch.SunGlareColor?.W);
        Assert.Equal(string.Empty, patch.SunDiskTexture);
        Assert.Equal(0f, patch.SunDiskScreenSizeMin);
        Assert.Equal(0.138f, patch.SunDiskScreenSizeMax);
        Assert.Equal(7f, patch.DuskDawnPreset?.DirectionalColor?.X);
        Assert.Equal(50f, patch.DuskDawnPreset?.TransitionStartAngle);
        Assert.Equal(80f, patch.DuskDawnPreset?.TransitionEndAngle);
        Assert.Equal(10f, patch.NightPreset?.DirectionalColor?.X);
        Assert.Equal(100f, patch.NightPreset?.DirectionalIlluminance);
        Assert.Equal(0f, patch.NightPreset?.GlareColor?.X);
        Assert.Equal(1f, patch.NightPreset?.GlareColor?.W);
    }

    [Fact]
    public void Decoder_projects_parent_only_diff_as_absent_nullable_members()
    {
        var data = StarfieldSunPresetTestStreamBuilder.BuildDiff();

        var success = StarfieldSunPresetDecoder.TryDecode(
            data, StarfieldSunPresetPayloadKind.Diff, out var patch, out var error);

        Assert.True(success, error);
        Assert.NotNull(patch);
        Assert.Equal(0x000E66B6u, patch.ParentFormId);
        Assert.Null(patch.SunColor);
        Assert.Null(patch.SunIlluminance);
        Assert.Null(patch.SunGlareColor);
        Assert.Null(patch.SunDiskTexture);
        Assert.Null(patch.SunDiskScreenSizeMin);
        Assert.Null(patch.SunDiskScreenSizeMax);
        Assert.Null(patch.DuskDawnPreset);
        Assert.Null(patch.NightPreset);
    }

    [Fact]
    public void Decoder_retains_partial_nested_diff_and_authored_zero_and_empty()
    {
        var data = StarfieldSunPresetTestStreamBuilder.BuildDiff(
            includeSunColorX: true,
            sunColorX: 0,
            diskTexture: string.Empty,
            includeDawnColor: true,
            includeNightColor: true);

        var success = StarfieldSunPresetDecoder.TryDecode(
            data, StarfieldSunPresetPayloadKind.Diff, out var patch, out var error);

        Assert.True(success, error);
        Assert.NotNull(patch);
        Assert.NotNull(patch.SunColor);
        Assert.True(patch.SunColor.X.HasValue);
        Assert.Equal(0f, patch.SunColor.X.Value);
        Assert.Null(patch.SunColor.Y);
        Assert.Equal(string.Empty, patch.SunDiskTexture);
        Assert.Equal(0.25f, patch.DuskDawnPreset?.DirectionalColor?.X);
        Assert.Null(patch.DuskDawnPreset?.TransitionStartAngle);
        Assert.Equal(0f, patch.NightPreset?.DirectionalColor?.X);
        Assert.Null(patch.NightPreset?.DirectionalIlluminance);
        Assert.Null(patch.NightPreset?.GlareColor);
    }

    [Theory]
    [InlineData(StarfieldSunPresetSchemaMutation.WrongClassOrder)]
    [InlineData(StarfieldSunPresetSchemaMutation.WrongFormToken)]
    [InlineData(StarfieldSunPresetSchemaMutation.WrongFloat4Flags)]
    [InlineData(StarfieldSunPresetSchemaMutation.WrongRootFieldOrder)]
    [InlineData(StarfieldSunPresetSchemaMutation.WrongRootFieldType)]
    [InlineData(StarfieldSunPresetSchemaMutation.WrongRuntimeOffset)]
    public void Decoder_rejects_any_CLAS_metadata_drift(
        StarfieldSunPresetSchemaMutation mutation)
    {
        var data = StarfieldSunPresetTestStreamBuilder.BuildFull(schemaMutation: mutation);

        AssertDecodeFails(data, StarfieldSunPresetPayloadKind.FullObject);
    }

    [Fact]
    public void Decoder_rejects_OBJT_as_diff_and_DIFF_as_full()
    {
        AssertDecodeFails(
            StarfieldSunPresetTestStreamBuilder.BuildFull(),
            StarfieldSunPresetPayloadKind.Diff);
        AssertDecodeFails(
            StarfieldSunPresetTestStreamBuilder.BuildDiff(),
            StarfieldSunPresetPayloadKind.FullObject);
    }

    [Theory]
    [InlineData("DIFF")]
    [InlineData("LIST")]
    public void Decoder_rejects_wrong_full_object_chunk(string objectChunk)
    {
        AssertDecodeFails(
            StarfieldSunPresetTestStreamBuilder.BuildFull(objectChunk: objectChunk),
            StarfieldSunPresetPayloadKind.FullObject);
    }

    [Fact]
    public void Decoder_rejects_truncated_object_and_trailing_stream_bytes()
    {
        AssertDecodeFails(
            StarfieldSunPresetTestStreamBuilder.BuildFull(truncateObject: true),
            StarfieldSunPresetPayloadKind.FullObject);
        AssertDecodeFails(
            StarfieldSunPresetTestStreamBuilder.BuildFull(appendTrailingByte: true),
            StarfieldSunPresetPayloadKind.FullObject);
    }

    [Fact]
    public void Decoder_rejects_duplicate_out_of_range_and_unterminated_diff_indices()
    {
        AssertDecodeFails(
            StarfieldSunPresetTestStreamBuilder.BuildDiff(duplicateParentField: true),
            StarfieldSunPresetPayloadKind.Diff);
        AssertDecodeFails(
            StarfieldSunPresetTestStreamBuilder.BuildDiff(appendOutOfRangeFieldIndex: true),
            StarfieldSunPresetPayloadKind.Diff);
        AssertDecodeFails(
            StarfieldSunPresetTestStreamBuilder.BuildDiff(omitRootTerminator: true),
            StarfieldSunPresetPayloadKind.Diff);
    }

    [Fact]
    public void Decoder_rejects_nonfinite_float()
    {
        AssertDecodeFails(
            StarfieldSunPresetTestStreamBuilder.BuildFull(sunIlluminance: float.NaN),
            StarfieldSunPresetPayloadKind.FullObject);
    }

    [Fact]
    public void Decoder_rejects_parent_reference_with_non_UInt32_value_type()
    {
        AssertDecodeFails(
            StarfieldSunPresetTestStreamBuilder.BuildFull(
                referenceValueType: StarfieldSunPresetTestStreamBuilder.TypeFloat),
            StarfieldSunPresetPayloadKind.FullObject);
        AssertDecodeFails(
            StarfieldSunPresetTestStreamBuilder.BuildDiff(
                referenceValueType: StarfieldSunPresetTestStreamBuilder.TypeFloat),
            StarfieldSunPresetPayloadKind.Diff);
    }

    [Fact]
    public void Decoder_rejects_unknown_payload_kind_without_reading_data()
    {
        var success = StarfieldSunPresetDecoder.TryDecode(
            [], StarfieldSunPresetPayloadKind.Unknown, out var patch, out var error);

        Assert.False(success);
        Assert.Null(patch);
        Assert.NotNull(error);
    }

    [Fact]
    public void Projector_rejects_wrong_root_and_unexpected_field()
    {
        var wrongRoot = Object("WrongRoot");
        AssertProjectionFails(wrongRoot, StarfieldSunPresetPayloadKind.Diff);

        var unexpected = Object(
            StarfieldSunPresetSchemaValidator.RootType,
            ("Unexpected", new BethesdaReflectionUnsignedValue(1)));
        AssertProjectionFails(unexpected, StarfieldSunPresetPayloadKind.Diff);
    }

    [Fact]
    public void Projector_rejects_missing_full_field_wrong_nested_type_and_wrong_scalar_type()
    {
        AssertProjectionFails(
            Object(StarfieldSunPresetSchemaValidator.RootType),
            StarfieldSunPresetPayloadKind.FullObject);

        AssertProjectionFails(
            Object(
                StarfieldSunPresetSchemaValidator.RootType,
                ("SunColor", new BethesdaReflectionObjectValue(
                    Object(StarfieldSunPresetSchemaValidator.NightType)))),
            StarfieldSunPresetPayloadKind.Diff);

        AssertProjectionFails(
            Object(
                StarfieldSunPresetSchemaValidator.RootType,
                ("SunIlluminance", new BethesdaReflectionStringValue("20000"))),
            StarfieldSunPresetPayloadKind.Diff);
    }

    [Fact]
    public void Projector_rejects_nonfinite_manual_value_and_non_UInt32_reference()
    {
        AssertProjectionFails(
            Object(
                StarfieldSunPresetSchemaValidator.RootType,
                ("SunIlluminance", new BethesdaReflectionFloatValue(double.PositiveInfinity))),
            StarfieldSunPresetPayloadKind.Diff);

        AssertProjectionFails(
            Object(
                StarfieldSunPresetSchemaValidator.RootType,
                ("pParent", new BethesdaReflectionReferenceValue(
                    "Float", new BethesdaReflectionFloatValue(1)))),
            StarfieldSunPresetPayloadKind.Diff);
    }

    private static BethesdaReflectionObject Object(
        string type,
        params (string Name, BethesdaReflectionValue Value)[] fields) =>
        new(
            type,
            fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));

    private static void AssertDecodeFails(
        byte[] data,
        StarfieldSunPresetPayloadKind payloadKind)
    {
        var success = StarfieldSunPresetDecoder.TryDecode(data, payloadKind, out var patch, out var error);
        Assert.False(success);
        Assert.Null(patch);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    private static void AssertProjectionFails(
        BethesdaReflectionObject reflected,
        StarfieldSunPresetPayloadKind payloadKind)
    {
        var success = StarfieldSunPresetProjector.TryProject(
            reflected, payloadKind, out var patch, out var error);
        Assert.False(success);
        Assert.Null(patch);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
