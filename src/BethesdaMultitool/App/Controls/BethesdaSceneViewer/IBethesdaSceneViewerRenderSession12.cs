using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Vortice.Direct3D12;

namespace BethesdaMultitool;

/// <summary>
///     Observable readiness of the native viewer's mandatory direct-render session. A tab may retain
///     its compatibility WebView while this is <see cref="Initializing" /> or
///     <see cref="Faulted" /> and remove that fallback only after <see cref="Ready" />.
/// </summary>
internal enum BethesdaSceneViewerRenderState
{
    Initializing,
    Ready,
    Faulted
}

internal sealed class BethesdaSceneViewerRenderStateChangedEventArgs(
    BethesdaSceneViewerRenderState state,
    string? message) : EventArgs
{
    internal BethesdaSceneViewerRenderState State { get; } = state;

    internal string? Message { get; } = message;
}

/// <summary>
///     One fully prepared native-viewer frame. The host has already selected and cleared the HDR
///     scene target, bound the shared bindless heap/root signature, and begun the command recorder.
///     Implementations record authored Bethesda passes only; the host owns resolve, submit, present,
///     resize, and panel lifetime.
/// </summary>
internal readonly record struct BethesdaSceneViewerFrame12(
    BethesdaViewerScene Scene,
    BethesdaSceneViewerGraphicsContext12 Graphics,
    GpuSwapChainSurface12 Surface,
    ID3D12GraphicsCommandList CommandList,
    BethesdaSceneViewerCameraFrame Camera,
    int FrameIndex,
    float DeltaSeconds);

/// <summary>
///     Mandatory bridge between the shared WinUI host and a direct Bethesda D3D12 renderer. The
///     initial implementation may assemble/upload asynchronously, but it must not report
///     <see cref="BethesdaSceneViewerRenderState.Ready" /> until <see cref="Render" /> can issue the
///     scene's real geometry/material passes.
/// </summary>
internal interface IBethesdaSceneViewerRenderSession12 : IDisposable
{
    event EventHandler? StateChanged;

    BethesdaSceneViewerRenderState State { get; }

    string? StatusMessage { get; }

    /// <summary>
    ///     True while streaming, controllers, or animation require another frame without a camera or
    ///     scene invalidation. Static settled scenes should return false.
    /// </summary>
    bool RequiresContinuousFrames { get; }

    /// <summary>Names of independently selectable native animation clips on the current scene.</summary>
    IReadOnlyList<string> AnimationClipNames { get; }

    int SelectedAnimationClipIndex { get; }

    bool IsAnimationPlaying { get; }

    float AnimationTimeSeconds { get; }

    float AnimationDurationSeconds { get; }

    /// <summary>Receives the app-scoped D3D stack once, on the WinUI thread.</summary>
    void Initialize(BethesdaSceneViewerGraphicsContext12 graphics);

    /// <summary>Publishes or clears the renderer-neutral scene without projecting it through GLB.</summary>
    void SetScene(BethesdaViewerScene? scene);

    /// <summary>Selects a clip and resets its playback clock to the authored start.</summary>
    void SelectAnimationClip(int clipIndex);

    void SetAnimationPlaying(bool playing);

    /// <summary>Seeks in seconds relative to the selected clip's authored start.</summary>
    void SeekAnimation(float timeSeconds);

    /// <summary>Records real Bethesda scene passes into the host's current command list.</summary>
    void Render(in BethesdaSceneViewerFrame12 frame);
}
