namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Which orthographic camera the top-down scene renderer frames a world rectangle with.
/// </summary>
internal enum TopDownProjection
{
    /// <summary>
    ///     Straight down −Z, world axes aligned to image axes (east → right, north → up).
    ///     <para>
    ///         Required by the 2D map: its overlay has to register pixel-for-pixel with the map's own
    ///         terrain layer, markers and grid, all of which are drawn in that same world-aligned
    ///         frame. Any tilt would put the rendered meshes somewhere other than the dots they
    ///         replace.
    ///     </para>
    /// </summary>
    Straight,

    /// <summary>
    ///     Tilted orthographic trimetric — see <see cref="TrimetricViewProjBuilder" />. Reads far
    ///     better as a standalone picture because vertical surfaces stay visible, at the cost of no
    ///     longer being a plan that maps 1:1 to world XY.
    /// </summary>
    Trimetric
}
