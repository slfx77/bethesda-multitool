using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using Xunit;

// PlacedObjectCategory (internal) is referenced only via category.ToString() below; no using needed
// beyond the Models namespace which RecordCollection/ObjectBoundsIndex already live in.

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models;

/// <summary>
///     Morrowind (TES3) puts every non-CELL/LAND/LTEX record into GenericRecords with its raw 4-char
///     code, so <see cref="ObjectBoundsIndex.BuildCombined" /> must categorize those codes or placed
///     refs resolve to Unknown and render no map dot/box (Bug 5).
/// </summary>
public class ObjectBoundsIndexTes3Tests
{
    // Expected category passed by name (PlacedObjectCategory is internal, so it can't be an InlineData
    // parameter type on a public test method).
    [Theory]
    [InlineData("STAT", "Static")]
    [InlineData("ACTI", "Activator")]
    [InlineData("DOOR", "Door")]
    [InlineData("CONT", "Container")]
    [InlineData("LIGH", "Light")]
    [InlineData("FURN", "Furniture")]
    [InlineData("WEAP", "Item")]
    [InlineData("ARMO", "Item")]
    [InlineData("ALCH", "Item")]
    [InlineData("INGR", "Item")]
    [InlineData("CLOT", "Item")]
    [InlineData("LEVC", "Creature")]
    public void BuildCombined_CategorizesTes3GenericRecords(string recordType, string expectedCategory)
    {
        var records = new RecordCollection
        {
            IsTes3 = true,
            GenericRecords = [new GenericEsmRecord { FormId = 0x10, RecordType = recordType }]
        };

        var (_, categories) = ObjectBoundsIndex.BuildCombined(records);

        Assert.True(categories.TryGetValue(0x10u, out var category));
        Assert.Equal(expectedCategory, category.ToString());
    }

    [Fact]
    public void BuildCombined_LeavesNonPlaceableTes3TypeUncategorized()
    {
        // A SCPT (script) is not a placeable world object and must not get a placement category.
        // (FormId chosen clear of the hardcoded engine-marker FormIds 0x17/0x20 that BuildCombined seeds.)
        var records = new RecordCollection
        {
            IsTes3 = true,
            GenericRecords = [new GenericEsmRecord { FormId = 0x9001, RecordType = "SCPT" }]
        };

        var (_, categories) = ObjectBoundsIndex.BuildCombined(records);

        Assert.False(categories.ContainsKey(0x9001u));
    }
}