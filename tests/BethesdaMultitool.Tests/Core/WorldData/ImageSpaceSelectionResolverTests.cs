using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class ImageSpaceSelectionResolverTests
{
    [Fact]
    public void ExteriorCellLookup_UsesSharedFloorRuleAtPositiveAndNegativeBoundaries()
    {
        const float cellSize = 4096f;
        var negative = Cell(0x10, -1, 0, imageSpaceId: 0x100);
        var origin = Cell(0x11, 0, 0, imageSpaceId: 0x101);
        var east = Cell(0x12, 1, 0, imageSpaceId: 0x102);
        var cells = Grid(negative, origin, east);

        Assert.Same(negative, Lookup(-0.01f).Cell);
        Assert.Same(origin, Lookup(0f).Cell);
        Assert.Same(origin, Lookup(cellSize - 0.01f).Cell);

        var boundary = Lookup(cellSize);
        Assert.Same(east, boundary.Cell);
        Assert.Equal(1, boundary.GridX);
        Assert.Equal(0, boundary.GridY);
        Assert.Equal("camera-grid", boundary.SourceTelemetry);

        ImageSpaceCellContext Lookup(float x) =>
            ImageSpaceSelectionResolver.ResolveExteriorCell(cells, x, 0f, cellSize);
    }

    [Fact]
    public void Resolve_ExteriorCellXcimWinsOverWorldspaceInam()
    {
        var worldspace = new WorldspaceRecord { FormId = 0x20, ImageSpaceFormId = 0x200 };
        var cell = Cell(0x10, 0, 0, imageSpaceId: 0x100);
        var cellContext = ImageSpaceSelectionResolver.ResolveExteriorCell(Grid(cell), 1f, 1f, 4096f);

        var result = ImageSpaceSelectionResolver.Resolve(
            cellContext, worldspace, [worldspace], interior: false, useClassicDefault: true);

        Assert.Equal(0x100u, result.ImageSpaceFormId);
        Assert.Equal(ImageSpaceSelectionSource.CellXcim, result.Source);
        Assert.Equal(0x10u, result.HistoryCellId);
        Assert.Equal(0x20u, result.HistoryContextId);
        Assert.Null(result.SourceWorldspaceFormId);
    }

    [Fact]
    public void Resolve_CellWithoutXcimUsesDirectWorldspaceInam()
    {
        var worldspace = new WorldspaceRecord { FormId = 0x20, ImageSpaceFormId = 0x200 };
        var cell = Cell(0x10, 0, 0, imageSpaceId: null);
        var cellContext = ImageSpaceSelectionResolver.ResolveExteriorCell(Grid(cell), 1f, 1f, 4096f);

        var result = ImageSpaceSelectionResolver.Resolve(
            cellContext, worldspace, [worldspace], interior: false, useClassicDefault: true);

        Assert.Equal(0x200u, result.ImageSpaceFormId);
        Assert.Equal(ImageSpaceSelectionSource.WorldspaceInam, result.Source);
        Assert.Equal(0x20u, result.ContextWorldspaceFormId);
        Assert.Equal(0x20u, result.SourceWorldspaceFormId);
    }

    [Fact]
    public void Resolve_UseParentImageSpaceWalksParentChainAndReportsSupplyingWorldspace()
    {
        var parent = new WorldspaceRecord { FormId = 0x20, ImageSpaceFormId = 0x200 };
        var child = new WorldspaceRecord
        {
            FormId = 0x21,
            ParentWorldspaceFormId = parent.FormId,
            ParentUseFlags = 1 << 5,
            // Must be ignored because PNAM delegates this field to the parent.
            ImageSpaceFormId = 0x201,
        };

        var result = ImageSpaceSelectionResolver.Resolve(
            default, child, [child, parent], interior: false, useClassicDefault: true);

        Assert.Equal(0x200u, result.ImageSpaceFormId);
        Assert.Equal(ImageSpaceSelectionSource.ParentWorldspaceInam, result.Source);
        Assert.Equal(child.FormId, result.ContextWorldspaceFormId);
        Assert.Equal(parent.FormId, result.SourceWorldspaceFormId);
        Assert.Equal(child.FormId, result.HistoryContextId);
    }

    [Fact]
    public void Resolve_MalformedParentCycleFailsClosedToClassicDefault()
    {
        var first = new WorldspaceRecord
        {
            FormId = 0x20,
            ParentWorldspaceFormId = 0x21,
            ParentUseFlags = 1 << 5,
            ImageSpaceFormId = 0x200,
        };
        var second = new WorldspaceRecord
        {
            FormId = 0x21,
            ParentWorldspaceFormId = 0x20,
            ParentUseFlags = 1 << 5,
            ImageSpaceFormId = 0x201,
        };

        var result = ImageSpaceSelectionResolver.Resolve(
            default, first, [first, second], interior: false, useClassicDefault: true);

        Assert.Equal(ImageSpaceSelectionResolver.DefaultImageSpaceExteriorFormId, result.ImageSpaceFormId);
        Assert.Equal(ImageSpaceSelectionSource.DefaultExterior, result.Source);
        Assert.Null(result.SourceWorldspaceFormId);
    }

    [Fact]
    public void Resolve_ClassicDefaultsRespectBehavesLikeExteriorClassification()
    {
        var interior = ImageSpaceSelectionResolver.Resolve(
            default, contextWorldspace: null, allWorldspaces: null,
            interior: true, useClassicDefault: true);
        var exterior = ImageSpaceSelectionResolver.Resolve(
            default, contextWorldspace: null, allWorldspaces: null,
            interior: false, useClassicDefault: true);

        Assert.Equal(ImageSpaceSelectionResolver.DefaultImageSpaceInteriorFormId, interior.ImageSpaceFormId);
        Assert.Equal(ImageSpaceSelectionSource.DefaultInterior, interior.Source);
        Assert.Equal(ImageSpaceSelectionResolver.DefaultImageSpaceExteriorFormId, exterior.ImageSpaceFormId);
        Assert.Equal(ImageSpaceSelectionSource.DefaultExterior, exterior.Source);
    }

    [Fact]
    public void TonemapHistoryIdentity_ChangesExactlyOnceWhenSequenceCrossesCellBoundaryOnce()
    {
        const float cellSize = 4096f;
        // Deliberately use the same XCIM in adjacent cells: the selected CELL identity itself must
        // invalidate adaptation at the boundary, while movement within either cell remains stable.
        var west = Cell(0x10, 0, 0, imageSpaceId: 0x100);
        var east = Cell(0x11, 1, 0, imageSpaceId: 0x100);
        var cells = Grid(west, east);
        var worldspace = new WorldspaceRecord { FormId = 0x20, ImageSpaceFormId = 0x200 };
        float[] positions = [cellSize - 2f, cellSize - 0.01f, cellSize, cellSize + 2f];

        var keys = positions.Select(position =>
        {
            var cellContext = ImageSpaceSelectionResolver.ResolveExteriorCell(
                cells, position, 0f, cellSize);
            var selection = ImageSpaceSelectionResolver.Resolve(
                cellContext, worldspace, [worldspace], interior: false, useClassicDefault: true);
            return HistoryKey(selection);
        }).ToArray();

        Assert.Equal(keys[0], keys[1]);
        Assert.NotEqual(keys[1], keys[2]);
        Assert.Equal(keys[2], keys[3]);
        Assert.Equal(1, keys.Zip(keys.Skip(1), (left, right) => left != right).Count(changed => changed));
    }

    [Fact]
    public void TonemapHistoryIdentity_IncludesImageSpaceSourceKind()
    {
        var cellSource = TonemapHistoryKeyBuilder.Build(
            BethesdaGame.FalloutNewVegas,
            contextId: 0x20,
            activeCellId: 0x10,
            imageSpaceSource: (uint)ImageSpaceSelectionSource.CellXcim,
            imageSpaceId: 0x100,
            currentWeatherId: 0x300,
            outgoingWeatherId: 0,
            interior: false,
            hdrEnabled: true,
            modifiersEnabled: true);
        var worldSource = TonemapHistoryKeyBuilder.Build(
            BethesdaGame.FalloutNewVegas,
            contextId: 0x20,
            activeCellId: 0x10,
            imageSpaceSource: (uint)ImageSpaceSelectionSource.WorldspaceInam,
            imageSpaceId: 0x100,
            currentWeatherId: 0x300,
            outgoingWeatherId: 0,
            interior: false,
            hdrEnabled: true,
            modifiersEnabled: true);

        Assert.NotEqual(cellSource, worldSource);
    }

    private static ulong HistoryKey(ResolvedImageSpaceSelection selection) =>
        TonemapHistoryKeyBuilder.Build(
            BethesdaGame.FalloutNewVegas,
            selection.HistoryContextId,
            selection.HistoryCellId,
            selection.HistorySourceTag,
            selection.ImageSpaceFormId ?? 0,
            currentWeatherId: 0x300,
            outgoingWeatherId: 0,
            interior: false,
            hdrEnabled: true,
            modifiersEnabled: true);

    private static Dictionary<(int gx, int gy), CellRecord> Grid(params CellRecord[] cells) =>
        cells.ToDictionary(cell => (cell.GridX!.Value, cell.GridY!.Value));

    private static CellRecord Cell(uint formId, int gx, int gy, uint? imageSpaceId) =>
        new()
        {
            FormId = formId,
            GridX = gx,
            GridY = gy,
            ImageSpaceFormId = imageSpaceId,
        };
}
