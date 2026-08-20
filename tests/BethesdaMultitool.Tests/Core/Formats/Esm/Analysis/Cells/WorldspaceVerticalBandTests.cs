using BethesdaMultitool.Core.Formats.Esm.Analysis.Cells;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Analysis.Cells;

/// <summary>
///     Pins <see cref="WorldspaceVerticalBand" />, the elevation plausibility guard the DMP recovery
///     passes apply before placing a ref into an exterior worldspace by its X/Y alone.
///     <para>
///         Regression source: <c>Fallout_Release_Beta.xex21</c> fabricated 463 Hoover Dam INTERIOR
///         placements (<c>NVHooverDamGenerator</c> + the <c>Utl*</c> corridor tileset, Z 10368-12416)
///         into TheStripWorld tiles (1,-3) and (1,-2), because interior-LOCAL coordinates fell inside
///         the Strip's captured grid span. Every placement in the Strip's genuinely captured cells sits
///         at Z 942-2667, so Z separates them cleanly where X/Y cannot.
///     </para>
/// </summary>
public sealed class WorldspaceVerticalBandTests
{
    private const uint StripWorld = 0x0010B96Fu;
    private const uint OtherWorld = 0x0000FFFFu;

    private static CellRecord ExteriorCell(
        uint worldspaceFormId,
        int gridX,
        int gridY,
        string? assignmentSource,
        params float[] placementZs)
    {
        var cell = new CellRecord
        {
            FormId = 0x1000u + (uint)gridX,
            GridX = gridX,
            GridY = gridY,
            WorldspaceFormId = worldspaceFormId,
            WorldspaceAssignmentSource = assignmentSource,
            PlacedObjects = []
        };
        foreach (var z in placementZs)
        {
            cell.PlacedObjects.Add(new PlacedReference { FormId = 0x2000u, Z = z });
        }

        return cell;
    }

    [Fact]
    public void CapturedPlacements_DefineTheBand_AndTheXex21InteriorIsRejected()
    {
        // The real Strip band, from captured cells.
        var band = WorldspaceVerticalBand.Measure(
        [
            ExteriorCell(StripWorld, -1, 0, "CellGrup", 942f, 1011f, 2667f)
        ]);

        Assert.Equal(1, band.WorldspaceCount);
        Assert.Equal((942f, 2667f), band.BandFor(StripWorld));

        // Real Strip elevations stay plausible.
        Assert.True(band.IsPlausibleElevation(StripWorld, 942f));
        Assert.True(band.IsPlausibleElevation(StripWorld, 1011f));
        Assert.True(band.IsPlausibleElevation(StripWorld, 2667f));

        // The Hoover Dam interior block does not — every observed Z is rejected.
        Assert.False(band.IsPlausibleElevation(StripWorld, 10368f));
        Assert.False(band.IsPlausibleElevation(StripWorld, 11648f));
        Assert.False(band.IsPlausibleElevation(StripWorld, 12416f));
    }

    [Fact]
    public void MarginAllowsTallExteriorContentJustOutsideTheObservedExtremes()
    {
        var band = WorldspaceVerticalBand.Measure(
        [
            ExteriorCell(StripWorld, 0, 0, "RuntimeCellMap", 1000f, 2000f)
        ]);

        // One cell edge of slack in both directions — a tower or flyover above captured content.
        Assert.True(band.IsPlausibleElevation(StripWorld, 2000f + WorldspaceVerticalBand.MarginWorldUnits));
        Assert.True(band.IsPlausibleElevation(StripWorld, 1000f - WorldspaceVerticalBand.MarginWorldUnits));
        Assert.False(band.IsPlausibleElevation(StripWorld, 2000f + WorldspaceVerticalBand.MarginWorldUnits + 1f));
        Assert.False(band.IsPlausibleElevation(StripWorld, 1000f - WorldspaceVerticalBand.MarginWorldUnits - 1f));
    }

