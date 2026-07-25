using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     TES4 worldspace cell classification. Unlike later games, Oblivion's persistent dummy CELL can
///     carry XCLC (0,0) (SETheFringe) — treating it as an ordinary exterior cell collides with the
///     real (0,0) cell in the spatial index and evicts it — and a real exterior cell can omit XCLC
///     entirely (Toddland's only cell), which the engine zero-defaults to grid (0,0) but the old
///     grid-presence heuristic misread as "the persistent dummy". Both shapes lost the worldspace's
///     (0,0) tile in the viewer. Classification is now structural (GRUP Type-1 direct child vs
///     Type-4/5 block member).
/// </summary>
public class Tes4PersistentDummyCellTests
{
    private const uint WorldFormId = 0x00000858;
    private const uint DummyCellFormId = 0x00023776;
    private const uint RealCellFormId = 0x000009BF;

    private static EsmTestFileBuilder.PlacedRefData Ref(uint formId, float x = 0, float y = 0)
    {
        return new EsmTestFileBuilder.PlacedRefData
        {
            RecordType = "REFR",
            FormId = formId,
            BaseFormId = 0x00001234,
            X = x,
            Y = y
        };
    }

    [Fact]
    public void DummyWithZeroGridXclc_IsPersistentAndGridless_RealCellKeepsGrid()
    {
        // SETheFringe shape: dummy carries XCLC (0,0), real exterior cell also at (0,0).
        var result = new EsmTestFileBuilder()
            .AddWorldspace(new EsmTestFileBuilder.WorldspaceData
            {
                FormId = WorldFormId,
                EditorId = "TestFringe",
                PersistentCellHasZeroGridXclc = true,
                PersistentCell = new EsmTestFileBuilder.CellData
                {
                    FormId = DummyCellFormId,
                    PersistentRefs = [Ref(0x2000), Ref(0x2001), Ref(0x2002)]
                },
                ExteriorCells =
                [
                    new EsmTestFileBuilder.CellData
                    {
                        FormId = RealCellFormId,
                        GridX = 0,
                        GridY = 0,
                        TemporaryRefs = [Ref(0x3000)]
                    }
                ]
            })
            .BuildAndAnalyze();

        var dummy = result.Collection.Cells.Single(c => c.FormId == DummyCellFormId);
        Assert.True(dummy.IsPersistentCell);
        Assert.Null(dummy.GridX);
        Assert.Null(dummy.GridY);

        var real = result.Collection.Cells.Single(c => c.FormId == RealCellFormId);
        Assert.False(real.IsPersistentCell);
        Assert.Equal(0, real.GridX);
        Assert.Equal(0, real.GridY);
    }

    [Fact]
    public void ExteriorCellWithoutXclc_DefaultsToZeroGrid_NotPersistent()
    {
        // Toddland shape: gridless dummy + a real exterior cell that ships no XCLC at all.
        var result = new EsmTestFileBuilder()
            .AddWorldspace(new EsmTestFileBuilder.WorldspaceData
            {
                FormId = WorldFormId,
                EditorId = "TestToddland",
                PersistentCell = new EsmTestFileBuilder.CellData
                {
                    FormId = DummyCellFormId,
                    PersistentRefs = [Ref(0x2000)]
                },
                ExteriorCells =
                [
                    new EsmTestFileBuilder.CellData
                    {
                        FormId = RealCellFormId,
                        GridX = 0,
                        GridY = 0,
                        OmitXclc = true,
                        TemporaryRefs = [Ref(0x3000), Ref(0x3001)]
                    }
                ]
            })
            .BuildAndAnalyze();

        var real = result.Collection.Cells.Single(c => c.FormId == RealCellFormId);
        Assert.False(real.IsPersistentCell);
        Assert.Equal(0, real.GridX);
        Assert.Equal(0, real.GridY);

        var dummy = result.Collection.Cells.Single(c => c.FormId == DummyCellFormId);
        Assert.True(dummy.IsPersistentCell);
        Assert.Null(dummy.GridX);
    }

