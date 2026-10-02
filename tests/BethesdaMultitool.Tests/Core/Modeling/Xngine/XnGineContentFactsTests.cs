using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Xngine;

/// <summary>
///     The content step's measurements (cut-1c plan section 6.2, step 4) on records written by the independent
///     <see cref="XnGineTestMeshBuilder" />: which plane-header size walks, the header words +16/+20/+44, the v2.5
///     tripled point offsets, non-mesh tags, and the Incomplete outcome that only a truncated prefix may produce.
/// </summary>
public class XnGineContentFactsTests
{
    [Fact]
    public void EightByteRecord_WalksOnlyWithDaggerfallHeaders_AndReadsTheHeaderWords()
    {
        var facts = XnGineContentFacts.Measure(XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 5510));

        Assert.Equal("v2.7", facts.Tag);
        Assert.True(facts.IsMeshTag);
        Assert.Equal(4, facts.PointCount);
        Assert.Equal(1, facts.PlaneCount);
        Assert.Equal(0, facts.HeaderPlus16);
        Assert.Equal(5510, facts.HeaderPlus20);
        Assert.Equal(0, facts.HeaderPlus44);
        Assert.Equal(XnGineLayoutWalk.Walks, facts.DaggerfallWalk);
        Assert.Equal(XnGineLayoutWalk.Fails, facts.BattlespireWalk);
        Assert.True(facts.WalksWithDaggerfallLayout);
        Assert.False(facts.WalksWithBattlespireLayout);
        Assert.False(facts.WalksWithBothLayouts);
        Assert.False(facts.WalksWithNeitherLayout);
        Assert.False(facts.IsInconclusive);
        Assert.True(facts.WalksWith(XnGineMeshLayout.Daggerfall));
        Assert.False(facts.WalksWith(XnGineMeshLayout.Battlespire));
    }

    [Fact]
    public void TenByteRecord_WalksOnlyWithBattlespireHeaders()
    {
        var facts = XnGineContentFacts.Measure(XnGineTestMeshBuilder.TenByteRecord());

        Assert.Equal(XnGineLayoutWalk.Fails, facts.DaggerfallWalk);
        Assert.Equal(XnGineLayoutWalk.Walks, facts.BattlespireWalk);
        Assert.True(facts.WalksWith(XnGineMeshLayout.Battlespire));
        Assert.Equal(0, facts.HeaderPlus20);
    }

    /// <summary>The shape of the 20 ARCH3D records that walk both ways: one degenerate plane and two spare zero bytes.</summary>
    [Fact]
    public void DegenerateRecordWithTwoSpareBytes_WalksBothWays()
    {
        var facts = XnGineContentFacts.Measure(XnGineTestMeshBuilder.BothWaysRecord());

        Assert.True(facts.WalksWithBothLayouts);
        Assert.Equal(XnGineLayoutWalk.Walks, facts.DaggerfallWalk);
        Assert.Equal(XnGineLayoutWalk.Walks, facts.BattlespireWalk);

        // Control: without the two spare bytes the 10-byte reading runs past the end and only the 8-byte walk holds.
        var builder = new XnGineTestMeshBuilder("v2.7", 8);
        builder.AddPoint(0, 0, 0);
        builder.AddPoint(256, 0, 0);
        var tight = XnGineContentFacts.Measure(builder.AddDegeneratePlane(0).Build());
        Assert.True(tight.WalksWithDaggerfallLayout);
        Assert.Equal(XnGineLayoutWalk.Fails, tight.BattlespireWalk);
    }

    /// <summary>A v2.5 record stores point offsets divided by three; read as v2.7 the same corners address no point.</summary>
    [Fact]
    public void V25_TripledPointOffsets_WalkOnlyUnderTheV25Rule()
    {
        var v25 = XnGineTestMeshBuilder.EightByteRecord(tag: "v2.5");
        Assert.True(XnGineContentFacts.Measure(v25).WalksWithDaggerfallLayout);

        // Control: the identical bytes retagged v2.7 read the stored offsets literally (4, 8, 12 are not point offsets).
        var retagged = (byte[])v25.Clone();
        "v2.7"u8.CopyTo(retagged);
        var facts = XnGineContentFacts.Measure(retagged);
        Assert.Equal("v2.7", facts.Tag);
        Assert.Equal(XnGineLayoutWalk.Fails, facts.DaggerfallWalk);
        Assert.Equal(XnGineLayoutWalk.Fails, facts.BattlespireWalk);
    }

    [Theory]
    [InlineData("v4.0")]
    [InlineData("v5.0")]
    [InlineData("MZ\0\0")]
    public void NonMeshTag_FailsBothWalks(string tag)
    {
        var facts = XnGineContentFacts.Measure(XnGineTestMeshBuilder.EightByteRecord(tag: tag));

        Assert.False(facts.IsMeshTag);
        Assert.Equal(tag.TrimEnd('\0'), facts.Tag);
        Assert.True(facts.WalksWithNeitherLayout);
        Assert.False(facts.IsInconclusive);
    }

    [Fact]
    public void HeaderWords_16And44_AreReadAsStored()
    {
        var builder = new XnGineTestMeshBuilder { HeaderPlus16 = 38, HeaderPlus20 = 64, HeaderPlus44 = 1 };
        builder.AddPoint(0, 0, 0);
        builder.AddPoint(256, 0, 0);
        builder.AddPoint(0, 256, 0);
        builder.AddPlane(0, [(0, 0, 0), (1, 0, 0), (2, 0, 0)]);

        var facts = XnGineContentFacts.Measure(builder.Build());

        Assert.Equal(38, facts.HeaderPlus16);
        Assert.Equal(64, facts.HeaderPlus20);
        Assert.Equal(1, facts.HeaderPlus44);
    }

    [Fact]
    public void ShorterThanTheHeader_FailsWhenComplete_AndIsIncompleteOtherwise()
    {
        var head = XnGineTestMeshBuilder.EightByteRecord().AsSpan(0, 40).ToArray();

        var complete = XnGineContentFacts.Measure(head);
        Assert.Equal("v2.7", complete.Tag);
        Assert.Equal(XnGineLayoutWalk.Fails, complete.DaggerfallWalk);
        Assert.Equal(XnGineLayoutWalk.Fails, complete.BattlespireWalk);
        Assert.False(complete.IsInconclusive);

        var prefix = XnGineContentFacts.Measure(head, isComplete: false);
        Assert.Equal(XnGineLayoutWalk.Incomplete, prefix.DaggerfallWalk);
        Assert.True(prefix.IsInconclusive);
        Assert.True(prefix.WalksWithNeitherLayout);
    }

    [Fact]
    public void TruncatedInsideThePlaneList_IsIncompleteOnlyForAnIncompletePrefix()
    {
        var record = XnGineTestMeshBuilder.EightByteRecord();
        var cut = record.AsSpan(0, record.Length - 5).ToArray();

        var prefix = XnGineContentFacts.Measure(cut, isComplete: false);
        Assert.Equal(XnGineLayoutWalk.Incomplete, prefix.DaggerfallWalk);
        Assert.True(prefix.IsInconclusive);

        // Control: the same bytes declared complete fail outright, never Incomplete.
        var complete = XnGineContentFacts.Measure(cut);
        Assert.Equal(XnGineLayoutWalk.Fails, complete.DaggerfallWalk);
        Assert.False(complete.IsInconclusive);
    }

    /// <summary>A corner that addresses no point fails even on an incomplete prefix: Incomplete is only for running past the end.</summary>
    [Fact]
    public void CornerAddressingNoPoint_FailsEvenOnAnIncompletePrefix()
    {
        var record = XnGineTestMeshBuilder.EightByteRecord();
        var pointListOffset = BitConverter.ToInt32(record, 48);
        var planeListOffset = BitConverter.ToInt32(record, 60);
        Assert.Equal(64, pointListOffset);

        // Corner 0's stored byte offset (at plane header + 8 bytes) becomes 4 * 12, past the 4 points.
        BitConverter.GetBytes(4 * 12).CopyTo(record, planeListOffset + 8);

        Assert.Equal(XnGineLayoutWalk.Fails, XnGineContentFacts.Measure(record, isComplete: false).DaggerfallWalk);
        Assert.Equal(XnGineLayoutWalk.Fails, XnGineContentFacts.Measure(record).DaggerfallWalk);
    }

    [Fact]
    public void PlaneHeaderLengthOf_IsEightOrTen()
    {
        Assert.Equal(8, XnGineContentFacts.PlaneHeaderLengthOf(XnGineMeshLayout.Daggerfall));
        Assert.Equal(10, XnGineContentFacts.PlaneHeaderLengthOf(XnGineMeshLayout.Battlespire));
        Assert.Equal(new[] { "v2.5", "v2.6", "v2.7" }, XnGineContentFacts.MeshTags);
    }
}
