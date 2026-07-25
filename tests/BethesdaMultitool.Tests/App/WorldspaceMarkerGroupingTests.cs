using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Tests for <see cref="WorldspaceMarkerGrouping" /> — the 2D-map marker grouping extracted to Core so
///     the parent-inheritance rules are headless-testable. FO3+: a child worldspace that "Uses Map Data"
///     (WNAM parent + PNAM bit 2 = 0x0004) is drawn on its parent's world map, so its markers must appear
///     on the parent's map (the FNV/FO3 sub-worldspaces hanging off the main wasteland). TES4: WRLD has no
///     PNAM at all — the WNAM link is the whole contract and children share the parent coordinate space,
///     so markers fold up the entire chain with an identity transform.
/// </summary>
public sealed class WorldspaceMarkerGroupingTests
{
    private const ushort UseMapData = 0x0004;
    private const ushort UseLandData = 0x0001;

    private static PlacedReference Marker(uint formId, float x = 0, float y = 0, float z = 0)
    {
        return new PlacedReference
        {
            FormId = formId,
            IsMapMarker = true,
            X = x,
            Y = y,
            Z = z
        };
    }

    private static WorldspaceRecord Worldspace(
        uint formId, uint[] markerFormIds, uint? parent = null, ushort? parentFlags = null)
    {
        return new WorldspaceRecord
        {
            FormId = formId,
            ParentWorldspaceFormId = parent,
            ParentUseFlags = parentFlags,
            Cells =
            [
                new CellRecord
                    { FormId = formId + 0x1000, PlacedObjects = markerFormIds.Select(id => Marker(id)).ToList() }
            ]
        };
    }

    [Fact]
    public void Child_WithUseMapData_FoldsMarkersIntoParent()
    {
        var parent = Worldspace(1, [0x10]);
        var child = Worldspace(2, [0x20], 1, UseMapData);

        var grouped = WorldspaceMarkerGrouping.GroupByWorldspace([parent, child], BethesdaGame.FalloutNewVegas);

        // Parent's map now shows BOTH its own marker and the child's.
        Assert.Equal(new uint[] { 0x10, 0x20 }, grouped[1].Select(m => m.FormId).OrderBy(x => x));
        // Child keeps its own entry, unaffected.
        Assert.Equal(new uint[] { 0x20 }, grouped[2].Select(m => m.FormId));
    }

    [Fact]
    public void Child_WithoutUseMapDataBit_IsNotFolded()
    {
        var parent = Worldspace(1, [0x10]);
        var child = Worldspace(2, [0x20], 1, UseLandData); // land only, not map

        var grouped = WorldspaceMarkerGrouping.GroupByWorldspace([parent, child], BethesdaGame.FalloutNewVegas);

        Assert.Equal(new uint[] { 0x10 }, grouped[1].Select(m => m.FormId));
        Assert.Equal(new uint[] { 0x20 }, grouped[2].Select(m => m.FormId));
    }

    [Fact]
    public void Child_WithUseMapData_CreatesParentEntry_WhenParentHasNoOwnMarkers()
    {
        var parent = Worldspace(1, []); // parent has no markers of its own
        var child = Worldspace(2, [0x20], 1, UseMapData);

        var grouped = WorldspaceMarkerGrouping.GroupByWorldspace([parent, child], BethesdaGame.FalloutNewVegas);

        Assert.Equal(new uint[] { 0x20 }, grouped[1].Select(m => m.FormId));
    }

    [Fact]
    public void StandaloneWorldspace_KeepsOnlyItsOwnMarkers()
    {
        var ws = Worldspace(1, [0x10, 0x11]);

        var grouped = WorldspaceMarkerGrouping.GroupByWorldspace([ws], BethesdaGame.FalloutNewVegas);

        Assert.Single(grouped);
        Assert.Equal(2, grouped[1].Count);
    }

    [Fact]
    public void Child_WithOnam_TransformsCopyIntoParent_ScaleThenOffset()
    {
        var marker = Marker(0x20, 3000, 6000, 400);
        var parent = Worldspace(1, [0x10]);
        var child = Worldspace(2, [], 1, UseMapData) with
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

        var grouped = WorldspaceMarkerGrouping.GroupByWorldspace([parent, child], BethesdaGame.FalloutNewVegas);

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
        var marker = Marker(0x20, 100, 200, 300);

        var transformed = WorldspaceMarkerGrouping.TransformMarkerToParentMap(child, marker);

        Assert.Equal(105, transformed.X);
        Assert.Equal(193, transformed.Y);
        Assert.Equal(300, transformed.Z);
    }

