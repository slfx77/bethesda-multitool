using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     v3 Phase 2a — verifies <see cref="TerrainMeshBuilder" /> produces the expected mesh
///     topology, vertex positions, and normals for synthetic heightmaps. Pure CPU, no GPU,
///     no Bucket B real-data load.
/// </summary>
public sealed class TerrainMeshBuilderTests
{
    [Fact]
    public void Build_FlatHeightmap_ProducesExpectedTopologyAndPositions()
    {
        var cell = new CellRecord
        {
            FormId = 0x12345678,
            GridX = 5,
            GridY = 7,
            Heightmap = new LandHeightmap
            {
                HeightOffset = 100f,             // rowStart = 800 (HeightOffset * 8)
                HeightDeltas = new sbyte[33 * 33] // all zeros → every vertex Z = 800
            }
        };

        var mesh = TerrainMeshBuilder.Build(cell);

        Assert.NotNull(mesh);
        Assert.Equal(1089, mesh.Value.Vertices.Length); // 33×33
        Assert.Equal(6144, mesh.Value.Indices.Length);  // 32×32 quads × 6 indices

        var verts = mesh.Value.Vertices;

        // (0, 0) → cell origin at world (gx*4096, gy*4096) at Z = HeightOffset*8.
        Assert.Equal(new Vector3(5 * 4096f, 7 * 4096f, 800f), verts[0].Position);

        // (32, 32) → opposite corner at world ((gx+1)*4096, (gy+1)*4096).
        var lastIdx = 33 * 33 - 1;
        Assert.Equal(new Vector3(6 * 4096f, 8 * 4096f, 800f), verts[lastIdx].Position);

        // Mid-edge sanity: (16, 0) → +x midpoint of south edge.
        Assert.Equal(new Vector3(5 * 4096f + 16 * 128f, 7 * 4096f, 800f), verts[16].Position);

        // All normals point ≈ +Z on a flat surface.
        foreach (var v in verts)
        {
            Assert.Equal(0f, v.Normal.X, 5);
            Assert.Equal(0f, v.Normal.Y, 5);
            Assert.Equal(1f, v.Normal.Z, 5);
        }

        // Default vertex color is white when LandVisualData is null.
        Assert.Equal(Vector4.One, verts[0].VertexColor);

        // UV spans 0..1 across the 33-wide grid.
        Assert.Equal(0f, verts[0].TexCoord.X, 5);
        Assert.Equal(0f, verts[0].TexCoord.Y, 5);
        Assert.Equal(1f, verts[lastIdx].TexCoord.X, 5);
        Assert.Equal(1f, verts[lastIdx].TexCoord.Y, 5);
    }

    [Fact]
    public void Build_SingleSpike_TiltsNeighborNormalsAwayFromBump()
    {
        // ExactHeights overrides the cumulative-delta decoder, so we can drop one isolated
        // spike at (10, 10) and leave the rest of the grid flat at z=0.
        var exact = new float[33, 33];
        exact[10, 10] = 100f;

        var cell = new CellRecord
        {
            FormId = 0x1,
            GridX = 0,
            GridY = 0,
            Heightmap = new LandHeightmap
            {
                HeightOffset = 0f,
                HeightDeltas = new sbyte[33 * 33],
                ExactHeights = exact
            }
        };

        var mesh = TerrainMeshBuilder.Build(cell);
        Assert.NotNull(mesh);
        var verts = mesh.Value.Vertices;

        // Spike vertex itself: z = 100, but its own normal stays +Z (symmetric tent — neighbors slope down equally on both sides).
        var spike = verts[10 * 33 + 10];
        Assert.Equal(100f, spike.Position.Z, 5);
        Assert.Equal(0f, spike.Normal.X, 5);
        Assert.Equal(0f, spike.Normal.Y, 5);
        Assert.Equal(1f, spike.Normal.Z, 5);

        // Neighbor west of the spike (9, 10): height rises to the east → normal tilts west (-X).
        var west = verts[10 * 33 + 9];
        Assert.True(west.Normal.X < -0.1f, $"expected -X normal, got {west.Normal.X}");

        // Neighbor east (11, 10): height rises to the west → normal tilts east (+X).
        var east = verts[10 * 33 + 11];
        Assert.True(east.Normal.X > 0.1f, $"expected +X normal, got {east.Normal.X}");

        // Neighbor south (10, 9): height rises to the north → normal tilts south (-Y).
        var south = verts[9 * 33 + 10];
        Assert.True(south.Normal.Y < -0.1f, $"expected -Y normal, got {south.Normal.Y}");

        // Neighbor north (10, 11): height rises to the south → normal tilts north (+Y).
        var north = verts[11 * 33 + 10];
        Assert.True(north.Normal.Y > 0.1f, $"expected +Y normal, got {north.Normal.Y}");

        // Vertex away from the spike is unaffected.
        var faraway = verts[0];
        Assert.Equal(0f, faraway.Position.Z, 5);
        Assert.Equal(Vector3.UnitZ, faraway.Normal);
    }

