using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.Abstractions;

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
        global::FalloutXbox360Utils.WorldSpatialIndex? spatialIndex);
}
