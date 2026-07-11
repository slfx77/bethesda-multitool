using System.Numerics;

namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>
///     The engine's leaf rock/rustle wind driver — a faithful port of
///     <c>BSTreeManager::UpdateWindMatrices</c> (360 0x824986A8 ≡ PC FUN_006658b0) +
///     <c>SpeedTreeLeafShader::UpdateWindTimers</c> (360 0x82AA0B80), per
///     <c>tools/GhidraProject/speedtree_wind_design.md</c>. Four sin/cos oscillators run at
///     <c>freq · 20 · S</c> with per-oscillator time rescaling (<c>foldStrength/S</c>) for phase
///     continuity across wind-strength changes; oscillator 2's <c>|sin|+|cos|</c> is the frame's
///     "sway" gustiness, which modulates both the rock/rustle amplitudes and their timer rates.
///     <para>
///         Inputs the engine uses that we mirror: wind strength S = weather wind-speed byte / 255
///         (0 = perfectly static). The six <c>fLeaf*</c> settings come from <see cref="Profile" />
///         (per-game: FNV/FO3 compiled defaults vs Oblivion.esm GMSTs — see
///         <see cref="SpeedTreeWindProfile" />). Oblivion's driver (<c>FUN_0055e060</c>) is
///         instruction-identical to FNV's, oscillator constants byte-identical; only the setting
///         values differ. The .spt's own 1011 wind section is DEAD DATA in both engines (parsed
///         then bypassed by <c>SetWindStrength(0, −1, −1)</c>); do not feed it in.
///     </para>
/// </summary>
public sealed class SpeedTreeWindRig
{
    // ffrequencies[4][2] — byte-read from both FalloutNV binaries (360 0x832458D0 / PC 0x119B8CC)
    // AND byte-identical in Oblivion.exe (0x00B12538).
    private static readonly (float Sin, float Cos)[] Frequencies =
    [
        (0.15f, 0.17f), (0.25f, 0.15f), (0.19f, 0.05f), (0.15f, 0.22f),
    ];

    /// <summary>Per-game <c>fLeaf*</c> setting values. Swap when the loaded game changes.</summary>
    public SpeedTreeWindProfile Profile { get; set; } = SpeedTreeWindProfile.FalloutNewVegas;

    // The tilt scale each oscillator's sin/cos pair is multiplied by before becoming a wind-matrix
    // yaw/pitch angle — the 0.61 literal in BSTreeManager::UpdateWindMatrices (both binaries).
    private const float MatrixTiltScale = 0.61f;

    private readonly float[] _matrixTimes = new float[4];
    private readonly Matrix4x4[] _windMatrices =
        [Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity];
    private float _foldStrength;
    private float _rockTime;
    private float _rustleTime;
    private float _lastTimeSeconds = float.NaN;

    /// <summary>Rock amplitude (engine <c>RockParams.x</c> = S · swayFac(RockAmountSwayInfluence)).</summary>
    public float RockAmount { get; private set; }

    /// <summary>Rustle amplitude (engine <c>RustleParams.x</c>).</summary>
    public float RustleAmount { get; private set; }

    /// <summary>Rock phase (engine <c>RockParams.y</c> with the TREE CNAM RockSpeed multiplier
    /// held at 1.0 — the per-tree speeds are a deferred per-batch refinement).</summary>
    public float RockPhase => _rockTime;

    /// <inheritdoc cref="RockPhase" />
    public float RustlePhase => _rustleTime;

    /// <summary>
    ///     The wind matrix for slot <paramref name="index" /> (0..3) — the slow whole-canopy sway layer.
    ///     <c>SpeedTreeShader::SetMatrixRotation</c> (PC FUN_00bb2fc0, tail calls byte-verified to
    ///     <c>D3DXMatrixRotationYawPitchRoll(yaw = 0.61·S·sinOsc, pitch = 0.61·S·cosOsc, roll = 0)</c>) —
    ///     a pure rotation about the two model-space horizontal axes (Gamebryo trees are Z-up), i.e. a
    ///     small trunk tilt around the tree origin. Vertices lerp toward the tilted position by their
    ///     per-vertex wind weight. Identity while S = 0 (a perfectly static tree).
    /// </summary>
    public Matrix4x4 WindMatrix(int index) => _windMatrices[index];

    /// <summary>
    ///     Advance the oscillators to <paramref name="timeSeconds" /> at wind strength
    ///     <paramref name="strength" /> (0..1). Call once per rendered frame; large gaps (pauses,
    ///     first frame) are clamped to a single 100 ms step so the timers never jump.
    /// </summary>
    public void Tick(float strength, float timeSeconds)
    {
        var s = Math.Clamp(strength, 0f, 1f);
        var dt = float.IsNaN(_lastTimeSeconds) ? 0f : Math.Clamp(timeSeconds - _lastTimeSeconds, 0f, 0.1f);
        _lastTimeSeconds = timeSeconds;

        var sway = 0f;
        for (var i = 0; i < 4; i++)
        {
            _matrixTimes[i] += dt;
            if (s > 0f)
            {
                // Phase-continuity rescale: keeps freq·20·S·t continuous when S changes mid-flight.
                _matrixTimes[i] *= _foldStrength / s;
            }

            var w = s * 20f;
            var a = MathF.Sin(Frequencies[i].Sin * w * _matrixTimes[i]);
            var b = MathF.Cos(Frequencies[i].Cos * w * _matrixTimes[i]);
            if (i == 2)
            {
                sway = MathF.Abs(a) + MathF.Abs(b);
            }

            // System.Numerics CreateFromYawPitchRoll shares D3DX's roll→pitch→yaw application order,
            // so this IS D3DXMatrixRotationYawPitchRoll(0.61·S·a, 0.61·S·b, 0) — sans the engine's
            // upload transpose, which our HLSL mul() convention doesn't need.
            _windMatrices[i] = s > 0f
                ? Matrix4x4.CreateFromYawPitchRoll(MatrixTiltScale * s * a, MatrixTiltScale * s * b, 0f)
                : Matrix4x4.Identity;
        }

        _foldStrength = s;

        var p = Profile;
        RockAmount = s * SpeedTreeWindProfile.SwayFactor(p.RockAmountSwayInfluence, sway);
        RustleAmount = s * SpeedTreeWindProfile.SwayFactor(p.RustleAmountSwayInfluence, sway);
        _rockTime += dt * p.RockTimeScale * SpeedTreeWindProfile.SwayFactor(p.RockSpeedSwayInfluence, sway);
        _rustleTime += dt * p.RustleTimeScale * SpeedTreeWindProfile.SwayFactor(p.RustleSpeedSwayInfluence, sway);
    }
}
