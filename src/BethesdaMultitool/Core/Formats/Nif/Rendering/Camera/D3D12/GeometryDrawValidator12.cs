#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     Draw-record-time validation for the opaque instanced path
///     (FALLOUT_VIEWER_GEOMETRY_VALIDATE): proves, against the CPU-mapped memory the GPU will
///     actually read, that a batch's geometry views still belong to a live arena allocation and
///     that its compacted instance matrices are sane. Violations are logged once per submesh with
///     full lifetime context (and mirrored to the profiler JSONL trace) so a corrupt-draw repro
///     names its mechanism instead of rendering garbage silently.
/// </summary>
internal static unsafe class GeometryDrawValidator12
{
    private const int MaxDetailedEvents = 64;
    private static readonly Logger Log = Logger.Instance;
    private static readonly HashSet<CachedSubmesh12> Reported = new();
    private static int _violations;

    /// <summary>
    ///     Validates one batch right before its geometry bind + draw. Returns false only when a
    ///     violation was found AND FALLOUT_VIEWER_GEOMETRY_VALIDATE_SKIP_DRAW is set (the caller
    ///     then suppresses the cmd.* calls but MUST keep the instance accounting advancing).
    /// </summary>
    public static bool ValidateOpaqueDraw(
        OpaqueBatchState batchState,
        ReferenceMeshCache12 meshCache,
        nint instanceCpuBase,
        uint instanceStart,
        int drawCount,
        int shadowCount,
        int framesSinceBuild,
        int buildEvictionGeneration,
        bool reusedBatches)
    {
        var sub = batchState.Submesh;
        var violation = FindGeometryViolation(sub, meshCache)
                        ?? FindInstanceViolation(instanceCpuBase, instanceStart, drawCount + shadowCount);
        if (violation is null)
        {
            return true;
        }

        Report(batchState, meshCache, violation.Value, drawCount, shadowCount,
            framesSinceBuild, buildEvictionGeneration, reusedBatches);
        return !GeometryArenaDiagnostics.SkipDrawOnViolation;
    }

    private static (string Kind, string Detail)? FindGeometryViolation(
        CachedSubmesh12 sub, ReferenceMeshCache12 meshCache)
    {
        if (sub.OwnerMesh is not { } owner)
        {
            return null; // Built outside the mesh cache (tests) — nothing to validate against.
        }

        if (owner.IsDisposed)
        {
            return ("disposed-owner",
                "batch drew a submesh of an evicted mesh (arena range queued/handed for reuse)");
        }

        var arena = meshCache.GeometryArenaForDiagnostics;
        var liveness = arena.QueryLiveness(owner.Geometry);
        if (liveness is ArenaLiveness.NotLive or ArenaLiveness.Recycled)
        {
            return (liveness == ArenaLiveness.Recycled ? "recycled-range" : "freed-range",
                $"arena reports the mesh's allocation is {liveness} while its owner is not disposed");
        }

        // Ring-backed views (live particles / CPU-skinned pose) legitimately live outside the
        // arena; the static-view window checks below only apply to the plain arena-backed case.
        if (sub.LiveParticles is null && sub.AnimatedVertexBufferView is null)
        {
            var g = owner.Geometry;
            var vbv = sub.VertexBufferView;
            var ibv = sub.IndexBufferView;
            if (vbv.BufferLocation < g.VertexBufferLocation ||
                vbv.BufferLocation + vbv.SizeInBytes > g.VertexBufferLocation + g.VertexBytes)
            {
                return ("view-out-of-range",
                    $"VBV [0x{vbv.BufferLocation:X}, +{vbv.SizeInBytes}) outside allocation " +
                    $"[0x{g.VertexBufferLocation:X}, +{g.VertexBytes})");
            }

            if (ibv.BufferLocation < g.IndexBufferLocation ||
                ibv.BufferLocation + ibv.SizeInBytes > g.IndexBufferLocation + g.IndexBytes)
            {
                return ("view-out-of-range",
                    $"IBV [0x{ibv.BufferLocation:X}, +{ibv.SizeInBytes}) outside allocation " +
                    $"[0x{g.IndexBufferLocation:X}, +{g.IndexBytes})");
            }

            if ((uint)sub.IndexCount * sizeof(ushort) > ibv.SizeInBytes)
            {
                return ("view-out-of-range",
                    $"IndexCount {sub.IndexCount} overruns the IBV window ({ibv.SizeInBytes} B)");
            }

            if (GeometryArenaDiagnostics.ValidateLevel >= 2 && sub.DebugVertexHash != 0)
            {
                if (arena.TryHashRange(g, vbv.BufferLocation, vbv.SizeInBytes, out var vertexHash) &&
                    vertexHash != sub.DebugVertexHash)
                {
                    return ("bytes-overwritten",
                        $"vertex window hash {vertexHash:X16} != upload-time {sub.DebugVertexHash:X16}");
                }

                if (arena.TryHashRange(g, ibv.BufferLocation, ibv.SizeInBytes, out var indexHash) &&
                    indexHash != sub.DebugIndexHash)
                {
                    return ("bytes-overwritten",
                        $"index window hash {indexHash:X16} != upload-time {sub.DebugIndexHash:X16}");
                }
            }
        }

        return null;
    }

