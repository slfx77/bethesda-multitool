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
    public const int FrameCount = 32;  // ini uSurfaceFrameCount
    public const int TextureSize = 128; // ini uSurfaceTextureSize

    // (cyclesX, cyclesY, temporalCycles, relativeAmplitude): integer spatial cycle counts per
    // texture tile keep every frame seamlessly tileable; integer temporal cycle counts keep the
    // 32-frame loop seamless. A small diverse set reads as choppy low-frequency water rather than
    // a single rolling sine.
    private static readonly (int Kx, int Ky, int Cycles, float Amplitude)[] Waves =
    [
        (1, 2, 1, 1.00f),
        (3, -1, 2, 0.55f),
        (-2, 3, 1, 0.40f),
        (5, 4, 3, 0.22f),
        (-4, -6, 2, 0.16f),
    ];

    /// <summary>Peak normal XY tilt at the default strength (≈ gentle-ripple slope).</summary>
    private const float BaseTilt = 0.35f;

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
        var t = ((frame % FrameCount) + FrameCount) % FrameCount / (float)FrameCount;
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
                foreach (var (kx, ky, cycles, amplitude) in Waves)
                {
                    var phase = MathF.Tau * ((kx * u) + (ky * v) + (cycles * t));
                    var slope = amplitude * MathF.Cos(phase);
                    // Normalize by the wave-vector magnitude so every component contributes the
                    // same peak SLOPE regardless of its spatial frequency.
                    var invMagnitude = 1f / MathF.Sqrt((kx * kx) + (ky * ky));
                    dhdx += slope * kx * invMagnitude;
                    dhdy += slope * ky * invMagnitude;
                }

                var nx = -dhdx * BaseTilt;
                var ny = -dhdy * BaseTilt;
                var invLen = 1f / MathF.Sqrt((nx * nx) + (ny * ny) + 1f);
                var offset = ((y * TextureSize) + x) * 4;
                pixels[offset] = EncodeUnorm(nx * invLen);
                pixels[offset + 1] = EncodeUnorm(ny * invLen);
                pixels[offset + 2] = EncodeUnorm(invLen);
                pixels[offset + 3] = 255;
            }
        }

        return pixels;
    }

    private static byte EncodeUnorm(float component) =>
        (byte)Math.Clamp((int)MathF.Round((component * 0.5f + 0.5f) * 255f), 0, 255);
}
