using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;

/// <summary>
///     Caps on how many placed-light emitters reach the shader, and the selection applied when the
///     whole-frame budget is exceeded.
///     <para>
///         The per-cell caps alone bound nothing outdoors: the gather runs per visible cell, so a
///         dense exterior would upload <see cref="MaxPerExteriorCell" /> × visibleCells into an
///         unbounded shader <c>[loop]</c> that every terrain and reference pixel walks.
///         <see cref="ClipToFrameBudget" /> is what actually bounds that loop.
///     </para>
///     <para>
///         Lives in <c>Core/</c> because the selection is pure geometry over a list — the renderer
///         that consumes it is <c>#if WINDOWS_GUI</c>, and while this logic sat in <c>App/</c> the
///         only available coverage was a regex that re-extracted the cap constants out of the
///         renderer's source text. Nearest-N selection and the tie-break had no coverage at all.
///     </para>
/// </summary>
internal static class PlacedLightFrameBudget
{
    /// <summary>
    ///     Interior per-cell cap. 64 is an interim ceiling until the engine-parity light-volume
    ///     selection work lands.
    /// </summary>
    public const int MaxPerInteriorCell = 64;

    /// <summary>
    ///     Exterior per-cell cap — deliberately lower, and deliberately unchanged: exteriors
    ///     accumulate across every visible cell before the frame cap applies, and the active-ADT
    ///     base route keys on <c>PlacedLightCount == 0</c>, so this population must not move.
    /// </summary>
    public const int MaxPerExteriorCell = 16;

    /// <summary>
    ///     Whole-frame ceiling on uploaded emitters. Must be at least
    ///     <see cref="MaxPerInteriorCell" /> so a single interior cell can never exceed the frame
    ///     budget it effectively bypasses — see
    ///     <c>PlacedLightFrameBudgetTests.Caps_AreOrdered…</c>.
    /// </summary>
    public const int MaxPerFrame = 64;

    /// <summary>
    ///     Trims <paramref name="lights" /> to <see cref="MaxPerFrame" />, keeping the nearest to
    ///     <paramref name="cameraPosition" />. Returns how many were clipped (0 when within budget,
    ///     in which case the list is left untouched — including its order).
    ///     <para>
    ///         Nearest-first is the only ordering that degrades gracefully; FormID breaks ties so
    ///         the survivors do not shuffle between frames at equal distance, which would read as
    ///         flicker.
    ///     </para>
    /// </summary>
    public static int ClipToFrameBudget(List<PlacedLight> lights, Vector3 cameraPosition)
    {
        ArgumentNullException.ThrowIfNull(lights);

        if (lights.Count <= MaxPerFrame)
        {
            return 0;
        }

        var clipped = lights.Count - MaxPerFrame;
        lights.Sort((left, right) =>
        {
            var distanceOrder = Vector3.DistanceSquared(left.Position, cameraPosition)
                .CompareTo(Vector3.DistanceSquared(right.Position, cameraPosition));
            return distanceOrder != 0 ? distanceOrder : left.FormId.CompareTo(right.FormId);
        });
        lights.RemoveRange(MaxPerFrame, clipped);

        return clipped;
    }
}
