using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.Abstractions;

/// <summary>
///     v3 Pass 4 Step 2 — interface implemented by both <c>TerrainRenderer</c>
///     (D3D11) and <c>TerrainRenderer12</c> (D3D12). The textured heightmap layer
///     toggled by D2; the VCLR-only debug overlay toggled by D4.
/// </summary>
internal interface ITerrainRenderer : IWorldRenderer
{
    /// <summary>Total exterior-cell count from the most recent <c>LoadData</c>.
    /// Used in the HUD status overlay.</summary>
    int CellCount { get; }

    /// <summary>Toggles VCLR-only debug mode (Phase 2a look). Applied on next render.</summary>
    void SetVclrOnlyMode(bool on);

    void LoadData(Dictionary<(int gx, int gy), CellRecord> cells);

    void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        global::FalloutXbox360Utils.WorldSpatialIndex? spatialIndex,
        global::FalloutXbox360Utils.WorldRenderCache? renderCache);
}
