using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.Abstractions;

/// <summary>
///     v3 Pass 4 Step 2 — interface implemented by both <c>WaterRenderer</c>
///     (D3D11) and <c>WaterRenderer12</c> (D3D12). The alpha-blended water-quad
///     layer toggled by D3.
/// </summary>
internal interface IWaterRenderer : IWorldRenderer
{
    void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight);

    void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::FalloutXbox360Utils.WorldSpatialIndex? spatialIndex);
}
