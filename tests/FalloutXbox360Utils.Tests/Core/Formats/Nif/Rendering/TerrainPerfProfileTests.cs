using System.Diagnostics;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     v3 Phase 2a perf profiling — opt-in (set <c>PROFILE_TERRAIN=1</c>) timings for the CPU
///     hot paths that run during 3D-view rendering. Run locally to identify the actual
///     bottleneck behind reported stutter; the assertions are advisory ceilings (not strict),
///     since timings vary per machine.
///     <para>
///         Defaults to a silent pass when <c>PROFILE_TERRAIN</c> is unset so CI stays quick.
///         Output goes to xUnit's <see cref="ITestOutputHelper" /> — run with
///         <c>--xunit-info on --show-live-output on</c> (or read the binary log) to see it.
///     </para>
/// </summary>
public sealed class TerrainPerfProfileTests
{
    /// <summary>Approximate exterior-cell count of WastelandNV — drives a realistic mesh-build batch size.</summary>
    private const int WorldspaceCellCountTarget = 4000;

    private readonly Xunit.ITestOutputHelper _output;

    public TerrainPerfProfileTests(Xunit.ITestOutputHelper output) => _output = output;

    [Fact]
    public void Profile_TerrainMeshBuilder_BulkBuildTime()
    {
        if (!IsProfileEnabled())
        {
            _output.WriteLine("Skipped — set PROFILE_TERRAIN=1 to run this profile.");
            return;
        }

        var cells = BuildSyntheticWorldspace(WorldspaceCellCountTarget);

        // Warmup the JIT before timing.
        for (var i = 0; i < 32; i++) TerrainMeshBuilder.Build(cells[i]);

        var sw = Stopwatch.StartNew();
        var totalVertices = 0;
        var built = 0;
        foreach (var cell in cells)
        {
            var mesh = TerrainMeshBuilder.Build(cell);
            if (mesh is not { } m) continue;
            built++;
            totalVertices += m.Vertices.Length;
        }
        sw.Stop();

        var totalMs = sw.Elapsed.TotalMilliseconds;
        var avgUs = totalMs * 1000 / built;
        var perFrameAt60Hz = 16.67 / (totalMs / built);

        _output.WriteLine($"TerrainMeshBuilder.Build over {built} cells:");
        _output.WriteLine($"  total: {totalMs:F1} ms");
        _output.WriteLine($"  avg:   {avgUs:F1} µs/cell");
        _output.WriteLine($"  total verts: {totalVertices:N0}");
        _output.WriteLine($"  at 60 Hz frame budget (16.67 ms), can fit ~{perFrameAt60Hz:F0} CPU mesh-builds per frame");
    }

    [Fact]
    public void Profile_TerrainMeshBuilder_AllocationCost_BuildVsTryBuild()
    {
        if (!IsProfileEnabled())
        {
            _output.WriteLine("Skipped — set PROFILE_TERRAIN=1 to run this profile.");
            return;
        }

        var cells = BuildSyntheticWorldspace(WorldspaceCellCountTarget);

        // Warmup
        for (var i = 0; i < 32; i++) TerrainMeshBuilder.Build(cells[i]);
        var vertScratch = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.GpuMeshUploader.GpuVertex[TerrainMeshBuilder.VertexCount];
        var idxScratch = new ushort[TerrainMeshBuilder.IndexCount];
        for (var i = 0; i < 32; i++) TerrainMeshBuilder.TryBuild(cells[i], vertScratch, idxScratch);

        // Measure Build (allocates per cell)
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        var beforeBuild = GC.GetTotalAllocatedBytes(precise: true);
        foreach (var cell in cells) _ = TerrainMeshBuilder.Build(cell);
        var afterBuild = GC.GetTotalAllocatedBytes(precise: true);
        var buildBytes = afterBuild - beforeBuild;

        // Measure TryBuild (reuses scratch)
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        var beforeTry = GC.GetTotalAllocatedBytes(precise: true);
        foreach (var cell in cells) _ = TerrainMeshBuilder.TryBuild(cell, vertScratch, idxScratch);
        var afterTry = GC.GetTotalAllocatedBytes(precise: true);
        var tryBytes = afterTry - beforeTry;

        _output.WriteLine($"Allocations for {cells.Count} cell builds:");
        _output.WriteLine($"  Build(cell):                       {buildBytes / 1024.0:F1} KB total ({buildBytes / (double)cells.Count:F0} B/cell)");
        _output.WriteLine($"  TryBuild(cell, span, span):        {tryBytes / 1024.0:F1} KB total ({tryBytes / (double)cells.Count:F0} B/cell)");
        _output.WriteLine($"  reduction: {(1.0 - tryBytes / (double)buildBytes) * 100:F1}%");
        _output.WriteLine($"  At 16 builds/frame × 60 Hz:");
        _output.WriteLine($"    Build:    {buildBytes / (double)cells.Count * 16 * 60 / (1024 * 1024):F1} MB/sec");
        _output.WriteLine($"    TryBuild: {tryBytes / (double)cells.Count * 16 * 60 / (1024 * 1024):F1} MB/sec");
    }

