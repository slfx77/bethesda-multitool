using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models;

/// <summary>
///     Locks the tree categorization behind the viewer's Trees visibility toggle
///     (<see cref="PlacedObjectCategory.Tree" />): TREE records are the engine-authoritative identity
///     (Oblivion/FO3/FNV .spt MODLs are bare names with no folder segment, so path logic can never
///     find them), and Skyrim/FO4-style STAT trees under <c>landscape\trees\</c> are caught by
///     whole-segment path matching (first-segment matching filed them as Landscape).
/// </summary>
public sealed class ObjectBoundsIndexCategoryTests
{
    private static Dictionary<uint, PlacedObjectCategory> Categorize(RecordCollection records) =>
        ObjectBoundsIndex.BuildCombined(records).Categories;

    [Fact]
    public void TreeRecord_WithBareSptModl_CategorizesAsTree()
    {
        // FNV WastelandShrub01: TREE record, MODL = "\WastelandShrub01.spt" (no folder segment).
        var records = new RecordCollection
        {
            GenericRecords =
            [
                new GenericEsmRecord
                {
                    FormId = 0x1234, RecordType = "TREE", ModelPath = "\\WastelandShrub01.spt",
                },
            ],
        };
        Assert.Equal(PlacedObjectCategory.Tree, Categorize(records)[0x1234]);
    }

    [Fact]
    public void Static_UnderLandscapeTrees_CategorizesAsTree()
    {
        // FO4/Skyrim NIF trees are STATs under landscape\trees\ — first-segment folder matching
        // used to file these as Landscape, putting them out of reach of a Trees toggle.
        var records = new RecordCollection
        {
            Statics =
            [
                new StaticRecord { FormId = 0x2345, ModelPath = "meshes\\landscape\\trees\\treepineforest01.nif" },
            ],
        };
        Assert.Equal(PlacedObjectCategory.Tree, Categorize(records)[0x2345]);
    }

    [Fact]
    public void GetStaticCategoryFromModelPath_TreeSegmentRules()
    {
        // (PlacedObjectCategory is internal, so a [Theory] can't carry it in InlineData.)
        (string ModelPath, PlacedObjectCategory Expected)[] cases =
        [
            ("trees\\treejoshua01.spt", PlacedObjectCategory.Tree),
            ("meshes/landscape/trees/treeblasted01.nif", PlacedObjectCategory.Tree),
            ("architecture\\treehouse.nif", PlacedObjectCategory.Architecture), // whole-segment only
            ("plants\\shrub.nif", PlacedObjectCategory.Plants), // other vegetation stays Plants
            ("landscape\\rock01.nif", PlacedObjectCategory.Landscape),
        ];
        foreach (var (modelPath, expected) in cases)
        {
            Assert.Equal(expected, ObjectBoundsIndex.GetStaticCategoryFromModelPath(modelPath));
        }
    }

    [Fact]
    public void GetStaticCategoryFromModelPath_BareFileName_StaysUncategorized()
    {
        // No folder segment → null (TREE identity must come from the record-type arm, not the path).
        Assert.Null(ObjectBoundsIndex.GetStaticCategoryFromModelPath("\\WastelandShrub01.spt"));
        Assert.Null(ObjectBoundsIndex.GetStaticCategoryFromModelPath("WastelandShrub01.spt"));
    }
}
