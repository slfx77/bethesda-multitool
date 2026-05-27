using FalloutXbox360Utils.CLI.Commands.Dmp;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.CLI.Commands.Dmp;

public class DmpCellInventoryCommandTests
{
    [Fact]
    public void BuildMatchedCells_ReassignsUnresolvedPlacementToStrongExactGridCell()
    {
        var targetCell = new CellRecord
        {
            FormId = 0x1000,
            GridX = 0,
            GridY = 1,
            WorldspaceFormId = 0x2000,
            PlacedObjects = Enumerable.Range(0, 10)
                .Select(i => Placement(0x3000u + (uint)i, 0x9000, 512, 5000))
                .ToList()
        };
        var unresolved = new CellRecord
        {
            FormId = 0xFE100001,
            EditorId = "[Unresolved Unknown]",
            IsVirtual = true,
            IsUnresolvedBucket = true,
            PlacedObjects =
            [
                Placement(0x4000, 0x9000, 1024, 6000),
                Placement(0x4001, 0x9000, 2048, 7000)
            ]
        };

        var matches = DmpCellInventoryCommand.BuildMatchedCellsForTesting(
            [targetCell, unresolved],
            new Dictionary<uint, string> { [0x9000] = "STAT" });

        var match = Assert.Single(matches);
        Assert.Equal(0x1000u, match.Cell.FormId);
        Assert.Equal(12, match.Hits.Count);
    }

    [Fact]
    public void BuildMatchedCells_CreatesBoundedVirtualCellForUniqueWorldspaceMatch()
    {
        var left = new CellRecord
        {
            FormId = 0x1000,
            GridX = 0,
            GridY = -2,
            WorldspaceFormId = 0x0010B96F
        };
        var right = new CellRecord
        {
            FormId = 0x1001,
            GridX = 2,
            GridY = -2,
            WorldspaceFormId = 0x0010B96F
        };
        var unresolved = new CellRecord
        {
            FormId = 0xFE100001,
            EditorId = "[Unresolved Unknown]",
            IsVirtual = true,
            IsUnresolvedBucket = true,
            PlacedObjects =
            [
                Placement(0x4000, 0x9000, 5000, -7000),
                Placement(0x4001, 0x9000, 6000, -7500)
            ]
        };

        var matches = DmpCellInventoryCommand.BuildMatchedCellsForTesting(
            [left, right, unresolved],
            new Dictionary<uint, string> { [0x9000] = "STAT" });

        var match = Assert.Single(matches);
        Assert.True(match.Cell.IsVirtual);
        Assert.False(match.Cell.IsUnresolvedBucket);
        Assert.Equal(0x0010B96Fu, match.Cell.WorldspaceFormId);
        Assert.Equal(1, match.Cell.GridX);
        Assert.Equal(-2, match.Cell.GridY);
        Assert.Equal(2, match.Hits.Count);
    }

    [Fact]
    public void BuildMatchedCells_PrefersCapturedWorldspaceBoundsForOverlappingAuthorityRanges()
    {
        var stripA = new CellRecord
        {
            FormId = 0x1000,
            GridX = 0,
            GridY = 0,
            WorldspaceFormId = 0x0010B96F,
            PlacedObjects = [Placement(0x3000, 0x9000, 100, 100)]
        };
        var stripB = new CellRecord
        {
            FormId = 0x1001,
            GridX = 2,
            GridY = 2,
            WorldspaceFormId = 0x0010B96F,
            PlacedObjects = [Placement(0x3001, 0x9000, 9000, 9000)]
        };
        var overlappingWastelandA = new CellRecord
        {
            FormId = 0x2000,
            GridX = 0,
            GridY = 0,
            WorldspaceFormId = 0x000DA726
        };
        var overlappingWastelandB = new CellRecord
        {
            FormId = 0x2001,
            GridX = 2,
            GridY = 2,
            WorldspaceFormId = 0x000DA726
        };
        var unresolved = new CellRecord
        {
            FormId = 0xFE100001,
            EditorId = "[Unresolved Unknown]",
            IsVirtual = true,
            IsUnresolvedBucket = true,
            PlacedObjects = [Placement(0x4000, 0x9000, 5000, 5000)]
        };

        var matches = DmpCellInventoryCommand.BuildMatchedCellsForTesting(
            [stripA, stripB, overlappingWastelandA, overlappingWastelandB, unresolved],
            new Dictionary<uint, string> { [0x9000] = "STAT" });

        var virtualMatch = Assert.Single(matches, match => match.Cell.IsVirtual);
        Assert.False(virtualMatch.Cell.IsUnresolvedBucket);
        Assert.Equal(0x0010B96Fu, virtualMatch.Cell.WorldspaceFormId);
        Assert.Equal(1, virtualMatch.Cell.GridX);
        Assert.Equal(1, virtualMatch.Cell.GridY);
    }

