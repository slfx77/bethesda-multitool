using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Abstractions;

/// <summary>
///     Backend-neutral contract for the textured heightmap layer and its VCLR debug mode, currently
///     implemented by <c>TerrainRenderer12</c>.
/// </summary>
internal interface ITerrainRenderer : IWorldRenderer
{
    /// <summary>Total exterior-cell count from the most recent <c>LoadData</c>.
    /// Used in the HUD status overlay.</summary>
    int CellCount { get; }

    /// <summary>Independently toggles terrain diffuse texturing and per-vertex (VCLR) tinting.
    /// Both on = engine look; textures off + vclr on = the old "vertex colors only" debug mode;
    /// both off = flat shaded. Applied on the next render.</summary>
    void SetDebugModes(bool showTextures, bool showVertexColors);

    /// <summary>When <c>false</c>, the per-frame cell build/upload budget is lifted so a few back-to-back
    /// renders build the whole visible terrain — used by the top-down overlay's depth pre-pass so ground
    /// occlusion converges in lockstep with the (also-unthrottled) reference meshes. The live 60fps loop
    /// leaves this <c>true</c>. Default <c>true</c>.</summary>
    bool StreamingThrottled { get; set; }

    /// <summary>
    ///     Depth-only render (no color writes) for the 2D map's top-down overlay pre-pass, so
    ///     placed references depth-test against the terrain and partially-buried meshes are clipped.
    ///     Returns the cell count drawn.
    /// </summary>
    int RenderDepthOnly(System.Numerics.Matrix4x4 viewProj, VisibilityCylinder cylinder);

    /// <summary>Loads the exterior cells to render, replacing any previously loaded set.</summary>
    void LoadData(Dictionary<(int gx, int gy), CellRecord> cells);

    /// <summary>Loads the exterior cells along with a spatial index and shared render cache for streaming.</summary>
    void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        global::BethesdaMultitool.WorldSpatialIndex? spatialIndex,
        global::BethesdaMultitool.WorldRenderCache? renderCache);
}
