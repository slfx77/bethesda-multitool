using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Rasterization;

namespace BethesdaMultitool;

/// <summary>
///     Offscreen render path for the dedicated 3D export (the toolbar "Export" button → MapExport3DDialog
///     → <c>OnExportButtonClick</c>). Renders one tile of an orthographic / isometric /
///     trimetric projection of the active worldspace into a reused <see cref="GpuOffscreenSceneTarget12" />
///     and reads it back to BGRA. Unlike the 2D map's top-down overlay (<see cref="ITopDownSceneRenderer" />,
///     which forces lighting + fog OFF), the export honors the dialog's lighting/fog/visibility choices and
///     draws the FULL coloured scene (terrain + references + water + optional navmesh/collision/grid).
///     <para>
///         Tiles come from <see cref="OrthoViewProjBuilder.BuildViewProjTile" /> — off-center sub-frustums
///         of one global ortho projection, so they stitch seamlessly at any elevation (the property
///         <c>OrthoViewProjBuilderTests.BuildViewProjTile_IsometricTilesSubdivideClipExactly</c> locks).
///     </para>
/// </summary>
public sealed partial class WorldView3DControl
{
    /// <summary>Per-tile offscreen edge cap. Tiling delivers resolution beyond this; one MSAA target at
    /// this size is the memory ceiling per tile. Matches the top-down overlay's conservative bound.</summary>
    private const int Export3DMaxTileDimension = 2048;

    // Reused across export tiles (all the same size); separate from the top-down overlay target so the
    // two don't fight over dimensions. Disposed in DisposeRenderResources via DisposeExport3DTarget().
    private GpuOffscreenSceneTarget12? _export3DTarget;
    private int _export3DTargetW;
    private int _export3DTargetH;

    /// <summary>True when the D3D12 backend + scene renderers are ready for an offscreen export.</summary>
    internal bool CanRenderProjectionExport => CanRenderTopDownCore;

    internal void DisposeExport3DTarget()
    {
        _export3DTarget?.Dispose();
        _export3DTarget = null;
        _export3DTargetW = _export3DTargetH = 0;
    }