    [Fact]
    public void BuildMatchedCells_SplitsUnresolvedBucketByGridWhenInteriorsMakeBoundsUnsafe()
    {
        var interior = new CellRecord
        {
            FormId = 0x2000,
            Flags = 0x01,
            PlacedObjects = [Placement(0x3000, 0x9000, 100, 100)]
        };
        var exteriorAnchor = new CellRecord
        {
            FormId = 0x1000,
            GridX = 0,
            GridY = 0,
            WorldspaceFormId = 0x000DA726
        };
        var unresolved = new CellRecord
        {
            FormId = 0xFE100001,
            EditorId = "[Unresolved Unknown]",
            IsVirtual = true,
            IsUnresolvedBucket = true,
            PlacedObjects =
            [
                Placement(0x4000, 0x9000, 1000, 1000),
                Placement(0x4001, 0x9000, 6000, 1000)
            ]
        };

        var matches = DmpCellInventoryCommand.BuildMatchedCellsForTesting(
            [interior, exteriorAnchor, unresolved],
            new Dictionary<uint, string> { [0x9000] = "STAT" });

        var unresolvedMatches = matches
            .Where(match => match.Cell.IsUnresolvedBucket)
            .OrderBy(match => match.Cell.GridX)
            .ToArray();
        Assert.Equal(2, unresolvedMatches.Length);
        Assert.All(unresolvedMatches, match => Assert.NotEqual(0xFE100001u, match.Cell.FormId));
        Assert.Equal(new int?[] { 0, 1 }, unresolvedMatches.Select(match => match.Cell.GridX).ToArray());
        Assert.Equal(new int?[] { 0, 0 }, unresolvedMatches.Select(match => match.Cell.GridY).ToArray());
    }

    [Fact]
    public void BuildMatchedCells_UsesReferenceParentAuthorityForUnresolvedPlacements()
    {
        var unresolved = new CellRecord
        {
            FormId = 0xFE100001,
            EditorId = "[Unresolved Unknown]",
            IsVirtual = true,
            IsUnresolvedBucket = true,
            PlacedObjects = [Placement(0x4000, 0x9000, 100, 100)]
        };

        var matches = DmpCellInventoryCommand.BuildMatchedCellsForTesting(
            [unresolved],
            new Dictionary<uint, string> { [0x9000] = "STAT" },
            new Dictionary<uint, uint>(),
            new Dictionary<uint, uint> { [0x4000] = 0x2000 },
            new Dictionary<uint, CellAuthorityMetadata>
            {
                [0x2000] = new()
                {
                    IsInterior = true,
                    EditorId = "ResolvedInterior",
                    FullName = "Resolved Interior"
                }
            });

        var match = Assert.Single(matches);
        Assert.Equal(0x2000u, match.Cell.FormId);
        Assert.True(match.Cell.IsInterior);
        Assert.Equal("ResolvedInterior", match.Cell.EditorId);
        Assert.Equal("Resolved Interior", match.Cell.FullName);
        Assert.Equal(0x4000u, Assert.Single(match.Hits).FormId);
    }

    private static PlacedReference Placement(uint formId, uint baseFormId, float x, float y)
    {
        return new PlacedReference
        {
            FormId = formId,
            BaseFormId = baseFormId,
            RecordType = "REFR",
            X = x,
            Y = y,
            Z = 0
        };
    }
}
