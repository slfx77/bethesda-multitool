using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;

/// <summary>
///     Identifies vertices that occupy the same bind-pose position across different source NIFs
///     (e.g., outfit sleeve vs hand mesh at the wrist) and snaps their post-skinning positions
///     to a shared average, eliminating visible seams caused by differing bone weight authoring.
/// </summary>
internal static class NpcBoundaryVertexStitcher
{
    /// <summary>
    ///     Grid cell size for spatial hashing of bind-pose positions.
    ///     Wrist boundary vertices should be at effectively identical positions (sub-0.001 unit),
    ///     so 0.05 units gives generous matching while avoiding false positives.
    /// </summary>
    private const float CellSize = 0.05f;

    private const float MatchThreshold = 0.01f;
    private const float MatchThresholdSq = MatchThreshold * MatchThreshold;
    private static readonly Logger Log = Logger.Instance;

    internal static void StitchBoundaryVertices(List<RenderableSubmesh> submeshes)
    {
        var groups = DiscoverBoundaryVertexGroups(submeshes);
        var stitchedCount = ApplyBoundaryVertexGroups(submeshes, groups);

        if (stitchedCount > 0)
        {
            var distinctSources = CollectDistinctSources(submeshes);
            Log.Debug("Boundary vertex stitcher: snapped {0} vertices across {1} source NIFs",
                stitchedCount, distinctSources.Count);
        }

        ClearBindPoseData(submeshes);
    }

    /// <summary>
    ///     Finds bind-pose boundary matches without changing positions or consuming bind-pose data.
    ///     Returned indices address the input list and remain valid while its mesh/vertex ordering is
    ///     unchanged, so the same groups can be applied after every animated skinning update.
    /// </summary>
    internal static IReadOnlyList<BethesdaViewerBoundaryStitchGroup> DiscoverBoundaryVertexGroups(
        IReadOnlyList<RenderableSubmesh> submeshes)
    {
        ArgumentNullException.ThrowIfNull(submeshes);

        // Collect submeshes that have bind-pose data and a source NIF path.
        var candidates = new List<(RenderableSubmesh Sub, int Index)>();
        for (var i = 0; i < submeshes.Count; i++)
        {
            var sub = submeshes[i];
            if (sub.BindPosePositions != null && sub.SourceNifPath != null)
            {
                candidates.Add((sub, i));
            }
        }

        if (candidates.Count < 2)
        {
            return [];
        }

        // Check if there are at least 2 different source NIFs
        var distinctSources = CollectDistinctSources(submeshes);
        if (distinctSources.Count < 2)
        {
            return [];
        }

        // Build spatial hash: quantized bind-pose position → list of (submeshIndex, vertexIndex)
        var spatialHash = new Dictionary<long, List<(int SubIdx, int VertIdx)>>();

        foreach (var (sub, subIdx) in candidates)
        {
            var bindPositions = sub.BindPosePositions!;
            var vertCount = bindPositions.Length / 3;
            for (var v = 0; v < vertCount; v++)
            {
                var key = HashPosition(
                    bindPositions[v * 3],
                    bindPositions[v * 3 + 1],
                    bindPositions[v * 3 + 2]);

                if (!spatialHash.TryGetValue(key, out var bucket))
                {
                    bucket = new List<(int, int)>(2);
                    spatialHash[key] = bucket;
                }

                bucket.Add((subIdx, v));
            }
        }

        // For each bucket, retain the stable indices of cross-NIF bind-pose matches.
        var stitchGroups = new List<BethesdaViewerBoundaryStitchGroup>();
        foreach (var bucket in spatialHash.Values)
        {
            if (bucket.Count < 2)
            {
                continue;
            }

            // Group by source NIF
            var groups = new Dictionary<string, List<(int SubIdx, int VertIdx)>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (subIdx, vertIdx) in bucket)
            {
                var source = submeshes[subIdx].SourceNifPath!;
                if (!groups.TryGetValue(source, out var group))
                {
                    group = new List<(int, int)>(2);
                    groups[source] = group;
                }

                group.Add((subIdx, vertIdx));
            }

            if (groups.Count < 2)
            {
                continue;
            }

            // Collect all vertices in this bucket that actually match in bind-pose space
            var matchedVertices = FindMatchingVertices(bucket, submeshes);
            if (matchedVertices.Count < 2)
            {
                continue;
            }

            // Verify they span multiple source NIFs
            var matchedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (subIdx, _) in matchedVertices)
            {
                matchedSources.Add(submeshes[subIdx].SourceNifPath!);
            }

            if (matchedSources.Count < 2)
            {
                continue;
            }

