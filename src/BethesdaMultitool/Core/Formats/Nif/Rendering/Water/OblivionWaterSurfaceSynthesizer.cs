using System.Collections.Concurrent;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Orchestration;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     CPU reproduction of Oblivion's runtime water-height generator, adapted to the viewer's
///     existing 32-frame normal-map input.
/// </summary>
/// <remarks>
///     Retail does not ship <c>textures\water\water00-31.dds</c>. The recovered PC executable
///     builds a Phillips/Tessendorf spectrum in <c>FUN_007E0840</c>/<c>FUN_007DF640</c>, evolves it
///     and runs a two-dimensional FFT with WATERHMAP000..004, then converts the absolute height
///     field to an encoded normal with WATERHMAP005. <c>bUseWaterHiRes</c> selects either a 128² /
///     seven-stage or 256² / eight-stage grid without changing the recovered 2*pi/128 wave-number
///     step. The high-resolution seed also retains the executable's 32-wide edge/corner copy rules.
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
internal static partial class OblivionWaterSurfaceSynthesizer
{
    // Oblivion_default.ini [Water]. The application targets the installed retail profile recorded
    // in RendererInfo.txt ("Water high res : yes"); callers can select low resolution for a
    // controlled bUseWaterHiRes=0 comparison.
    public const int FrameCount = 32;
    public const int FramesPerSecond = 12;
    public const int LowResolutionTextureSize = 128;
    public const int HighResolutionTextureSize = 256;
    public const int TextureSize = HighResolutionTextureSize;
    public const bool DefaultUseHighResolution = true;

