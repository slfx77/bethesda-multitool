using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.Abstractions;

/// <summary>
///     v3 Pass 4 Step 2 — interface implemented by both <c>ReferenceRenderer</c>
///     (D3D11) and <c>ReferenceRenderer12</c> (D3D12). The placed-object NIF mesh
///     layer toggled by D5.
/// </summary>
internal interface IReferenceRenderer : IWorldRenderer
{
    /// <summary>The count of REFRs that issued at least one submesh draw on the
    /// previous frame. Surfaced in the HUD chip.</summary>
    int ReferencesDrawnLastFrame { get; }

    void LoadData(
        global::FalloutXbox360Utils.WorldRenderCache renderCache,
        Dictionary<(int gx, int gy), CellRecord> cells,
        global::FalloutXbox360Utils.WorldSpatialIndex? spatialIndex);
}
