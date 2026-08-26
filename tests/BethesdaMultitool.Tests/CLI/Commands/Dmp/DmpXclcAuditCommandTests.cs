using BethesdaMultitool.CLI.Commands.Dmp;
using Xunit;
using static BethesdaMultitool.CLI.Commands.Dmp.DmpXclcAuditCommand;

namespace BethesdaMultitool.Tests.CLI.Commands.Dmp;

/// <summary>
///     Synthetic coverage for the <c>dmp xclc-audit</c> pairing/counting seam
///     (<see cref="DmpXclcAuditCommand.Audit" />). Pins that the audit mirrors the production
///     claim rule from <c>CellRecordHandler.ParseCellFromScanResult</c>: first CellGrids LIST
///     entry with |offset diff| &lt; 200 — list order, not nearest-by-offset — and that the
///     contest/theft/impact counters derive correctly. No dumps are touched.
/// </summary>
public sealed class DmpXclcAuditCommandTests
{
    [Fact]
    public void Audit_EmptyInputs_AllZero()
    {
        var result = Audit([], []);

        Assert.Equal(0, result.TotalCells);
        Assert.Equal(0, result.TotalGrids);
        Assert.Equal(0, result.CellsWithClaim);
        Assert.Equal(0, result.ContestedXclcCount);
        Assert.Equal(0, result.CellPairsSameXclc);
        Assert.Equal(0, result.TheftCells);
        Assert.Equal(0, result.DifferingGridContestedCells);
        Assert.Empty(result.Cells);
        Assert.Empty(result.ContestedGrids);
    }

    [Fact]
    public void Audit_UncontestedCell_ClaimsItsOwnGrid()
    {
        // One cell, one XCLC 20 bytes away: clean claim, no contests.
        var result = Audit(
            [new AuditCell(0x1000, 10_000)],
            [new AuditGrid(3, 4, 10_020)]);

        var cell = Assert.Single(result.Cells);
        Assert.Equal(0, cell.ClaimedGridIndex);
        Assert.Equal(0, cell.NearestGridIndex);
        Assert.False(cell.IsContested);
        Assert.False(cell.ClaimedGridNearerToOtherCell);
        Assert.Equal(1, result.CellsWithClaim);
        Assert.Equal(0, result.ContestedXclcCount);
        Assert.Equal(0, result.DifferingGridContestedCells);
    }

    [Fact]
    public void Audit_WindowIsStrict_Exactly200BytesDoesNotMatch()
    {
        // Production rule is Math.Abs(diff) < 200 — a grid exactly 200 away must NOT be claimed.
        var result = Audit(
            [new AuditCell(0x1000, 10_000)],
            [new AuditGrid(1, 1, 10_200), new AuditGrid(2, 2, 9_801)]);

        var cell = Assert.Single(result.Cells);
        Assert.Equal(1, cell.CandidateCount); // only the 199-away grid
        Assert.Equal(1, cell.ClaimedGridIndex);
    }

    [Fact]
    public void Audit_ClaimFollowsListOrder_NotNearestOffset()
    {
        // Grids list deliberately NOT offset-sorted: the FAR grid (350) is listed before the
        // NEAR grid (100). A cell at 250 has both in-window; production FirstOrDefault takes
        // the first LIST entry — the far one — so the audit must report the same.
        var result = Audit(
            [new AuditCell(0xA, 250)],
            [new AuditGrid(7, 7, 350), new AuditGrid(9, 9, 100)]);

        var cell = Assert.Single(result.Cells);
        Assert.Equal(2, cell.CandidateCount);
        Assert.Equal(0, cell.ClaimedGridIndex); // list-first (offset 350, dist 100)
        Assert.Equal(0, cell.NearestGridIndex); // 350 is also nearest (dist 100 vs 150)
        Assert.True(cell.HasDifferingCandidates); // (7,7) vs (9,9)
        Assert.Equal(1, result.DifferingGridContestedCells);
    }

    [Fact]
    public void Audit_TheftAndSharedClaim_CountedOnce()
    {
        // Layout:
        //   g0 @100 (1,1)   g1 @350 (2,2)   g2 @10000 (5,5)
        //   cellB @120 -> claims g0 (its own; dist 20)
        //   cellA @250 -> candidates {g0 (dist 150), g1 (dist 100)}; list-first claim = g0,
        //                 which is strictly nearer cellB => THEFT; candidate grids differ.
        //   cellC @10050 -> claims g2 cleanly.
        var result = Audit(
            [
                new AuditCell(0xB, 120),
                new AuditCell(0xA, 250),
                new AuditCell(0xC, 10_050)
            ],
            [
                new AuditGrid(1, 1, 100),
                new AuditGrid(2, 2, 350),
                new AuditGrid(5, 5, 10_000)
            ]);

        Assert.Equal(3, result.TotalCells);
        Assert.Equal(3, result.TotalGrids);
        Assert.Equal(3, result.CellsWithClaim);

        var cellA = result.Cells.Single(c => c.CellFormId == 0xA);
        Assert.Equal(0, cellA.ClaimedGridIndex); // stole g0 (list-first)
        Assert.Equal(1, cellA.NearestGridIndex); // g1 was nearer for A
        Assert.True(cellA.ClaimedGridNearerToOtherCell);
        Assert.True(cellA.HasDifferingCandidates);

        var cellB = result.Cells.Single(c => c.CellFormId == 0xB);
        Assert.Equal(0, cellB.ClaimedGridIndex);
        Assert.False(cellB.ClaimedGridNearerToOtherCell);

        // (i) g0 contested (cells A+B in window); g1 has only A in window and is unclaimed.
        Assert.Equal(1, result.ContestedXclcCount);
        var contested = Assert.Single(result.ContestedGrids);
        Assert.Equal(0, contested.GridIndex);
        Assert.Equal(2, contested.CellsInWindow);
        Assert.Equal(2, contested.ClaimedByCount);
        Assert.Equal((uint?)0xB, contested.NearestCellFormId);
        Assert.True(contested.ClaimedByNonNearestCell);

        // (ii) A and B resolve to the same XCLC -> one pair.
        Assert.Equal(1, result.CellPairsSameXclc);

        // Theft: only A.
        Assert.Equal(1, result.TheftCells);
        Assert.Equal(0, result.TheftSoleCandidateCells); // A had an alternative (g1)

        // (iii) impact: only A has differing candidate grids.
        Assert.Equal(1, result.DifferingGridContestedCells);
    }

