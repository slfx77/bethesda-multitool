using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

/// <summary>
///     Retail native-viewer gate for Morrowind's only sighted CYCLE_REVERSE model. The two symmetric
///     presentation times must resolve to the same rock pose; the early control time must differ.
/// </summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class Tes3ReverseControllerCycleRetailTests
{
    private const string ModelPath = @"meshes\r\atronach_storm.nif";
    private const string ModelSha256 =
        "C78C249A38EFCB13A386609FA2538E6A4CB3F3EE466B9ECE8D741FE0EF6A700B";
    private static readonly string? Bsa =
        RealAssetPaths.SteamGameFile("Morrowind", @"Data Files\Morrowind.bsa");

    [Fact]
    public void StormAtronach_ExposesExactFullReverseCycleAlongsideEmbeddedIdle()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(File.Exists(Bsa), "Morrowind.bsa not present (dev-machine-only asset).");

        using var service = NifBrowserService.CreateFromBsa(Bsa!);
        var data = Assert.IsType<byte[]>(service.ReadNifData(ModelPath));
        Assert.Equal(640_391, data.Length);
        Assert.Equal(ModelSha256, Convert.ToHexString(SHA256.HashData(data)));

        var build = service.BuildViewerSceneWithDiagnostics(
            data,
            "atronach_storm.nif",
            ModelPath);
        var scene = Assert.IsType<BethesdaViewerScene>(build.Scene);
        var idle = Assert.Single(
            scene.AnimationClips,
            static clip => clip.Name == "Embedded Idle");
        var fullCycle = Assert.Single(
            scene.AnimationClips,
            static clip => clip.Name == "Embedded Controller Cycle");

        // Preserve the normal ambient/text-key selection rather than replacing it with a 98 s tour.
        Assert.Equal(47.4f, idle.StartTime, 3);
        Assert.Equal(49.06667f, idle.EndTime, 4);
        Assert.True(idle.Loops);
        Assert.False(idle.PingPongs);

        Assert.Equal(0f, fullCycle.StartTime);
        Assert.Equal(49.06667f, fullCycle.EndTime, 4);
        Assert.True(fullCycle.Loops);
        Assert.True(fullCycle.PingPongs);
        var clock = BethesdaViewerAnimationClockPolicy.Resolve(fullCycle);
        Assert.Equal(0f, clock.RawOriginSeconds, 4);
        Assert.Equal(98.13334f, clock.PresentationDurationSeconds, 4);

        var targets = fullCycle.NodeTracks
            .Select(track => scene.Nodes[track.NodeIndex].LookupName ?? scene.Nodes[track.NodeIndex].Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "Rock_1", "Rock_2", "Rock_3", "Rock_4", "Rock_5" }, targets);

        var evaluator = new BethesdaViewerAnimationPoseEvaluator(
            scene.Nodes.Select(static node => node.LocalTransform).ToArray(),
            scene.Nodes.Select(static node => node.ParentIndex).ToArray(),
            fullCycle);
        var forward = new Matrix4x4[scene.Nodes.Count];
        var reverse = new Matrix4x4[scene.Nodes.Count];
        var control = new Matrix4x4[scene.Nodes.Count];
        evaluator.EvaluateNodeWorlds(40.56667f, forward);
        evaluator.EvaluateNodeWorlds(57.56667f, reverse);
        evaluator.EvaluateNodeWorlds(8.5f, control);

        foreach (var track in fullCycle.NodeTracks)
        {
            AssertVectorNear(
                forward[track.NodeIndex].Translation,
                reverse[track.NodeIndex].Translation,
                0.001f);
        }

        Assert.Contains(
            fullCycle.NodeTracks,
            track => Vector3.Distance(
                         forward[track.NodeIndex].Translation,
                         control[track.NodeIndex].Translation) > 80f);
    }

    private static void AssertVectorNear(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.InRange(MathF.Abs(actual.X - expected.X), 0f, tolerance);
        Assert.InRange(MathF.Abs(actual.Y - expected.Y), 0f, tolerance);
        Assert.InRange(MathF.Abs(actual.Z - expected.Z), 0f, tolerance);
    }
}
