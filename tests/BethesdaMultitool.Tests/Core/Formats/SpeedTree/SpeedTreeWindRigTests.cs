using System.Numerics;
using BethesdaMultitool.Core.Formats.SpeedTree;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.SpeedTree;

/// <summary>
///     Pins the engine leaf-wind driver formulas (FNV FUN_006658b0 ≡ Oblivion FUN_0055e060;
///     see tools/GhidraProject/speedtree_wind_design.md + speedtree_oblivion_wind_decompiled.txt).
///     Both engines share <c>swayFac(k) = k·sway·0.5 + (1−k)</c>; only the fLeaf* setting values
///     differ per game.
/// </summary>
public sealed class SpeedTreeWindRigTests
{
    private static SpeedTreeWindRig TickedRig(SpeedTreeWindProfile profile, float strength, int frames = 120)
    {
        var rig = new SpeedTreeWindRig { Profile = profile };
        for (var i = 0; i <= frames; i++)
        {
            rig.Tick(strength, i * (1f / 30f));
        }

        return rig;
    }

    [Fact]
    public void SwayFactor_AtInfluenceOne_IsHalfSway()
    {
        // FNV compiled defaults (k=1): swayFac = sway/2 — the two rival formulas coincide here.
        Assert.Equal(0.75f, SpeedTreeWindProfile.SwayFactor(1f, 1.5f), 6);
        Assert.Equal(0f, SpeedTreeWindProfile.SwayFactor(1f, 0f), 6);
    }

    [Fact]
    public void SwayFactor_AtLowInfluence_IsNearlySteady()
    {
        // Oblivion's shipped k=0.01: amount ≈ S regardless of gustiness (H1 — the (1−k) term
        // is present in both binaries; H2 (k·sway/2 → dead leaves) is decompile-refuted).
        Assert.Equal(0.99f, SpeedTreeWindProfile.SwayFactor(0.01f, 0f), 6);
        Assert.Equal(1.00f, SpeedTreeWindProfile.SwayFactor(0.01f, 2f), 6);
    }

    [Fact]
    public void SwayFactor_AboveOne_GoesNegativeInCalm()
    {
        // Oblivion fLeafRustleSpeedSwayInfluence = 1.5: at low sway the factor is negative —
        // the rustle phase timer legitimately runs backwards (authentic engine behavior).
        Assert.True(SpeedTreeWindProfile.SwayFactor(1.5f, 0.2f) < 0f);
        Assert.True(SpeedTreeWindProfile.SwayFactor(1.5f, 2f) > 0f);
    }

