using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Slice 8, NiLODNode: the levels form one exclusive group whose finest level (the smallest NiRangeLODData Near
///     Extent, ties to the lowest ordinal) is default-on; the LOD data stays native. Without usable ranges the stored
///     Index is the default and a diagnostic says why.
/// </summary>
public class NifModelLodLayerTests
{
    /// <summary>
    ///     0 NiNode "Root" [1]; 1 NiLODNode "Lod" [2, 3, 4] (stored Index <paramref name="index" />, LOD data 5); 2 "Far";
    ///     3 "Near"; 4 "Mid"; 5 the LOD data <paramref name="addData" /> writes.
    /// </summary>
    private static byte[] Fixture(uint index, Action<NifTestFileBuilder> addData, bool bigEndian = false)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        AddNode(builder, builder.AddString("Root"), [1]);
        var name = builder.AddString("Lod");
        builder.AddBlock("NiLODNode", w => NifTestBlockLayouts.NodeWithTail(w, 34, name, [2, 3, 4],
            tail => NifTestBlockLayouts.LodTail(tail, 3, index, 5)));
        AddNode(builder, builder.AddString("Far"), []);
        AddNode(builder, builder.AddString("Near"), []);
        AddNode(builder, builder.AddString("Mid"), []);
        addData(builder);
        return builder.Build();
    }

    private static Action<NifTestFileBuilder> Ranges(params (float Near, float Far)[] levels)
    {
        return builder => builder.AddBlock("NiRangeLODData",
            w => NifTestBlockLayouts.RangeLodData(w, (0f, 0f, 10f), levels));
    }

    /// <summary>
    ///     "Near" (ordinal 1, Near Extent 0) is the finest level and default-on. Control: the stored Index 0 would turn on
    ///     "Far", and the first level would too.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinestLevel_IsDefaultOn(bool bigEndian)
    {
        var result = Read(Fixture(0, Ranges((100f, 500f), (0f, 100f), (500f, 1000f)), bigEndian));
        var document = result.Document;

        Assert.Equal(["Root", "Lod", "Far", "Near", "Mid"], document.Nodes.Select(n => n.Name));
        Assert.Equal(["lod:1:0", "lod:1:1", "lod:1:2"], document.LayerSets.Select(s => s.Id));
        Assert.Equal([false, true, false], document.LayerSets.Select(s => s.DefaultOn));
        Assert.All(document.LayerSets, set =>
        {
            Assert.Equal("lod:1", set.ExclusiveGroup);
            Assert.Equal(NifModelLayerReader.LodSourceKind, set.SourceKind);
        });
        Assert.Equal([3], document.LayerSets[1].Members);
        Assert.False(document.LayerSets[0].DefaultOn);

        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:1").Kind);
        var data = result.Coverage.GetClassification("block:5");
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, data.Kind);
        Assert.Equal(NifModelCoverage.LodDataReason, data.Reason);
        var facts = BlockPayload(document, 1)["node"]!["lod"]!;
        Assert.Equal(NifModelLayerReader.FinestLevelRule, (string)facts["rule"]!);
        Assert.Equal(3, facts["ranges"]!.AsArray().Count);
        Assert.DoesNotContain(document.Diagnostics, d => d.Code == NifModelLayerReader.LodDefaultDiagnostic);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     The finest level is the smallest Near Extent, not the smallest Far Extent: ordinal 0 (Near 0, Far 500) is on
    ///     although ordinal 1 (Near 10, Far 100) ends first. Control: a smallest-Far rule would turn on ordinal 1.
    /// </summary>
    [Fact]
    public void FinestLevel_IsTheSmallestNearExtent_NotTheSmallestFar()
    {
        var document = Read(Fixture(2, Ranges((0f, 500f), (10f, 100f), (500f, 1000f)))).Document;

        Assert.Equal([true, false, false], document.LayerSets.Select(s => s.DefaultOn));
    }

    /// <summary>Tied Near Extents choose the lowest ordinal. Control: the highest tied ordinal (2) stays off.</summary>
    [Fact]
    public void TiedNearExtents_ChooseTheLowestOrdinal()
    {
        var document = Read(Fixture(0, Ranges((50f, 100f), (0f, 50f), (0f, 60f)))).Document;

        Assert.Equal([false, true, false], document.LayerSets.Select(s => s.DefaultOn));
    }

    /// <summary>
    ///     Screen-proportion LOD data and a range count that differs from the child count establish no finest level: the
    ///     stored Index (2) is default-on and a diagnostic says so. Control: matching ranges pick ordinal 1 and report
    ///     nothing (the theory above).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnusableLodData_FallsBackToTheStoredIndex_AndIsReported(bool screen)
    {
        Action<NifTestFileBuilder> addData = screen
            ? builder => builder.AddBlock("NiScreenLODData",
                w => NifTestBlockLayouts.ScreenLodData(w, [0.5f, 0.25f, 0.1f]))
            : Ranges((0f, 100f), (100f, 200f));

        var document = Read(Fixture(2, addData)).Document;

        Assert.Equal([false, false, true], document.LayerSets.Select(s => s.DefaultOn));
        Assert.Contains(document.Diagnostics, d => d.Code == NifModelLayerReader.LodDefaultDiagnostic);
        SceneValidation.ValidateStructure(document);
    }
}
