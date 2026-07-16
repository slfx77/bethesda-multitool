using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class NifParticleAtlasNormalizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadAtlasRectangle_NormalizesAuthoredOrderInEitherEndian(bool bigEndian)
    {
        // Authored NIF order: U offset, U scale, V offset, V scale.
        var bytes = new byte[16];
        WriteSingle(bytes.AsSpan(0, 4), 0.25f, bigEndian);
        WriteSingle(bytes.AsSpan(4, 4), 0.125f, bigEndian);
        WriteSingle(bytes.AsSpan(8, 4), 0.5f, bigEndian);
        WriteSingle(bytes.AsSpan(12, 4), 0.375f, bigEndian);

        var rectangle = NifParticleSystemParser.ReadAtlasRectangle(bytes, 0, bigEndian);

        Assert.Equal(new Vector4(0.25f, 0.5f, 0.125f, 0.375f), rectangle);
    }

    private static void WriteSingle(Span<byte> destination, float value, bool bigEndian)
    {
        var bits = BitConverter.SingleToUInt32Bits(value);
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination, bits);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination, bits);
        }
    }
}
