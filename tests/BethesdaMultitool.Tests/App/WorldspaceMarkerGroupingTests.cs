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

    private static PlacedReference Marker(uint formId, float x = 0, float y = 0, float z = 0) => new()
    {
        FormId = formId,
        IsMapMarker = true,
        X = x,
        Y = y,
        Z = z
    };

    private static WorldspaceRecord Worldspace(
        uint formId, uint[] markerFormIds, uint? parent = null, ushort? parentFlags = null) => new()
    {
        FormId = formId,
        ParentWorldspaceFormId = parent,
        ParentUseFlags = parentFlags,
        Cells = [new CellRecord { FormId = formId + 0x1000, PlacedObjects = markerFormIds.Select(id => Marker(id)).ToList() }]
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

    [Fact]
    public void Child_WithOnam_TransformsCopyIntoParent_ScaleThenOffset()
    {
        var marker = Marker(0x20, x: 3000, y: 6000, z: 400);
        var parent = Worldspace(1, [0x10]);
        var child = Worldspace(2, [], parent: 1, parentFlags: UseMapData) with
        {
            // ONAM storage order: map scale, X offset, Y offset.
            MapOffsetScaleX = 0.5f,
            MapOffsetScaleY = 100,
            MapOffsetZ = -200,
            BoundsMinX = -1000,
            BoundsMinY = -2000,
            BoundsMaxX = 3000,
            BoundsMaxY = 6000,
            Cells = [new CellRecord { FormId = 0x1002, PlacedObjects = [marker] }]
        };

        var grouped = WorldspaceMarkerGrouping.GroupByWorldspace([parent, child]);

        var parentCopy = Assert.Single(grouped[1], item => item.FormId == marker.FormId);
        Assert.NotSame(marker, parentCopy);
        Assert.Equal(2100, parentCopy.X); // center 1000; scale to 2000; add 100
        Assert.Equal(3800, parentCopy.Y); // center 2000; scale to 4000; add -200
        Assert.Equal(200, parentCopy.Z); // engine scales the full point about center Z=0

        var childOriginal = Assert.Single(grouped[2]);
        Assert.Same(marker, childOriginal);
        Assert.Equal(3000, childOriginal.X);
        Assert.Equal(6000, childOriginal.Y);
        Assert.Equal(400, childOriginal.Z);
    }

    [Fact]
    public void TransformMarkerToParentMap_ScaleZeroSkipsScaling_ButAppliesOffsets()
    {
        var child = new WorldspaceRecord
        {
            MapOffsetScaleX = 0,
            MapOffsetScaleY = 5,
            MapOffsetZ = -7,
            BoundsMinX = -1000,
            BoundsMinY = -2000,
            BoundsMaxX = 3000,
            BoundsMaxY = 6000
        };
        var marker = Marker(0x20, x: 100, y: 200, z: 300);

        var transformed = WorldspaceMarkerGrouping.TransformMarkerToParentMap(child, marker);

        Assert.Equal(105, transformed.X);
        Assert.Equal(193, transformed.Y);
        Assert.Equal(300, transformed.Z);
    }

    [Fact]
    public void TransformMarkerToParentMap_AbsentOnamUsesEngineIdentityDefaults()
    {
        var marker = Marker(0x20, x: 100, y: 200, z: 300);

        var transformed = WorldspaceMarkerGrouping.TransformMarkerToParentMap(new WorldspaceRecord(), marker);

        Assert.NotSame(marker, transformed);
        Assert.Equal(marker, transformed);
    }
}
