using System.Numerics;
using BethesdaMultitool.Core.AssetBrowse;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Presentation;
using Slfx77.Multitool.WinUI.Direct3D12.Scenes;

namespace BethesdaMultitool;

/// <summary>Supplies measured bounds and an explicit Y-up camera for an already admitted Shadowkey snapshot.</summary>
/// <remarks>The shared presenter owns input, playback, source leases and native resources. No interchange file is created.</remarks>
internal static class ShadowkeyNativePresentation
{
    /// <summary>Measures the exact selected normalized scene without altering source positions or sampling options.</summary>
    /// <param name="preview">An admitted frame/skin snapshot with an immutable pack selection identity.</param>
    /// <param name="token">The source and selection cancellation checked during scene measurement.</param>
    /// <returns>The exact document and identity supplied to the shared native presenter.</returns>
    /// <exception cref="InvalidDataException">The selected scene lacks drawable geometry.</exception>
    /// <exception cref="NotSupportedException">An unexpected animation track would exceed static frame admission.</exception>
    internal static NativeScenePresentation Create(ShadowkeyPackPreview preview, CancellationToken token)
    {
        var scene = preview.Scene ?? throw new InvalidDataException("The selected Shadowkey slot has no admitted scene.");
        if (scene.Animations.Count != 0)
            throw new NotSupportedException("Shadowkey animation timing is not admitted by the frame preview.");
        var evaluator = new ScenePoseEvaluator(scene, token);
        var pose = evaluator.CreateWorkspace(scene.DefaultSceneIndex, 128L * 1024 * 1024, token);
        evaluator.EvaluateRest(pose, token);
        var bounds = new ScenePresentationWorkspace(scene, scene.DefaultSceneIndex, token).MeasureBounds(pose, token)
            ?? throw new InvalidDataException("The selected Shadowkey frame has no indexed world geometry.");
        return new NativeScenePresentation(scene, bounds, preview.Selection, Frame);
    }

    /// <summary>Frames the adapter's existing right-handed Y-up units with a bounded shared orbit camera.</summary>
    /// <param name="bounds">Measured indexed world geometry without a second basis conversion.</param>
    /// <param name="aspect">Actual positive native viewport width divided by height.</param>
    /// <returns>The initial pose, lens and movement bounds consumed unchanged by Shared.</returns>
    private static CameraNavigationState Frame(Bounds3 bounds, float aspect)
    {
        var frame = CameraFraming.Perspective(bounds, new Vector3(0.45f, 0.28f, -0.85f), Vector3.UnitY,
            MathF.PI / 3, aspect, minimumRadius: 0.01);
        var radius = Math.Max(bounds.Radius, 0.01);
        var distance = Vector3.Distance(frame.Pose.Position, bounds.Center);
        var maximumDistance = Math.Max(distance * 20d, radius * 40d);
        var lens = CameraLens.Perspective(frame.Lens.VerticalExtent, (float)(radius * 0.001),
            (float)(maximumDistance + radius * 2));
        return new CameraNavigationState(frame.Pose.Position, bounds.Center, Vector3.UnitY, lens,
            new CameraNavigationLimits(radius * 0.01, maximumDistance,
                (float)(radius * 0.01), (float)(radius * 40)));
    }
}
