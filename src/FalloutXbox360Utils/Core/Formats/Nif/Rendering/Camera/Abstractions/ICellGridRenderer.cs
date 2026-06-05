using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.Abstractions;

/// <summary>
///     v3 Pass 4 Step 2 — interface implemented by both <c>CellGridDebugRenderer</c>
///     (D3D11) and <c>CellGridDebugRenderer12</c> (D3D12). The wireframe cell-grid
///     overlay toggled by D1.
/// </summary>
internal interface ICellGridRenderer : IWorldRenderer
{
    /// <summary>Total exterior-cell count from the most recent <see cref="LoadData(IEnumerable{CellRecord})" />.
    /// Used in the HUD status overlay.</summary>
    int CellCount { get; }

    void LoadData(IEnumerable<CellRecord> exteriorCells);

    void LoadData(
        IEnumerable<CellRecord> exteriorCells,
        global::FalloutXbox360Utils.WorldSpatialIndex? spatialIndex);
}
