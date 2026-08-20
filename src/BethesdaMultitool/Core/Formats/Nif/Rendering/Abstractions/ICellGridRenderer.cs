using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Abstractions;

/// <summary>
///     Backend-neutral contract for the wireframe cell-grid overlay, currently implemented by
///     <c>CellGridDebugRenderer12</c>.
/// </summary>
internal interface ICellGridRenderer : IWorldRenderer
{
    /// <summary>
    ///     Total exterior-cell count from the most recent <see cref="LoadData(IEnumerable{CellRecord})" />.
    ///     Used in the HUD status overlay.
    /// </summary>
    int CellCount { get; }

    void LoadData(IEnumerable<CellRecord> exteriorCells);

    void LoadData(
        IEnumerable<CellRecord> exteriorCells,
        WorldSpatialIndex? spatialIndex);
}