    private static (string Kind, string Detail)? FindInstanceViolation(
        nint instanceCpuBase, uint instanceStart, int instanceCount)
    {
        if (instanceCpuBase == 0 || instanceCount <= 0)
        {
            return null;
        }

        var span = new ReadOnlySpan<Matrix4x4>(
            (void*)(instanceCpuBase + (nint)instanceStart * sizeof(Matrix4x4)), instanceCount);
        for (var i = 0; i < span.Length; i++)
        {
            var m = span[i];
            if (!IsFinite(m))
            {
                return ("instance-matrix", $"instance {i}/{instanceCount}: non-finite world matrix {m}");
            }

            var xLen = new Vector3(m.M11, m.M12, m.M13).Length();
            var yLen = new Vector3(m.M21, m.M22, m.M23).Length();
            var zLen = new Vector3(m.M31, m.M32, m.M33).Length();
            const float minAxis = 1e-4f;
            const float maxAxis = 1e4f;
            if (xLen < minAxis || xLen > maxAxis ||
                yLen < minAxis || yLen > maxAxis ||
                zLen < minAxis || zLen > maxAxis)
            {
                return ("instance-matrix",
                    $"instance {i}/{instanceCount}: basis magnitudes ({xLen:G4}, {yLen:G4}, {zLen:G4}) " +
                    $"outside [{minAxis}, {maxAxis}] — world {m}");
            }
        }

        return null;
    }

    private static void Report(
        OpaqueBatchState batchState,
        ReferenceMeshCache12 meshCache,
        (string Kind, string Detail) violation,
        int drawCount,
        int shadowCount,
        int framesSinceBuild,
        int buildEvictionGeneration,
        bool reusedBatches)
    {
        _violations++;
        var sub = batchState.Submesh;
        if (!Reported.Add(sub) || Reported.Count > MaxDetailedEvents)
        {
            return;
        }

        var owner = sub.OwnerMesh;
        var allocation = owner?.Geometry.Allocation;
        Log.Error(
            "GeometryDrawValidator12: {0} — {1} (mesh '{2}', tallGrass={3}, draw={4}+{5}, " +
            "framesSinceBuild={6}, evictionGen build={7} now={8}, reused={9}, violations={10})",
            violation.Kind, violation.Detail, owner?.SourcePath ?? "<uncached>",
            sub.IsTallGrass, drawCount, shadowCount, framesSinceBuild,
            buildEvictionGeneration, meshCache.EvictionGeneration, reusedBatches, _violations);
        RendererProfilerTrace.Event("geometry-violation", new Dictionary<string, object?>
        {
            ["kind"] = violation.Kind,
            ["detail"] = violation.Detail,
            ["path"] = owner?.SourcePath,
            ["allocId"] = allocation?.AllocationId,
            ["block"] = allocation?.BlockIndex,
            ["offset"] = allocation?.Offset,
            ["isTallGrass"] = sub.IsTallGrass,
            ["grassWaveMultiplier"] = batchState.GrassWaveMultiplier,
            ["drawCount"] = drawCount,
            ["shadowCount"] = shadowCount,
            ["batchLastTouchedFrame"] = batchState.LastTouchedFrame,
            ["framesSinceBuild"] = framesSinceBuild,
            ["buildEvictionGen"] = buildEvictionGeneration,
            ["currentEvictionGen"] = meshCache.EvictionGeneration,
            ["reusedBatches"] = reusedBatches,
            ["skipped"] = GeometryArenaDiagnostics.SkipDrawOnViolation,
            ["violationsSoFar"] = _violations,
        });
    }

    private static bool IsFinite(in Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
#endif
