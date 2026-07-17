using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.Abstractions;

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
        global::BethesdaMultitool.WorldSpatialIndex? spatialIndex);

    /// <summary>Loads water cells plus the worldspace's resolved WATR appearance (DNAM colors)
    /// used to tint + light the surface. <paramref name="appearance" /> null falls back to a
    /// default tint.</summary>
    void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::BethesdaMultitool.WorldSpatialIndex? spatialIndex,
        WaterAppearance? appearance);

    /// <summary>As above, plus the bindless SRV index of the resolved WATR NNAM noise/normal map
    /// (from <c>TerrainTextureResolver12.ResolveNormalMapBindlessIndex</c>). <paramref
    /// name="normalMapBindlessIndex" /> null makes the surface fall back to a procedural ripple
    /// normal — proto/test worlds with no water texture still animate.</summary>
    void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::BethesdaMultitool.WorldSpatialIndex? spatialIndex,
        WaterAppearance? appearance,
        uint? normalMapBindlessIndex);

    /// <summary>
    ///     As above, preserving the authored normal-map layer order. Skyrim WATR records repeat NNAM
    ///     three times; each resolved index must reach its corresponding shader layer rather than all
    ///     three layers silently sampling the first map. Missing entries fall back to the first map.
    /// </summary>
    void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::BethesdaMultitool.WorldSpatialIndex? spatialIndex,
        WaterAppearance? appearance,
        IReadOnlyList<uint?>? normalMapBindlessIndices);

    /// <summary>Sets the scene-depth source for this frame so the shader can compute the real
    /// water-column depth fade instead of the view-angle proxy. <paramref name="depthBindlessIndex" />
    /// is an R32_FLOAT Texture2D or Texture2DMS SRV in the shared bindless heap; <c>0xFFFFFFFF</c>
    /// (or depth unavailable) reverts to the proxy + hardware-depth-test PSO.
    /// <paramref name="near" />/<paramref name="far" /> linearize the sampled depth and
    /// <paramref name="sampleCount" /> selects the matching SRV declaration. The host must transition
    /// the depth resource to DEPTH_READ | PIXEL_SHADER_RESOURCE (and unbind the DSV) before
    /// <see cref="IWorldRenderer.Render" /> when a valid index is passed.</summary>
    void SetSceneDepth(uint depthBindlessIndex, float near, float far, int sampleCount = 1);

    /// <summary>
    ///     Supplies an optional TextureCube SRV for the opt-in FO4/FO76 cubemap technique. Null keeps
    ///     the recovered cubemap bit disabled and uses the current analytic-sky fallback; the caller
    ///     must place a TextureCube view at the supplied bindless index.
    /// </summary>
    void SetModernCubeMap(uint? cubeMapBindlessIndex);

    /// <summary>Selects the per-game water profile (shader variant + tuning constants) for the loaded
    /// game. Call before <see cref="IWorldRenderer.Render" /> on each worldspace/interior load. FNV/FO3
    /// resolve to the FNV profile (byte-identical); every other game falls back to it until its own water
    /// shader is reverse-engineered (binary-RE-only policy).</summary>
    void SetGame(BethesdaGame game);
}
