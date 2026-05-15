using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Parsing;

public class PersistentRefRedistributorTests
{
    [Fact]
    public void DeduplicatePlacedRefsToBestCell_PrefersRealCellOverSyntheticCrossWorldCopy()
    {
        var realRef = new PlacedReference
        {
            FormId = 0x00117EAF,
            X = 128f,
            Y = 128f,
            Z = 0f,
            AssignmentSource = "PersistentRedistributed"
        };
        var syntheticRef = realRef with { AssignmentSource = "PersistentRedistributedSynthetic" };

        var cells = new List<CellRecord>
        {
            new()
            {
                FormId = 0x0010B982,
                EditorId = "GomorrahTSW",
                GridX = 0,
                GridY = 0,
                WorldspaceFormId = 0x0010B96F,
                PlacedObjects = [realRef]
            },
            new()
            {
                FormId = 0xFE800260,
                EditorId = "[Virtual 0,0 CampMcCarranWorld]",
                GridX = 0,
                GridY = 0,
                WorldspaceFormId = 0x000ECAC5,
                IsVirtual = true,
                PlacedObjects = [syntheticRef]
            }
        };

        var removed = PersistentRefRedistributor.DeduplicatePlacedRefsToBestCell(cells);

        Assert.Equal(1, removed);
        Assert.Single(cells[0].PlacedObjects);
        Assert.Empty(cells[1].PlacedObjects);
    }

    [Fact]
    public void DeduplicatePlacedRefsToBestCell_PrefersGridCorrectSyntheticWhenNoRealCellMatches()
    {
        var realWrongCellRef = new PlacedReference
        {
            FormId = 0x01020304,
            X = 128f,
            Y = 128f,
            Z = 0f,
            AssignmentSource = "ParentCell"
        };
        var syntheticRef = realWrongCellRef with { AssignmentSource = "PersistentRedistributedSynthetic" };

        var cells = new List<CellRecord>
        {
            new()
            {
                FormId = 0x00000010,
                EditorId = "WrongExteriorCell",
                GridX = 5,
                GridY = 5,
                WorldspaceFormId = 0x00000020,
                PlacedObjects = [realWrongCellRef]
            },
            new()
            {
                FormId = 0xFE800010,
                EditorId = "[Virtual 0,0 SomeWorld]",
                GridX = 0,
                GridY = 0,
                WorldspaceFormId = 0x00000020,
                IsVirtual = true,
                PlacedObjects = [syntheticRef]
            }
        };

        var removed = PersistentRefRedistributor.DeduplicatePlacedRefsToBestCell(cells);

        Assert.Equal(1, removed);
        Assert.Empty(cells[0].PlacedObjects);
        Assert.Single(cells[1].PlacedObjects);
    }
}