    [Fact]
    public void Build_ConsumesVertexColorsFromLandVisualData()
    {
        var colors = new byte[33 * 33 * 3];
        colors[0] = 255; // R
        colors[1] = 128; // G
        colors[2] = 64;  // B

        var cell = new CellRecord
        {
            FormId = 0x1,
            GridX = 0,
            GridY = 0,
            Heightmap = new LandHeightmap
            {
                HeightOffset = 0f,
                HeightDeltas = new sbyte[33 * 33]
            },
            LandVisualData = new LandVisualData { VertexColors = colors }
        };

        var mesh = TerrainMeshBuilder.Build(cell);
        Assert.NotNull(mesh);
        var v0 = mesh.Value.Vertices[0];

        Assert.Equal(1f, v0.VertexColor.X, 4);
        Assert.Equal(128f / 255f, v0.VertexColor.Y, 4);
        Assert.Equal(64f / 255f, v0.VertexColor.Z, 4);
        Assert.Equal(1f, v0.VertexColor.W);
    }

    [Fact]
    public void Build_ReturnsNullWhenCellHasNoGridCoordinates()
    {
        var cell = new CellRecord
        {
            FormId = 0x1,
            // Interior cell: GridX / GridY are null
            Heightmap = new LandHeightmap
            {
                HeightOffset = 0f,
                HeightDeltas = new sbyte[33 * 33]
            }
        };

        Assert.Null(TerrainMeshBuilder.Build(cell));
    }

    [Fact]
    public void Build_ReturnsNullWhenNoHeightSource()
    {
        var cell = new CellRecord
        {
            FormId = 0x1,
            GridX = 0,
            GridY = 0
            // No Heightmap, no RuntimeTerrainMesh
        };

        Assert.Null(TerrainMeshBuilder.Build(cell));
    }

    [Fact]
    public void Build_IndicesHaveCcwWindingFromAbove()
    {
        // A flat heightmap with one elevated corner — verify the two triangles per quad
        // both wind CCW when viewed from +Z. We pick quad (0, 0): corners v00=(0,0),
        // v10=(1,0), v01=(0,1), v11=(1,1). CCW pairing from +Z:
        //   tri 1 = (v00, v10, v01) — cross (east × NE) = +Z
        //   tri 2 = (v01, v10, v11) — cross (SE × north) = +Z
        var cell = new CellRecord
        {
            FormId = 0x1,
            GridX = 0,
            GridY = 0,
            Heightmap = new LandHeightmap
            {
                HeightOffset = 0f,
                HeightDeltas = new sbyte[33 * 33]
            }
        };

        var mesh = TerrainMeshBuilder.Build(cell);
        Assert.NotNull(mesh);
        var indices = mesh.Value.Indices;
        var verts = mesh.Value.Vertices;

        // Triangle 1 of quad (0, 0): indices[0..2].
        var t1 = TriangleNormal(verts[indices[0]].Position, verts[indices[1]].Position, verts[indices[2]].Position);
        Assert.True(t1.Z > 0, $"triangle 1 should face +Z, got {t1}");

        // Triangle 2 of quad (0, 0): indices[3..5].
        var t2 = TriangleNormal(verts[indices[3]].Position, verts[indices[4]].Position, verts[indices[5]].Position);
        Assert.True(t2.Z > 0, $"triangle 2 should face +Z, got {t2}");
    }

    [Fact]
    public void Build_SplitsIndicesIntoFourQuadrantRanges()
    {
        // v3 Phase 2b — the 6144-index buffer is laid out as 4 contiguous quadrant ranges of
        // 1536 indices each, in order 0=SW, 1=SE, 2=NW, 3=NE. Each range may only reference
        // vertices in its 17×17 sub-grid (including the shared center cross).
        var cell = new CellRecord
        {
            FormId = 0x1,
            GridX = 0,
            GridY = 0,
            Heightmap = new LandHeightmap
            {
                HeightOffset = 0f,
                HeightDeltas = new sbyte[33 * 33]
            }
        };

        var mesh = TerrainMeshBuilder.Build(cell);
        Assert.NotNull(mesh);
        var indices = mesh.Value.Indices;

        Assert.Equal(1536, TerrainMeshBuilder.IndicesPerQuadrant);

        for (var q = 0; q < 4; q++)
        {
            var iMin = (q & 1) == 0 ? 0 : 16;
            var jMin = (q & 2) == 0 ? 0 : 16;
            var start = q * TerrainMeshBuilder.IndicesPerQuadrant;
            var end = start + TerrainMeshBuilder.IndicesPerQuadrant;
            for (var k = start; k < end; k++)
            {
                var v = indices[k];
                var vi = v % 33;
                var vj = v / 33;
                Assert.InRange(vi, iMin, iMin + 16);
                Assert.InRange(vj, jMin, jMin + 16);
            }
        }
    }

    [Fact]
    public void BuildSharedIndexBufferData_MatchesPerCellMeshIndices()
    {
        var cell = new CellRecord
        {
            FormId = 0x1,
            GridX = 0,
            GridY = 0,
            Heightmap = new LandHeightmap
            {
                HeightOffset = 0f,
                HeightDeltas = new sbyte[33 * 33]
            }
        };

        var mesh = TerrainMeshBuilder.Build(cell);
        var shared = TerrainMeshBuilder.BuildSharedIndexBufferData();

        Assert.NotNull(mesh);
        Assert.Equal(mesh.Value.Indices, shared);
    }

    private static Vector3 TriangleNormal(Vector3 a, Vector3 b, Vector3 c)
    {
        return Vector3.Cross(b - a, c - a);
    }
}
