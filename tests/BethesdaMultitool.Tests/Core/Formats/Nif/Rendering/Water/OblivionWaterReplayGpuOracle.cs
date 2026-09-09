using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>Independent raster/sampling/conversion around the reviewed scalar shader algebra.</summary>
internal static class OblivionWaterReplayGpuOracle
{
    internal const int Size = 256;

    internal static Vector4[] Decode(byte[] bytes, bool normal)
    {
        var stride = normal ? 4 : 8;
        var pixels = new Vector4[bytes.Length / stride];
        for (var index = 0; index < pixels.Length; index++)
        {
            var offset = index * stride;
            pixels[index] = normal
                ? new Vector4(bytes[offset], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3]) / 255f
                : new Vector4(ReadWord(bytes, offset), ReadWord(bytes, offset + 2),
                    ReadWord(bytes, offset + 4), ReadWord(bytes, offset + 6)) / 65535f;
        }

        return pixels;
    }

    internal static byte[] Expected(OblivionWaterSimulationPass? pass,
        OblivionWaterDisplacementConstants? constants,
        IReadOnlyDictionary<OblivionWaterSimulationResource, Vector4[]> preceding,
        bool normal, bool unadaptedViewport = false, int outputSize = Size)
    {
        var result = new byte[outputSize * outputSize * (normal ? 4 : 8)];
        for (var y = 0; y < outputSize; y++)
        {
            for (var x = 0; x < outputSize; x++)
            {
                var value = pass is null
                    ? OblivionWaterSimulationMath.InitialState
                    : Pixel(pass, constants!.Value, preceding, x, y, unadaptedViewport, outputSize);
                Encode(result, y * outputSize + x, value, normal);
            }
        }

        return result;
    }

    private static Vector4 Pixel(OblivionWaterSimulationPass pass, OblivionWaterDisplacementConstants constants,
        IReadOnlyDictionary<OblivionWaterSimulationResource, Vector4[]> preceding,
        int x, int y, bool unadapted, int outputSize)
    {
        // Source D3D9 samples integer pixel centers. The deliberate negative control uses
        // D3D12 half-integer barycentrics without the reviewed viewport translation.
        var pixel = new Vector2(x, y) + (unadapted ? new Vector2(0.5f) : Vector2.Zero);
        if (pass.Stage is OblivionWaterSimulationStage.RainStamp or OblivionWaterSimulationStage.WadingStamp)
        {
            var ratio = constants.TexRatio0;
            if (pass.Stage == OblivionWaterSimulationStage.WadingStamp)
            {
                // This controlled cohort uses axis-aligned positive scales, with distinct X/Y
                // scales and translations. General actor geometry is a separate raster cohort.
                var first = constants.Translation0;
                var second = constants.Translation1;
                if (!first.Y.Equals(0f) || !second.X.Equals(0f) || first.X <= 0f || second.Y <= 0f)
                {
                    throw new InvalidOperationException(
                        "The wading oracle requires its explicit axis-aligned test matrix.");
                }

                ratio = new Vector4(first.X, second.Y, first.Z, second.Z);
            }

            var left = (1f + ratio.Z - ratio.X) * (Size / 2f);
            var right = (1f + ratio.Z + ratio.X) * (Size / 2f);
            var top = (1f - ratio.W - ratio.Y) * (Size / 2f);
            var bottom = (1f - ratio.W + ratio.Y) * (Size / 2f);
            return pixel.X >= left && pixel.X < right && pixel.Y >= top && pixel.Y < bottom
                ? OblivionWaterSimulationMath.StampState
                : preceding[pass.Output][y * Size + x];
        }

        var source = preceding[pass.Input0!.Value];
        var ratio0 = constants.TexRatio0;
        var uv = pixel / outputSize * new Vector2(ratio0.X, ratio0.Y) + new Vector2(ratio0.Z, ratio0.W);

        Vector4 Sample(Vector2 offset)
        {
            return SampleBilinear(source, uv + offset, pass.Address);
        }

        var step = pass.Stage == OblivionWaterSimulationStage.FftNormal ? constants.Surface.W : 1f / Size;
        var cardinal = new Vector4(Sample(new Vector2(-step, 0)).X, Sample(new Vector2(step, 0)).X,
            Sample(new Vector2(0, -step)).X, Sample(new Vector2(0, step)).X);
        return pass.Stage switch
        {
            OblivionWaterSimulationStage.RainEvolution or OblivionWaterSimulationStage.WadingEvolution =>
                OblivionWaterSimulationMath.Evolve(Sample(Vector2.Zero), cardinal, pass.Controls),
            OblivionWaterSimulationStage.Normal => OblivionWaterSimulationMath.EncodeNormal(cardinal,
                new Vector4(Sample(new Vector2(-step, -step)).X, Sample(new Vector2(step, -step)).X,
                    Sample(new Vector2(-step, step)).X, Sample(new Vector2(step, step)).X), pass.Dampener),
            OblivionWaterSimulationStage.FftNormal => OblivionWaterSimulationMath.EncodeNormal(cardinal,
                new Vector4(Sample(new Vector2(-step, -step)).X, Sample(new Vector2(step, -step)).X,
                    Sample(new Vector2(-step, step)).X, Sample(new Vector2(step, step)).X), 0.8f),
            OblivionWaterSimulationStage.FftAbsoluteHeight => AbsoluteHeight(Sample(Vector2.Zero).X),
            OblivionWaterSimulationStage.MixedHeight => OblivionWaterSimulationMath.BlendHeight(
                Sample(Vector2.Zero).X,
                SampleBilinear(preceding[pass.Input1!.Value], uv, pass.Address).X, pass.Dampener, pass.BlendAmount),
            OblivionWaterSimulationStage.Recenter => OblivionWaterSimulationMath.Recenter(uv,
                Sample(pass.Offset)),
            _ => throw new InvalidOperationException("The recorded stage is outside the numerical readback cohort.")
        };
    }

    private static Vector4 AbsoluteHeight(float value)
    {
        var height = MathF.Abs(value);
        return new Vector4(height, height, height, 1f);
    }

    private static Vector4 SampleBilinear(Vector4[] source, Vector2 uv, OblivionWaterSimulationAddress address)
    {
        var sourceSize = (int)Math.Sqrt(source.Length);
        var texel = uv * sourceSize - new Vector2(0.5f);
        var left = (int)MathF.Floor(texel.X);
        var top = (int)MathF.Floor(texel.Y);
        var weight = texel - new Vector2(left, top);

        int Coordinate(int value)
        {
            return address == OblivionWaterSimulationAddress.Clamp
                ? Math.Clamp(value, 0, sourceSize - 1)
                : (value % sourceSize + sourceSize) % sourceSize;
        }

        Vector4 At(int x, int y)
        {
            return source[Coordinate(y) * sourceSize + Coordinate(x)];
        }

        return Vector4.Lerp(Vector4.Lerp(At(left, top), At(left + 1, top), weight.X),
            Vector4.Lerp(At(left, top + 1), At(left + 1, top + 1), weight.X), weight.Y);
    }

    internal static (int Maximum, int DifferentChannels, int OutsideTolerance) Difference(
        byte[] actual, byte[] expected, bool normal, int tolerance)
    {
        var maximum = 0;
        var different = 0;
        var outside = 0;
        var stride = normal ? 1 : 2;
        for (var offset = 0; offset < actual.Length; offset += stride)
        {
            var delta = normal
                ? Math.Abs(actual[offset] - expected[offset])
                : Math.Abs(ReadWord(actual, offset) - ReadWord(expected, offset));
            maximum = Math.Max(maximum, delta);
            if (delta > 0) different++;
            if (delta > tolerance) outside++;
        }

        return (maximum, different, outside);
    }

    private static ushort ReadWord(byte[] bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    }

    private static void Encode(byte[] bytes, int pixel, Vector4 value, bool normal)
    {
        var maximum = normal ? 255 : 65535;
        var stride = normal ? 1 : 2;
        for (var channel = 0; channel < 4; channel++)
        {
            var component = channel switch { 0 => value.X, 1 => value.Y, 2 => value.Z, _ => value.W };
            var word = (ushort)Math.Round((double)Math.Clamp(component, 0f, 1f) * maximum,
                MidpointRounding.ToEven);
            var offset = (pixel * 4 + channel) * stride;
            if (normal) bytes[offset] = (byte)word;
            else BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), word);
        }
    }
}