    [Fact]
    public void TransformMarkerToParentMap_AbsentOnamUsesEngineIdentityDefaults()
    {
        var marker = Marker(0x20, 100, 200, 300);

        var transformed = WorldspaceMarkerGrouping.TransformMarkerToParentMap(new WorldspaceRecord(), marker);

        Assert.NotSame(marker, transformed);
        Assert.Equal(marker, transformed);
    }

    [Fact]
    public void Tes4Child_WithWnamParentAndNoPnam_FoldsIdentityCopyIntoParent()
    {
        var parent = Worldspace(1, [0x10]);
        var child = Worldspace(2, [0x20], 1); // TES4: no PNAM flags at all

        var grouped = WorldspaceMarkerGrouping.GroupByWorldspace([parent, child], BethesdaGame.Oblivion);

        Assert.Equal(new uint[] { 0x10, 0x20 }, grouped[1].Select(m => m.FormId).OrderBy(x => x));
        var folded = Assert.Single(grouped[1], m => m.FormId == 0x20);
        // Children share the parent coordinate space — identity transform.
        Assert.Equal(0, folded.X);
        Assert.Equal(0, folded.Y);
        Assert.Equal(new uint[] { 0x20 }, grouped[2].Select(m => m.FormId));
    }

    [Fact]
    public void Tes4Grandchild_FoldsOwnMarkersUpTheFullWnamChain_ExactlyOnce()
    {
        var root = Worldspace(1, [0x10]);
        var mid = Worldspace(2, [0x20], 1);
        var grandchild = Worldspace(3, [0x30], 2);

        var grouped = WorldspaceMarkerGrouping.GroupByWorldspace(
            [root, mid, grandchild], BethesdaGame.Oblivion);

        // Root receives mid's AND the grandchild's own markers, each once.
        Assert.Equal(new uint[] { 0x10, 0x20, 0x30 }, grouped[1].Select(m => m.FormId).OrderBy(x => x));
        // Mid receives only the grandchild's (its own copy never re-folds).
        Assert.Equal(new uint[] { 0x20, 0x30 }, grouped[2].Select(m => m.FormId).OrderBy(x => x));
        Assert.Equal(new uint[] { 0x30 }, grouped[3].Select(m => m.FormId));
    }

    [Fact]
    public void Tes4_WnamCycle_TerminatesWithoutDuplicates()
    {
        // Malformed A↔B cycle: each must receive the other's markers exactly once and terminate.
        var a = Worldspace(1, [0x10], 2);
        var b = Worldspace(2, [0x20], 1);

        var grouped = WorldspaceMarkerGrouping.GroupByWorldspace([a, b], BethesdaGame.Oblivion);

        Assert.Equal(new uint[] { 0x10, 0x20 }, grouped[1].Select(m => m.FormId).OrderBy(x => x));
        Assert.Equal(new uint[] { 0x10, 0x20 }, grouped[2].Select(m => m.FormId).OrderBy(x => x));
    }

    [Fact]
    public void Fo3Family_ChildWithoutPnam_IsStillNotFolded()
    {
        // The TES4 WNAM-only fold must not leak into FO3+: absent PNAM means no fold there.
        var parent = Worldspace(1, [0x10]);
        var child = Worldspace(2, [0x20], 1);

        var grouped = WorldspaceMarkerGrouping.GroupByWorldspace([parent, child], BethesdaGame.Fallout3);

        Assert.Equal(new uint[] { 0x10 }, grouped[1].Select(m => m.FormId));
    }

    [Fact]
    public void Tes4_FoldStillAppliesOnamWhenAuthored()
    {
        // Defensive: a TES4-era record carrying ONAM-style values still routes through the shared
        // transform (identity is just the no-ONAM degenerate case).
        var parent = Worldspace(1, []);
        var child = Worldspace(2, [], 1) with
        {
            MapOffsetScaleX = 1f,
            MapOffsetScaleY = 50,
            MapOffsetZ = -25,
            Cells = [new CellRecord { FormId = 0x1002, PlacedObjects = [Marker(0x20, 100, 200)] }]
        };

        var grouped = WorldspaceMarkerGrouping.GroupByWorldspace([parent, child], BethesdaGame.Oblivion);

        var folded = Assert.Single(grouped[1]);
        Assert.Equal(150, folded.X);
        Assert.Equal(175, folded.Y);
    }
}