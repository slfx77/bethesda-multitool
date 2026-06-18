using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils;

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
    ///     True when the D3D12 backend + terrain + reference renderers are initialised, so
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
    Task<TopDownRender?> RenderTopDownAsync(
        float worldMinX, float worldMaxX, float worldMinY, float worldMaxY,
        int pixelWidth, int pixelHeight, bool showDisabled, bool showWater, uint? worldspaceFormId,
        IReadOnlyCollection<PlacedObjectCategory> hiddenCategories, CancellationToken ct);
}

/// <summary>
///     A completed top-down render: BGRA pixels (<see cref="Width" /> × <see cref="Height" />, tightly
///     packed) covering the world rectangle [<see cref="WorldMinX" />,<see cref="WorldMaxX" />] ×
///     [<see cref="WorldMinY" />,<see cref="WorldMaxY" />] (world north-Y).
/// </summary>
internal sealed record TopDownRender(
    byte[] Bgra,
    int Width,
    int Height,
    float WorldMinX,
    float WorldMaxX,
    float WorldMinY,
    float WorldMaxY,
    bool IsComplete);
