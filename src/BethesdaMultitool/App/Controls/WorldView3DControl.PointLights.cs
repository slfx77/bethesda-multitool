using System.Numerics;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.Direct3D12;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    private const int MaxPlacedLightsPerCell = 16;

    private static readonly bool PlacedLightsEnvEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.PlacedLights) != "0";
    private static readonly bool ExteriorPlacedLightsEnabled =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.ExteriorPlacedLights);

    private readonly List<PlacedLight> _framePlacedLights = new(MaxPlacedLightsPerCell);
    private readonly List<PlacedLight> _cellPlacedLightScratch = new(MaxPlacedLightsPerCell * 2);
    private readonly List<WorldSpatialCell> _placedLightCellScratch = [];
    private readonly HashSet<uint> _placedLightClipLoggedCells = [];

    /// <summary>
    ///     Selects visible-cell emitters, uploads one global structured buffer, and binds root SRV
    ///     t9 before terrain/references draw. Interior cells are enabled by default; exterior cells
    ///     require <c>FALLOUT_VIEWER_EXTERIOR_LIGHTS=1</c> until the global forward loop's cost is
    ///     proven in dense worldspaces. A 64-byte dummy is always bound for the zero-light case so
    ///     every PSO sees an initialized root descriptor.
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
                AppendCellLights(interior, _camera.Position);
            }
            else if (ExteriorPlacedLightsEnabled && visibility is { } cylinder && _spatialIndex is not null)
            {
                _spatialIndex.QueryCellsInRadius(
                    cylinder.Position.X,
                    -cylinder.Position.Y,
                    cylinder.Radius,
                    _placedLightCellScratch);
                foreach (var visibleCell in _placedLightCellScratch)
                {
                    AppendCellLights(visibleCell.Cell, cylinder.Position);
                }
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

    private void AppendCellLights(CellRecord cell, Vector3 cameraPosition)
    {
        var source = _data!.RenderCache.GetPlacedLights(cell);
        var clipped = PlacedLightSelector.AppendNearest(
            source,
            cameraPosition,
            MaxPlacedLightsPerCell,
            includeInitiallyDisabled: _showDisabled,
            destination: _framePlacedLights,
            scratch: _cellPlacedLightScratch);
        if (clipped <= 0 || !_placedLightClipLoggedCells.Add(cell.FormId)) return;

        Log.Warn(
            "WorldView3DControl: cell 0x{0:X8} has {1} eligible placed lights; keeping nearest {2} " +
            "for the forward-light loop ({3} clipped).",
            cell.FormId,
            MaxPlacedLightsPerCell + clipped,
            MaxPlacedLightsPerCell,
            clipped);
    }
}
