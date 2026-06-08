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

    /// <summary>Loads water cells plus the worldspace's resolved WATR appearance (DNAM colors)
    /// used to tint + light the surface. <paramref name="appearance" /> null falls back to a
    /// default tint.</summary>
    void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::FalloutXbox360Utils.WorldSpatialIndex? spatialIndex,
        WaterAppearance? appearance);

    /// <summary>As above, plus the bindless SRV index of the resolved WATR NNAM noise/normal map
    /// (from <c>TerrainTextureResolver12.ResolveNormalMapBindlessIndex</c>). <paramref
    /// name="normalMapBindlessIndex" /> null makes the surface fall back to a procedural ripple
    /// normal — proto/test worlds with no water texture still animate.</summary>
    void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::FalloutXbox360Utils.WorldSpatialIndex? spatialIndex,
        WaterAppearance? appearance,
        uint? normalMapBindlessIndex);
}
