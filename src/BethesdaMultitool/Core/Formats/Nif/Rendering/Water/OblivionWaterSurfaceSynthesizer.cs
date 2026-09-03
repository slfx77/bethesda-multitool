using System.Collections.Concurrent;
using System.Threading;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     CPU reproduction of Oblivion's runtime water-height generator, adapted to the viewer's
///     existing 32-frame normal-map input.
/// </summary>
/// <remarks>
///     Retail does not ship <c>textures\water\water00-31.dds</c>. The recovered PC executable
///     builds a 128² Phillips/Tessendorf spectrum in <c>FUN_007E0840</c>/<c>FUN_007DF640</c>,
///     evolves it and runs a seven-stage two-dimensional FFT with WATERHMAP000..004, then converts
///     the absolute height field to an encoded normal with WATERHMAP005. This implementation keeps
///     those recovered spatial equations and the shader's exact normal kernel.
///     <para>
///         Retail evolves the height map continuously. The viewer currently accepts a legacy
///         32-frame sequence at 12 FPS, so each recovered deep-water dispersion rate is rounded to
///         the nearest whole number of cycles in that sequence (with one cycle as the non-static
///         floor). That is the sole surface-model approximation here and makes frame N exactly
///         equal to frame N+32. The deterministic random seed substitutes for retail's process-wide
///         C <c>rand()</c> state, whose state depends on unrelated calls and cannot be recovered from
///         the executable alone; it changes the stochastic realization, not the authored spectrum.
///     </para>
/// </remarks>
internal static class OblivionWaterSurfaceSynthesizer
{
    // Oblivion_default.ini [Water].
    public const int FrameCount = 32;
    public const int FramesPerSecond = 12;
    public const int TextureSize = 128;

    // Oblivion.exe FUN_007E0ED0 defaults and FUN_007DF640/FUN_007E0840 constants.
    internal const float DefaultWindVelocity = 5f;
    internal const float DefaultWindDirectionDegrees = 90f;
    internal const float DefaultWaveAmplitude = 0.5f;
    internal const float DefaultWaveFrequency = 1f;
    internal const float Gravity = 9.81f;
    internal const float AmplitudeDivisor = 100000f;
    internal const float ShortWaveDampingDivisor = 75f;
    internal const float WaveNumberStep = 0.04908738657832146f; // 2*pi/128

    // WATERHMAP005 constants c0.x/c0.y. Its eight taps are a Sobel kernel scaled by 0.8.
    internal const float DiagonalNormalWeight = 0.8f;
    internal const float AxialNormalWeight = 1.6f;

    private static readonly SurfaceSettings DefaultSettings = new(
        DefaultWindVelocity,
        DefaultWindDirectionDegrees,
        DefaultWaveAmplitude,
        DefaultWaveFrequency);

    private static readonly ConcurrentDictionary<SurfaceSettings, Lazy<byte[][]>> FramesBySettings = new();

    /// <summary>
    ///     Returns the immutable, process-cached default-water sequence. Consumers upload the bytes
    ///     but do not mutate them, so retaining one 2 MiB sequence avoids rebuilding 32 FFTs on each
    ///     worldspace load.
    /// </summary>
    public static byte[][] GenerateFrames() => GenerateFrames(DefaultSettings);

    /// <summary>
    ///     Generates (or retrieves) the sequence for the active TES4 WATR. FUN_00499570 copies
    ///     these four DATA fields into the HMAP globals and rebuilds the spectrum when any changes;
    ///     constructor defaults apply only when no water material is available.
    /// </summary>
    public static byte[][] GenerateFrames(WaterSurfaceParams? surface) =>
        GenerateFrames(ResolveSettings(surface));

    internal static string GetSettingsKey(WaterSurfaceParams? surface)
    {
        var settings = ResolveSettings(surface);
        return $"{BitConverter.SingleToUInt32Bits(settings.WindVelocity):X8}-" +
               $"{BitConverter.SingleToUInt32Bits(settings.WindDirectionDegrees):X8}-" +
               $"{BitConverter.SingleToUInt32Bits(settings.WaveAmplitude):X8}-" +
               $"{BitConverter.SingleToUInt32Bits(settings.WaveFrequency):X8}";
    }

    internal static byte[] GenerateFrame(int frame)
    {
        var wrappedFrame = (frame % FrameCount + FrameCount) % FrameCount;
        return GenerateFrames(DefaultSettings)[wrappedFrame];
    }

    internal static byte[] GenerateFrame(int frame, WaterSurfaceParams? surface)
    {
        var wrappedFrame = (frame % FrameCount + FrameCount) % FrameCount;
        return GenerateFrames(ResolveSettings(surface))[wrappedFrame];
    }

