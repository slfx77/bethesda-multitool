namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;

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
        // Collect submeshes that have bind-pose data and a source NIF path
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
            ClearBindPoseData(submeshes);
            return;
        }

        // Check if there are at least 2 different source NIFs
        var distinctSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sub, _) in candidates)
        {
            distinctSources.Add(sub.SourceNifPath!);
        }

        if (distinctSources.Count < 2)
        {
            ClearBindPoseData(submeshes);
            return;
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

        // For each bucket, find cross-NIF vertex pairs and average their skinned positions
        var stitchedCount = 0;
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

            // Compute average skinned position
            var avgX = 0f;
            var avgY = 0f;
            var avgZ = 0f;
            foreach (var (subIdx, vertIdx) in matchedVertices)
            {
                var positions = submeshes[subIdx].Positions;
                avgX += positions[vertIdx * 3];
                avgY += positions[vertIdx * 3 + 1];
                avgZ += positions[vertIdx * 3 + 2];
            }

            var count = matchedVertices.Count;
            avgX /= count;
            avgY /= count;
            avgZ /= count;

            // Snap all matched vertices to the average
            foreach (var (subIdx, vertIdx) in matchedVertices)
            {
                var positions = submeshes[subIdx].Positions;
                positions[vertIdx * 3] = avgX;
                positions[vertIdx * 3 + 1] = avgY;
                positions[vertIdx * 3 + 2] = avgZ;
            }

            stitchedCount += count;
        }

        if (stitchedCount > 0)
        {
            Log.Debug("Boundary vertex stitcher: snapped {0} vertices across {1} source NIFs",
                stitchedCount, distinctSources.Count);
        }

        ClearBindPoseData(submeshes);
    }

    private static List<(int SubIdx, int VertIdx)> FindMatchingVertices(
        List<(int SubIdx, int VertIdx)> bucket,
        List<RenderableSubmesh> submeshes)
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
        return ((long)(ix & 0x1FFFFF) << 42) |
               ((long)(iy & 0x1FFFFF) << 21) |
               (long)(iz & 0x1FFFFF);
    }

    private static void ClearBindPoseData(List<RenderableSubmesh> submeshes)
    {
        foreach (var sub in submeshes)
        {
            sub.BindPosePositions = null;
        }
    }
}
