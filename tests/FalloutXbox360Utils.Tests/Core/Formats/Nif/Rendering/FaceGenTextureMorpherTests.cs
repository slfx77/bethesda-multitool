using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class FaceGenTextureMorpherTests
{
    [Fact]
    public void BuildNativeDeltaTexture_EngineQuantized256_TruncatesCoefficientAt256Steps()
    {
        var egt = CreateSinglePixelEgt(1.0f, 127, 0, 0);

        var current = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            [0.005f],
            FaceGenTextureMorpher.TextureAccumulationMode.CurrentFloat,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.Centered128);
        var quantized = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            [0.005f],
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
    public void BuildNativeDeltaTexture_EngineCompressedEncoding_UsesRecoveredClampFloorAndHalfScale()
    {
        var egt = CreateSinglePixelEgt(1.0f, 1, 0, 0);

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
    public void ApplyEncodedDeltaTexture_UpscalesCenteredDeltaOntoBaseTexture()
    {
        var baseTexture = CreateTexture(2, 2, 100, 110, 120);
        var deltaTexture = CreateTexture(1, 1, 138, 123, 128);

        var applied = FaceGenTextureMorpher.ApplyEncodedDeltaTexture(baseTexture, deltaTexture);

        // Shader decode: delta = byte * 2 - 255 (matches SKIN2000.pso's (sample - 0.5) * 2.0)
        // R: 138*2-255 = 21, 100+21 = 121
        // G: 123*2-255 = -9, 110-9 = 101
        // B: 128*2-255 = 1,  120+1 = 121
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

    private static EgtParser CreateSinglePixelEgt(float scale, sbyte deltaR, sbyte deltaG, sbyte deltaB)
    {
        var bytes = new byte[64 + 4 + 3];
        Encoding.ASCII.GetBytes("FREGT003").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 0);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(64), scale);
        bytes[68] = unchecked((byte)deltaR);
        bytes[69] = unchecked((byte)deltaG);
        bytes[70] = unchecked((byte)deltaB);

        return Assert.IsType<EgtParser>(EgtParser.Parse(bytes));
    }
}