    [Fact]
    public void Tick_ZeroStrength_IsPerfectlyStatic()
    {
        foreach (var profile in new[] { SpeedTreeWindProfile.FalloutNewVegas, SpeedTreeWindProfile.Oblivion })
        {
            var rig = TickedRig(profile, 0f);
            Assert.Equal(0f, rig.RockAmount);
            Assert.Equal(0f, rig.RustleAmount);
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal(Matrix4x4.Identity, rig.WindMatrix(i)); // calm = byte-static sway layer
            }
        }
    }

    [Fact]
    public void Tick_WindMatrices_AreBoundedYawPitchTilts()
    {
        // SpeedTreeShader::SetMatrixRotation (PC FUN_00bb2fc0 → D3DXMatrixRotationYawPitchRoll):
        // yaw = 0.61·S·sinOsc, pitch = 0.61·S·cosOsc, roll = 0 — a pure rotation (no translation)
        // whose combined tilt of the up axis stays within the two angle bounds.
        var rig = TickedRig(SpeedTreeWindProfile.FalloutNewVegas, 0.4f);
        var maxAngle = 0.61f * 0.4f;
        var anyNonIdentity = false;
        for (var i = 0; i < 4; i++)
        {
            var m = rig.WindMatrix(i);
            Assert.Equal(0f, m.M41);
            Assert.Equal(0f, m.M42);
            Assert.Equal(0f, m.M43);
            anyNonIdentity |= m != Matrix4x4.Identity;
            var up = Vector3.TransformNormal(Vector3.UnitZ, m);
            Assert.Equal(1f, up.Length(), 3);
            var tilt = MathF.Acos(Math.Clamp(Vector3.Dot(up, Vector3.UnitZ), -1f, 1f));
            Assert.True(tilt <= 2f * maxAngle + 1e-3f, $"matrix {i} tilt {tilt} exceeds the yaw+pitch bound");
        }

        Assert.True(anyNonIdentity, "expected live sway matrices at S = 0.4");
    }

    [Fact]
    public void Tick_FnvProfile_AmountsPulseWithinSwayBounds()
    {
        var rig = new SpeedTreeWindRig { Profile = SpeedTreeWindProfile.FalloutNewVegas };
        const float s = 0.196f; // FNV clear-weather wind byte 50 / 255
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var i = 0; i <= 3000; i++)
        {
            rig.Tick(s, i * (1f / 30f));
            min = MathF.Min(min, rig.RockAmount);
            max = MathF.Max(max, rig.RockAmount);
        }

        // amount = S·sway/2, sway = |sin|+|cos| ∈ [0, √2·…] ⊂ [0, 2] → amount ∈ [0, S], and over
        // 100 s the oscillator must actually traverse a wide band (gust pulsing).
        Assert.InRange(min, 0f, s * 0.4f);
        Assert.InRange(max, s * 0.6f, s);
        Assert.True(max - min > s * 0.3f, $"expected pulsing, got [{min}, {max}]");
    }

    [Fact]
    public void Tick_OblivionProfile_AmountIsSteadyNearStrength()
    {
        var rig = new SpeedTreeWindRig { Profile = SpeedTreeWindProfile.Oblivion };
        const float s = 0.098f; // Oblivion "Clear" wind byte 25 / 255
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var i = 0; i <= 3000; i++)
        {
            rig.Tick(s, i * (1f / 30f));
            min = MathF.Min(min, rig.RockAmount);
            max = MathF.Max(max, rig.RockAmount);
        }

        // amount = S·(0.99 + 0.005·sway) ∈ [0.990·S, 1.000·S] — steady, unmodulated.
        Assert.InRange(min, s * 0.989f, s * 1.001f);
        Assert.InRange(max, s * 0.989f, s * 1.001f);
    }

    [Fact]
    public void Tick_FnvProfile_RockPhaseAdvancesFasterThanOblivion()
    {
        // fLeafRockTimeScale 2.0 (FNV) vs 1.0 with 0.85-influence sway modulation (Oblivion):
        // FNV's rock phase must accumulate meaningfully faster at equal strength.
        var fnv = TickedRig(SpeedTreeWindProfile.FalloutNewVegas, 0.15f, 900);
        var oblivion = TickedRig(SpeedTreeWindProfile.Oblivion, 0.15f, 900);
        Assert.True(fnv.RockPhase > oblivion.RockPhase * 1.3f,
            $"FNV {fnv.RockPhase} vs Oblivion {oblivion.RockPhase}");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(10f)]
    public void ResetAndReplayConstantWind_IsIndependentOfPriorLiveHistory(float captureTime)
    {
        const float strength = 50f / 255f;
        var dirty = TickedRig(SpeedTreeWindProfile.FalloutNewVegas, 0.8f, 900);
        dirty.Tick(0.05f, 45f);
        dirty.ResetAndReplayConstantWind(strength, captureTime);

        var clean = new SpeedTreeWindRig { Profile = SpeedTreeWindProfile.FalloutNewVegas };
        clean.ResetAndReplayConstantWind(strength, captureTime);

        AssertSameState(clean, dirty);
    }

    [Fact]
    public void ResetAndReplayConstantWind_TenSecondsAdvancesBeyondInitialPose()
    {
        const float strength = 50f / 255f;
        var rig = new SpeedTreeWindRig { Profile = SpeedTreeWindProfile.FalloutNewVegas };
        rig.ResetAndReplayConstantWind(strength, 0f);
        var initialMatrices = Enumerable.Range(0, 4).Select(rig.WindMatrix).ToArray();
        var initialRockPhase = rig.RockPhase;
        var initialRustlePhase = rig.RustlePhase;

        rig.ResetAndReplayConstantWind(strength, 10f);

        Assert.True(rig.RockPhase > initialRockPhase);
        Assert.True(rig.RustlePhase > initialRustlePhase);
        Assert.Contains(Enumerable.Range(0, 4), index => rig.WindMatrix(index) != initialMatrices[index]);
    }

    [Fact]
    public void Tick_LargeGapStillUsesOneHundredMillisecondLiveStep()
    {
        const float strength = 0.25f;
        var largeGap = new SpeedTreeWindRig { Profile = SpeedTreeWindProfile.FalloutNewVegas };
        var clampedGap = new SpeedTreeWindRig { Profile = SpeedTreeWindProfile.FalloutNewVegas };
        largeGap.Tick(strength, 0f);
        clampedGap.Tick(strength, 0f);

        largeGap.Tick(strength, 10f);
        clampedGap.Tick(strength, 0.1f);

        AssertSameState(clampedGap, largeGap);
    }

    [Fact]
    public void PerspectiveCaptureUsesSeparateReplayRigWithoutResettingLiveWind()
    {
        var root = FindRepoRoot();
        var renderer = File.ReadAllText(Path.Combine(
            root, "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
            "Camera", "D3D12", "ReferenceRenderer12.cs"));
        var capture = File.ReadAllText(Path.Combine(
            root, "src", "BethesdaMultitool", "App", "Controls",
            "WorldView3DControl.SceneCapture.cs"));

        Assert.Contains("_windRig.Tick(_windStrength", renderer, StringComparison.Ordinal);
        Assert.Contains("_captureWindRig.ResetAndReplayConstantWind", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("_windRig.ResetAndReplayConstantWind", renderer, StringComparison.Ordinal);
        Assert.Contains("_references?.SetWindForCapture(", capture, StringComparison.Ordinal);
    }

    private static void AssertSameState(SpeedTreeWindRig expected, SpeedTreeWindRig actual)
    {
        Assert.Equal(expected.RockAmount, actual.RockAmount);
        Assert.Equal(expected.RockPhase, actual.RockPhase);
        Assert.Equal(expected.RustleAmount, actual.RustleAmount);
        Assert.Equal(expected.RustlePhase, actual.RustlePhase);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(expected.WindMatrix(i), actual.WindMatrix(i));
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}