using System.Numerics;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.SpeedTree;

/// <summary>
///     Pins the live-vs-capture wind seam. <see cref="SpeedTreeWindRig" /> INTEGRATES its oscillator
///     clocks from the (strength, time) history it is fed, so — unlike the leaf billboard basis, which
///     the live frame re-supplies unconditionally — a one-shot offscreen render that pins a pose
///     through the LIVE seam permanently perturbs the live sway. That is the 2026-08-11 "SPT trees
///     sway too strongly again" regression: the 2D map's top-down overlay pinned its rest pose with
///     <c>SetWind(dir, 0, 0)</c> on the shared renderer.
/// </summary>
public sealed class SpeedTreeLiveWindRigSeamTests
{
    // The live-confirmed clear-weather strength (SptMeshDumper v8 ExportWind: S = 50/255 to the digit).
    private const float ClearWeatherStrength = 50f / 255f;
    private const float FrameSeconds = 1f / 60f;

    private static float SettleLiveRig(SpeedTreeWindRig rig, int frames = 3000)
    {
        var clock = 0f;
        for (var i = 0; i < frames; i++)
        {
            clock += FrameSeconds;
            rig.Tick(ClearWeatherStrength, clock);
        }

        return clock;
    }

    /// <summary>
    ///     A settled rig runs its four canopy groups at DIFFERENT frequencies, so the four sway
    ///     matrices are decorrelated — the whole canopy never heaves as one rigid body.
    /// </summary>
    [Fact]
    public void SettledLiveRig_KeepsTheFourCanopyGroupsDecorrelated()
    {
        var rig = new SpeedTreeWindRig { Profile = SpeedTreeWindProfile.FalloutNewVegas };
        SettleLiveRig(rig);

        Assert.NotEqual(rig.WindMatrix(0), rig.WindMatrix(1));
        Assert.NotEqual(rig.WindMatrix(0), rig.WindMatrix(2));
        Assert.NotEqual(rig.WindMatrix(0), rig.WindMatrix(3));
    }

    /// <summary>
    ///     Feeding the LIVE rig an offscreen pose pin (strength 0, time 0) leaves foldStrength 0, so the
    ///     next live frame's phase-continuity rescale (<c>_matrixTimes *= foldStrength / S</c>) zeroes
    ///     every oscillator clock: all four groups restart at phase 0, which is yaw 0 / pitch 0.61·S —
    ///     the cos extreme — in unison. This is the mechanism, not the desired behavior; the rescale
    ///     itself is decompile-exact (BSTreeManager::UpdateWindMatrices) and must not be "fixed" here.
    ///     The fix is at the call site: offscreen renders use the capture seam.
    /// </summary>
    [Fact]
    public void PinningTheLiveRigThroughSetWind_CollapsesTheCanopyIntoOnePhase()
    {
        var rig = new SpeedTreeWindRig { Profile = SpeedTreeWindProfile.FalloutNewVegas };
        var clock = SettleLiveRig(rig);

        rig.Tick(0f, 0f);                            // what SetWind(dir, 0f, 0f) did to the live rig
        rig.Tick(ClearWeatherStrength, clock + FrameSeconds); // the next live frame

        Assert.Equal(rig.WindMatrix(0), rig.WindMatrix(1));
        Assert.Equal(rig.WindMatrix(0), rig.WindMatrix(2));
        Assert.Equal(rig.WindMatrix(0), rig.WindMatrix(3));
        // Pitch is pinned at the cos extreme 0.61·S: M33 = cos(yaw)·cos(pitch) = cos(0.61·S).
        Assert.Equal(MathF.Cos(0.61f * ClearWeatherStrength), rig.WindMatrix(0).M33, 5);
    }

    /// <summary>
    ///     The capture seam replays its own rig from rest, so pinning a pose there leaves the live rig's
    ///     integrated phase byte-identical — the property that makes a one-shot render safe.
    /// </summary>
    [Fact]
    public void PinningThroughTheCaptureRig_LeavesTheLiveRigUntouched()
    {
        var live = new SpeedTreeWindRig { Profile = SpeedTreeWindProfile.FalloutNewVegas };
        var clock = SettleLiveRig(live);
        var before = new[] { live.WindMatrix(0), live.WindMatrix(1), live.WindMatrix(2), live.WindMatrix(3) };

        var capture = new SpeedTreeWindRig { Profile = SpeedTreeWindProfile.FalloutNewVegas };
        capture.ResetAndReplayConstantWind(0f, 0f); // what SetWindForCapture(dir, 0f, 0f) does

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(before[i], live.WindMatrix(i));
            Assert.Equal(Matrix4x4.Identity, capture.WindMatrix(i)); // strength 0 = the rest pose
        }

        // And the live rig still advances from its own history, undisturbed.
        live.Tick(ClearWeatherStrength, clock + FrameSeconds);
        Assert.NotEqual(live.WindMatrix(0), live.WindMatrix(3));
    }

    /// <summary>
    ///     The 2D map's top-down overlay renders on a repeat loop while streaming converges, on the
    ///     SHARED renderer the live 3D view uses. It must pin its rest pose through the capture seam.
    /// </summary>
    [Fact]
    public void TopDownOverlayPinsWindThroughTheCaptureSeam()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.TopDown.cs");

        Assert.Contains(
            "_references.SetWindForCapture(WindDirection, 0f, 0f);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_references.SetWind(", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The two seams must stay separate rigs: <c>SetWindForCapture</c> touching <c>_windRig</c>
    ///     would reintroduce the same live-state corruption behind a capture-shaped name.
    /// </summary>
    [Fact]
    public void CaptureSeamNeverTicksTheLiveRig()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        var body = SourceContract.Extract(
            source, "public void SetWindForCapture(", "public void SetWindProfile(");

        Assert.Contains("_captureWindRig.ResetAndReplayConstantWind(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_windRig.", body, StringComparison.Ordinal);
    }
}
