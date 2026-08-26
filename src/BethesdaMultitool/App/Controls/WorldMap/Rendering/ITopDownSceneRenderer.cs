using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool;

/// <summary>
///     Implemented by the 3D worldspace control so the 2D map can borrow its live D3D12 stack
///     (device + terrain/reference renderers + mesh/texture caches) to produce a top-down
///     orthographic render of a world-XY rectangle, read back to a CPU pixel buffer. The 2D map
///     composites that as its "Rendered models" overlay — placed objects rendered through the real
///     depth buffer so terrain occludes partially-buried meshes.
/// </summary>
internal interface ITopDownSceneRenderer
{
    /// <summary>
    ///     True when the D3D12 backend + terrain + reference renderers are initialized, so
    ///     <see cref="RenderTopDownAsync" /> can run. Drives whether the 2D map's toggle is enabled.
    /// </summary>
    bool CanRenderTopDown { get; }

    /// <summary>
    ///     Renders the given WORLD-space rectangle (X east, Y north) top-down and returns the BGRA
    ///     pixels. MUST be called on the UI thread (records + submits D3D12 commands on the device
    ///     thread); the GPU fence wait + readback happen on a background thread internally. Returns
    ///     null when unavailable or on failure. The result's <see cref="TopDownRender.IsComplete" />
    ///     is false while terrain cells / reference meshes / textures are still streaming in — the
    ///     caller should re-request until it goes true.
    /// </summary>
    /// <param name="worldspaceFormId">
    ///     The exterior worldspace to render (null = the unlinked-exterior set). The provider
    ///     switches its active worldspace to match before rendering — the 3D control selects its own
    ///     initial worldspace independently, so without this the overlay would render whichever
    ///     worldspace the 3D view happens to hold. Returns null if no matching exterior worldspace.
    /// </param>
    /// <param name="showWater">
    ///     When true, water is rendered into the top-down pass so it occludes submerged geometry but
    ///     not docks/bridges above the water plane — height-correct via the scene depth buffer (a flat
    ///     2D water overlay can't do this). The 2D map suppresses its own water layer wherever this
    ///     overlay is drawn, so this must follow the map's water toggle.
    /// </param>
    /// <param name="hiddenCategories">
    ///     Placed-object categories to hide in the overlay — the 2D map's legend filter, so the
    ///     "Rendered models" overlay matches the rest of the map (the user's category toggles drive
    ///     the rendered 3D meshes too, not just the 2D markers). Empty = show all.
    /// </param>
    /// <param name="enableLighting">
    ///     When true, the overlay bakes directional sun + ambient lighting from <paramref name="gameHour" />
    ///     (the 2D map's lighting control); when false, flat legacy shade. Fog is always off (the 2D map
    ///     has no fog control).
    /// </param>
    /// <param name="gameHour">Time of day (0–24h) driving the sun direction/intensity when lighting is on.</param>
    /// <param name="interiorCellFormId">
    ///     When non-null, render this INTERIOR cell top-down instead of an exterior worldspace
    ///     (<paramref name="worldspaceFormId" /> is then ignored). Interiors have no grid coords; their
    ///     objects sit at absolute world coords — the same frame the 2D map's cell-detail view draws them
    ///     in — so the world rectangle aligns. The provider clips just below the interior's geometry
    ///     ceiling so the top-down view shows the floor plan instead of the roof. Returns null if the
    ///     interior cell isn't found.
    /// </param>
    /// <param name="includeTerrainColor">
    ///     When false (the default, and what the 2D map wants), terrain contributes DEPTH ONLY: the
    ///     ground is transparent and the map composites this over its own terrain layer. When true,
    ///     terrain is drawn in colour so the result is a self-contained image — required by any
    ///     consumer that saves the render directly to a file, which would otherwise get objects
    ///     floating on nothing.
    ///     <para>
    ///         Setting this also forces the terrain pass on regardless of the projected-cell-size
    ///         gate that normally skips it at overview zoom. That gate assumes a terrain layer
    ///         exists underneath; with no compositing there is no such fallback, so honouring it
    ///         would produce exactly the empty image this flag exists to prevent.
    ///     </para>
    /// </param>
    /// <param name="projection">
    ///     Camera framing. <see cref="TopDownProjection.Straight" /> (the default the 2D map needs)
    ///     keeps the image world-axis-aligned so it registers with the map's other layers;
    ///     <see cref="TopDownProjection.Trimetric" /> tilts the camera for a more legible standalone
    ///     picture and no longer corresponds 1:1 to world XY.
    /// </param>
    /// <param name="contentWorldZ">
    ///     World-Z extent of the subject's content, for framing a tilted camera vertically. Ignored
    ///     by <see cref="TopDownProjection.Straight" />, which frames purely in XY. Null falls back
    ///     to a flat band at the ground plane.
    /// </param>
    /// <param name="trimetricYawDegrees">
    ///     Camera azimuth for <see cref="TopDownProjection.Trimetric" /> (ignored by Straight).
    ///     Exists so a capture harness can shoot the same subject from several compass directions —
    ///     a single tilted view hides whatever stands behind the near-side walls.
    /// </param>
    Task<TopDownRender?> RenderTopDownAsync(
        float worldMinX, float worldMaxX, float worldMinY, float worldMaxY,
        int pixelWidth, int pixelHeight, bool showDisabled, bool showWater, uint? worldspaceFormId,
        IReadOnlyCollection<PlacedObjectCategory> hiddenCategories,
        bool enableLighting, float gameHour, uint? interiorCellFormId, bool includeTerrainColor,
        TopDownProjection projection, (float Min, float Max)? contentWorldZ,
        float trimetricYawDegrees, CancellationToken ct);
}

/// <summary>
///     A completed top-down render: BGRA pixels (<see cref="Width" /> × <see cref="Height" />, tightly
///     packed) covering the world rectangle [<see cref="WorldMinX" />,<see cref="WorldMaxX" />] ×
///     [<see cref="WorldMinY" />,<see cref="WorldMaxY" />] (world north-Y).
///     <para>
///         <see cref="IsComplete" /> is the LOOSE gate ("nothing actively streaming") — the live
///         overlay's re-request key, which must converge even in regions with permanently-missing
///         assets. <see cref="IsFullySettled" /> is the STRICT export gate (additionally no submesh
///         withheld on pending textures, <see cref="StreamingQuiescence" />) — callers keying on it
///         MUST time-box their loop.
///     </para>
/// </summary>
internal sealed record TopDownRender(
    byte[] Bgra,
    int Width,
    int Height,
    float WorldMinX,
    float WorldMaxX,
    float WorldMinY,
    float WorldMaxY,
    bool IsComplete,
    bool IsFullySettled,
    int ReferenceInstances = 0,
    int ReferenceDrawn = 0,
    int SpeedTreeBranchInstances = 0,
    int SpeedTreeLeafInstances = 0,
    int SpeedTreeBillboardInstances = 0);