    /// <summary>
    ///     Renders one export tile from a prebuilt ortho/iso/tri <paramref name="viewProj" /> + cull
    ///     <paramref name="cylinder" />, honouring <paramref name="opts" />. MUST be called on the UI
    ///     thread (records + submits D3D12 commands); the fence wait + readback run on a worker thread.
    ///     The caller (<see cref="WorldView3DExportRunner" />) pauses the live render loop first, switches
    ///     the active worldspace, and re-requests each tile until <see cref="Export3DTile.IsComplete" />.
    /// </summary>
    internal async Task<Export3DTile?> RenderProjectionTileAsync(
        Matrix4x4 viewProj, VisibilityCylinder cylinder, Vector3 leafRight, Vector3 leafUp,
        Vector3 shadingEye, int pixelWidth, int pixelHeight, Export3DOptions opts, CancellationToken ct)
    {
        if (!CanRenderProjectionExport) return null;
        if (pixelWidth <= 0 || pixelHeight <= 0) return null;
        ct.ThrowIfCancellationRequested();

        var finalW = Math.Clamp(pixelWidth, 1, Export3DMaxTileDimension);
        var finalH = Math.Clamp(pixelHeight, 1, Export3DMaxTileDimension);
        // MSAA targets do their own edge AA → skip the supersample multiply (memory-neutral); 1-sample
        // targets supersample 2× then box-downsample, mirroring the top-down overlay.
        var supersample = _gpu12!.SceneSampleCount > 1 ? 1 : TopDownSupersample;
        var ssWidth = finalW * supersample;
        var ssHeight = finalH * supersample;

        GpuOffscreenSceneTarget12 target;
        try
        {
            if (_export3DTarget is null || _export3DTargetW != ssWidth || _export3DTargetH != ssHeight)
            {
                _export3DTarget?.Dispose();
                _export3DTarget = new GpuOffscreenSceneTarget12(_gpu12!, ssWidth, ssHeight);
                _export3DTargetW = ssWidth;
                _export3DTargetH = ssHeight;
            }
            target = _export3DTarget;
        }
        catch (Exception ex)
        {
            Log.Warn("WorldView3DControl: 3D export target alloc failed: {0}", ex.Message);
            DisposeExport3DTarget();
            return null;
        }

        ulong fenceValue;
        bool isComplete;
        var prevShowDisabled = _references!.ShowInitiallyDisabled;
        var prevRefThrottled = _references.StreamingThrottled;
        var prevTerrainThrottled = _terrain!.StreamingThrottled;
        // BindAtmosphereConstants reads the live _show* lighting/fog/sky gates; override them with the
        // dialog's choices for the duration of the capture so the export is authoritative, then restore.
        var prevShowLighting = _showLighting;
        var prevShowFog = _showFog;
        var prevShowSky = _showSky;
        var prevShowMarkers = _references.ShowMarkers;
        try
        {
            _references.ShowInitiallyDisabled = opts.ShowDisabled;
            _references.ShowMarkers = opts.ShowMarkers;
            _references.SetHiddenCategories(opts.HiddenCategories);
            _references.StreamingThrottled = false;
            _terrain.StreamingThrottled = false;
            _showLighting = opts.EnableLighting;
            _showFog = opts.EnableFog;
            _showSky = false; // sky dome is camera-centered (misplaced in ortho); export draws the clear

            var recorder = _commandRecorder12!;
            recorder.BeginFrame();
            try
            {
                var cmd = recorder.CommandList;
                _deletionQueue12!.Tick();
                _ringBuffer12!.ResetFrame();
                _cbvSrvUavHeap12!.BeginFrame(recorder.FrameIndex);
                _gpu12!.PumpDebugMessages();

                cmd.SetDescriptorHeaps(1, new[] { _cbvSrvUavHeap12.Heap });
                cmd.SetGraphicsRootSignature(_rootSignature12!.RootSignature);

                // Absolute matrices (no camera-relative origin); the ortho eye as the shading camera so
                // specular reads parallel. enableFog is forced OFF to match the live projection view: the
                // shader measures fog as distance from the camera, and the ortho eye sits 1,000,000 units
                // away, so the fog factor saturates and the whole frame collapses to a flat fog wash.
                // (Previously this passed enableFog:true, which is why export disagreed with the live view.)
                BindAtmosphereConstants(
                    cmd, recorder.FrameIndex, enableFog: false, enableLighting: true,
                    cameraRelative: false, shadingCameraPosOverride: shadingEye);

                target.Bind(cmd);

                // Leaf cards re-face the ortho camera basis (top-down → flat in the ground plane). Wind
                // off for a clean static capture.
                _references.SetLeafBillboardBasis(leafRight, leafUp);
                _references.SetWind(WindDirection, 0f, 0f);

                if (opts.ShowTerrain) _terrain!.Render(viewProj, cylinder);
                // Defer blended reference submeshes until after water so water never paints over them
                // (mirrors the live frame). cullViewProj == viewProj (absolute coords, no camera-relative).
                if (opts.ShowReferences) _references.Render(viewProj, cylinder, deferBlended: true, cullViewProj: viewProj);
                if (opts.ShowWater && _water is not null)
                {
                    _water.SetNifWaterPlanes(_references.NifWaterPlanes);
                    _water.SetSceneDepth(NoDepthSrv, _camera.NearPlane, _camera.FarPlane);
                    _water.Render(viewProj, cylinder);
                }
                if (opts.ShowReferences) _references.RenderBlendedDeferred();
                if (opts.ShowNavMesh) _navMesh?.Render(viewProj, cylinder);
                if (opts.ShowCollision) _collisionDebug?.Render(viewProj, cylinder);
                if (opts.ShowGrid) _cellGrid?.Render(viewProj, cylinder);

                target.RecordReadback(cmd);
            }
            finally
            {
                recorder.EndFrame();
            }

            fenceValue = recorder.LastSubmittedFenceValue;
            isComplete = TopDownStreamingComplete();
            _gpu12.PumpDebugMessages();
        }
        catch (Exception ex)
        {
            try { _commandRecorder12?.WaitForGpuIdle(); }
            catch (Exception waitEx) { Log.Warn("WaitForGpuIdle after 3D export failure threw: {0}", waitEx.Message); }
            DisposeExport3DTarget();
            Log.Warn("WorldView3DControl: 3D export render failed: {0}", ex.Message);
            try { _gpu12?.PumpDebugMessages(); }
            catch (Exception pumpEx) { Log.Warn("PumpDebugMessages threw: {0}", pumpEx.Message); }
            return null;
        }
        finally
        {
            _references.ShowInitiallyDisabled = prevShowDisabled;
            _references.ShowMarkers = prevShowMarkers;
            _references.SetHiddenCategories(_hiddenCategories);
            _references.StreamingThrottled = prevRefThrottled;
            _terrain.StreamingThrottled = prevTerrainThrottled;
            _showLighting = prevShowLighting;
            _showFog = prevShowFog;
            _showSky = prevShowSky;
        }

        var frameFence = _gpu12!.FrameFence;
        try
        {
            return await Task.Run(() =>
            {
                WaitForFrameFence(frameFence, fenceValue);
                ct.ThrowIfCancellationRequested();
                var ssBytes = target.ReadbackToBytes(); // BGRA
                var pixels = supersample > 1
                    ? NifSpriteRenderer.Downsample(ssBytes, ssWidth, ssHeight, supersample)
                    : ssBytes;
                return new Export3DTile(pixels, finalW, finalH, isComplete);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}

/// <summary>Visibility + atmosphere choices for a 3D export, mirroring the viewer's own toggles.</summary>
internal sealed record Export3DOptions(
    bool ShowTerrain,
    bool ShowReferences,
    bool ShowWater,
    bool ShowNavMesh,
    bool ShowCollision,
    bool ShowGrid,
    bool ShowDisabled,
    bool ShowMarkers,
    bool EnableLighting,
    bool EnableFog,
    IReadOnlyCollection<PlacedObjectCategory> HiddenCategories);

/// <summary>One rendered export tile: BGRA pixels (<see cref="Width" />×<see cref="Height" />, tightly
/// packed). <see cref="IsComplete" /> is false while meshes/textures are still streaming — re-request.</summary>
internal sealed record Export3DTile(byte[] Bgra, int Width, int Height, bool IsComplete);
