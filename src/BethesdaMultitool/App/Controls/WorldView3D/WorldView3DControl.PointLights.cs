using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.WorldData;
using Vortice.Direct3D12;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    /// <summary>
    ///     Interiors are hard-limited by the per-cell cap alone (no frame-level accumulation runs on
    ///     that branch), and 16 made dense interiors — the Strip casinos in particular — drop and pop
    ///     lights as the camera moved. There is no hardware ceiling forcing 16: <c>uPointLights</c> is
    ///     an unbounded StructuredBuffer at a root SRV and the shader loop bound is a runtime count
    ///     (<c>scene_lighting.hlsli</c>). 64 is an interim ceiling until the engine-parity light-volume
    ///     selection work lands (docs/backlog; successor: the decompiled retail light-association
    ///     oracle under Rendering/Lighting, deliberately unreferenced here until promoted).
    ///     Invariant: <see cref="MaxPlacedLightsPerFrame" /> &gt;= this, so an interior cell can never
    ///     exceed the frame budget it bypasses.
    /// </summary>
    private const int MaxPlacedLightsPerInteriorCell = PlacedLightFrameBudget.MaxPerInteriorCell;

    /// <summary>
    ///     Exterior per-cell cap — deliberately unchanged at 16: exteriors accumulate across every
    ///     visible cell before <see cref="ApplyFramePlacedLightCap" />, and
    ///     <see cref="FnvActiveAdtBasePolicy.IsEligible" /> keys the active ADT base route on
    ///     <c>PlacedLightCount == 0</c>, so the exterior population must not change here.
    /// </summary>
    private const int MaxPlacedLightsPerExteriorCell = PlacedLightFrameBudget.MaxPerExteriorCell;

    /// <summary>
    ///     Whole-frame ceiling on uploaded emitters. The per-cell cap alone bounds nothing outdoors:
    ///     the gather runs per visible cell, so a dense exterior would upload 16 × visibleCells into
    ///     an unbounded shader <c>[loop]</c> that every terrain and reference pixel walks. Selection
    ///     is nearest-to-camera, which is the only ordering that degrades gracefully.
    /// </summary>
    private const int MaxPlacedLightsPerFrame = PlacedLightFrameBudget.MaxPerFrame;

    private static readonly bool PlacedLightsEnvEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.PlacedLights) != "0";

    // Default-on exact tiled-forward A/B gate. Zero binds a one-tile all-active mask, preserving
    // the prior shader workload and image while keeping the root/buffer ABI identical.
    private static readonly bool PlacedLightTilesEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.PlacedLightTiles) != "0";

    private readonly List<PlacedLight> _cellPlacedLightScratch = new(MaxPlacedLightsPerInteriorCell * 2);

    private readonly List<PlacedLight> _framePlacedLights = new(MaxPlacedLightsPerFrame);
    private readonly List<WorldSpatialCell> _placedLightCellScratch = [];
    private readonly HashSet<uint> _placedLightClipLoggedCells = [];
    private ulong[] _placedLightTileMaskScratch = new ulong[1];
    private ulong[] _placedLightTileCachedMasks = new ulong[1];
    private readonly List<PlacedLight> _placedLightTileCachedLights = new(MaxPlacedLightsPerFrame);
    private bool _placedLightTileCacheValid;
    private Matrix4x4 _placedLightTileCachedViewProjection;
    private Vector3 _placedLightTileCachedRenderOrigin;
    private int _placedLightTileCachedViewportWidth;
    private int _placedLightTileCachedViewportHeight;
    private int _placedLightTileCachedMaskCount;
    private PlacedLightTileCullResult _placedLightTileCachedResult;
    private long _placedLightTileCachedTotalLights;
    private int _placedLightTileCachedMaxLights;
    private int _placedLightTileCachedEmptyTiles;
    private bool _framePlacedLightCapLogged;
    private ulong _lastPointLightTilesGpuAddress;

    private readonly record struct PlacedLightTileFrameTelemetry(
        double BuildMilliseconds,
        int TileCount,
        int UploadBytes,
        double AverageLightsPerTile,
        int MaxLightsPerTile,
        double EmptyTilePercent,
        string? FallbackReason);

    private PlacedLightTileFrameTelemetry _lastPlacedLightTileTelemetry;

    /// <summary>
    ///     Selects visible-cell emitters, uploads one global structured buffer, and binds root SRV
    ///     t9 before terrain/references draw. A 64-byte dummy is always bound for the zero-light case
    ///     so every PSO sees an initialized root descriptor.
    ///     <para>
    ///         Exteriors used to additionally require <c>FALLOUT_VIEWER_EXTERIOR_LIGHTS=1</c>, which
    ///         nothing sets — so every exterior uploaded zero lights no matter what the UI toggle said,
    ///         in every game (the gate was game-agnostic, so it silently suppressed Oblivion, FO3,
    ///         Skyrim and FO4 exteriors too). The toggle now governs both cell kinds; what the env gate
    ///         was really protecting against — an unbounded upload in dense worldspaces — is handled
    ///         properly by <see cref="MaxPlacedLightsPerFrame" />.
    ///     </para>
    /// </summary>
    private unsafe int BindPlacedLights(
        ID3D12GraphicsCommandList cmd,
        int frameIndex,
        VisibilityCylinder? visibility,
        Vector3 renderOrigin,
        bool lightingEnabled,
        Matrix4x4? lightViewProjection = null,
        int lightViewportWidth = 0,
        int lightViewportHeight = 0)
    {
        _framePlacedLights.Clear();

        if (PlacedLightsEnvEnabled && _placedLightsEnabled && lightingEnabled && _data is not null)
        {
            if (_selectedInterior is { } interior)
            {
                AppendCellLights(interior, _camera.Position, MaxPlacedLightsPerInteriorCell);
            }
            else if (visibility is { } cylinder && _spatialIndex is not null)
            {
                _spatialIndex.QueryCellsInRadius(
                    cylinder.Position.X,
                    -cylinder.Position.Y,
                    cylinder.Radius,
                    _placedLightCellScratch);
                foreach (var visibleCell in _placedLightCellScratch)
                {
                    AppendCellLights(visibleCell.Cell, cylinder.Position, MaxPlacedLightsPerExteriorCell);
                }

                ApplyFramePlacedLightCap(cylinder.Position);
            }
        }

        var elementCount = Math.Max(_framePlacedLights.Count, 1);
        var alloc = _ringBuffer12!.Allocate(
            frameIndex,
            checked((uint)elementCount * GpuPointLight.ByteSize),
            alignment: 16);
        var destination = (GpuPointLight*)alloc.CpuPtr;
        if (_framePlacedLights.Count == 0)
        {
            destination[0] = default;
        }
        else
        {
            for (var i = 0; i < _framePlacedLights.Count; i++)
            {
                destination[i] = new GpuPointLight(_framePlacedLights[i], renderOrigin);
            }
        }

        cmd.SetGraphicsRootShaderResourceView(
            (uint)GpuRootSignature12.Slots.PointLightsSrv,
            alloc.GpuAddress);

        BindPlacedLightTiles(
            cmd,
            frameIndex,
            lightViewProjection,
            lightViewportWidth,
            lightViewportHeight,
            renderOrigin);
        return _framePlacedLights.Count;
    }

    /// <summary>
    ///     Builds and binds the exact-superset tiled-forward mask. Every fallback is all-active:
    ///     failure can cost performance, but cannot remove illumination relative to the old loop.
    /// </summary>
    private unsafe void BindPlacedLightTiles(
        ID3D12GraphicsCommandList cmd,
        int frameIndex,
        Matrix4x4? viewProjection,
        int viewportWidth,
        int viewportHeight,
        Vector3 renderOrigin)
    {
        var started = StartProfileTimestamp();
        var lightCount = _framePlacedLights.Count;
        var activeMask = lightCount == PlacedLightTileCuller.MaxLights
            ? ulong.MaxValue
            : (1UL << lightCount) - 1UL;

        var tileCountX = 1;
        var tileCountY = 1;
        var tileCount = 1;
        var maskSource = _placedLightTileMaskScratch;
        var reusedCachedMasks = false;
        var storeBuiltMasks = false;
        var builtResult = default(PlacedLightTileCullResult);
        string? fallbackReason = null;
        EnsurePlacedLightTileMaskCapacity(1);
        _placedLightTileMaskScratch[0] = activeMask;

        if (!PlacedLightTilesEnabled)
        {
            fallbackReason = "Disabled";
        }
        else if (lightCount == 0)
        {
            // Do not build thousands of empty frustums. A one-tile zero mask is exact.
            _placedLightTileMaskScratch[0] = 0;
        }
        else if (viewProjection is not { } matrix)
        {
            fallbackReason = "MissingViewProjection";
        }
        else
        {
            var required = PlacedLightTileCuller.RequiredMaskCount(viewportWidth, viewportHeight);
            EnsurePlacedLightTileMaskCapacity(required);
            maskSource = _placedLightTileMaskScratch;
            if (TryReusePlacedLightTileMasks(
                    matrix, viewportWidth, viewportHeight, renderOrigin, required, out var cachedResult))
            {
                maskSource = _placedLightTileCachedMasks;
                tileCountX = cachedResult.TileCountX;
                tileCountY = cachedResult.TileCountY;
                tileCount = cachedResult.TileCount;
                reusedCachedMasks = true;
            }
            else
            {
                builtResult = PlacedLightTileCuller.Build(
                    matrix,
                    viewportWidth,
                    viewportHeight,
                    renderOrigin,
                    CollectionsMarshal.AsSpan(_framePlacedLights),
                    _placedLightTileMaskScratch.AsSpan(0, required));
                fallbackReason = builtResult.UsedFallback ? builtResult.FallbackReason.ToString() : null;
                if (!builtResult.UsedFallback)
                {
                    tileCountX = builtResult.TileCountX;
                    tileCountY = builtResult.TileCountY;
                    tileCount = builtResult.TileCount;
                    storeBuiltMasks = true;
                }
                else
                {
                    // The culler filled every requested tile all-active. Collapse that equivalent
                    // fallback to one element so an invalid matrix cannot also waste ring capacity.
                    _placedLightTileMaskScratch[0] = activeMask;
                }
            }
        }

        var totalLights = reusedCachedMasks ? _placedLightTileCachedTotalLights : 0L;
        var maxLights = reusedCachedMasks ? _placedLightTileCachedMaxLights : 0;
        var emptyTiles = reusedCachedMasks ? _placedLightTileCachedEmptyTiles : 0;
        if (!reusedCachedMasks)
        {
            for (var i = 0; i < tileCount; i++)
            {
                var count = BitOperations.PopCount(maskSource[i]);
                totalLights += count;
                maxLights = Math.Max(maxLights, count);
                if (count == 0) emptyTiles++;
            }
        }

        if (storeBuiltMasks)
        {
            StorePlacedLightTileMasks(
                viewProjection!.Value,
                viewportWidth,
                viewportHeight,
                renderOrigin,
                builtResult,
                totalLights,
                maxLights,
                emptyTiles);
        }

        // uint2 header + one uint2 mask per tile. ulong writes are layout-identical on the
        // little-endian D3D12 host: low/high dwords map to uint2.x/y.
        var uploadBytes = checked((uint)((tileCount + 1) * sizeof(ulong)));
        var tileAlloc = _ringBuffer12!.Allocate(frameIndex, uploadBytes, alignment: 16);
        var destination = (ulong*)tileAlloc.CpuPtr;
        destination[0] = (uint)tileCountX | ((ulong)(uint)tileCountY << 32);
        for (var i = 0; i < tileCount; i++)
        {
            destination[i + 1] = maskSource[i];
        }

        _lastPointLightTilesGpuAddress = tileAlloc.GpuAddress;
        cmd.SetGraphicsRootShaderResourceView(
            (uint)GpuRootSignature12.Slots.PointLightTilesSrv,
            tileAlloc.GpuAddress);

        _lastPlacedLightTileTelemetry = new PlacedLightTileFrameTelemetry(
            ElapsedMilliseconds(started),
            tileCount,
            (int)uploadBytes,
            tileCount == 0 ? 0 : (double)totalLights / tileCount,
            maxLights,
            tileCount == 0 ? 0 : 100.0 * emptyTiles / tileCount,
            fallbackReason);
    }

    private void EnsurePlacedLightTileMaskCapacity(int required)
    {
        if (_placedLightTileMaskScratch.Length >= required) return;
        Array.Resize(ref _placedLightTileMaskScratch, required);
    }

    private bool TryReusePlacedLightTileMasks(
        Matrix4x4 viewProjection,
        int viewportWidth,
        int viewportHeight,
        Vector3 renderOrigin,
        int requiredMaskCount,
        out PlacedLightTileCullResult result)
    {
        result = default;
        if (!_placedLightTileCacheValid ||
            _placedLightTileCachedMaskCount != requiredMaskCount ||
            !PlacedLightTileCachePolicy.Matches(
                _placedLightTileCachedViewProjection,
                _placedLightTileCachedViewportWidth,
                _placedLightTileCachedViewportHeight,
                _placedLightTileCachedRenderOrigin,
                CollectionsMarshal.AsSpan(_placedLightTileCachedLights),
                viewProjection,
                viewportWidth,
                viewportHeight,
                renderOrigin,
                CollectionsMarshal.AsSpan(_framePlacedLights)))
        {
            return false;
        }

        result = _placedLightTileCachedResult;
        return true;
    }

    private void StorePlacedLightTileMasks(
        Matrix4x4 viewProjection,
        int viewportWidth,
        int viewportHeight,
        Vector3 renderOrigin,
        PlacedLightTileCullResult result,
        long totalLights,
        int maxLights,
        int emptyTiles)
    {
        var maskCount = result.TileCount;
        if (_placedLightTileCachedMasks.Length < maskCount)
        {
            Array.Resize(ref _placedLightTileCachedMasks, maskCount);
        }

        _placedLightTileMaskScratch.AsSpan(0, maskCount)
            .CopyTo(_placedLightTileCachedMasks);
        _placedLightTileCachedLights.Clear();
        _placedLightTileCachedLights.AddRange(_framePlacedLights);
        _placedLightTileCachedViewProjection = viewProjection;
        _placedLightTileCachedViewportWidth = viewportWidth;
        _placedLightTileCachedViewportHeight = viewportHeight;
        _placedLightTileCachedRenderOrigin = renderOrigin;
        _placedLightTileCachedMaskCount = maskCount;
        _placedLightTileCachedResult = result;
        _placedLightTileCachedTotalLights = totalLights;
        _placedLightTileCachedMaxLights = maxLights;
        _placedLightTileCachedEmptyTiles = emptyTiles;
        _placedLightTileCacheValid = true;
    }

    /// <summary>
    ///     Trims the accumulated exterior list to <see cref="MaxPlacedLightsPerFrame" />, keeping the
    ///     lights nearest the camera.
    ///     <para>
    ///         Deliberately NOT a "camera is outside the light's radius" reject, which was the obvious
    ///         candidate: a light illuminates the GEOMETRY around it, not the camera, so discarding
    ///         lights further away than their own radius would extinguish every lit pool the moment you
    ///         stepped back from it — indistinguishable from the "placed lights do nothing" bug being
    ///         fixed. The shader already skips pixels outside a light's radius
    ///         (<c>scene_lighting.hlsli</c>: <c>distanceSquared >= radiusSquared</c>), so the only
    ///         thing left to bound on the CPU is how long that loop runs.
    ///     </para>
    /// </summary>
    private void ApplyFramePlacedLightCap(Vector3 cameraPosition)
    {
        // Selection lives in Core/ so it is unit-testable; this TFM's sources are invisible to the
        // test project. Only the once-per-session logging stays here.
        var clipped = PlacedLightFrameBudget.ClipToFrameBudget(_framePlacedLights, cameraPosition);
        if (clipped == 0) return;

        if (_framePlacedLightCapLogged) return;
        _framePlacedLightCapLogged = true;
        Log.Warn(
            "WorldView3DControl: {0} visible placed lights exceed the {1}-light frame budget; " +
            "keeping the nearest ({2} clipped). Logged once per session.",
            MaxPlacedLightsPerFrame + clipped,
            MaxPlacedLightsPerFrame,
            clipped);
    }

    private void AppendCellLights(CellRecord cell, Vector3 cameraPosition, int maxPerCell)
    {
        var source = _data!.RenderCache.GetPlacedLights(cell);
        var clipped = PlacedLightSelector.AppendNearest(
            source,
            cameraPosition,
            maxPerCell,
            enabledOverrides: _referenceEnabledOverrides,
            includeInitiallyDisabled: _showDisabled,
            destination: _framePlacedLights,
            scratch: _cellPlacedLightScratch,
            dayNightStates: _dayNightStates);
        if (clipped <= 0 || !_placedLightClipLoggedCells.Add(cell.FormId)) return;

        Log.Warn(
            "WorldView3DControl: cell 0x{0:X8} has {1} eligible placed lights; keeping nearest {2} " +
            "for the forward-light loop ({3} clipped).",
            cell.FormId,
            maxPerCell + clipped,
            maxPerCell,
            clipped);
    }
}