    [Fact]
    public void Audit_TheftWithNoAlternative_TrackedSeparately()
    {
        // cellB @100 owns g0 @110 (dist 10). cellA @260 has ONLY g0 in-window (dist 150) and
        // steals it: theft with no alternative candidate. Grid values cannot differ (one
        // candidate), so it must not count toward DifferingGridContestedCells.
        var result = Audit(
            [
                new AuditCell(0xB, 100),
                new AuditCell(0xA, 260)
            ],
            [new AuditGrid(4, 4, 110)]);

        var cellA = result.Cells.Single(c => c.CellFormId == 0xA);
        Assert.True(cellA.ClaimedGridNearerToOtherCell);
        Assert.Equal(1, cellA.CandidateCount);
        Assert.False(cellA.HasDifferingCandidates);

        Assert.Equal(1, result.TheftCells);
        Assert.Equal(1, result.TheftSoleCandidateCells);
        Assert.Equal(0, result.DifferingGridContestedCells);
        Assert.Equal(1, result.CellPairsSameXclc); // both cells resolve to g0
        Assert.Equal(1, result.ContestedXclcCount);
    }

    // ===== proximity ref-claim audit (ResolveCellRefs DMP fallback mirror) =====

    [Fact]
    public void AuditProximity_EmptyInputs_AllZero()
    {
        var result = AuditProximity([], []);

        Assert.Empty(result.Cells);
        Assert.Equal(0, result.TotalRefrs);
        Assert.Equal(0.0, result.MedianClaimedRefs);
        Assert.Null(result.LastCell);
    }

    [Fact]
    public void AuditProximity_WindowCappedAtNextCell_StrictBoundaries()
    {
        // Cells at 1000 and 5000. Refs: 1000 (== cell offset, excluded), 1500 and 4999
        // (claimed by cell A), 5000 (== next cell's offset, excluded from A; == B's own
        // offset, excluded from B), 5500 (claimed by B).
        var result = AuditProximity(
            [
                new AuditCell(0xA, 1_000),
                new AuditCell(0xB, 5_000)
            ],
            [1_000, 1_500, 4_999, 5_000, 5_500]);

        var cellA = result.Cells.Single(c => c.FormId == 0xA);
        Assert.Equal(2, cellA.ClaimedRefs);
        Assert.Equal(5_000, cellA.WindowEnd); // capped at next cell, not 501_000
        Assert.False(cellA.IsLastCell);
        Assert.Equal(3_999, cellA.TailExtentBytes);

        var cellB = result.Cells.Single(c => c.FormId == 0xB);
        Assert.Equal(1, cellB.ClaimedRefs);
        Assert.True(cellB.IsLastCell);
        Assert.Equal(5_000 + DmpXclcAuditCommand.ProximityReachBytes, cellB.WindowEnd);
    }

    [Fact]
    public void AuditProximity_LastCellReaches500KbAndCountsDeepTail()
    {
        // Single cell at 10_000: no next-cell cap. Refs at +50 KB (inside threshold),
        // +150 KB and +499 KB (beyond the 100 KB tail threshold), +500 KB (outside the
        // production reach entirely — strict end).
        var result = AuditProximity(
            [new AuditCell(0xC, 10_000)],
            [60_000, 160_000, 509_000, 510_000]);

        Assert.NotNull(result.LastCell);
        var last = result.LastCell!;
        Assert.Equal(0xCu, last.FormId);
        Assert.True(last.IsLastCell);
        Assert.Equal(3, last.ClaimedRefs); // 510_000 == start + 500_000 is excluded
        Assert.Equal(2, last.RefsBeyondTailThreshold); // 160_000 and 509_000
        Assert.Equal(499_000, last.TailExtentBytes);
    }

    [Fact]
    public void AuditProximity_MedianOverAllCells()
    {
        // Claim counts 0 / 1 / 4 -> median 1.
        var result = AuditProximity(
            [
                new AuditCell(0x1, 0),
                new AuditCell(0x2, 10_000),
                new AuditCell(0x3, 20_000)
            ],
            [10_500, 20_100, 20_200, 20_300, 20_400]);

        Assert.Equal(1.0, result.MedianClaimedRefs);
        Assert.Equal(4, result.LastCell!.ClaimedRefs);
    }

    [Fact]
    public void Audit_IdenticalCandidateGrids_ContestedButNoImpact()
    {
        // Two in-window XCLCs carrying the SAME grid value: contested by count, but the
        // choice cannot change the coordinates -> no differing-grid impact.
        var result = Audit(
            [new AuditCell(0x1, 500)],
            [new AuditGrid(6, 6, 450), new AuditGrid(6, 6, 550)]);

        var cell = Assert.Single(result.Cells);
        Assert.Equal(2, cell.CandidateCount);
        Assert.Equal(1, cell.DistinctCandidateGrids);
        Assert.True(cell.IsContested);
        Assert.False(cell.HasDifferingCandidates);
        Assert.Equal(0, result.DifferingGridContestedCells);
    }
}