    // FUN_007E0840's high-resolution branch copies only three 32-wide corner regions. Positive-edge
    // coordinates 225..256 map to 33..64 through coordinate + 64 - N; the rest are generated.
    internal const int HighResolutionEdgeWidth = 32;
    internal const int HighResolutionCopyOffset = 64;

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
        DefaultWaveFrequency,
        HighResolutionTextureSize);

    private static readonly ConcurrentDictionary<SurfaceSettings, Lazy<byte[][]>> FramesBySettings = new();

    /// <summary>
    ///     Returns the immutable, process-cached default-water sequence. Consumers upload the bytes
    ///     but do not mutate them, so retaining one 8 MiB sequence avoids rebuilding 32 FFTs on each
    ///     worldspace load.
    /// </summary>
    public static byte[][] GenerateFrames()
    {
        return GenerateFrames(DefaultSettings);
    }

    /// <summary>
    ///     Generates (or retrieves) the sequence for the active TES4 WATR. FUN_00499570 copies
    ///     these four DATA fields into the HMAP globals and rebuilds the spectrum when any changes;
    ///     constructor defaults apply only when no water material is available.
    /// </summary>
    public static byte[][] GenerateFrames(WaterSurfaceParams? surface)
    {
        return GenerateFrames(surface, DefaultUseHighResolution);
    }

    internal static byte[][] GenerateFrames(WaterSurfaceParams? surface, bool useHighResolution)
    {
        return GenerateFrames(ResolveSettings(surface, useHighResolution));
    }

    internal static string GetSettingsKey(WaterSurfaceParams? surface)
    {
        return GetSettingsKey(surface, DefaultUseHighResolution);
    }

    internal static string GetSettingsKey(WaterSurfaceParams? surface, bool useHighResolution)
    {
        var settings = ResolveSettings(surface, useHighResolution);
        return $"n{settings.GridResolution}-" +
               $"{BitConverter.SingleToUInt32Bits(settings.WindVelocity):X8}-" +
               $"{BitConverter.SingleToUInt32Bits(settings.WindDirectionDegrees):X8}-" +
               $"{BitConverter.SingleToUInt32Bits(settings.WaveAmplitude):X8}-" +
               $"{BitConverter.SingleToUInt32Bits(settings.WaveFrequency):X8}";
    }

    internal static int GetTextureSize(bool useHighResolution)
    {
        return useHighResolution ? HighResolutionTextureSize : LowResolutionTextureSize;
    }

    internal static byte[] GenerateFrame(int frame)
    {
        var wrappedFrame = (frame % FrameCount + FrameCount) % FrameCount;
        return GenerateFrames(DefaultSettings)[wrappedFrame];
    }

    internal static byte[] GenerateFrame(int frame, WaterSurfaceParams? surface)
    {
        var wrappedFrame = (frame % FrameCount + FrameCount) % FrameCount;
        return GenerateFrames(ResolveSettings(surface, DefaultUseHighResolution))[wrappedFrame];
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
        return waveAmplitude / AmplitudeDivisor *
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

    private static byte[][] GenerateFrames(SurfaceSettings settings)
    {
        return FramesBySettings.GetOrAdd(
            settings,
            static key => new Lazy<byte[][]>(
                () => GenerateFramesUncached(key),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static byte[][] GenerateFramesUncached(SurfaceSettings settings)
    {
        var seed = BuildSpectrumSeed(settings);
        var frames = new byte[FrameCount][];
        // Every evolved frame reads the same immutable spectrum seed. Build them concurrently so
        // the source-backed 256² profile does not turn a first worldspace selection into a long
        // single-core UI stall. This path is reached from the STA UI thread, so its join must not
        // use Parallel.For's COM-pumping wait (see NonPumpingParallel); all work is joined before
        // the immutable cached array is published.
        NonPumpingParallel.For(0, FrameCount, frame => frames[frame] = GenerateFrame(frame, seed));

        return frames;
    }

    private static SpectrumSeed BuildSpectrumSeed(SurfaceSettings settings)
    {
        var textureSize = settings.GridResolution;
        // FUN_007E15C0 allocates (N+1) rows of (N+1) scalars. FUN_007E06B0 uploads only N² output
        // texels, but reads the inclusive endpoints for h0(-k) when an output row/column is zero.
        var seedStride = textureSize + 1;
        var sampleCount = seedStride * seedStride;
        var amplitudes = new float[sampleCount];
        var loopCycles = new byte[sampleCount];
        var loopDurationSeconds = FrameCount / (float)FramesPerSecond;

        for (var y = 0; y <= textureSize; y++)
        {
            var latticeY = y - textureSize / 2;
            for (var x = 0; x <= textureSize; x++)
            {
                var latticeX = x - textureSize / 2;
                var index = y * seedStride + x;
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

                amplitudes[index] = ResolveSpectrumSeedAmplitude(
                    settings,
                    amplitudes,
                    seedStride,
                    x,
                    y,
                    latticeX,
                    latticeY);
            }
        }

        return new SpectrumSeed(textureSize, seedStride, amplitudes, loopCycles);
    }

    private static float ResolveSpectrumSeedAmplitude(
        SurfaceSettings settings,
        float[] precedingAmplitudes,
        int seedStride,
        int x,
        int y,
        int latticeX,
        int latticeY)
    {
        // bUseWaterHiRes leaves the negative/negative 32x32 corner at the memset zero and does not
        // generate three positive-edge corner regions. Retail copies those scalar h0 seeds from
        // coordinates +64-N after writing the destination coordinate's own omega. The source
        // rows/columns are always earlier in row-major order, so this direct lookup follows
        // FUN_007E0840's exact data dependency.
        if (settings.GridResolution == HighResolutionTextureSize)
        {
            if (IsHighResolutionSuppressedSeedCoordinate(x, y))
            {
                return 0f;
            }

            if (GetHighResolutionSpectrumCopySource(x, y) is { } source)
            {
                return precedingAmplitudes[source.Y * seedStride + source.X];
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
            return 0f;
        }

        // FUN_007E0840 multiplies FUN_007DF580's distributed sample by sqrt(P(k)) and by the cosine
        // of a second random phase. Reconstruct the executable's lookup-table distribution
        // continuously; only its process-global rand() position is unknowable.
        var randomState = MixCoordinates(x, y);
        var distributedSample = NextSpectrumSample(ref randomState);
        var phase = MathF.Tau * NextUnit(ref randomState);
        return distributedSample * MathF.Sqrt(spectrum) * MathF.Cos(phase);
    }

    /// <summary>
    ///     Retail's high-resolution branch skips the top-left 32x32 seed corner after clearing the
    ///     allocation. This is the direct <c>row &lt; 32 &amp;&amp; column &lt; 32</c> jump at 0x007E097D.
    /// </summary>
    internal static bool IsHighResolutionSuppressedSeedCoordinate(int x, int y)
    {
        ValidateHighResolutionCoordinate(x, y);
        return x < HighResolutionEdgeWidth && y < HighResolutionEdgeWidth;
    }

    /// <summary>
    ///     Returns the source coordinate used by the exact <c>bUseWaterHiRes</c> edge/corner copy,
    ///     or null when retail evaluates a new Phillips/random seed at this coordinate.
    /// </summary>
    internal static (int X, int Y)? GetHighResolutionSpectrumCopySource(int x, int y)
    {
        ValidateHighResolutionCoordinate(x, y);

        const int threshold = HighResolutionTextureSize - HighResolutionEdgeWidth;
        var xOnPositiveEdge = x > threshold;
        var yOnPositiveEdge = y > threshold;

        if (yOnPositiveEdge && x < HighResolutionEdgeWidth)
        {
            return (x, y + HighResolutionCopyOffset - HighResolutionTextureSize);
        }

        if (y < HighResolutionEdgeWidth && xOnPositiveEdge)
        {
            return (x + HighResolutionCopyOffset - HighResolutionTextureSize, y);
        }

        return xOnPositiveEdge && yOnPositiveEdge
            ? (x + HighResolutionCopyOffset - HighResolutionTextureSize,
                y + HighResolutionCopyOffset - HighResolutionTextureSize)
            : null;
    }

    private static void ValidateHighResolutionCoordinate(int x, int y)
    {
        if ((uint)x > HighResolutionTextureSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x), x,
                $"Coordinate must be on the inclusive 0..{HighResolutionTextureSize} seed lattice.");
        }

        if ((uint)y > HighResolutionTextureSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(y), y,
                $"Coordinate must be on the inclusive 0..{HighResolutionTextureSize} seed lattice.");
        }
    }

    private static byte[] GenerateFrame(int frame, SpectrumSeed seed)
    {
        var textureSize = seed.GridResolution;
        var seedStride = seed.SeedStride;
        var real = new float[textureSize * textureSize];
        var imaginary = new float[real.Length];
        var frameFraction = frame / (float)FrameCount;

        for (var y = 0; y < textureSize; y++)
        {
            for (var x = 0; x < textureSize; x++)
            {
                var index = y * textureSize + x;
                var seedIndex = y * seedStride + x;
                var opposite = GetOppositeSeedCoordinate(x, y, textureSize);
                var oppositeSeedIndex = opposite.Y * seedStride + opposite.X;
                var phase = MathF.Tau * seed.LoopCycles[seedIndex] * frameFraction;
                var cosine = MathF.Cos(phase);
                var sine = MathF.Sin(phase);
                var h0 = seed.Amplitudes[seedIndex];
                var h0Opposite = seed.Amplitudes[oppositeSeedIndex];

                // WATERHMAP000 evolves h0(-k) * exp(+iwt) + conjugate(h0(k)) * exp(-iwt).
                // FUN_007E06B0 duplicates each scalar seed into real and imaginary channels, which
                // yields the following exact reduced form for the shader's complex output.
                real[index] = (h0Opposite + h0) * (cosine - sine);
                imaginary[index] = (h0Opposite - h0) * (sine + cosine);
            }
        }

        InverseFft2D(real, imaginary, textureSize);
        return EncodeNormalMap(real, textureSize);
    }

    /// <summary>
    ///     Exact inclusive-endpoint lookup used by FUN_007E06B0 for h0(-k). It is deliberately
    ///     <c>N-coordinate</c>, not modulo N: output coordinate zero reads seed endpoint N.
    /// </summary>
    internal static (int X, int Y) GetOppositeSeedCoordinate(int x, int y, int textureSize)
    {
        if ((uint)x >= (uint)textureSize) throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)y >= (uint)textureSize) throw new ArgumentOutOfRangeException(nameof(y));
        return (textureSize - x, textureSize - y);
    }

    private static void InverseFft2D(float[] real, float[] imaginary, int textureSize)
    {
        for (var y = 0; y < textureSize; y++)
        {
            InverseFft1D(real, imaginary, y * textureSize, 1, textureSize);
        }

        for (var x = 0; x < textureSize; x++)
        {
            InverseFft1D(real, imaginary, x, textureSize, textureSize);
        }

        // WATERHMAP001/002 butterfly passes contain additions and twiddle multiplies but no 1/N
        // factor; do not normalize the inverse transform here.
    }

    private static void InverseFft1D(
        float[] real,
        float[] imaginary,
        int offset,
        int stride,
        int textureSize)
    {
        var bitCount = textureSize == HighResolutionTextureSize ? 8 : 7;
        for (var i = 1; i < textureSize; i++)
        {
            var reversed = ReverseBits(i, bitCount);
            if (i >= reversed)
            {
                continue;
            }

            var left = offset + i * stride;
            var right = offset + reversed * stride;
            (real[left], real[right]) = (real[right], real[left]);
            (imaginary[left], imaginary[right]) = (imaginary[right], imaginary[left]);
        }

        for (var length = 2; length <= textureSize; length <<= 1)
        {
            var angleStep = MathF.Tau / length;
            var stepReal = MathF.Cos(angleStep);
            var stepImaginary = MathF.Sin(angleStep);
            var halfLength = length >> 1;
            for (var block = 0; block < textureSize; block += length)
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

    private static byte[] EncodeNormalMap(float[] heights, int textureSize)
    {
        // FUN_0049D7B0 always binds TESWaterSystem+0x10 for ordinary above-water rendering. That
        // target is allocated at the active FFT size with NiTexture::FormatPrefs TRUE_COLOR_32 and
        // one level. Selector case 8's A16B16G16R16 target is instead +0x0C, the temporary filtered
        // height map used only while a water-type blend is active.
        const int bytesPerPixel = 4;
        var pixels = new byte[textureSize * textureSize * bytesPerPixel];
        for (var y = 0; y < textureSize; y++)
        {
            var north = (y + textureSize - 1) % textureSize;
            var south = (y + 1) % textureSize;
            for (var x = 0; x < textureSize; x++)
            {
                var west = (x + textureSize - 1) % textureSize;
                var east = (x + 1) % textureSize;
                var normal = ComputeNormal(
                    heights[north * textureSize + west],
                    heights[north * textureSize + x],
                    heights[north * textureSize + east],
                    heights[y * textureSize + west],
                    heights[y * textureSize + east],
                    heights[south * textureSize + west],
                    heights[south * textureSize + x],
                    heights[south * textureSize + east]);
                var pixel = (y * textureSize + x) * bytesPerPixel;
                pixels[pixel] = EncodeUnorm8(normal.X);
                pixels[pixel + 1] = EncodeUnorm8(normal.Y);
                pixels[pixel + 2] = EncodeUnorm8(normal.Z);
                pixels[pixel + 3] = byte.MaxValue;
            }
        }

        return pixels;
    }

    internal static int ReverseBits(int value, int bitCount)
    {
        if (bitCount is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(bitCount));
        var reversed = 0;
        for (var bit = 0; bit < bitCount; bit++)
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

    internal static float TransformSpectrumSample(float standardNormal)
    {
        return standardNormal < 0f ? 1f + standardNormal : standardNormal;
    }

    private static SurfaceSettings ResolveSettings(
        WaterSurfaceParams? surface,
        bool useHighResolution)
    {
        var textureSize = GetTextureSize(useHighResolution);
        if (surface is null)
        {
            return DefaultSettings with { GridResolution = textureSize };
        }

        return new SurfaceSettings(
            NormalizeNonNegative(surface.WindVelocity, DefaultWindVelocity),
            float.IsFinite(surface.WindDirection) ? surface.WindDirection : DefaultWindDirectionDegrees,
            NormalizeNonNegative(surface.WaveAmplitude, DefaultWaveAmplitude),
            NormalizeNonNegative(surface.WaveFrequency, DefaultWaveFrequency),
            textureSize);
    }

    private static float NormalizeNonNegative(float value, float fallback)
    {
        if (!float.IsFinite(value) || value < 0f)
        {
            return fallback;
        }

        // A negative-zero WATR field is numerically valid but would otherwise create a second
        // bitwise cache identity. Inspect the representation directly: exact float equality is
        // deliberately avoided here because the analyzer correctly rejects it for general values.
        return BitConverter.SingleToUInt32Bits(value) == 0x80000000u ? 0f : value;
    }

    internal static byte EncodeUnorm8(float component)
    {
        return (byte)Math.Clamp(
            (int)MathF.Round((component * 0.5f + 0.5f) * byte.MaxValue),
            byte.MinValue,
            byte.MaxValue);
    }

    private readonly record struct SurfaceSettings(
        float WindVelocity,
        float WindDirectionDegrees,
        float WaveAmplitude,
        float WaveFrequency,
        int GridResolution);

    private sealed record SpectrumSeed(
        int GridResolution,
        int SeedStride,
        float[] Amplitudes,
        byte[] LoopCycles);
}