            stitchGroups.Add(new BethesdaViewerBoundaryStitchGroup
            {
                Vertices = matchedVertices
                    .Select(static vertex => new BethesdaViewerMeshVertexIndex(
                        vertex.SubIdx,
                        vertex.VertIdx))
                    .ToArray()
            });
        }

        return stitchGroups;
    }

    /// <summary>
    ///     Replaces each discovered group's current (normally post-skinning) positions with their
    ///     shared average. Bind-pose arrays remain intact so callers may invoke this every frame.
    /// </summary>
    internal static int ApplyBoundaryVertexGroups(
        IReadOnlyList<RenderableSubmesh> submeshes,
        IReadOnlyList<BethesdaViewerBoundaryStitchGroup> stitchGroups)
    {
        ArgumentNullException.ThrowIfNull(submeshes);
        ArgumentNullException.ThrowIfNull(stitchGroups);

        var stitchedCount = 0;
        foreach (var stitchGroup in stitchGroups)
        {
            if (stitchGroup.Vertices.Length < 2)
            {
                continue;
            }

            // Compute average skinned position.
            var avgX = 0f;
            var avgY = 0f;
            var avgZ = 0f;
            foreach (var vertex in stitchGroup.Vertices)
            {
                var positions = submeshes[vertex.MeshPartIndex].Positions;
                avgX += positions[vertex.VertexIndex * 3];
                avgY += positions[vertex.VertexIndex * 3 + 1];
                avgZ += positions[vertex.VertexIndex * 3 + 2];
            }

            var count = stitchGroup.Vertices.Length;
            avgX /= count;
            avgY /= count;
            avgZ /= count;

            // Snap all matched vertices to the average.
            foreach (var vertex in stitchGroup.Vertices)
            {
                var positions = submeshes[vertex.MeshPartIndex].Positions;
                positions[vertex.VertexIndex * 3] = avgX;
                positions[vertex.VertexIndex * 3 + 1] = avgY;
                positions[vertex.VertexIndex * 3 + 2] = avgZ;
            }

            stitchedCount += count;
        }

        return stitchedCount;
    }

    /// <summary>
    ///     Rebuilds a finalized viewer scene's animation-ready seam groups from its mesh-part order.
    ///     Call this after all NPC/creature mesh parts have been composed and before returning the
    ///     scene to the native viewer.
    /// </summary>
    internal static void PopulateViewerSceneBoundaryGroups(BethesdaViewerScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var submeshes = new RenderableSubmesh[scene.MeshParts.Count];
        for (var index = 0; index < scene.MeshParts.Count; index++)
        {
            submeshes[index] = scene.MeshParts[index].Submesh;
        }

        scene.BoundaryStitchGroups.Clear();
        scene.BoundaryStitchGroups.AddRange(DiscoverBoundaryVertexGroups(submeshes));
    }

    private static List<(int SubIdx, int VertIdx)> FindMatchingVertices(
        List<(int SubIdx, int VertIdx)> bucket,
        IReadOnlyList<RenderableSubmesh> submeshes)
    {
        // Use the first vertex as reference; include all others within threshold
        var (refSubIdx, refVertIdx) = bucket[0];
        var refBind = submeshes[refSubIdx].BindPosePositions!;
        var refX = refBind[refVertIdx * 3];
        var refY = refBind[refVertIdx * 3 + 1];
        var refZ = refBind[refVertIdx * 3 + 2];

        var matched = new List<(int SubIdx, int VertIdx)>(bucket.Count) { (refSubIdx, refVertIdx) };

        for (var i = 1; i < bucket.Count; i++)
        {
            var (subIdx, vertIdx) = bucket[i];
            var bind = submeshes[subIdx].BindPosePositions!;
            var dx = bind[vertIdx * 3] - refX;
            var dy = bind[vertIdx * 3 + 1] - refY;
            var dz = bind[vertIdx * 3 + 2] - refZ;

            if (dx * dx + dy * dy + dz * dz <= MatchThresholdSq)
            {
                matched.Add((subIdx, vertIdx));
            }
        }

        return matched;
    }

    private static long HashPosition(float x, float y, float z)
    {
        var ix = (int)MathF.Floor(x / CellSize);
        var iy = (int)MathF.Floor(y / CellSize);
        var iz = (int)MathF.Floor(z / CellSize);
        // Pack three 21-bit integers into a 64-bit key
        // Each component is cast after masking: without it the third operand stays int and
        // sign-extends into the upper 43 bits, colliding cells whose iz is negative.
        return ((long)(ix & 0x1FFFFF) << 42) |
               ((long)(iy & 0x1FFFFF) << 21) |
               (long)(iz & 0x1FFFFF);
    }

    private static HashSet<string> CollectDistinctSources(
        IReadOnlyList<RenderableSubmesh> submeshes)
    {
        var distinctSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < submeshes.Count; index++)
        {
            var submesh = submeshes[index];
            if (submesh.BindPosePositions != null && submesh.SourceNifPath != null)
            {
                distinctSources.Add(submesh.SourceNifPath);
            }
        }

        return distinctSources;
    }

    private static void ClearBindPoseData(List<RenderableSubmesh> submeshes)
    {
        foreach (var sub in submeshes)
        {
            sub.BindPosePositions = null;
        }
    }
}