    [Fact]
    public void LaterGamesShape_GridlessDummyAndGriddedCells_Unchanged()
    {
        // FO3/FNV shape: gridless dummy, every exterior cell carries XCLC — behavior must not change.
        var result = new EsmTestFileBuilder()
            .AddWorldspace(new EsmTestFileBuilder.WorldspaceData
            {
                FormId = WorldFormId,
                EditorId = "TestWasteland",
                PersistentCell = new EsmTestFileBuilder.CellData
                {
                    FormId = DummyCellFormId,
                    PersistentRefs = [Ref(0x2000)]
                },
                ExteriorCells =
                [
                    new EsmTestFileBuilder.CellData { FormId = RealCellFormId, GridX = 3, GridY = -2 }
                ]
            })
            .BuildAndAnalyze();

        var dummy = result.Collection.Cells.Single(c => c.FormId == DummyCellFormId);
        Assert.True(dummy.IsPersistentCell);
        Assert.Null(dummy.GridX);

        var real = result.Collection.Cells.Single(c => c.FormId == RealCellFormId);
        Assert.False(real.IsPersistentCell);
        Assert.Equal(3, real.GridX);
        Assert.Equal(-2, real.GridY);
    }

    [Fact]
    public void PreferGridLookupCell_NeverLetsPersistentDummyEvictRealCell()
    {
        // Defense-in-depth for structure-less inputs (DMP): even a ref-heavy persistent dummy that
        // reached the grid must not win the (0,0) slot from the real exterior cell.
        var dummy = new CellRecord
        {
            FormId = DummyCellFormId,
            GridX = 0,
            GridY = 0,
            IsPersistentCell = true,
            PlacedObjects = Enumerable.Range(0, 50)
                .Select(i => new PlacedReference { FormId = (uint)(0x4000 + i), RecordType = "REFR" })
                .ToList()
        };
        var real = new CellRecord
        {
            FormId = RealCellFormId,
            GridX = 0,
            GridY = 0,
            PlacedObjects = [new PlacedReference { FormId = 0x3000, RecordType = "REFR" }]
        };

        Assert.False(WorldSpatialIndex.PreferGridLookupCell(dummy, real));
        Assert.True(WorldSpatialIndex.PreferGridLookupCell(real, dummy));
    }
}

/// <summary>
///     Real-asset regression over retail Oblivion.esm: the worldspaces that lost their (0,0) tile
///     (Toddland — real cell 0x000009BF omits XCLC; SETheFringe — dummy 0x00012093 carries
///     XCLC (0,0)) must each surface exactly one non-persistent grid-(0,0) cell, and no persistent
///     dummy may carry a grid.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class Tes4PersistentDummyCellIntegrationTests
{
    private static string? ResolveOblivionEsm()
    {
        var root = Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "Oblivion.esm")))
        {
            return Path.Combine(root, "Oblivion.esm");
        }

        const string steam = @"E:\SteamLibrary\SteamApps\common\Oblivion\Data\Oblivion.esm";
        return File.Exists(steam) ? steam : null;
    }

    [Theory]
    [InlineData("Toddland")]
    [InlineData("SETheFringe")]
    public async Task SmallWorldspaces_KeepTheirRealZeroZeroCell(string worldspaceEditorId)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = ResolveOblivionEsm();
        Assert.SkipUnless(esm is not null,
            "Oblivion.esm not found (set BETHESDA_TEST_DATA_ROOT or install Oblivion).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);

        var ws = result.Records.Worldspaces.FirstOrDefault(w =>
            string.Equals(w.EditorId, worldspaceEditorId, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(ws);

        var cells = result.Records.Cells.Where(c => c.WorldspaceFormId == ws!.FormId).ToList();
        Assert.NotEmpty(cells);

        // No persistent dummy may reach the spatial grid.
        Assert.DoesNotContain(cells, c => c.IsPersistentCell && c.GridX is not null);

        // Exactly one REAL exterior cell owns grid (0,0).
        var zeroCells = cells.Where(c => c is { GridX: 0, GridY: 0 }).ToList();
        var real = Assert.Single(zeroCells);
        Assert.False(real.IsPersistentCell);
    }
}