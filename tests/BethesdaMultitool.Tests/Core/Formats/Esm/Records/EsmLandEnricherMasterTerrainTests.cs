using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Records;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Records;

/// <summary>
///     Viewer-shaped master-terrain fallback — the backend of the DMP-only "Master ESM terrain"
///     toggle. Grid-keyed against Load-Order master <see cref="CellRecord" />s (so it reaches dump
///     cells whose FormIDs diverged from the master), per-category on visuals (the dump wins every
///     field it has), and fills the terrain-geometry hole (heightmap) that visual categories alone
///     cannot — recovered placements must not float over nothing.
/// </summary>
public class EsmLandEnricherMasterTerrainTests
{
    private const uint Wasteland = 0x000DA726;

    private static byte[] ValidVnml(byte fill)
    {
        var bytes = new byte[33 * 33 * 3];
        Array.Fill(bytes, fill);
        return bytes;
    }

    private static LandHeightmap Heightmap(float offset = 1000f)
    {
        return new LandHeightmap
        {
            HeightOffset = offset,
            HeightDeltas = new sbyte[33 * 33]
        };
    }

    private static LandTextureLayer Layer(uint textureFormId)
    {
        return new LandTextureLayer
        {
            Kind = LandTextureLayerKind.Base,
            TextureFormId = textureFormId
        };
    }

    private static CellRecord ExteriorCell(uint formId, int gridX = 1, int gridY = 2)
    {
        return new CellRecord
        {
            FormId = formId,
            WorldspaceFormId = Wasteland,
            GridX = gridX,
            GridY = gridY
        };
    }

    [Fact]
    public void FillsVisualsAndHeightmap_WhenDumpCellLostItsLand()
    {
        // FormIDs deliberately diverge (0x2001 vs 0x999): grid keying must still connect them —
        // this is exactly the case the FormID-keyed load-order cell merge cannot serve.
        var dumpCell = ExteriorCell(0x2001);
        var masterVclr = ValidVnml(7);
        var masterHeightmap = Heightmap();
        var masterCell = ExteriorCell(0x999) with
        {
            Heightmap = masterHeightmap,
            LandVisualData = new LandVisualData
            {
                VertexColors = masterVclr,
                TextureLayers = [Layer(0x1234)],
                Source = VisualDataSource.MasterEsm
            }
        };

        var result = EsmLandEnricher.EnrichCellsWithMasterEsmLandFallback([dumpCell], [masterCell]);

        var enriched = Assert.Single(result);
        Assert.NotSame(dumpCell, enriched);
        Assert.Same(masterVclr, enriched.LandVisualData!.VertexColors);
        Assert.Same(masterHeightmap, enriched.Heightmap);
        Assert.Equal(0x1234u, Assert.Single(enriched.LandVisualData.TextureLayers).TextureFormId);
        Assert.Equal(VisualDataSource.MasterEsm, enriched.LandVisualData.TextureLayersSource);
    }

    [Fact]
    public void DumpFieldsWin_MasterFillsOnlyTheHoles()
    {
        var dumpVclr = ValidVnml(1);
        var dumpHeightmap = Heightmap(500f);
        var dumpCell = ExteriorCell(0x2001) with
        {
            Heightmap = dumpHeightmap,
            LandVisualData = new LandVisualData
            {
                VertexColors = dumpVclr,
                Source = VisualDataSource.Dmp
            }
        };
        var masterVnml = ValidVnml(9);
        var masterCell = ExteriorCell(0x999) with
        {
            Heightmap = Heightmap(9999f),
            LandVisualData = new LandVisualData
            {
                VertexColors = ValidVnml(3),
                VertexNormals = masterVnml,
                Source = VisualDataSource.MasterEsm
            }
        };

        var result = EsmLandEnricher.EnrichCellsWithMasterEsmLandFallback([dumpCell], [masterCell]);

        var enriched = Assert.Single(result);
        Assert.Same(dumpVclr, enriched.LandVisualData!.VertexColors); // dump wins the field it has
        Assert.Same(masterVnml, enriched.LandVisualData.VertexNormals); // master fills the hole
        Assert.Same(dumpHeightmap, enriched.Heightmap); // own geometry is never replaced
    }

    [Fact]
    public void HeightmapNotFilled_WhenRuntimeMeshAlreadyProvidesGeometry()
    {
        // The 3D decode reads Heightmap ?? RuntimeTerrainMesh — a runtime mesh IS geometry, so the
        // master heightmap must not shadow it. With nothing else to give, the cell comes back
        // identical by reference.
        var dumpCell = ExteriorCell(0x2001) with
        {
            RuntimeTerrainMesh = new RuntimeTerrainMesh { Vertices = new float[3] }
        };
        var masterCell = ExteriorCell(0x999) with { Heightmap = Heightmap() };

        var result = EsmLandEnricher.EnrichCellsWithMasterEsmLandFallback([dumpCell], [masterCell]);

        Assert.Same(dumpCell, Assert.Single(result));
    }

