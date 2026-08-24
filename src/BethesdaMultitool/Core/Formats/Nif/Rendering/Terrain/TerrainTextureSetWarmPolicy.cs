using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Decides whether a worldspace's per-cell <see cref="CellTerrainTextureSet" />s may be
///     pre-built in bulk when the 3D terrain renderer loads it.
///     <para>
///         The warm is a latency optimization, never a correctness requirement: every consumer
///         reaches these sets through <c>WorldRenderCache.GetOrBuildTerrainTextureSet</c>, which
///         builds any missing cell on demand. Skipping it costs the streaming mesh build ~0.05 ms
///         per cold cell and drops nothing.
///     </para>
///     <para>
///         What makes the bulk warm dangerous is that it is <b>resident</b>, and its per-cell cost
///         grows with the square of the LAND grid: a 33-grid cell holds ~68 KB of vertex weights,
///         but a 129-grid one holds ~1 MB. Sized against the ~5k-cell, 33-grid worldspaces it was
///         profiled on (~340 MB), the warm is affordable; applied unchanged to Fallout 76's
///         Appalachia — a 129-grid worldspace with 41,219 gridded cells — it asks for ~42 GB and
///         exhausts memory before the first frame draws, which is what made "switch to 3D view"
///         hang the app.
///     </para>
/// </summary>
internal static class TerrainTextureSetWarmPolicy
{
    /// <summary>Size of one vertex-weight entry (<see cref="System.Numerics.Vector4" />).</summary>
    private const int VertexWeightBytes = 16;

    /// <summary>
    ///     Ceiling on what the bulk warm may make resident. Chosen well above every 33-grid
    ///     worldspace that ships (Fallout New Vegas's largest is ~6k cells ≈ 400 MB) so their load
    ///     behavior is unchanged, and far below what a 129-grid worldspace would demand, so those
    ///     fall back to on-demand building instead of dying.
    /// </summary>
    internal const long MaxWarmBytes = 1024L * 1024 * 1024;

    /// <summary>Resident bytes one cell's texture set occupies at <paramref name="gridSize" />.</summary>
    internal static long EstimateCellBytes(int gridSize) =>
        (long)TerrainMeshBuilder.VertexCountFor(gridSize) * CellTerrainTextureSet.SlotVectors * VertexWeightBytes;

    /// <summary>Total resident bytes a full warm of <paramref name="cellCount" /> cells would cost.</summary>
    internal static long EstimateWarmBytes(int gridSize, int cellCount) =>
        EstimateCellBytes(gridSize) * cellCount;

    /// <summary>
    ///     True when the whole worldspace's texture sets fit <see cref="MaxWarmBytes" /> and may be
    ///     pre-built at load; false when they must be left to on-demand building.
    /// </summary>
    internal static bool ShouldWarm(int gridSize, int cellCount) =>
        cellCount > 0 && gridSize > 0 && EstimateWarmBytes(gridSize, cellCount) <= MaxWarmBytes;
}