    /// <summary>
    ///     Evaluates the exact recovered Phillips spectrum for a centered integer lattice mode.
    ///     Wind direction follows the executable's <c>sin(theta)*kx + cos(theta)*ky</c> convention;
    ///     modes opposite the wind are clamped to zero before the conjugate pair is assembled.
    /// </summary>
    internal static float EvaluatePhillipsSpectrum(
        int latticeX,
        int latticeY,
        float windVelocity = DefaultWindVelocity,
        float windDirectionDegrees = DefaultWindDirectionDegrees,
        float waveAmplitude = DefaultWaveAmplitude)
    {
        if ((latticeX == 0 && latticeY == 0) ||
            !float.IsFinite(windVelocity) || windVelocity <= 0f ||
            !float.IsFinite(windDirectionDegrees) ||
            !float.IsFinite(waveAmplitude) || waveAmplitude <= 0f)
        {
            return 0f;
        }

        var kx = latticeX * WaveNumberStep;
        var ky = latticeY * WaveNumberStep;
        var kSquared = kx * kx + ky * ky;
        var windAngle = windDirectionDegrees * (MathF.PI / 180f);
        var windDotK = MathF.Sin(windAngle) * kx + MathF.Cos(windAngle) * ky;
        if (windDotK < 0f)
        {
            return 0f;
        }

        var largeWaveLength = windVelocity * windVelocity / Gravity;
        var shortWaveLength = largeWaveLength / ShortWaveDampingDivisor;
        var directional = windDotK * windDotK;
        var longWaveCutoff = MathF.Exp(-1f / (kSquared * largeWaveLength * largeWaveLength));
        var shortWaveCutoff = MathF.Exp(-kSquared * shortWaveLength * shortWaveLength);
        return (waveAmplitude / AmplitudeDivisor) *
               longWaveCutoff * directional * shortWaveCutoff /
               (kSquared * kSquared * kSquared);
    }

    /// <summary>
    ///     Reproduces WATERHMAP005 for one neighborhood. Heights are made absolute before the
    ///     derivative, exactly as the eight shader <c>abs</c> instructions specify.
    /// </summary>
    internal static (float X, float Y, float Z) ComputeNormal(
        float northWest,
        float north,
        float northEast,
        float west,
        float east,
        float southWest,
        float south,
        float southEast)
    {
        var nw = MathF.Abs(northWest);
        var n = MathF.Abs(north);
        var ne = MathF.Abs(northEast);
        var w = MathF.Abs(west);
        var e = MathF.Abs(east);
        var sw = MathF.Abs(southWest);
        var s = MathF.Abs(south);
        var se = MathF.Abs(southEast);

        var gradientX = DiagonalNormalWeight * (ne + se - nw - sw) +
                        AxialNormalWeight * (e - w);
        var gradientY = DiagonalNormalWeight * (sw + se - nw - ne) +
                        AxialNormalWeight * (s - n);
        var x = -gradientX;
        var y = gradientY;
        var inverseLength = 1f / MathF.Sqrt(x * x + y * y + 1f);
        return (x * inverseLength, y * inverseLength, inverseLength);
    }

