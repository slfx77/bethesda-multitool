using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Retail gate for rigid node-animated statics on the exact asset that motivated them:
///     <c>nv_gs-saloon-sign.nif</c> carries a NiControllerManager "SpecialIdle" sequence
///     (0→7.667 s) driving the Frame/Chain1/Object02/Sign nodes with NO skin — the collectors must
///     produce the rig, and the baker must hand the two chain strips (blocks 23/38) and the
///     swinging board (44) real motion tracks while the static post (Frame:0, 48 — its
///     interpolator has no key data) and the NoLighting glow pane (31) stay untracked.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class FnvRigidNodeAnimationRetailTests
{
    private const string MeshesBsaRelative =
        @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa";

    private const string GoodspringsSignPath =
        @"meshes\architecture\goodsprings\nv_gs-saloon-sign.nif";

    public FnvRigidNodeAnimationRetailTests()
    {
        BucketBTestGuard.SkipUnlessEnabled();
    }

    [Fact]
    public void GoodspringsSign_BakesChainAndBoardTracks_LeavesPostAndGlowStatic()
    {
        var bsaPath = SampleFileFixture.FindSamplePath(MeshesBsaRelative);
        Assert.SkipWhen(bsaPath is null, "FNV PC final meshes BSA not available");
        using var archives = MeshArchiveSet.Open(bsaPath!, null, false);
        Assert.True(
            archives.TryExtractFile(GoodspringsSignPath, out var data, out _),
            $"Retail NIF missing: {GoodspringsSignPath}");
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));

        var signature = NifAnimationDetector.Detect(data, nif);
        Assert.False(signature.HasInternalSkin);
        Assert.True(signature.HasControllerSequenceTracks);

        var rig = NifControllerSequenceTrackCollector.Collect(data, nif);
        Assert.NotNull(rig);
        Assert.Equal(7.6667f, rig.ClipStop, 3);

        var tracks = NifRigidNodeAnimationBaker.Bake(data, nif, rig);
        Assert.NotNull(tracks);

        // Chain strips + the swinging board carry motion; the post and glow pane do not.
        Assert.Contains(23, tracks.Keys); // Chain1:0
        Assert.Contains(38, tracks.Keys); // Object02:0 (second chain)
        Assert.Contains(44, tracks.Keys); // Sign:0 (board)
        Assert.DoesNotContain(48, tracks.Keys); // Frame:0 (post) — static interpolator
        Assert.DoesNotContain(31, tracks.Keys); // Object01:0 (glow pane) — no track at all

        // The baked deltas actually move: scan the whole clip and verify the board's track
        // displaces a probe point somewhere in the cycle (the sway is gentle — phase-agnostic).
        var board = tracks[44];
        Assert.True(board.Loops);
        Assert.Equal(7.6667f, board.ClipLength, 3);
        var probe = new Vector3(20f, 0f, 0f);
        var reference = Vector3.Transform(probe, board.Evaluate(0.0));
        var maxDisplacement = 0f;
        for (var t = 0.0; t < board.ClipLength; t += 0.1)
        {
            var moved = Vector3.Transform(probe, board.Evaluate(t));
            maxDisplacement = MathF.Max(maxDisplacement, (moved - reference).Length());
        }

        Assert.True(
            maxDisplacement > 0.25f,
            $"board track max probe displacement across the clip = {maxDisplacement}");

        // The authored swing starts at (near) the hanging rest pose, so the t=0 delta must be
        // close to identity — this discriminates the XYZ-Euler composition order empirically: a
        // wrong order would leave a large rotation baked into every sample, including t=0.
        Assert.True(
            (reference - probe).Length() < 2.0f,
            $"t=0 delta moved the probe by {(reference - probe).Length()} — Euler order suspect");
    }
}