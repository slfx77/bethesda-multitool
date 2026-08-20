namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Synthesizes the Oblivion water-surface animation frames. Retail TES4 ships NO
///     <c>textures\water\water00-31.dds</c> — the engine GENERATES its 32-frame surface sequence at
///     runtime from the ini <c>[Water]</c> settings (<c>SSurfaceTexture=water</c>,
///     <c>uSurfaceFrameCount=32</c>, <c>uSurfaceFPS=12</c>, <c>uSurfaceTextureSize=128</c>,
///     <c>fSurfaceTileSize=2048</c>). This produces an equivalent seamless loop of tileable RGBA8
///     normal maps for the viewer's existing legacy-frame plumbing.
///     <para>
///         The wave field is an UN-DECOMPILED STAND-IN (the engine's generator has not been
///         decompiled yet — see the decompile-first mandate): a fixed sum of sine waves whose wave
///         vectors are integer per-texture cycle counts (exact spatial tiling) and whose phases
///         advance an integer number of cycles across the frame loop (exact temporal loop, frame N
///         == frame N+FrameCount). Amplitudes are normalized so the encoded normal tilt stays in
///         the gentle-ripple range the engine's surface shows.
///     </para>
/// </summary>
internal static class OblivionWaterSurfaceSynthesizer
{
    public const int FrameCount = 32; // ini uSurfaceFrameCount
    public const int TextureSize = 128; // ini uSurfaceTextureSize

    /// <summary>Peak normal XY tilt at the default strength (≈ gentle-ripple slope).</summary>
    private const float BaseTilt = 0.35f;

    // Flat-spectrum multi-octave wave table (rebuilt 2026-08-17 per the adversarial-review
    // verdict: the previous FIVE low-integer waves WERE the reported checkerboard — the dominant
    // wavelength was ≈ tile/2.2, so the surface was literally a lattice of identical glyphs,
    // amplified by pow(·, SunPower) over a near-black body). Sixteen waves across four octave
    // bands with the LOWEST at ≥ 8 cycles/tile (largest feature ≈ tile/8 ≈ 171 world units at the
    // WATER007 4096/3-unit surface tile) — no single wave is large enough to read as a repeating
    // glyph, and the bands overlap into broadband chop. Directions are azimuthally spread; per-wave
    // phase offsets decorrelate waves sharing a temporal rate. Integer spatial cycle counts keep
    // every frame seamlessly tileable and integer temporal cycle counts keep the 32-frame loop
    // exact (both invariants unchanged); temporal rates grow ≈ √k for a deep-water dispersion
    // flavor. The highest band tops out at 32 cycles = 4 texels/cycle at 128², clear of Nyquist.
    internal static readonly (int Kx, int Ky, int Cycles, float Amplitude, float Phase)[] Waves =
    [
        // ~8-10 cycles/tile
        (8, 1, 2, 0.13f, 0.00f),
        (5, -7, 2, 0.11f, 0.29f),
        (-3, 9, 2, 0.12f, 0.61f),
        (-9, -4, 2, 0.10f, 0.83f),
        // ~13-15 cycles/tile
        (12, 5, 3, 0.09f, 0.13f),
        (-14, 3, 3, 0.08f, 0.47f),
        (7, -13, 3, 0.08f, 0.71f),
        (-10, -11, 3, 0.07f, 0.91f),
        // ~20-24 cycles/tile
        (19, 8, 4, 0.06f, 0.07f),
        (-6, 21, 4, 0.06f, 0.37f),
        (-17, -14, 4, 0.05f, 0.59f),
        (23, -5, 4, 0.05f, 0.79f),
        // ~27-32 cycles/tile
        (26, 9, 5, 0.04f, 0.23f),
        (-11, 28, 5, 0.04f, 0.43f),
        (-24, -19, 5, 0.035f, 0.67f),
        (30, -8, 5, 0.035f, 0.97f)
    ];

    /// <summary>
    ///     Generates <see cref="FrameCount" /> RGBA8 normal-map frames of
    ///     <see cref="TextureSize" />² texels each (encoded n·0.5+0.5, opaque alpha).
    /// </summary>
    public static byte[][] GenerateFrames()
    {
        var frames = new byte[FrameCount][];
        for (var frame = 0; frame < FrameCount; frame++)
        {
            frames[frame] = GenerateFrame(frame);
        }

        return frames;
    }

    internal static byte[] GenerateFrame(int frame)
    {
        var pixels = new byte[TextureSize * TextureSize * 4];
        // Reduce modulo the loop FIRST so frame N+FrameCount is bit-identical to frame N (adding
        // whole 2π cycles to the float phase instead drifts the last ULP).
        var t = (frame % FrameCount + FrameCount) % FrameCount / (float)FrameCount;
        for (var y = 0; y < TextureSize; y++)
        {
            var v = y / (float)TextureSize;
            for (var x = 0; x < TextureSize; x++)
            {
                var u = x / (float)TextureSize;

                // Analytic height gradient of the summed wave field: exact, so the encoded normals
                // are as periodic as the height field itself (no finite-difference edge seams).
                var dhdx = 0f;
                var dhdy = 0f;
                foreach (var (kx, ky, cycles, amplitude, phaseOffset) in Waves)
                {
                    var phase = MathF.Tau * (kx * u + ky * v + cycles * t + phaseOffset);
                    var slope = amplitude * MathF.Cos(phase);
                    // Normalize by the wave-vector magnitude so every component contributes the
                    // same peak SLOPE regardless of its spatial frequency.
                    var invMagnitude = 1f / MathF.Sqrt(kx * kx + ky * ky);
                    dhdx += slope * kx * invMagnitude;
                    dhdy += slope * ky * invMagnitude;
                }

                var nx = -dhdx * BaseTilt;
                var ny = -dhdy * BaseTilt;
                var invLen = 1f / MathF.Sqrt(nx * nx + ny * ny + 1f);
                var offset = (y * TextureSize + x) * 4;
                pixels[offset] = EncodeUnorm(nx * invLen);
                pixels[offset + 1] = EncodeUnorm(ny * invLen);
                pixels[offset + 2] = EncodeUnorm(invLen);
                pixels[offset + 3] = 255;
            }
        }

        return pixels;
    }

    private static byte EncodeUnorm(float component)
    {
        return (byte)Math.Clamp((int)MathF.Round((component * 0.5f + 0.5f) * 255f), 0, 255);
    }
}
