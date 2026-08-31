using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.Reflection;

public sealed class StarfieldCurve3DDecoderTests
{
    [Fact]
    public void Decoder_preserves_exact_metadata_marker_raw_bodies_and_unresampled_controls()
    {
        var data = StarfieldCurve3DTestStreamBuilder.Build(
            serializedControlListMarker: 0xA1B2C3D4);

        var success = StarfieldCurve3DDecoder.TryDecode(data, out var definition, out var error);

        Assert.True(success, error);
        Assert.NotNull(definition);
        Assert.Equal(1f, definition.XCurve.MaxInput);
        Assert.Equal(-2f, definition.XCurve.MinInput);
        Assert.Equal(3f, definition.XCurve.InputDistance);
        Assert.Equal(100f, definition.XCurve.MaxValue);
        Assert.Equal(-25f, definition.XCurve.MinValue);
        Assert.Equal(0.5f, definition.XCurve.DefaultValue);
        Assert.Equal("CubicSpline", definition.XCurve.CurveType);
        Assert.Equal("Clamp", definition.XCurve.EdgeMode);
        Assert.True(definition.XCurve.IsSampleInterpolating);
        Assert.Equal(0xA1B2C3D4u, definition.XCurve.SerializedControlListMarker);
        Assert.Equal(3, definition.XCurve.Controls.Count);
        Assert.Equal(
            new StarfieldFloatCurveControl(-2f, 100f), definition.XCurve.Controls[0]);
        Assert.Equal(
            new StarfieldFloatCurveControl(0f, 0f), definition.XCurve.Controls[1]);
        Assert.Equal(
            new StarfieldFloatCurveControl(1f, -25f), definition.XCurve.Controls[2]);
        Assert.Equal(51, definition.XCurve.RawSerializedMetadata.Length);
        Assert.Equal(32, definition.XCurve.RawControlListBody.Length);
        Assert.Equal(
            0xA1B2C3D4u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                definition.XCurve.RawSerializedMetadata.AsSpan(
                    definition.XCurve.RawSerializedMetadata.Length - 4, 4)));
        Assert.Equal(
            3u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                definition.XCurve.RawControlListBody.AsSpan(4, 4)));

        Assert.Equal(4, definition.YCurve.Controls.Count);
        Assert.Equal(2f, definition.YCurve.Controls[^1].Input);
        Assert.Equal(40f, definition.YCurve.Controls[^1].Value);
        Assert.Equal(2, definition.ZCurve.Controls.Count);
    }

    [Theory]
    [InlineData(StarfieldCurve3DSchemaMutation.WrongClassOrder)]
    [InlineData(StarfieldCurve3DSchemaMutation.WrongFormToken)]
    [InlineData(StarfieldCurve3DSchemaMutation.WrongFloatCurveFlags)]
    [InlineData(StarfieldCurve3DSchemaMutation.WrongFieldOrder)]
    [InlineData(StarfieldCurve3DSchemaMutation.DuplicateField)]
    [InlineData(StarfieldCurve3DSchemaMutation.WrongFieldType)]
    [InlineData(StarfieldCurve3DSchemaMutation.WrongRuntimeOffset)]
    public void Decoder_rejects_exact_CLAS_schema_drift(StarfieldCurve3DSchemaMutation mutation)
    {
        AssertDecodeFails(StarfieldCurve3DTestStreamBuilder.Build(schemaMutation: mutation));
    }

    [Theory]
    [InlineData(StarfieldCurve3DLayoutMutation.DiffObject)]
    [InlineData(StarfieldCurve3DLayoutMutation.ReorderedUserAndList)]
    [InlineData(StarfieldCurve3DLayoutMutation.UnknownSideChunk)]
    [InlineData(StarfieldCurve3DLayoutMutation.DuplicateFinalChunk)]
    [InlineData(StarfieldCurve3DLayoutMutation.WrongUserType)]
    [InlineData(StarfieldCurve3DLayoutMutation.WrongListElementType)]
    public void Decoder_rejects_unknown_duplicate_or_order_invalid_chunk_layouts(
        StarfieldCurve3DLayoutMutation mutation)
    {
        AssertDecodeFails(StarfieldCurve3DTestStreamBuilder.Build(layoutMutation: mutation));
    }

    [Theory]
    [InlineData(StarfieldCurve3DLayoutMutation.ImpossibleControlCount)]
    [InlineData(StarfieldCurve3DLayoutMutation.MalformedMetadataString)]
    [InlineData(StarfieldCurve3DLayoutMutation.InvalidInterpolationBool)]
    [InlineData(StarfieldCurve3DLayoutMutation.NonFiniteMetadata)]
    [InlineData(StarfieldCurve3DLayoutMutation.NonFiniteControl)]
    [InlineData(StarfieldCurve3DLayoutMutation.TruncatedStream)]
    [InlineData(StarfieldCurve3DLayoutMutation.TrailingByte)]
    public void Decoder_rejects_malformed_or_truncated_payloads(
        StarfieldCurve3DLayoutMutation mutation)
    {
        AssertDecodeFails(StarfieldCurve3DTestStreamBuilder.Build(layoutMutation: mutation));
    }

    [Fact]
    public void Decoder_rejects_empty_input_without_a_partial_definition()
    {
        AssertDecodeFails([]);
    }

    private static void AssertDecodeFails(byte[] data)
    {
        var success = StarfieldCurve3DDecoder.TryDecode(data, out var definition, out var error);

        Assert.False(success);
        Assert.Null(definition);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