    private static byte[][] GenerateFrames(SurfaceSettings settings) =>
        FramesBySettings.GetOrAdd(
            settings,
            static key => new Lazy<byte[][]>(
                () => GenerateFramesUncached(key),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static byte[][] GenerateFramesUncached(SurfaceSettings settings)
    {
        var seed = BuildSpectrumSeed(settings);
        var frames = new byte[FrameCount][];
        for (var frame = 0; frame < FrameCount; frame++)
        {
            frames[frame] = GenerateFrame(frame, seed);
        }

        return frames;
    }

    private static SpectrumSeed BuildSpectrumSeed(SurfaceSettings settings)
    {
        var sampleCount = TextureSize * TextureSize;
        var amplitudes = new float[sampleCount];
        var loopCycles = new byte[sampleCount];
        var loopDurationSeconds = FrameCount / (float)FramesPerSecond;

        for (var y = 0; y < TextureSize; y++)
        {
            var latticeY = y - TextureSize / 2;
            for (var x = 0; x < TextureSize; x++)
            {
                var latticeX = x - TextureSize / 2;
                var index = y * TextureSize + x;
                if (latticeX != 0 || latticeY != 0)
                {
                    // FUN_007E0840 writes omega for every non-zero k, including the directional
                    // half whose P(k) is zero. Both members of a conjugate pair must therefore use
                    // the same rate or the inverse FFT would cease to be real-valued.
                    var kMagnitude = WaveNumberStep *
                                     MathF.Sqrt(latticeX * latticeX + latticeY * latticeY);
                    var angularFrequency = MathF.Sqrt(Gravity * kMagnitude) * settings.WaveFrequency;
                    if (angularFrequency > 0f)
                    {
                        loopCycles[index] = (byte)Math.Clamp(
                            (int)MathF.Round(angularFrequency * loopDurationSeconds / MathF.Tau),
                            1,
                            byte.MaxValue);
                    }
                }

                var spectrum = EvaluatePhillipsSpectrum(
                    latticeX,
                    latticeY,
                    settings.WindVelocity,
                    settings.WindDirectionDegrees,
                    settings.WaveAmplitude);
                if (spectrum <= 0f)
                {
                    continue;
                }

                // FUN_007E0840 multiplies FUN_007DF580's distributed sample by sqrt(P(k)) and by
                // the cosine of a second random phase. Reconstruct the executable's lookup-table
                // distribution continuously; only its process-global rand() position is unknowable.
                var randomState = MixCoordinates(x, y);
                var distributedSample = NextSpectrumSample(ref randomState);
                var phase = MathF.Tau * NextUnit(ref randomState);
                amplitudes[index] = distributedSample * MathF.Sqrt(spectrum) * MathF.Cos(phase);
            }
        }

        return new SpectrumSeed(amplitudes, loopCycles);
    }

    private static byte[] GenerateFrame(int frame, SpectrumSeed seed)
    {
        var real = new float[TextureSize * TextureSize];
        var imaginary = new float[real.Length];
        var frameFraction = frame / (float)FrameCount;

        for (var y = 0; y < TextureSize; y++)
        {
            var oppositeY = (TextureSize - y) % TextureSize;
            for (var x = 0; x < TextureSize; x++)
            {
                var oppositeX = (TextureSize - x) % TextureSize;
                var index = y * TextureSize + x;
                var oppositeIndex = oppositeY * TextureSize + oppositeX;
                var phase = MathF.Tau * seed.LoopCycles[index] * frameFraction;
                var cosine = MathF.Cos(phase);
                var sine = MathF.Sin(phase);
                var h0 = seed.Amplitudes[index];
                var h0Opposite = seed.Amplitudes[oppositeIndex];

                // WATERHMAP000 evolves h0(-k) * exp(+iwt) + conjugate(h0(k)) * exp(-iwt).
                // FUN_007E06B0 duplicates each scalar seed into real and imaginary channels, which
                // yields the following exact reduced form for the shader's complex output.
                real[index] = (h0Opposite + h0) * (cosine - sine);
                imaginary[index] = (h0Opposite - h0) * (sine + cosine);
            }
        }

        InverseFft2D(real, imaginary);
        return EncodeNormalMap(real);
    }

    private static void InverseFft2D(float[] real, float[] imaginary)
    {
        for (var y = 0; y < TextureSize; y++)
        {
            InverseFft1D(real, imaginary, y * TextureSize, 1);
        }

        for (var x = 0; x < TextureSize; x++)
        {
            InverseFft1D(real, imaginary, x, TextureSize);
        }

        // WATERHMAP001/002 butterfly passes contain additions and twiddle multiplies but no 1/N
        // factor; do not normalize the inverse transform here.
    }

    private static void InverseFft1D(float[] real, float[] imaginary, int offset, int stride)
    {
        for (var i = 1; i < TextureSize; i++)
        {
            var reversed = ReverseSevenBits(i);
            if (i >= reversed)
            {
                continue;
            }

            var left = offset + i * stride;
            var right = offset + reversed * stride;
            (real[left], real[right]) = (real[right], real[left]);
            (imaginary[left], imaginary[right]) = (imaginary[right], imaginary[left]);
        }

        for (var length = 2; length <= TextureSize; length <<= 1)
        {
            var angleStep = MathF.Tau / length;
            var stepReal = MathF.Cos(angleStep);
            var stepImaginary = MathF.Sin(angleStep);
            var halfLength = length >> 1;
            for (var block = 0; block < TextureSize; block += length)
            {
                var twiddleReal = 1f;
                var twiddleImaginary = 0f;
                for (var j = 0; j < halfLength; j++)
                {
                    var even = offset + (block + j) * stride;
                    var odd = offset + (block + j + halfLength) * stride;
                    var oddReal = real[odd] * twiddleReal - imaginary[odd] * twiddleImaginary;
                    var oddImaginary = real[odd] * twiddleImaginary + imaginary[odd] * twiddleReal;
                    var evenReal = real[even];
                    var evenImaginary = imaginary[even];
                    real[even] = evenReal + oddReal;
                    imaginary[even] = evenImaginary + oddImaginary;
                    real[odd] = evenReal - oddReal;
                    imaginary[odd] = evenImaginary - oddImaginary;

                    var nextReal = twiddleReal * stepReal - twiddleImaginary * stepImaginary;
                    twiddleImaginary = twiddleReal * stepImaginary + twiddleImaginary * stepReal;
                    twiddleReal = nextReal;
                }
            }
        }
    }

    private static byte[] EncodeNormalMap(float[] heights)
    {
        var pixels = new byte[TextureSize * TextureSize * 4];
        for (var y = 0; y < TextureSize; y++)
        {
            var north = (y + TextureSize - 1) % TextureSize;
            var south = (y + 1) % TextureSize;
            for (var x = 0; x < TextureSize; x++)
            {
                var west = (x + TextureSize - 1) % TextureSize;
                var east = (x + 1) % TextureSize;
                var normal = ComputeNormal(
                    heights[north * TextureSize + west],
                    heights[north * TextureSize + x],
                    heights[north * TextureSize + east],
                    heights[y * TextureSize + west],
                    heights[y * TextureSize + east],
                    heights[south * TextureSize + west],
                    heights[south * TextureSize + x],
                    heights[south * TextureSize + east]);
                var pixel = (y * TextureSize + x) * 4;
                pixels[pixel] = EncodeUnorm(normal.X);
                pixels[pixel + 1] = EncodeUnorm(normal.Y);
                pixels[pixel + 2] = EncodeUnorm(normal.Z);
                pixels[pixel + 3] = 255;
            }
        }

        return pixels;
    }

    private static int ReverseSevenBits(int value)
    {
        var reversed = 0;
        for (var bit = 0; bit < 7; bit++)
        {
            reversed = (reversed << 1) | (value & 1);
            value >>= 1;
        }

        return reversed;
    }

    private static uint MixCoordinates(int x, int y)
    {
        var value = 0xA341316Cu ^ ((uint)x * 0x9E3779B9u) ^ ((uint)y * 0x85EBCA6Bu);
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;
        return value == 0 ? 0x6D2B79F5u : value;
    }

    private static float NextUnit(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        // Keep the endpoints out of Box-Muller's logarithm while retaining 24 random mantissa bits.
        return ((state >> 8) + 0.5f) * (1f / 16777216f);
    }

    private static float NextSpectrumSample(ref uint state)
    {
        var u1 = NextUnit(ref state);
        var u2 = NextUnit(ref state);
        var standardNormal = MathF.Sqrt(-2f * MathF.Log(u1)) * MathF.Cos(MathF.Tau * u2);

        // Oblivion.exe .data VA 0x00B2D638 is a (z, normal-CDF(z)) table: e.g. (.01,.504),
        // (1.65,.9505), (2.33,.9901), plus a 99 sentinel. FUN_007DF580 returns z for the upper
        // half and 1-z for the mirrored lower half. A normal's sign is independent of |z|, so this
        // is the continuous equivalent without copying the table's coarse quantization/sentinel.
        return TransformSpectrumSample(standardNormal);
    }

    internal static float TransformSpectrumSample(float standardNormal) =>
        standardNormal < 0f ? 1f + standardNormal : standardNormal;

    private static SurfaceSettings ResolveSettings(WaterSurfaceParams? surface)
    {
        if (surface is null)
        {
            return DefaultSettings;
        }

        return new SurfaceSettings(
            NormalizeNonNegative(surface.WindVelocity, DefaultWindVelocity),
            float.IsFinite(surface.WindDirection) ? surface.WindDirection : DefaultWindDirectionDegrees,
            NormalizeNonNegative(surface.WaveAmplitude, DefaultWaveAmplitude),
            NormalizeNonNegative(surface.WaveFrequency, DefaultWaveFrequency));
    }

    private static float NormalizeNonNegative(float value, float fallback)
    {
        if (!float.IsFinite(value) || value < 0f)
        {
            return fallback;
        }

        return value == 0f ? 0f : value; // canonicalize -0 for the cache key
    }

    private readonly record struct SurfaceSettings(
        float WindVelocity,
        float WindDirectionDegrees,
        float WaveAmplitude,
        float WaveFrequency);

    private sealed record SpectrumSeed(float[] Amplitudes, byte[] LoopCycles);

    private static byte EncodeUnorm(float component)
    {
        return (byte)Math.Clamp((int)MathF.Round((component * 0.5f + 0.5f) * 255f), 0, 255);
    }
}
