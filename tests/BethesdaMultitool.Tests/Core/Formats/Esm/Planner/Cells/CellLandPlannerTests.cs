using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class CellLandPlannerTests
{
    private const uint MasterCellId = 0x000DDF1C;
    private const uint MasterLandId = 0x000ABCDE;

    [Fact]
    public void DmpNew_Exterior_Prefers_Captured_Heightmap()
    {
        var captured = Heightmap(7);
        var cell = Exterior() with
        {
            Heightmap = captured,
            RuntimeTerrainMesh = new RuntimeTerrainMesh { Vertices = [0f, 0f, 0f] }
        };

        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        var decision = Assert.Single(result.DecisionsByCellSourceFormId.Values);
        Assert.Same(captured, decision.Heightmap);
        Assert.Equal(CellLandHeightSource.CapturedHeightmap, decision.HeightSource);
    }

    [Fact]
    public void Complete_Runtime_Mesh_Synthesizes_Land()
    {
        var cell = Exterior() with { RuntimeTerrainMesh = CompleteRuntimeMesh() };
        var quality = cell.RuntimeTerrainMesh.DiagnoseQuality();
        Assert.Equal(33 * 33 - 1, quality.SourceSampleCount);
        Assert.True(quality.SourceCoveragePercent < 100f);

        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        var decision = Assert.Single(result.DecisionsByCellSourceFormId.Values);
        Assert.Equal(CellLandHeightSource.CompleteRuntimeMesh, decision.HeightSource);
        Assert.Equal(33 * 33, decision.Heightmap.HeightDeltas.Length);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Complete_Runtime_Mesh_Preserves_Enriched_Absolute_Base_Height()
    {
        var mesh = CompleteRuntimeMesh() with { RuntimeBaseHeight = 1024f };
        var enriched = mesh.ToLandHeightmap(1024f);
        enriched.SourceParentCellFormId = 0x0010B901;
        var cell = Exterior() with
        {
            Heightmap = enriched,
            RuntimeTerrainMesh = mesh
        };

        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        var decision = Assert.Single(result.DecisionsByCellSourceFormId.Values);
        Assert.Same(enriched, decision.Heightmap);
        Assert.Equal(CellLandHeightSource.CompleteRuntimeMesh, decision.HeightSource);
        Assert.Equal(
            mesh.ToLandHeightmap().CalculateHeights()[0, 0] + 1024f,
            decision.Heightmap.CalculateHeights()[0, 0]);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Complete_Runtime_Mesh_Without_Base_Height_Provenance_Fails_Closed()
    {
        var mesh = CompleteRuntimeMesh() with { RuntimeBaseHeight = null };
        var cell = Exterior() with
        {
            Heightmap = mesh.ToLandHeightmap(),
            RuntimeTerrainMesh = mesh
        };

        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
        Assert.Equal("land.runtime-base-height-missing", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Complete_Flat_Runtime_Mesh_Synthesizes_Land()
    {
        var mesh = FullGridMesh((_, _) => 42f);
        var quality = mesh.DiagnoseQuality();
        Assert.Equal("Flat", quality.Classification);
        Assert.Equal(100f, quality.SourceCoveragePercent);

        var result = CellLandPlanner.DecideAll(
            [Entry(Exterior() with { RuntimeTerrainMesh = mesh }, SourceKind.DmpNew)]);

        var decision = Assert.Single(result.DecisionsByCellSourceFormId.Values);
        Assert.Equal(CellLandHeightSource.CompleteRuntimeMesh, decision.HeightSource);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Complete_FewPixel_Runtime_Mesh_Synthesizes_Land()
    {
        var mesh = FullGridMesh((x, y) => x == 32 && y == 32 ? 9f : 1f);
        var quality = mesh.DiagnoseQuality();
        Assert.Equal("FewPixels", quality.Classification);
        Assert.Equal(100f, quality.SourceCoveragePercent);

        var result = CellLandPlanner.DecideAll(
            [Entry(Exterior() with { RuntimeTerrainMesh = mesh }, SourceKind.DmpNew)]);

        var decision = Assert.Single(result.DecisionsByCellSourceFormId.Values);
        Assert.Equal(CellLandHeightSource.CompleteRuntimeMesh, decision.HeightSource);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Authoritative_Zero_Origin_Runtime_Mesh_Is_Complete()
    {
        var mesh = CompleteRuntimeMesh() with
        {
            SourceVertexMask = Enumerable.Repeat(true, 33 * 33).ToArray()
        };
        var quality = mesh.DiagnoseQuality();
        Assert.Equal(100f, quality.SourceCoveragePercent);

        var result = CellLandPlanner.DecideAll(
            [Entry(Exterior() with { RuntimeTerrainMesh = mesh }, SourceKind.DmpNew)]);

        Assert.Single(result.DecisionsByCellSourceFormId);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Unmasked_Zero_Origin_Plus_Another_Missing_Sample_Is_Not_Complete()
    {
        var complete = CompleteRuntimeMesh();
        var vertices = complete.Vertices[..^3];
        var mesh = complete with { Vertices = vertices };
        var quality = mesh.DiagnoseQuality();
        Assert.Equal(33 * 33 - 2, quality.SourceSampleCount);

        var result = CellLandPlanner.DecideAll(
            [Entry(Exterior() with { RuntimeTerrainMesh = mesh }, SourceKind.DmpNew)]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
        Assert.Equal("land.runtime-mesh-not-complete", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Arbitrary_Zeroed_Vertex_Does_Not_Qualify_As_Origin_Ambiguity()
    {
        var complete = FullGridMesh((_, _) => 42f);
        var vertices = (float[])complete.Vertices.Clone();
        var arbitraryOffset = (10 * 33 + 10) * 3;
        vertices[arbitraryOffset] = 0f;
        vertices[arbitraryOffset + 1] = 0f;
        vertices[arbitraryOffset + 2] = 0f;
        var mesh = complete with { Vertices = vertices };
        var quality = mesh.DiagnoseQuality();
        Assert.Equal(33 * 33 - 1, quality.SourceSampleCount);

        var result = CellLandPlanner.DecideAll(
            [Entry(Exterior() with { RuntimeTerrainMesh = mesh }, SourceKind.DmpNew)]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
        Assert.Equal("land.runtime-mesh-not-complete", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Sanitized_Origin_Is_The_One_Allowed_Missing_Source_Slot()
    {
        var sanitizedMask = new bool[33 * 33];
        sanitizedMask[0] = true;
        var mesh = CompleteRuntimeMesh() with
        {
            SanitizedZCount = 1,
            SanitizedMask = sanitizedMask,
            SanitizedZeroOriginIndex = 0
        };

        var result = CellLandPlanner.DecideAll(
            [Entry(Exterior() with { RuntimeTerrainMesh = mesh }, SourceKind.DmpNew)]);

        Assert.Single(result.DecisionsByCellSourceFormId);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Authoritative_Centered_Zero_Origin_Remains_A_Source_Sample()
    {
        var mesh = CenteredFullGridMesh((_, _) => 0f) with
        {
            SourceVertexMask = Enumerable.Repeat(true, 33 * 33).ToArray()
        };
        mesh = mesh.SanitizeVertices();

        Assert.Equal(0, mesh.SanitizedZCount);
        Assert.Null(mesh.SanitizedMask);
        var coverage = mesh.TryGetCanonicalSourceCoverageMask();
        Assert.NotNull(coverage);
        Assert.True(coverage[16, 16]);
        Assert.Equal(100f, mesh.DiagnoseQuality().SourceCoveragePercent);

        var result = CellLandPlanner.DecideAll(
            [Entry(Exterior() with { RuntimeTerrainMesh = mesh }, SourceKind.DmpNew)]);

        Assert.Single(result.DecisionsByCellSourceFormId);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Sanitized_Unmasked_Centered_Local_Origin_Maps_To_The_Canonical_Center()
    {
        var mesh = CenteredFullGridMesh((_, _) => 0f).SanitizeVertices();

        Assert.Equal(1, mesh.SanitizedZCount);
        Assert.True(mesh.SanitizedMask![16 * 33 + 16]);
        Assert.Equal(16 * 33 + 16, mesh.SanitizedZeroOriginIndex);
        var coverage = mesh.TryGetCanonicalSourceCoverageMask();
        Assert.NotNull(coverage);
        Assert.False(coverage[16, 16]);

        var result = CellLandPlanner.DecideAll(
            [Entry(Exterior() with { RuntimeTerrainMesh = mesh }, SourceKind.DmpNew)]);

        Assert.Single(result.DecisionsByCellSourceFormId);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Sanitized_Corrupt_Origin_Is_Not_The_Zero_Slot_Ambiguity(float corruptHeight)
    {
        var complete = FullGridMesh((_, _) => 42f);
        var vertices = (float[])complete.Vertices.Clone();
        vertices[2] = corruptHeight;
        var mesh = (complete with { Vertices = vertices }).SanitizeVertices();

        Assert.Equal(1, mesh.SanitizedZCount);
        Assert.True(mesh.SanitizedMask![0]);
        Assert.Null(mesh.SanitizedZeroOriginIndex);

        var result = CellLandPlanner.DecideAll(
            [Entry(Exterior() with { RuntimeTerrainMesh = mesh }, SourceKind.DmpNew)]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
        Assert.Equal("land.runtime-mesh-not-complete", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Authoritative_Sanitized_Source_Is_Not_Complete()
    {
        var sanitizedMask = new bool[33 * 33];
        sanitizedMask[0] = true;
        var mesh = FullGridMesh((_, _) => 0f) with
        {
            SourceVertexMask = Enumerable.Repeat(true, 33 * 33).ToArray(),
            SanitizedZCount = 1,
            SanitizedMask = sanitizedMask
        };

        var result = CellLandPlanner.DecideAll(
            [Entry(Exterior() with { RuntimeTerrainMesh = mesh }, SourceKind.DmpNew)]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
        Assert.Equal("land.runtime-mesh-not-complete", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Authoritative_SourceMask_With_One_Declared_Hole_Is_Not_Complete()
    {
        var sourceMask = Enumerable.Repeat(true, 33 * 33).ToArray();
        sourceMask[10] = false;
        var mesh = FullGridMesh((_, _) => 42f) with { SourceVertexMask = sourceMask };
        var quality = mesh.DiagnoseQuality();
        Assert.Equal(33 * 33 - 1, quality.SourceSampleCount);
        Assert.True(quality.SourceCoveragePercent > 99f);

        var result = CellLandPlanner.DecideAll(
            [Entry(Exterior() with { RuntimeTerrainMesh = mesh }, SourceKind.DmpNew)]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
        Assert.Equal("land.runtime-mesh-not-complete", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Declared_Origin_Hole_Is_Not_Complete_Even_With_Origin_Sanitation()
    {
        var sourceMask = Enumerable.Repeat(true, 33 * 33).ToArray();
        sourceMask[0] = false;
        var unsanitized = FullGridMesh((_, _) => 0f) with { SourceVertexMask = sourceMask };

        var rejected = CellLandPlanner.DecideAll(
            [Entry(Exterior() with { RuntimeTerrainMesh = unsanitized }, SourceKind.DmpNew)]);
        Assert.Empty(rejected.DecisionsByCellSourceFormId);

        var sanitizedMask = new bool[33 * 33];
        sanitizedMask[0] = true;
        var sanitized = unsanitized with
        {
            SanitizedZCount = 1,
            SanitizedMask = sanitizedMask
        };

        var stillRejected = CellLandPlanner.DecideAll(
            [Entry(Exterior() with { RuntimeTerrainMesh = sanitized }, SourceKind.DmpNew)]);
        Assert.Empty(stillRejected.DecisionsByCellSourceFormId);
        Assert.Equal("land.runtime-mesh-not-complete", Assert.Single(stillRejected.Diagnostics).Code);
    }

    [Fact]
    public void Sanitized_NonOrigin_Source_Slot_Is_Not_Complete()
    {
        var sanitizedMask = new bool[33 * 33];
        sanitizedMask[10] = true;
        var mesh = FullGridMesh((_, _) => 42f) with
        {
            SanitizedZCount = 1,
            SanitizedMask = sanitizedMask
        };

        var result = CellLandPlanner.DecideAll(
            [Entry(Exterior() with { RuntimeTerrainMesh = mesh }, SourceKind.DmpNew)]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
        Assert.Equal("land.runtime-mesh-not-complete", Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(9)]
    public void Complete_WorldFrame_LowerLod_Runtime_Mesh_Synthesizes_Land(int gridSize)
    {
        var mesh = WorldFrameGridMesh(gridSize);
        var quality = mesh.DiagnoseQuality();
        Assert.Equal(gridSize, quality.DetectedGridSize);
        Assert.Equal(gridSize * gridSize, quality.SourceSampleCount);
        Assert.Equal(100f, quality.SourceCoveragePercent);

        var cell = Exterior() with { GridX = 2, GridY = 3, RuntimeTerrainMesh = mesh };
        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        Assert.Single(result.DecisionsByCellSourceFormId);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Sanitized_Mesh_Cannot_Refit_As_Complete_LowerLod()
    {
        var mesh = WorldFrameGridMesh(17) with { SanitizedZCount = 1 };
        Assert.Equal(100f, mesh.DiagnoseQuality().SourceCoveragePercent);

        var cell = Exterior() with { GridX = 2, GridY = 3, RuntimeTerrainMesh = mesh };
        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
        Assert.Equal("land.runtime-mesh-not-complete", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void CoordinateOnly_And_Partial_Runtime_Captures_Do_Not_Invent_Land()
    {
        var coordinateOnly = Exterior();
        var partial = Exterior(0x0010B902) with
        {
            RuntimeTerrainMesh = new RuntimeTerrainMesh
            {
                Vertices = [0f, 0f, 1f, 128f, 0f, 2f, 0f, 128f, 3f],
                SourceParentCellFormId = 0x0010B902
            }
        };
        var corruptQuality = partial.RuntimeTerrainMesh.DiagnoseQuality();
        Assert.Equal("None", corruptQuality.HeightSource);
        Assert.Equal("Corrupt", corruptQuality.Classification);
        Assert.Equal(0f, corruptQuality.SourceCoveragePercent);

        var result = CellLandPlanner.DecideAll(
            [Entry(coordinateOnly, SourceKind.DmpNew), Entry(partial, SourceKind.DmpNew)]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("land.runtime-mesh-not-complete", diagnostic.Code);
        Assert.Equal(partial.FormId, diagnostic.FormId);
    }

    [Fact]
    public void Enriched_Partial_Runtime_Heightmap_Does_Not_Bypass_Complete_Mesh_Gate()
    {
        var mesh = PartialRuntimeMesh();
        var cell = Exterior() with
        {
            Heightmap = mesh.ToLandHeightmap(),
            RuntimeTerrainMesh = mesh
        };

        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
        Assert.Equal("land.runtime-mesh-not-complete", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Partial_Flat_Runtime_Mesh_Does_Not_Bypass_Coverage_Gate()
    {
        var vertices = new List<float>();
        for (var y = 0; y < 12; y++)
        {
            for (var x = 0; x < 24; x++)
            {
                vertices.Add(x * 128f);
                vertices.Add(y * 128f);
                vertices.Add(0f);
            }
        }

        var mesh = new RuntimeTerrainMesh
        {
            Vertices = vertices.ToArray(),
            SourceParentCellFormId = 0x0010B901
        };
        var quality = mesh.DiagnoseQuality();
        Assert.Equal("RuntimeMESH", quality.HeightSource);
        Assert.Equal("Flat", quality.Classification);
        Assert.True(quality.SourceCoveragePercent < 99f);

        var result = CellLandPlanner.DecideAll(
            [Entry(Exterior() with { RuntimeTerrainMesh = mesh }, SourceKind.DmpNew)]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
        Assert.Equal("land.runtime-mesh-not-complete", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Partial_FewPixel_Runtime_Mesh_Does_Not_Bypass_Coverage_Gate()
    {
        var mesh = FullGridMesh((x, y) => x == 32 && y == 32 ? 8f : 1f);
        var sourceMask = Enumerable.Repeat(true, 33 * 33).ToArray();
        for (var i = 0; i < 14; i++)
        {
            sourceMask[i] = false;
        }

        mesh = mesh with { SourceVertexMask = sourceMask };
        var quality = mesh.DiagnoseQuality();
        Assert.Equal("RuntimeMESH", quality.HeightSource);
        Assert.Equal("FewPixels", quality.Classification);
        Assert.True(quality.SourceCoveragePercent < 99f);

        var result = CellLandPlanner.DecideAll(
            [Entry(Exterior() with { RuntimeTerrainMesh = mesh }, SourceKind.DmpNew)]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
        Assert.Equal("land.runtime-mesh-not-complete", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Captured_Vhgt_Wins_When_Runtime_Mesh_Is_Partial()
    {
        var captured = Heightmap(4);
        var mesh = PartialRuntimeMesh();
        var cell = Exterior() with
        {
            CapturedLandHeightmap = captured,
            Heightmap = mesh.ToLandHeightmap(),
            RuntimeTerrainMesh = mesh
        };

        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        var decision = Assert.Single(result.DecisionsByCellSourceFormId.Values);
        Assert.Same(captured, decision.Heightmap);
        Assert.Equal(CellLandHeightSource.CapturedHeightmap, decision.HeightSource);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void GridMatched_Captured_Heightmap_With_Different_Parent_Is_Rejected()
    {
        var cell = Exterior() with
        {
            CapturedLandHeightmap = Heightmap(4, 0x0010B999)
        };

        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
        Assert.Equal("land.captured-heightmap-parent-mismatch", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Mismatched_Captured_Heightmap_Falls_Back_To_Exact_Runtime_Mesh()
    {
        var cell = Exterior() with
        {
            CapturedLandHeightmap = Heightmap(4, 0x0010B999),
            RuntimeTerrainMesh = CompleteRuntimeMesh()
        };

        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        var decision = Assert.Single(result.DecisionsByCellSourceFormId.Values);
        Assert.Equal(CellLandHeightSource.CompleteRuntimeMesh, decision.HeightSource);
        Assert.Equal("land.captured-heightmap-parent-mismatch", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void GridMatched_Runtime_Mesh_With_Different_Parent_Is_Rejected()
    {
        var mesh = CompleteRuntimeMesh() with { SourceParentCellFormId = 0x0010B999 };
        var cell = Exterior() with { RuntimeTerrainMesh = mesh };

        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
        Assert.Equal("land.runtime-mesh-parent-mismatch", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Mismatched_Runtime_Mesh_Does_Not_Contribute_Vertex_Colors()
    {
        var mesh = CompleteRuntimeMesh() with
        {
            SourceParentCellFormId = 0x0010B999,
            Colors = Enumerable.Repeat(1f, 33 * 33 * 3).ToArray()
        };
        var cell = Exterior() with
        {
            CapturedLandHeightmap = Heightmap(4),
            RuntimeTerrainMesh = mesh
        };

        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        Assert.Null(Assert.Single(result.DecisionsByCellSourceFormId.Values).VisualData);
    }

    [Fact]
    public void GridMatched_Visual_Data_With_Different_Parent_Is_Dropped()
    {
        var cell = Exterior() with
        {
            CapturedLandHeightmap = Heightmap(4),
            LandVisualData = new LandVisualData
            {
                SourceParentCellFormId = 0x0010B999,
                VertexColors = new byte[33 * 33 * 3]
            }
        };

        var result = CellLandPlanner.DecideAll([Entry(cell, SourceKind.DmpNew)]);

        Assert.Null(Assert.Single(result.DecisionsByCellSourceFormId.Values).VisualData);
        Assert.Equal("land.visual-data-parent-mismatch", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Interior_And_Persistent_Cells_Never_Get_Land()
    {
        var interior = new CellRecord
        {
            FormId = 0x0010B903,
            Flags = 0x01,
            Heightmap = Heightmap(2, 0x0010B903)
        };
        var persistent = Exterior(0x0010B904) with
        {
            IsPersistentCell = true,
            Heightmap = Heightmap(3, 0x0010B904)
        };
        var interiorOverride = new CellRecord
        {
            FormId = 0x000DDF10,
            Flags = 0x01,
            Heightmap = Heightmap(1, 0x000DDF10)
        };

        var result = CellLandPlanner.DecideAll(
        [
            Entry(interior, SourceKind.DmpNew),
            Entry(persistent, SourceKind.DmpNew),
            OverrideEntry(interiorOverride, true)
        ]);

        Assert.Empty(result.DecisionsByCellSourceFormId);
    }

    [Fact]
    public void Master_Override_Complete_Capture_Overrides_Master_Land()
    {
        var captured = Heightmap(4, MasterCellId);
        var cell = Exterior(MasterCellId) with { CapturedLandHeightmap = captured };
        var masterVclr = Enumerable.Repeat((byte)200, 33 * 33 * 3).ToArray();

        var result = CellLandPlanner.DecideAll(
            [OverrideEntry(cell)],
            new Dictionary<uint, ParsedMainRecord>
            {
                [MasterLandId] = MasterLand(MasterLandId, 3, masterVclr)
            },
            new Dictionary<uint, List<uint>> { [MasterCellId] = [MasterLandId] });

        var decision = Assert.Single(result.DecisionsByCellSourceFormId.Values);
        Assert.Same(captured, decision.Heightmap);
        Assert.Equal(MasterLandId, decision.MasterLandFormId);
        Assert.NotNull(decision.VisualData);
        Assert.Equal(masterVclr, decision.VisualData!.VertexColors);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Master_Override_Flat_Capture_Rejected_Against_Sculpted_Master()
    {
        var cell = Exterior(MasterCellId) with
        {
            CapturedLandHeightmap = Heightmap(0, MasterCellId)
        };

        var result = CellLandPlanner.DecideAll(
            [OverrideEntry(cell)],
            new Dictionary<uint, ParsedMainRecord> { [MasterLandId] = MasterLand(MasterLandId, 5) },
            new Dictionary<uint, List<uint>> { [MasterCellId] = [MasterLandId] });

        Assert.Empty(result.DecisionsByCellSourceFormId);
        Assert.Equal("land.flat-rejected", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Master_Override_Without_Master_Land_Plans_New_Land()
    {
        var cell = Exterior(MasterCellId) with
        {
            CapturedLandHeightmap = Heightmap(4, MasterCellId)
        };

        var result = CellLandPlanner.DecideAll([OverrideEntry(cell)]);

        var decision = Assert.Single(result.DecisionsByCellSourceFormId.Values);
        Assert.Null(decision.MasterLandFormId);
    }

    [Fact]
    public void Master_Override_Incomplete_Capture_Does_Not_Override()
    {
        var cell = Exterior(MasterCellId) with
        {
            RuntimeTerrainMesh = PartialRuntimeMesh() with
            {
                SourceParentCellFormId = MasterCellId
            }
        };

        var result = CellLandPlanner.DecideAll(
            [OverrideEntry(cell)],
            new Dictionary<uint, ParsedMainRecord> { [MasterLandId] = MasterLand(MasterLandId, 5) },
            new Dictionary<uint, List<uint>> { [MasterCellId] = [MasterLandId] });

        Assert.Empty(result.DecisionsByCellSourceFormId);
        Assert.Equal("land.runtime-mesh-not-complete", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void CoordPaired_Override_Keys_Decision_On_Master_Cell_FormId()
    {
        var protoCell = Exterior(0x01001670) with
        {
            CapturedLandHeightmap = Heightmap(4, 0x01001670)
        };

        var result = CellLandPlanner.DecideAll(
            [OverrideEntry(protoCell, cellFormId: MasterCellId)],
            new Dictionary<uint, ParsedMainRecord> { [MasterLandId] = MasterLand(MasterLandId, 3) },
            new Dictionary<uint, List<uint>> { [MasterCellId] = [MasterLandId] });

        var (key, decision) = Assert.Single(result.DecisionsByCellSourceFormId);
        Assert.Equal(MasterCellId, key);
        Assert.Equal(protoCell.FormId, decision.CellSourceFormId);
        Assert.Equal(MasterLandId, decision.MasterLandFormId);
    }

    [Fact]
    public void CellSection_Plans_Land_Before_Navm_And_Temporary_Refs()
    {
        var cell = Exterior() with
        {
            Heightmap = Heightmap(1),
            PlacedObjects =
            [
                new PlacedReference
                {
                    FormId = 0xAA003000,
                    BaseFormId = 0x00001000,
                    RecordType = "REFR"
                }
            ]
        };
        var navm = new NavMeshRecord
        {
            FormId = 0xAA004000,
            CellFormId = cell.FormId,
            RawSubrecords = [new NavMeshSubrecord("DATA", [1, 2, 3, 4])]
        };

        var result = CellSectionPlanner.Plan(
            new Dictionary<uint, PcEsmCellContext>(),
            new Dictionary<uint, ParsedMainRecord>(),
            [cell],
            [navm],
            [],
            new HashSet<uint>(),
            new FormIdAllocator(),
            false,
            new HashSet<uint>());

        var plan = Assert.Single(result.CellsByFormId.Values);
        Assert.Equal(["LAND", "NAVM", "REFR"], plan.TemporaryChildren.Select(child => child.Type));
    }

    private static CellRecord Exterior(uint formId = 0x0010B901)
    {
        return new CellRecord
        {
            FormId = formId,
            WorldspaceFormId = 0x0010B96F,
            GridX = 0,
            GridY = 0
        };
    }

    private static CellCatalogEntry Entry(CellRecord cell, SourceKind source)
    {
        return new CellCatalogEntry
        {
            CellFormId = cell.FormId,
            Source = source,
            DmpModel = cell
        };
    }

    private static CellCatalogEntry OverrideEntry(
        CellRecord cell,
        bool isInterior = false,
        uint? cellFormId = null)
    {
        return new CellCatalogEntry
        {
            CellFormId = cellFormId ?? cell.FormId,
            Source = SourceKind.DmpOverride,
            DmpModel = cell,
            MasterContext = new PcEsmCellContext
            {
                CellFormId = cellFormId ?? cell.FormId,
                IsInterior = isInterior,
                WorldspaceFormId = isInterior ? null : 0x000DA726u,
                BlockGroupType = isInterior ? 2 : 4,
                SubblockGroupType = isInterior ? 3 : 5,
                BlockLabel = new byte[4],
                SubblockLabel = new byte[4]
            }
        };
    }

    private static ParsedMainRecord MasterLand(uint formId, sbyte delta, byte[]? vclr = null)
    {
        var vhgt = new byte[1096];
        BinaryPrimitives.WriteSingleLittleEndian(vhgt.AsSpan(0, 4), 50f);
        for (var i = 0; i < 1089; i++)
        {
            vhgt[4 + i] = (byte)delta;
        }

        var subrecords = new List<ParsedSubrecord>
        {
            new() { Signature = "VHGT", Data = vhgt }
        };
        if (vclr is not null)
        {
            subrecords.Add(new ParsedSubrecord { Signature = "VCLR", Data = vclr });
        }

        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "LAND",
                DataSize = 0,
                Flags = 0,
                FormId = formId,
                Timestamp = 0,
                VcsInfo = 0,
                Version = 15
            },
            Offset = 0,
            Subrecords = subrecords
        };
    }

    private static LandHeightmap Heightmap(
        sbyte value,
        uint sourceParentCellFormId = 0x0010B901)
    {
        return new LandHeightmap
        {
            HeightOffset = 100f,
            HeightDeltas = Enumerable.Repeat(value, 33 * 33).ToArray(),
            SourceParentCellFormId = sourceParentCellFormId
        };
    }

    private static RuntimeTerrainMesh CompleteRuntimeMesh()
    {
        return FullGridMesh((x, y) => x * 3f + y * 2f + x * y % 7);
    }

    private static RuntimeTerrainMesh FullGridMesh(Func<int, int, float> height)
    {
        var vertices = new float[33 * 33 * 3];
        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                var index = (y * 33 + x) * 3;
                vertices[index] = x * 128f;
                vertices[index + 1] = y * 128f;
                vertices[index + 2] = height(x, y);
            }
        }

        return new RuntimeTerrainMesh
        {
            Vertices = vertices,
            SourceParentCellFormId = 0x0010B901,
            RuntimeBaseHeight = 0f
        };
    }

    private static RuntimeTerrainMesh CenteredFullGridMesh(Func<int, int, float> height)
    {
        var vertices = new float[33 * 33 * 3];
        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                var index = (y * 33 + x) * 3;
                vertices[index] = (x - 16) * 128f;
                vertices[index + 1] = (y - 16) * 128f;
                vertices[index + 2] = height(x, y);
            }
        }

        return new RuntimeTerrainMesh
        {
            Vertices = vertices,
            SourceParentCellFormId = 0x0010B901,
            RuntimeBaseHeight = 0f
        };
    }

    private static RuntimeTerrainMesh WorldFrameGridMesh(int gridSize)
    {
        var vertices = new float[gridSize * gridSize * 3];
        var spacing = 4096f / (gridSize - 1);
        for (var y = 0; y < gridSize; y++)
        {
            for (var x = 0; x < gridSize; x++)
            {
                var index = (y * gridSize + x) * 3;
                vertices[index] = 2 * 4096f + x * spacing;
                vertices[index + 1] = 3 * 4096f + y * spacing;
                vertices[index + 2] = 100f + x * 3f + y * 2f + x * y % 7;
            }
        }

        return new RuntimeTerrainMesh
        {
            Vertices = vertices,
            SourceParentCellFormId = 0x0010B901,
            RuntimeBaseHeight = 0f
        };
    }

    private static RuntimeTerrainMesh PartialRuntimeMesh()
    {
        var vertices = new List<float>();
        for (var y = 0; y < 12; y++)
        {
            for (var x = 0; x < 24; x++)
            {
                vertices.Add(x * 128f);
                vertices.Add(y * 128f);
                vertices.Add(x + y);
            }
        }

        return new RuntimeTerrainMesh
        {
            Vertices = vertices.ToArray(),
            SourceParentCellFormId = 0x0010B901,
            RuntimeBaseHeight = 0f
        };
    }
}