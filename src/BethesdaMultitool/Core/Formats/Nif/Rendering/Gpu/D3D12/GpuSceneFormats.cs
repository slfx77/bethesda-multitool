using Vortice.DXGI;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Single source of truth for the 3D scene's render-target formats, so every scene PSO
///     (<c>ReferencePipelineFactory12</c>, <c>TerrainPipelineFactory12</c>, water, sky, the debug
///     overlays) and both scene targets (<see cref="GpuOffscreenSceneTarget12" /> headless +
///     <c>GpuSwapChainSurface12</c> live) agree on the color format. A PSO's
///     <c>RenderTargetFormats</c> MUST match the bound RTV's format or draw fails, so this MUST be
///     flipped in exactly one place.
///     <para>
///         <see cref="SceneColor" /> is an HDR float format: scene shaders already emit unclamped
///         linear color (emissive glow &gt; 1, sun specular, imagespace scales), which an 8-bit UNORM
///         target silently clipped to white. Rendering into a float target preserves that range so a
///         single tonemap pass (<see cref="GpuTonemapPass12" />) can roll highlights off and lift
///         midtones into the 8-bit <see cref="LdrOutput" /> for display — the engine's HDR+imagespace
///         stage this viewer previously skipped (which read as "too dark" + clipped neon/goo).
///     </para>
/// </summary>
internal static class GpuSceneFormats
{
    /// <summary>HDR scene color the whole 3D pass renders into (MSAA + resolve). Holds values &gt; 1
    /// (emissive glow, sun specular, imagespace scales) so the tonemap pass can roll highlights off +
    /// lift midtones instead of the 8-bit target clipping them. Both scene targets tonemap this into
    /// <see cref="LdrOutput" /> for display; the tonemap runs passthrough when this is an 8-bit format.
    /// <para>
    ///     <c>FALLOUT_VIEWER_HDR=0</c> reverts this to the legacy 8-bit format, so the ENTIRE HDR path
    ///     (float targets + tonemap curve) collapses to the pre-HDR behavior — a true kill-switch, in
    ///     case a hardware/driver combination misbehaves with the float scene target. Resolved once at
    ///     first use.
    /// </para></summary>
    internal static readonly Format SceneColor =
        Environment.GetEnvironmentVariable("FALLOUT_VIEWER_HDR") == "0"
            ? Format.B8G8R8A8_UNorm
            : Format.R16G16B16A16_Float;

    /// <summary>8-bit display format the tonemap pass writes and readback/present consume.</summary>
    internal const Format LdrOutput = Format.B8G8R8A8_UNorm;
}
