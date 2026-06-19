using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.Abstractions;

/// <summary>
///     v3 parity work — interface for the 3D navmesh overlay renderer (the 2D analog is
///     <c>WorldMapNavMeshOverlayRenderer</c>). Draws translucent NAVM triangles + edges for
///     every visible cell that has navmeshes, toggled by the "Nav mesh" checkbox.
/// </summary>
internal interface INavMeshRenderer : IWorldRenderer
{
    void LoadData(
        IReadOnlyDictionary<uint, List<NavMeshRecord>> navMeshesByCell,
        Dictionary<(int gx, int gy), CellRecord> cells,
        global::BethesdaMultitool.WorldSpatialIndex? spatialIndex);
}
