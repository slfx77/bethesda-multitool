using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

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
        var baseTexture = TestTextures.Uniform(2, 2, 100, 110, 120);
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
        var baseTexture = TestTextures.Uniform(2, 2, 100, 110, 120);
        var deltaTexture = TestTextures.Uniform(1, 1, 138, 123, 128);

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

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public void ApplyEncodedDeltaTexture_DecodesFractionalMidpointBeforeRounding(int width, int height)
    {
        var baseTexture = TestTextures.Single(100, 110, 120, 37);
        var deltaTexture = TestTextures.FromTexels(width, height,
            (127, 127, 0, 0), (128, 129, 255, 255));

        var applied = FaceGenTextureMorpher.ApplyEncodedDeltaTexture(baseTexture, deltaTexture);

        Assert.NotNull(applied);
        // Map0's filtered channels are 127.5, 128, 127.5: decoded deltas 0, 1, 0.
        // Truncating Map0 to bytes before decoding instead returns 99, 111, 119.
        Assert.Equal<byte>([100, 111, 120, 37], applied.Pixels);
        Assert.Equal<byte>([100, 110, 120, 37], baseTexture.Pixels);
        Assert.Equal<byte>([127, 127, 0, 0, 128, 129, 255, 255], deltaTexture.Pixels);
    }

    [Fact]
    public void ApplyEncodedDeltaTexture_FiltersBothAxesBeforeTheFinalRgbClamp()
    {
        var baseTexture = TestTextures.Single(100, 110, 120, 43);
        var deltaTexture = TestTextures.FromTexels(2, 2,
            (127, 127, 0, 0), (128, 128, 0, 64),
            (128, 129, 255, 128), (128, 129, 255, 255));

        var applied = FaceGenTextureMorpher.ApplyEncodedDeltaTexture(baseTexture, deltaTexture);

        Assert.NotNull(applied);
        // The four equal weights give Map0 127.75, 128.25, 127.5 and deltas 0.5, 1.5, 0.
        Assert.Equal<byte>([101, 112, 120, 43], applied.Pixels);
    }

    [Fact]
    public void ApplyEncodedDeltaTexture_UpsamplesFractionsAndClampsOnlyTheComposedRgb()
    {
        var baseTexture = TestTextures.Uniform(4, 1, 100, 110, 120, 53);
        var deltaTexture = TestTextures.FromTexels(2, 1,
            (127, 128, 0, 0), (128, 127, 255, 255));

        var applied = FaceGenTextureMorpher.ApplyEncodedDeltaTexture(baseTexture, deltaTexture);

        Assert.NotNull(applied);
        // Texel-center mapping samples at clamped X 0, 0.25, 0.75, 1.
        // Fractional signed deltas survive until addition to the base and the final byte rounding.
        Assert.Equal<byte>(
            [99, 111, 0, 53, 100, 111, 0, 53, 101, 110, 248, 53, 101, 109, 255, 53],
            applied.Pixels);
    }

    [Fact]
    public void ApplyEncodedDeltaTexture_PreservesAlignedTexelsAndBaseAlpha()
    {
        var baseTexture = TestTextures.FromTexels(2, 2,
            (100, 110, 120, 11), (100, 110, 120, 22),
            (100, 110, 120, 33), (100, 110, 120, 44));
        var deltaTexture = TestTextures.FromTexels(2, 2,
            (127, 128, 0, 255), (128, 127, 255, 0),
            (138, 123, 128, 255), (0, 255, 127, 0));

        var applied = FaceGenTextureMorpher.ApplyEncodedDeltaTexture(baseTexture, deltaTexture);

        Assert.NotNull(applied);
        Assert.Equal<byte>(
            [99, 111, 0, 11, 101, 109, 255, 22, 121, 101, 121, 33, 0, 255, 119, 44],
            applied.Pixels);
    }

    [Fact]
    public void ApplyEncodedDeltaTexture_PreservesConstantMapOnNonPowerOfTwoGrid()
    {
        var baseTexture = TestTextures.Uniform(7, 9, 100, 110, 120, 67);
        var deltaTexture = TestTextures.Uniform(3, 5, 127, 128, 138, 0);

        var applied = FaceGenTextureMorpher.ApplyEncodedDeltaTexture(baseTexture, deltaTexture);

        Assert.NotNull(applied);
        for (var offset = 0; offset < applied.Pixels.Length; offset += 4)
        {
            Assert.Equal(99, applied.Pixels[offset]);
            Assert.Equal(111, applied.Pixels[offset + 1]);
            Assert.Equal(141, applied.Pixels[offset + 2]);
            Assert.Equal(67, applied.Pixels[offset + 3]);
        }
    }

    [Fact]
    public void Apply_GeneratedEncodedDeltaKeepsFractionalSamples()
    {
        var baseTexture = TestTextures.Single(100, 110, 120, 71);
        var egt = EgtParser.CreateFromMorphs(2, 1,
        [
            new EgtMorph
            {
                Scale = 1,
                DeltaR = [0, 2],
                DeltaG = [2, 0],
                DeltaB = [0, 0]
            }
        ]);

        var applied = FaceGenTextureMorpher.Apply(baseTexture, egt, [1f]);

        Assert.NotNull(applied);
        // The established encoder produces R 127/128, G 128/127, B 127/127.
        // Their midpoint deltas are 0, 0, -1; this asserts the generated-EGT caller directly.
        Assert.Equal<byte>([100, 110, 119, 71], applied.Pixels);
    }

    [Fact]
    public void ApplyNativeResolution_RetainsItsExistingByteSampleContract()
    {
        var baseTexture = TestTextures.FromTexels(2, 1,
            (127, 126, 125, 124), (128, 129, 130, 131));
        var egt = CreateSinglePixelMorphEgt(1, 0, 0, 0);

        var applied = FaceGenTextureMorpher.ApplyNativeResolution(baseTexture, egt, [0f]);

        Assert.NotNull(applied);
        // This separate diagnostic samples the base into bytes before applying native deltas.
        // Its 127.5 midpoint still truncates; the Map0 correction must not alter that contract.
        Assert.Equal<byte>([127, 127, 127, 127], applied.Pixels);
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
