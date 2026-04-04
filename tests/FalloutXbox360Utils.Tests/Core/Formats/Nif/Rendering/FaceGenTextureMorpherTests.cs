using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class FaceGenTextureMorpherTests
{
    [Fact]
    public void BuildNativeDeltaTexture_EngineQuantized256_TruncatesCoefficientMidpointsAt256Steps()
    {
        var egt = CreateSinglePixelMorphEgt(1.0f, 127, 0, 0);
        const float midpointCoefficient = 1.5f / 256f;

        var current = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            [midpointCoefficient],
            FaceGenTextureMorpher.TextureAccumulationMode.CurrentFloat,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.Centered128);
        var quantized = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            [midpointCoefficient],
            FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantized256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.Centered128);

        Assert.NotNull(current);
        Assert.NotNull(quantized);
        Assert.Equal(129, current!.Pixels[0]);
        Assert.Equal(128, quantized!.Pixels[0]);
        Assert.Equal(128, current.Pixels[1]);
        Assert.Equal(128, quantized.Pixels[1]);
        Assert.Equal(128, current.Pixels[2]);
        Assert.Equal(128, quantized.Pixels[2]);
    }

    [Fact]
    public void BuildNativeDeltaTexture_DefaultsToTruncated256AndTruncateEncoding()
    {
        var egt = CreateSinglePixelMorphEgt(1.0f, -127, 0, 0);
        const float coefficient = 3f / 256f;

        var implicitTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            [coefficient]);
        var explicitTruncate = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            [coefficient],
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
        var explicitFloor = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            [coefficient],
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255Half);

        Assert.NotNull(implicitTexture);
        Assert.NotNull(explicitTruncate);
        Assert.NotNull(explicitFloor);
        Assert.Equal(explicitTruncate!.Pixels, implicitTexture!.Pixels);
        Assert.NotEqual(explicitFloor!.Pixels[0], implicitTexture.Pixels[0]);
    }

    [Fact]
    public void EgtParser_Parse_UsesAlignedRowsAndParseTimeRowFlip()
    {
        var bytes = new byte[64 + 4 + 8 * 2 * 3];
        Encoding.ASCII.GetBytes("FREGT003").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 0);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(64), 1.0f);

        var offset = 68;
        WriteAlignedChannel(bytes, ref offset, [1, 2, 3, 11, 12, 13], 3, 2, 8);
        WriteAlignedChannel(bytes, ref offset, [21, 22, 23, 31, 32, 33], 3, 2, 8);
        WriteAlignedChannel(bytes, ref offset, [41, 42, 43, 51, 52, 53], 3, 2, 8);

        var parsed = Assert.IsType<EgtParser>(EgtParser.Parse(bytes));
        var morph = Assert.Single(parsed.SymmetricMorphs);

        Assert.Equal<sbyte>([11, 12, 13, 1, 2, 3], morph.DeltaR);
        Assert.Equal<sbyte>([31, 32, 33, 21, 22, 23], morph.DeltaG);
        Assert.Equal<sbyte>([51, 52, 53, 41, 42, 43], morph.DeltaB);
    }

    [Fact]
    public void BuildNativeDeltaTexture_EngineCompressedEncoding_UsesRecoveredClampFloorAndHalfScale()
    {
        var egt = CreateSinglePixelMorphEgt(1.0f, 1, 0, 0);

        var centered = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            [1.0f],
            FaceGenTextureMorpher.TextureAccumulationMode.CurrentFloat,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.Centered128);
        var engineCompressed = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            [1.0f],
            FaceGenTextureMorpher.TextureAccumulationMode.CurrentFloat,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255Half);

        Assert.NotNull(centered);
        Assert.NotNull(engineCompressed);
        Assert.Equal(129, centered!.Pixels[0]);
        Assert.Equal(128, engineCompressed!.Pixels[0]);
        Assert.Equal(128, centered.Pixels[1]);
        Assert.Equal(127, engineCompressed.Pixels[1]);
        Assert.Equal(128, centered.Pixels[2]);
        Assert.Equal(127, engineCompressed.Pixels[2]);
    }

    [Fact]
    public void Apply_DefaultsToEncodedFacemodSemantics()
    {
        var baseTexture = CreateTexture(2, 2, 100, 110, 120);
        var egt = CreateSinglePixelMorphEgt(1.0f, 127, 0, 0);
        const float coefficient = 1f / 256f;

        var viaApply = FaceGenTextureMorpher.Apply(baseTexture, egt, [coefficient]);
        var encodedDelta = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            [coefficient],
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
        var viaEncodedDelta = FaceGenTextureMorpher.ApplyEncodedDeltaTexture(baseTexture, encodedDelta!);

        Assert.NotNull(viaApply);
        Assert.NotNull(encodedDelta);
        Assert.NotNull(viaEncodedDelta);
        Assert.Equal(viaEncodedDelta!.Pixels, viaApply!.Pixels);
        Assert.Equal(99, viaApply.Pixels[0]);
        Assert.Equal(109, viaApply.Pixels[1]);
        Assert.Equal(119, viaApply.Pixels[2]);
    }

    [Fact]
    public void ApplyEncodedDeltaTexture_UpscalesCenteredDeltaOntoBaseTexture()
    {
        var baseTexture = CreateTexture(2, 2, 100, 110, 120);
        var deltaTexture = CreateTexture(1, 1, 138, 123, 128);

        var applied = FaceGenTextureMorpher.ApplyEncodedDeltaTexture(baseTexture, deltaTexture);

        Assert.NotNull(applied);
        for (var offset = 0; offset < applied!.Pixels.Length; offset += 4)
        {
            Assert.Equal(121, applied.Pixels[offset]);
            Assert.Equal(101, applied.Pixels[offset + 1]);
            Assert.Equal(121, applied.Pixels[offset + 2]);
            Assert.Equal(255, applied.Pixels[offset + 3]);
        }
    }

    private static DecodedTexture CreateTexture(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = r;
            pixels[offset + 1] = g;
            pixels[offset + 2] = b;
            pixels[offset + 3] = 255;
        }

        return DecodedTexture.FromBaseLevel(pixels, width, height);
    }

    private static EgtParser CreateSinglePixelMorphEgt(float scale, sbyte deltaR, sbyte deltaG, sbyte deltaB)
    {
        return EgtParser.CreateFromMorphs(
            1,
            1,
            [
                new EgtMorph
                {
                    Scale = scale,
                    DeltaR = [deltaR],
                    DeltaG = [deltaG],
                    DeltaB = [deltaB]
                }
            ]);
    }

    private static void WriteAlignedChannel(
        byte[] destination,
        ref int offset,
        sbyte[] rowsByFileOrder,
        int cols,
        int rows,
        int rowStride)
    {
        Assert.Equal(cols * rows, rowsByFileOrder.Length);

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                destination[offset + col] = unchecked((byte)rowsByFileOrder[row * cols + col]);
            }

            offset += rowStride;
        }
    }
}