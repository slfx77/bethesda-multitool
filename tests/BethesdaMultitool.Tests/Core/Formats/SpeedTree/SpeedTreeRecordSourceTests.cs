using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.SpeedTree;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.SpeedTree;

/// <summary>
///     Regression tests for <see cref="SpeedTreeRecordSource" />. The defect these pin: TREE moved out of
///     <see cref="RecordCollection.GenericRecords" /> into the typed <see cref="RecordCollection.Trees" />
///     list for FNV/FO3, and the renderer's leaf-atlas map still scanned only GenericRecords. It therefore
///     resolved NO <c>TREE.ICON</c> on FNV/FO3 and fell back to the <c>.spt</c>'s dev-era leaf material —
///     for <c>trees\whiteoak01.spt</c> that is <c>TreeWOakLeaves01b.tga</c>, which never shipped (the
///     retail atlas is <c>textures\trees\leaves\whiteoakleaves01.dds</c>), so its leaf cards rendered
///     untextured. Schema-primary games (Oblivion) keep TREE in GenericRecords, so BOTH must be walked.
/// </summary>
public sealed class SpeedTreeRecordSourceTests
{
    /// <summary>FNV's WhiteOak01 TREE as it sits in FalloutNV.esm: MODL is a bare name with a leading
    /// backslash (no <c>Trees\</c> folder) and ICON is a bare atlas file name.</summary>
    private static TreeRecord WhiteOakTyped() => new()
    {
        FormId = 0x0003C356,
        EditorId = "WhiteOak01",
        ModelPath = @"\WhiteOak01.spt",
        IconPath = "WhiteOakLeaves01.dds",
        Seeds = [0x00049961],
        Data = new TreeData
        {
            LeafCurvature = 2.5f,
            BranchDimmingValue = 0.2f,
            LeafDimmingValue = 0.7f,
            ShadowRadius = 128,
            RockSpeed = 1f,
            RustleSpeed = 1f
        },
        BillboardSize = new TreeBillboardSize { Width = 768f, Height = 768f }
    };

    [Fact]
    public void BuildLeafTextureMap_TypedTreeRecord_ResolvesShippedAtlas()
    {
        var records = new RecordCollection { Trees = [WhiteOakTyped()] };

        var map = SpeedTreeRecordSource.BuildLeafTextureMap(records);

        Assert.Equal(@"textures\trees\leaves\whiteoakleaves01.dds", map[@"trees\WhiteOak01.spt"]);
    }

    [Fact]
    public void BuildLeafTextureMap_GenericRecordOnly_StillResolves()
    {
        // Schema-primary games (Oblivion/Skyrim/FO4) leave Trees empty and keep TREE generic.
        var records = new RecordCollection
        {
            GenericRecords =
            [
                new GenericEsmRecord
                {
                    RecordType = "TREE",
                    EditorId = "TreeWillowOak",
                    ModelPath = @"Trees\TreeWillowOakForest01SU.spt",
                    Fields = new Dictionary<string, object?> { ["ICON"] = "TreeWillowOakLeavesSU.dds" }
                }
            ]
        };

        var map = SpeedTreeRecordSource.BuildLeafTextureMap(records);

        Assert.Equal(
            @"textures\trees\leaves\treewillowoakleavessu.dds",
            map[@"Trees\TreeWillowOakForest01SU.spt"]);
    }

    [Fact]
    public void Enumerate_SamePathInBothCollections_YieldsTypedEntryOnce()
    {
        var records = new RecordCollection
        {
            Trees = [WhiteOakTyped()],
            GenericRecords =
            [
                new GenericEsmRecord
                {
                    RecordType = "TREE",
                    EditorId = "StaleGeneric",
                    ModelPath = @"\WhiteOak01.spt",
                    Fields = new Dictionary<string, object?> { ["ICON"] = "WrongLeaves.dds" }
                }
            ]
        };

        var entry = Assert.Single(SpeedTreeRecordSource.Enumerate(records));
        Assert.Equal("WhiteOak01", entry.EditorId);
        Assert.Equal(@"textures\trees\leaves\whiteoakleaves01.dds", entry.LeafTexturePath);
    }

    [Fact]
    public void BuildDimmingMap_TypedTreeRecord_CarriesCnamPhasePair()
    {
        var records = new RecordCollection { Trees = [WhiteOakTyped()] };

        var dimming = SpeedTreeRecordSource.BuildDimmingMap(records)[@"trees\WhiteOak01.spt"];

        Assert.Equal(0.7f, dimming.Leaf);
        Assert.Equal(0.2f, dimming.Branch);
        Assert.Equal(1f, dimming.RockSpeed);
        Assert.Equal(1f, dimming.RustleSpeed);
    }

    [Fact]
    public void Enumerate_NonSpeedTreeModel_Ignored()
    {
        var records = new RecordCollection
        {
            GenericRecords =
            [
                new GenericEsmRecord { RecordType = "MSTT", ModelPath = @"Clutter\Junk\Bucket01.nif" }
            ]
        };

        Assert.Empty(SpeedTreeRecordSource.Enumerate(records));
    }

    [Fact]
    public void Enumerate_TypedTreeRecord_ExposesSeedAndBillboard()
    {
        var records = new RecordCollection { Trees = [WhiteOakTyped()] };

        var entry = Assert.Single(SpeedTreeRecordSource.Enumerate(records));

        Assert.Equal(0x00049961u, entry.Seed);
        Assert.Equal(768f, entry.BillboardWidth);
        Assert.Equal(768f, entry.BillboardHeight);
    }
}
