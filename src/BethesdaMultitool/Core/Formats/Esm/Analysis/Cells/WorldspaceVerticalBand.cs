using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.Cells;

/// <summary>
///     The vertical (Z) band each worldspace's CAPTURED exterior content occupies, used to reject
///     position-inferred exterior placements that cannot belong to it.
///     <para>
///         Several DMP recovery passes place a ref whose parent CELL was never captured by mapping its
///         X/Y through <c>WorldToCellCoordinates</c> and fabricating (or reusing) an exterior tile in
///         whichever worldspace's captured grid span contains that coordinate. X/Y alone is weak
///         evidence: an INTERIOR cell's refs carry interior-LOCAL coordinates, and those routinely land
///         inside some exterior worldspace's grid span, which fabricates a phantom exterior copy of an
///         interior.
///     </para>
///     <para>
///         Confirmed on <c>Fallout_Release_Beta.xex21</c>: 463 Hoover Dam interior placements
///         (<c>NVHooverDamGenerator</c> + the <c>Utl*</c> utility-corridor tileset) were fabricated into
///         TheStripWorld tiles (1,-3) and (1,-2). Their Z spans 10368–12416 while every one of the
///         1,344 placements in TheStripWorld's genuinely captured cells sits in 942–2667 — no overlap,
///         and the interior floats ~7,700 units above the highest real Strip object. Z is the
///         discriminator X/Y cannot be, because an interior tileset has no reason to share the
///         worldspace's terrain elevation.
///     </para>
///     <para>
///         The band is deliberately generous (<see cref="MarginWorldUnits" /> beyond the observed
///         extremes) so legitimately tall or airborne exterior content is never rejected — this is a
///         "wrong by an order of magnitude" filter, not a tight fit. A worldspace with no captured
///         exterior placements yields no band and therefore no rejection: absence of evidence must not
///         become evidence.
///     </para>
/// </summary>
internal sealed class WorldspaceVerticalBand
{
    /// <summary>
    ///     Slack allowed beyond the captured Z extremes, in world units. One FNV cell edge (4096) —
    ///     wide enough for a tower or a flyover marker above anything the dump happened to capture,
    ///     narrow enough to still reject an interior tileset stacked thousands of units off.
    /// </summary>
    internal const float MarginWorldUnits = 4096f;

    private readonly Dictionary<uint, (float Min, float Max)> _bandsByWorldspace;

    private WorldspaceVerticalBand(Dictionary<uint, (float Min, float Max)> bands)
    {
        _bandsByWorldspace = bands;
    }

    /// <summary>Worldspaces that produced a usable band (i.e. had captured exterior placements).</summary>
    internal int WorldspaceCount => _bandsByWorldspace.Count;

    /// <summary>True when no worldspace produced a band, so every elevation test passes.</summary>
    internal bool IsEmpty => _bandsByWorldspace.Count == 0;

    /// <summary>
    ///     Measures the band from placements in cells that are TRUSTWORTHY evidence of a worldspace's
    ///     real elevation: captured exterior cells that are not virtual, not unresolved buckets, and not
    ///     themselves the product of an inference pass. Must be built ONCE before any fabrication runs —
    ///     otherwise the first fabricated cell widens the band that is supposed to reject it.
    /// </summary>
    internal static WorldspaceVerticalBand Measure(IEnumerable<CellRecord> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        var bands = new Dictionary<uint, (float Min, float Max)>();
        foreach (var cell in cells)
        {
            if (cell.IsInterior ||
                cell.IsVirtual ||
                cell.IsUnresolvedBucket ||
                cell.WorldspaceFormId is not > 0 ||
                !IsCapturedEvidence(cell.WorldspaceAssignmentSource))
            {
                continue;
            }

            var worldspaceFormId = cell.WorldspaceFormId.Value;
            foreach (var placed in cell.PlacedObjects)
            {
                if (!float.IsFinite(placed.Z))
                {
                    continue;
                }

                if (bands.TryGetValue(worldspaceFormId, out var band))
                {
                    bands[worldspaceFormId] = (MathF.Min(band.Min, placed.Z), MathF.Max(band.Max, placed.Z));
                }
                else
                {
                    bands[worldspaceFormId] = (placed.Z, placed.Z);
                }
            }
        }

        return new WorldspaceVerticalBand(bands);
    }

    /// <summary>
    ///     Whether <paramref name="z" /> is a plausible elevation for exterior content in
    ///     <paramref name="worldspaceFormId" />. Returns true when no band was measured for that
    ///     worldspace (nothing to test against) or when <paramref name="z" /> is not finite in a way we
    ///     can judge — every caller's prior behaviour is preserved except for the order-of-magnitude
    ///     mismatches this exists to catch.
    /// </summary>
    internal bool IsPlausibleElevation(uint worldspaceFormId, float z)
    {
        if (!float.IsFinite(z) || !_bandsByWorldspace.TryGetValue(worldspaceFormId, out var band))
        {
            return true;
        }

        return z >= band.Min - MarginWorldUnits && z <= band.Max + MarginWorldUnits;
    }

    /// <summary>The measured band for one worldspace, or null when it has no captured placements.</summary>
    internal (float Min, float Max)? BandFor(uint worldspaceFormId) =>
        _bandsByWorldspace.TryGetValue(worldspaceFormId, out var band) ? band : null;

    /// <summary>
    ///     Whether a cell's worldspace assignment came from CAPTURE (a GRUP hierarchy, the runtime
    ///     pCellMap, the authority JSON, bounds/fragment resolution of a real captured cell) rather than
    ///     from one of the position-inference passes whose output this band is meant to police.
    /// </summary>
    private static bool IsCapturedEvidence(string? assignmentSource) =>
        assignmentSource is not ("WorldspaceBoundsInference"
            or "AuthorityOffsetCluster"
            or "OffsetCluster"
            or "InteriorOffsetCluster"
            or "Virtual");
}
