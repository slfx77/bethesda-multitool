using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>
///     Pins how synthesized classic records are grouped for the Data Explorer tree.
/// </summary>
public sealed class RecordBrowserModelTests
{
    private static GenericEsmRecord Record(string type, uint formId, string? editorId = null, string? fullName = null)
    {
        return new GenericEsmRecord { RecordType = type, FormId = formId, EditorId = editorId, FullName = fullName };
    }

    private static RecordCollection Collection(params GenericEsmRecord[] records)
    {
        return new RecordCollection { GenericRecords = [.. records] };
    }

    [Fact]
    public void Build_GroupsByRecordTypeAndSortsTypesAlphabetically()
    {
        var groups = RecordBrowserModel.Build(Collection(
            Record("DLOC", 1, "b"), Record("ATPL", 2, "a"), Record("DLOC", 3, "c")));

        Assert.Equal(["ATPL", "DLOC"], groups.Select(g => g.RecordType));
        Assert.Equal(2, groups[1].Records.Count);
    }

    [Fact]
    public void Build_SortsRecordsByTheNameAUserWouldLookFor()
    {
        // ⚠ NOT by FormID: a synthesized id is a ClassicFormIdScheme hash of the source's identity,
        // so its numeric order means nothing to a reader.
        var groups = RecordBrowserModel.Build(Collection(
            Record("DLOC", 900, "Zebra"), Record("DLOC", 100, "Apple")));

        Assert.Equal(["Apple", "Zebra"], groups[0].Records.Select(r => r.EditorId));
    }

    [Fact]
    public void DisplayName_CarriesTheCount()
    {
        var groups = RecordBrowserModel.Build(Collection(Record("DREG", 1), Record("DREG", 2)));

        Assert.Equal("DREG (2)", groups[0].DisplayName);
    }

    [Fact]
    public void DescribeRecord_PrefersEditorIdThenFullNameThenFormId()
    {
        Assert.Equal("EDID", RecordBrowserModel.DescribeRecord(Record("A", 1, "EDID", "Full")));
        Assert.Equal("Full", RecordBrowserModel.DescribeRecord(Record("A", 1, null, "Full")));
        Assert.Equal("0x0000002A", RecordBrowserModel.DescribeRecord(Record("A", 42)));
    }

    [Fact]
    public void DescribeRecord_TreatsWhitespaceNamesAsAbsent()
    {
        // Several classic sources carry padded fixed-width names that decode to blanks.
        Assert.Equal("0x00000007", RecordBrowserModel.DescribeRecord(Record("A", 7, "   ", "  ")));
    }

    [Fact]
    public void RecordBrowserItem_DisplayName_IsTheLabelAPersonLooksFor()
    {
        // The Data Explorer's one tree template binds DisplayName on group and record nodes alike.
        Assert.Equal("EDID", new RecordBrowserItem(Record("A", 1, "EDID", "Full")).DisplayName);
        Assert.Equal("Full", new RecordBrowserItem(Record("A", 1, null, "Full")).DisplayName);
        Assert.Equal("0x0000002A", new RecordBrowserItem(Record("A", 42)).DisplayName);
    }

    [Fact]
    public void Build_WithNoRecords_ReturnsNoGroups()
    {
        Assert.Empty(RecordBrowserModel.Build(new RecordCollection()));
    }

    [Fact]
    public void Build_GroupsRecordTypesCaseInsensitively()
    {
        var groups = RecordBrowserModel.Build(Collection(Record("dloc", 1, "a"), Record("DLOC", 2, "b")));

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Records.Count);
    }
}