    [Fact]
    public void WorldspaceWithoutCapturedPlacements_IsNeverRejected()
    {
        // Absence of evidence must not become evidence: with no band, every elevation passes so the
        // recovery passes keep their prior behaviour.
        var band = WorldspaceVerticalBand.Measure([ExteriorCell(StripWorld, 0, 0, "CellGrup")]);

        Assert.True(band.IsEmpty);
        Assert.Null(band.BandFor(StripWorld));
        Assert.True(band.IsPlausibleElevation(StripWorld, 999999f));
        Assert.True(band.IsPlausibleElevation(OtherWorld, -999999f));
    }

    [Fact]
    public void InferredAndVirtualCells_AreNotEvidence()
    {
        // Bands must be measured before/independently of the passes they police — otherwise the first
        // fabricated cell widens the band that is supposed to reject the next one.
        var inferred = ExteriorCell(StripWorld, 1, -3, "WorldspaceBoundsInference", 11648f);
        var offsetCluster = ExteriorCell(StripWorld, 1, -2, "AuthorityOffsetCluster", 11264f);
        var virtualCell = ExteriorCell(StripWorld, 2, -2, "Virtual", 12032f);
        var captured = ExteriorCell(StripWorld, -1, 0, "CellGrup", 1000f, 2000f);

        var band = WorldspaceVerticalBand.Measure([inferred, offsetCluster, virtualCell, captured]);

        Assert.Equal((1000f, 2000f), band.BandFor(StripWorld));
        Assert.False(band.IsPlausibleElevation(StripWorld, 11648f));
    }

    [Fact]
    public void IsVirtualCells_AreExcludedRegardlessOfTheirAssignmentSource()
    {
        var virtualCell = ExteriorCell(StripWorld, 1, -2, "CellGrup", 11264f);
        var mutated = virtualCell with { IsVirtual = true };
        var captured = ExteriorCell(StripWorld, -1, 0, null, 1000f, 1500f);

        var band = WorldspaceVerticalBand.Measure([mutated, captured]);

        Assert.Equal((1000f, 1500f), band.BandFor(StripWorld));
    }

    [Fact]
    public void InteriorCellsAndUnresolvedBuckets_AreExcluded()
    {
        var interior = new CellRecord
        {
            FormId = 0x3000u,
            Flags = 0x01,
            PlacedObjects = [new PlacedReference { FormId = 1u, Z = 11648f }]
        };
        var bucket = new CellRecord
        {
            FormId = 0xFE100001u,
            WorldspaceFormId = StripWorld,
            IsVirtual = true,
            IsUnresolvedBucket = true,
            PlacedObjects = [new PlacedReference { FormId = 2u, Z = 11648f }]
        };
        var captured = ExteriorCell(StripWorld, 0, 0, "CellGrup", 1200f);

        var band = WorldspaceVerticalBand.Measure([interior, bucket, captured]);

        Assert.Equal((1200f, 1200f), band.BandFor(StripWorld));
        Assert.False(band.IsPlausibleElevation(StripWorld, 11648f));
    }

    [Fact]
    public void BandsAreTrackedPerWorldspace()
    {
        var band = WorldspaceVerticalBand.Measure(
        [
            ExteriorCell(StripWorld, 0, 0, "CellGrup", 1000f, 2000f),
            ExteriorCell(OtherWorld, 5, 5, "CellGrup", 11000f, 12000f)
        ]);

        Assert.Equal(2, band.WorldspaceCount);
        // The same Z is implausible for one worldspace and fine for the other.
        Assert.False(band.IsPlausibleElevation(StripWorld, 11500f));
        Assert.True(band.IsPlausibleElevation(OtherWorld, 11500f));
    }

    [Fact]
    public void NonFiniteElevations_AreIgnoredWhenMeasuringAndAlwaysPassWhenTested()
    {
        var band = WorldspaceVerticalBand.Measure(
        [
            ExteriorCell(StripWorld, 0, 0, "CellGrup", float.NaN, 1000f, float.PositiveInfinity, 2000f)
        ]);

        Assert.Equal((1000f, 2000f), band.BandFor(StripWorld));
        Assert.True(band.IsPlausibleElevation(StripWorld, float.NaN));
        Assert.True(band.IsPlausibleElevation(StripWorld, float.PositiveInfinity));
    }
}