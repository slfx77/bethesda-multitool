using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Guards the load-bearing A3 (terrain async build) correctness constraint: the per-cell vertex
///     build <see cref="TerrainMeshBuilder.TryBuildVertices(CellRecord, System.Span{GpuMeshUploader.GpuVertex})" />
///     is pure and safe to run concurrently
///     as long as each task owns its OWN vertex array. The async build path in TerrainRenderer12
///     depends on this — sharing one scratch array across background tasks would corrupt meshes
///     non-deterministically. We build a batch of distinct cells single-threaded (reference), then
///     rebuild them concurrently into per-task arrays and assert byte-identical results. A hidden
///     shared buffer inside the build would surface here as a cross-cell diff.
/// </summary>
public sealed class TerrainConcurrentBuildTests
{
    private static CellRecord MakeCell(int gx, int gy)
    {
        // Unique-ish height field per cell so an aliasing bug shows up as a vertex diff.
        var exact = new float[33, 33];
        for (var j = 0; j < 33; j++)
        {
            for (var i = 0; i < 33; i++)
            {
                exact[j, i] = (gx * 31 + gy * 7 + i * 3 + j) % 17;
            }
        }

        return new CellRecord
        {
            FormId = (uint)(0x1000 + gx * 100 + gy),
            GridX = gx,
            GridY = gy,
            Heightmap = new LandHeightmap
            {
                HeightOffset = 0f,
                HeightDeltas = new sbyte[33 * 33],
                ExactHeights = exact
            }
        };
    }

    [Fact]
    public void TryBuildVertices_ConcurrentPerTaskArrays_MatchSingleThreadedBuild()
    {
        const int cellCount = 64;
        var cells = new List<CellRecord>(cellCount);
        for (var n = 0; n < cellCount; n++)
        {
            cells.Add(MakeCell(n % 8, n / 8));
        }

        // Reference: build each cell single-threaded into its own array.
        var reference = new GpuMeshUploader.GpuVertex[cellCount][];
        for (var n = 0; n < cellCount; n++)
        {
            var arr = new GpuMeshUploader.GpuVertex[TerrainMeshBuilder.VertexCount];
            Assert.True(TerrainMeshBuilder.TryBuildVertices(cells[n], arr, null));
            reference[n] = arr;
        }

        // Concurrent: rebuild all cells in parallel, each task with its OWN array (the async path's
        // invariant). Several rounds widen the race window.
        for (var round = 0; round < 8; round++)
        {
            var concurrent = new GpuMeshUploader.GpuVertex[cellCount][];
            Parallel.For(0, cellCount, n =>
            {
                var arr = new GpuMeshUploader.GpuVertex[TerrainMeshBuilder.VertexCount];
                Assert.True(TerrainMeshBuilder.TryBuildVertices(cells[n], arr, null));
                concurrent[n] = arr;
            });

            for (var n = 0; n < cellCount; n++)
            {
                Assert.Equal(reference[n].Length, concurrent[n].Length);
                for (var v = 0; v < reference[n].Length; v++)
                {
                    Assert.Equal(reference[n][v].Position, concurrent[n][v].Position);
                    Assert.Equal(reference[n][v].Normal, concurrent[n][v].Normal);
                    Assert.Equal(reference[n][v].VertexColor, concurrent[n][v].VertexColor);
                }
            }
        }
    }
}