    [Fact]
    public void Profile_CellMeshLruCache_BulkInsertAndGet()
    {
        if (!IsProfileEnabled())
        {
            _output.WriteLine("Skipped — set PROFILE_TERRAIN=1 to run this profile.");
            return;
        }

        const int N = WorldspaceCellCountTarget;
        var entries = new TestEntry[N];
        for (var i = 0; i < N; i++) entries[i] = new TestEntry();

        var cache = new CellMeshLruCache<TestEntry>(capacity: N + 256);

        // Warmup
        for (var i = 0; i < 64; i++) cache.Insert((-1, -i), entries[0]);
        cache.Clear();

        var swInsert = Stopwatch.StartNew();
        for (var i = 0; i < N; i++)
        {
            var key = (i % 100, i / 100);
            cache.Insert(key, entries[i]);
        }
        swInsert.Stop();

        var swGet = Stopwatch.StartNew();
        var hits = 0;
        for (var i = 0; i < N; i++)
        {
            var key = (i % 100, i / 100);
            if (cache.TryGet(key, out _)) hits++;
        }
        swGet.Stop();

        _output.WriteLine($"CellMeshLruCache over {N} entries:");
        _output.WriteLine($"  insert: {swInsert.Elapsed.TotalMilliseconds:F1} ms ({swInsert.Elapsed.TotalMilliseconds * 1000 / N:F2} µs/op)");
        _output.WriteLine($"  get:    {swGet.Elapsed.TotalMilliseconds:F1} ms ({swGet.Elapsed.TotalMilliseconds * 1000 / N:F2} µs/op)");
        _output.WriteLine($"  hits:   {hits}/{N}");

        cache.Dispose();
    }

    private static bool IsProfileEnabled() =>
        Environment.GetEnvironmentVariable("PROFILE_TERRAIN") == "1";

    private static List<CellRecord> BuildSyntheticWorldspace(int count)
    {
        // Deterministic per-cell heightmaps that exercise the central-difference normal
        // calculation (non-zero deltas, no clipping).
        var rng = new Random(unchecked((int)0xCAFE_F00D));
        var cells = new List<CellRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var deltas = new sbyte[33 * 33];
            for (var k = 0; k < deltas.Length; k++)
                deltas[k] = (sbyte)(rng.Next(-32, 32));

            cells.Add(new CellRecord
            {
                FormId = (uint)(0x00100000 + i),
                GridX = i % 100,
                GridY = i / 100,
                Heightmap = new LandHeightmap
                {
                    HeightOffset = rng.Next(-50, 100),
                    HeightDeltas = deltas
                }
            });
        }
        return cells;
    }

    private sealed class TestEntry : IDisposable
    {
        public void Dispose() { }
    }
}
