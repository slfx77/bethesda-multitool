using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
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
    private const int MaxPlacedLightsPerInteriorCell = 64;

    /// <summary>
    ///     Exterior per-cell cap — deliberately unchanged at 16: exteriors accumulate across every
    ///     visible cell before <see cref="ApplyFramePlacedLightCap" />, and
    ///     <see cref="FnvActiveAdtBasePolicy.IsEligible" /> keys the active ADT base route on
    ///     <c>PlacedLightCount == 0</c>, so the exterior population must not change here.
    /// </summary>
    private const int MaxPlacedLightsPerExteriorCell = 16;

    /// <summary>
    ///     Whole-frame ceiling on uploaded emitters. The per-cell cap alone bounds nothing outdoors:
    ///     the gather runs per visible cell, so a dense exterior would upload 16 × visibleCells into
    ///     an unbounded shader <c>[loop]</c> that every terrain and reference pixel walks. Selection
    ///     is nearest-to-camera, which is the only ordering that degrades gracefully.
    /// </summary>
    private const int MaxPlacedLightsPerFrame = 64;

    private static readonly bool PlacedLightsEnvEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.PlacedLights) != "0";

    private readonly List<PlacedLight> _framePlacedLights = new(MaxPlacedLightsPerFrame);
    private readonly List<PlacedLight> _cellPlacedLightScratch = new(MaxPlacedLightsPerInteriorCell * 2);
    private readonly List<WorldSpatialCell> _placedLightCellScratch = [];
    private readonly HashSet<uint> _placedLightClipLoggedCells = [];
    private bool _framePlacedLightCapLogged;

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
        bool lightingEnabled)
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
        return _framePlacedLights.Count;
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
        if (_framePlacedLights.Count <= MaxPlacedLightsPerFrame) return;

        var clipped = _framePlacedLights.Count - MaxPlacedLightsPerFrame;
        _framePlacedLights.Sort((left, right) =>
        {
            var distanceOrder = Vector3.DistanceSquared(left.Position, cameraPosition)
                .CompareTo(Vector3.DistanceSquared(right.Position, cameraPosition));
            // FormId breaks ties so the survivors do not shuffle between frames at equal distance.
            return distanceOrder != 0 ? distanceOrder : left.FormId.CompareTo(right.FormId);
        });
        _framePlacedLights.RemoveRange(
            MaxPlacedLightsPerFrame, _framePlacedLights.Count - MaxPlacedLightsPerFrame);

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