    [Fact]
    public void InteriorsAndUnmatchedGrids_ComeBackIdentical()
    {
        var interior = new CellRecord { FormId = 0x3001, Flags = 0x01 };
        var unmatched = ExteriorCell(0x3002, gridX: 40, gridY: 40);
        var masterCell = ExteriorCell(0x999) with { Heightmap = Heightmap() };

        var result = EsmLandEnricher.EnrichCellsWithMasterEsmLandFallback(
            [interior, unmatched], [masterCell]);

        Assert.Same(interior, result[0]);
        Assert.Same(unmatched, result[1]);
    }

    [Fact]
    public void MasterAuthoredLayers_OverrideRuntimeCapturedLayers()
    {
        // The 2026-08-31 authored-source ruling carried into the viewer preview: a runtime layer
        // set is the resident SUBSET at crash time; the master's authored set outranks it.
        var dumpCell = ExteriorCell(0x2001) with
        {
            LandVisualData = new LandVisualData
            {
                TextureLayers = [Layer(0xAAAA), Layer(0xBBBB)],
                Source = VisualDataSource.Runtime
            }
        };
        var masterCell = ExteriorCell(0x999) with
        {
            LandVisualData = new LandVisualData
            {
                TextureLayers = [Layer(0x1234)],
                Source = VisualDataSource.MasterEsm
            }
        };

        var result = EsmLandEnricher.EnrichCellsWithMasterEsmLandFallback([dumpCell], [masterCell]);

        var visual = Assert.Single(result).LandVisualData!;
        Assert.Equal(0x1234u, Assert.Single(visual.TextureLayers).TextureFormId);
        Assert.Equal(VisualDataSource.MasterEsm, visual.TextureLayersSource);
    }

    [Fact]
    public void SameGridMasters_ContributeEachTerrainHalfIndependently_LastWins()
    {
        // A visual-only master cell must not starve the heightmap out of a same-grid cell that
        // carries the geometry; and between two carriers of the same half, the LATER load-order
        // entry wins (loadOrderRecords.Cells appends later overlays after the base list).
        var dumpCell = ExteriorCell(0x2001);
        var visualOnly = ExteriorCell(0x900) with
        {
            LandVisualData = new LandVisualData
            {
                VertexColors = ValidVnml(1),
                Source = VisualDataSource.MasterEsm
            }
        };
        var laterVisual = ValidVnml(2);
        var visualOnlyLater = ExteriorCell(0x901) with
        {
            LandVisualData = new LandVisualData
            {
                VertexColors = laterVisual,
                Source = VisualDataSource.MasterEsm
            }
        };
        var geometryHeightmap = Heightmap();
        var geometryOnly = ExteriorCell(0x902) with { Heightmap = geometryHeightmap };

        var result = EsmLandEnricher.EnrichCellsWithMasterEsmLandFallback(
            [dumpCell], [visualOnly, visualOnlyLater, geometryOnly]);

        var enriched = Assert.Single(result);
        Assert.Same(laterVisual, enriched.LandVisualData!.VertexColors);
        Assert.Same(geometryHeightmap, enriched.Heightmap);
    }

    [Fact]
    public void CompleteAuthoredCell_TakesTheFastPath_AndKeepsReferenceIdentity()
    {
        // A cell with geometry plus every visual category from an authored source gains nothing —
        // it must come back identical by reference even when a master matches its grid (this is
        // also what keeps lazy BTD-backed layer lists from being force-materialized for nothing).
        var dumpCell = ExteriorCell(0x2001) with
        {
            Heightmap = Heightmap(),
            LandVisualData = new LandVisualData
            {
                VertexColors = ValidVnml(1),
                VertexNormals = ValidVnml(2),
                TextureIndices = [1u, 2u],
                TextureLayers = [Layer(0xAAAA)],
                Source = VisualDataSource.Dmp
            }
        };
        var masterCell = ExteriorCell(0x999) with
        {
            Heightmap = Heightmap(9999f),
            LandVisualData = new LandVisualData
            {
                VertexColors = ValidVnml(9),
                Source = VisualDataSource.MasterEsm
            }
        };

        var result = EsmLandEnricher.EnrichCellsWithMasterEsmLandFallback([dumpCell], [masterCell]);

        Assert.Same(dumpCell, Assert.Single(result));
    }

    [Fact]
    public void MasterWithNothingTerrainShaped_LeavesEveryCellIdentical()
    {
        var dumpCell = ExteriorCell(0x2001);
        var masterCell = ExteriorCell(0x999); // no LandVisualData, no Heightmap → not indexed

        var result = EsmLandEnricher.EnrichCellsWithMasterEsmLandFallback([dumpCell], [masterCell]);

        Assert.Same(dumpCell, Assert.Single(result));
    }
}
