using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Abstractions;

/// <summary>
///     Backend-neutral contract for the placed-object NIF mesh layer, currently implemented by
///     <c>ReferenceRenderer12</c>.
/// </summary>
internal interface IReferenceRenderer : IWorldRenderer
{
    /// <summary>
    ///     The count of REFRs that issued at least one submesh draw on the
    ///     previous frame. Surfaced in the HUD chip.
    /// </summary>
    int ReferencesDrawnLastFrame { get; }

    /// <summary>
    ///     When <c>false</c> (default), references with the Initially Disabled flag
    ///     (header <c>0x0800</c>) are skipped at render time — matching the 2D viewer's default.
    ///     Toggled by the "Initially Disabled" checkbox; no cache rebuild (render-time filter).
    /// </summary>
    bool ShowInitiallyDisabled { get; set; }

    /// <summary>
    ///     When <c>false</c>, the per-frame mesh-streaming budget (upload count + time + bytes,
    ///     decode starts + concurrency) is lifted so a single render loads everything visible. The live
    ///     60fps loop leaves this <c>true</c>; the on-demand top-down overlay sets it <c>false</c> for the
    ///     duration of its render (it has no framerate target). Default <c>true</c>.
    /// </summary>
    bool StreamingThrottled { get; set; }

    void LoadData(
        WorldRenderCache renderCache,
        Dictionary<(int gx, int gy), CellRecord> cells,
        WorldSpatialIndex? spatialIndex);
}
