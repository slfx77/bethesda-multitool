using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Tests for <see cref="WorldspaceMarkerGrouping" /> — the 2D-map marker grouping extracted to Core so
///     the "Use Map Data" parent-inheritance rule is headless-testable. A child worldspace that "Uses Map
///     Data" (WNAM parent + PNAM bit 2 = 0x0004) is drawn on its parent's world map, so its markers must
///     appear on the parent's map (the FNV/FO3 sub-worldspaces hanging off the main wasteland).
/// </summary>
public sealed class WorldspaceMarkerGroupingTests
{
    private const ushort UseMapData = 0x0004;
    private const ushort UseLandData = 0x0001;

    private static PlacedReference Marker(uint formId) => new() { FormId = formId, IsMapMarker = true };

    private static WorldspaceRecord Worldspace(
        uint formId, uint[] markerFormIds, uint? parent = null, ushort? parentFlags = null) => new()
    {
        FormId = formId,
        ParentWorldspaceFormId = parent,
        ParentUseFlags = parentFlags,
        Cells = [new CellRecord { FormId = formId + 0x1000, PlacedObjects = markerFormIds.Select(Marker).ToList() }]
    };

    [Fact]
    public void Child_WithUseMapData_FoldsMarkersIntoParent()
    {
        var parent = Worldspace(1, [0x10]);
        var child = Worldspace(2, [0x20], parent: 1, parentFlags: UseMapData);

        var grouped = WorldspaceMarkerGrouping.GroupByWorldspace([parent, child]);

        // Parent's map now shows BOTH its own marker and the child's.
        Assert.Equal(new uint[] { 0x10, 0x20 }, grouped[1].Select(m => m.FormId).OrderBy(x => x));
        // Child keeps its own entry, unaffected.
        Assert.Equal(new uint[] { 0x20 }, grouped[2].Select(m => m.FormId));
    }

    [Fact]
    public void Child_WithoutUseMapDataBit_IsNotFolded()
    {
        var parent = Worldspace(1, [0x10]);
        var child = Worldspace(2, [0x20], parent: 1, parentFlags: UseLandData); // land only, not map

        var grouped = WorldspaceMarkerGrouping.GroupByWorldspace([parent, child]);

        Assert.Equal(new uint[] { 0x10 }, grouped[1].Select(m => m.FormId));
        Assert.Equal(new uint[] { 0x20 }, grouped[2].Select(m => m.FormId));
    }

    [Fact]
    public void Child_WithUseMapData_CreatesParentEntry_WhenParentHasNoOwnMarkers()
    {
        var parent = Worldspace(1, []); // parent has no markers of its own
        var child = Worldspace(2, [0x20], parent: 1, parentFlags: UseMapData);

        var grouped = WorldspaceMarkerGrouping.GroupByWorldspace([parent, child]);

        Assert.Equal(new uint[] { 0x20 }, grouped[1].Select(m => m.FormId));
    }

    [Fact]
    public void StandaloneWorldspace_KeepsOnlyItsOwnMarkers()
    {
        var ws = Worldspace(1, [0x10, 0x11]);

        var grouped = WorldspaceMarkerGrouping.GroupByWorldspace([ws]);

        Assert.Single(grouped);
        Assert.Equal(2, grouped[1].Count);
    }
}
