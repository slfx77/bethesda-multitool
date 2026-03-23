namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Applies EGM FaceGen morph deltas to NIF vertex positions.
///     Uses min(EGM vertex count, NIF vertex count) per engine decompilation
///     of BSFaceGenMorphDifferential::ApplyMorph.
/// </summary>
internal static class FaceGenMeshMorpher
{
    /// <summary>
    ///     Applies symmetric and asymmetric EGM morphs to the largest submesh
    ///     in a renderable model, using the provided coefficients.
    ///     Positions must be in the same coordinate space as the EGM deltas (bind-pose / NIF local).
    /// </summary>
    public static void Apply(
        NifRenderableModel model,
        EgmParser egm,
        float[]? symmetricCoeffs,
        float[]? asymmetricCoeffs)
    {
        var submesh = FindLargestSubmesh(model);
        if (submesh == null)
            return;

        var vertexCount = Math.Min(submesh.VertexCount, egm.VertexCount);
        AccumulateDeltas(submesh.Positions, egm, symmetricCoeffs, asymmetricCoeffs, vertexCount);
        RecalculateNormals(submesh);
        RecalculateBounds(model);
    }

    /// <summary>
    ///     Computes accumulated EGM morph deltas as a flat float[] (dx,dy,dz per vertex)
    ///     without applying to any model. Used for pre-skinning morph injection into the
    ///     extraction pipeline, ensuring deltas are applied in bind-pose space before
    ///     bone transforms rotate the vertices.
    /// </summary>
    public static float[]? ComputeAccumulatedDeltas(
        EgmParser egm,
        float[]? symmetricCoeffs,
        float[]? asymmetricCoeffs,
        int nifVertexCount)
    {
        var vertexCount = Math.Min(nifVertexCount, egm.VertexCount);
        if (vertexCount <= 0)
            return null;

        var deltas = new float[vertexCount * 3];
        AccumulateDeltas(deltas, egm, symmetricCoeffs, asymmetricCoeffs, vertexCount);

        // Check if any delta is non-zero
        for (var i = 0; i < deltas.Length; i++)
        {
            if (MathF.Abs(deltas[i]) > 1e-9f)
                return deltas;
        }

        return null;
    }

    private static void AccumulateDeltas(
        float[] target,
        EgmParser egm,
        float[]? symmetricCoeffs,
        float[]? asymmetricCoeffs,
        int vertexCount)
    {
        if (symmetricCoeffs != null)
        {
            var count = Math.Min(symmetricCoeffs.Length, egm.SymmetricMorphs.Length);
            for (var m = 0; m < count; m++)
            {
                var coeff = symmetricCoeffs[m];
                if (MathF.Abs(coeff) < 1e-7f)
                    continue;

                AddScaledDeltas(target, egm.SymmetricMorphs[m], coeff, vertexCount);
            }
        }

        if (asymmetricCoeffs != null)
        {
            var count = Math.Min(asymmetricCoeffs.Length, egm.AsymmetricMorphs.Length);
            for (var m = 0; m < count; m++)
            {
                var coeff = asymmetricCoeffs[m];
                if (MathF.Abs(coeff) < 1e-7f)
                    continue;

                AddScaledDeltas(target, egm.AsymmetricMorphs[m], coeff, vertexCount);
            }
        }
    }

    private static void AddScaledDeltas(float[] target, EgmMorph morph, float coefficient, int vertexCount)
    {
        var scale = morph.Scale * coefficient;
        var deltas = morph.Deltas;

        for (var v = 0; v < vertexCount; v++)
        {
            var pi = v * 3;
            target[pi] += deltas[pi] * scale;
            target[pi + 1] += deltas[pi + 1] * scale;
            target[pi + 2] += deltas[pi + 2] * scale;
        }
    }

    private static RenderableSubmesh? FindLargestSubmesh(NifRenderableModel model)
    {
        RenderableSubmesh? largest = null;
        var maxVerts = 0;

        foreach (var sub in model.Submeshes)
        {
            if (sub.VertexCount > maxVerts)
            {
                maxVerts = sub.VertexCount;
                largest = sub;
            }
        }

        return largest;
    }

    /// <summary>
    ///     Recalculates smooth vertex normals from the morphed triangle geometry.
    ///     Uses area-weighted face normal accumulation (cross product magnitude = 2x triangle area).
    /// </summary>
    internal static void RecalculateNormals(RenderableSubmesh submesh)
    {
        var normals = submesh.Normals;
        if (normals == null)
            return;

        var positions = submesh.Positions;
        var triangles = submesh.Triangles;

        Array.Clear(normals);

        for (var t = 0; t < triangles.Length; t += 3)
        {
            var i0 = triangles[t] * 3;
            var i1 = triangles[t + 1] * 3;
            var i2 = triangles[t + 2] * 3;

            var e1x = positions[i1] - positions[i0];
            var e1y = positions[i1 + 1] - positions[i0 + 1];
            var e1z = positions[i1 + 2] - positions[i0 + 2];
            var e2x = positions[i2] - positions[i0];
            var e2y = positions[i2 + 1] - positions[i0 + 1];
            var e2z = positions[i2 + 2] - positions[i0 + 2];

            var nx = e1y * e2z - e1z * e2y;
            var ny = e1z * e2x - e1x * e2z;
            var nz = e1x * e2y - e1y * e2x;

            normals[i0] += nx;
            normals[i0 + 1] += ny;
            normals[i0 + 2] += nz;
            normals[i1] += nx;
            normals[i1 + 1] += ny;
            normals[i1 + 2] += nz;
            normals[i2] += nx;
            normals[i2 + 1] += ny;
            normals[i2 + 2] += nz;
        }

        for (var v = 0; v < normals.Length; v += 3)
        {
            var nx = normals[v];
            var ny = normals[v + 1];
            var nz = normals[v + 2];
            var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-7f)
            {
                normals[v] = nx / len;
                normals[v + 1] = ny / len;
                normals[v + 2] = nz / len;
            }
        }

        WeldSeamNormals(positions, normals);
    }

    /// <summary>
    ///     Averages normals of vertices that share the same position (within epsilon).
    ///     EGM morphing breaks seam-vertex normal sharing, causing visible hard edges
    ///     at mesh boundaries (neck, ears). This restores smooth normals at those seams.
    /// </summary>
    private static void WeldSeamNormals(float[] positions, float[] normals)
    {
        const float epsilon = 0.001f;
        const float epsilonSq = epsilon * epsilon;

        var vertexCount = positions.Length / 3;
        if (vertexCount <= 1)
        {
            return;
        }

        // Build spatial buckets keyed by quantized position for O(n) average-case lookup.
        // Bucket size is larger than epsilon so co-located vertices land in the same or
        // adjacent buckets.
        const float bucketSize = 0.01f;
        var buckets = new Dictionary<(int, int, int), List<int>>();

        for (var i = 0; i < vertexCount; i++)
        {
            var bx = (int)MathF.Floor(positions[i * 3] / bucketSize);
            var by = (int)MathF.Floor(positions[i * 3 + 1] / bucketSize);
            var bz = (int)MathF.Floor(positions[i * 3 + 2] / bucketSize);
            var key = (bx, by, bz);
            if (!buckets.TryGetValue(key, out var list))
            {
                list = [];
                buckets[key] = list;
            }

            list.Add(i);
        }

        // For each vertex, check its bucket and the 26 neighbors for co-located vertices.
        var welded = new bool[vertexCount];
        foreach (var (bucketKey, indices) in buckets)
        {
            for (var ai = 0; ai < indices.Count; ai++)
            {
                var a = indices[ai];
                if (welded[a])
                {
                    continue;
                }

                // Collect all vertices at the same position (including from neighbor buckets).
                var ax = positions[a * 3];
                var ay = positions[a * 3 + 1];
                var az = positions[a * 3 + 2];

                var sumNx = normals[a * 3];
                var sumNy = normals[a * 3 + 1];
                var sumNz = normals[a * 3 + 2];
                var count = 1;

                // Check same bucket (remaining vertices after a).
                for (var bi = ai + 1; bi < indices.Count; bi++)
                {
                    var b = indices[bi];
                    if (welded[b])
                    {
                        continue;
                    }

                    var dx = positions[b * 3] - ax;
                    var dy = positions[b * 3 + 1] - ay;
                    var dz = positions[b * 3 + 2] - az;
                    if (dx * dx + dy * dy + dz * dz > epsilonSq)
                    {
                        continue;
                    }

                    sumNx += normals[b * 3];
                    sumNy += normals[b * 3 + 1];
                    sumNz += normals[b * 3 + 2];
                    count++;
                }

                // Check 26 neighbor buckets for boundary cases.
                for (var ox = -1; ox <= 1; ox++)
                for (var oy = -1; oy <= 1; oy++)
                for (var oz = -1; oz <= 1; oz++)
                {
                    if (ox == 0 && oy == 0 && oz == 0)
                    {
                        continue;
                    }

                    var neighborKey = (bucketKey.Item1 + ox, bucketKey.Item2 + oy, bucketKey.Item3 + oz);
                    if (!buckets.TryGetValue(neighborKey, out var neighborList))
                    {
                        continue;
                    }

                    foreach (var b in neighborList)
                    {
                        if (welded[b])
                        {
                            continue;
                        }

                        var dx = positions[b * 3] - ax;
                        var dy = positions[b * 3 + 1] - ay;
                        var dz = positions[b * 3 + 2] - az;
                        if (dx * dx + dy * dy + dz * dz > epsilonSq)
                        {
                            continue;
                        }

                        sumNx += normals[b * 3];
                        sumNy += normals[b * 3 + 1];
                        sumNz += normals[b * 3 + 2];
                        count++;
                    }
                }

                if (count <= 1)
                {
                    continue;
                }

                // Normalize the averaged normal.
                var len = MathF.Sqrt(sumNx * sumNx + sumNy * sumNy + sumNz * sumNz);
                if (len <= 1e-7f)
                {
                    continue;
                }

                var wnx = sumNx / len;
                var wny = sumNy / len;
                var wnz = sumNz / len;

                // Apply to all co-located vertices (re-scan to find them).
                for (var bi = ai; bi < indices.Count; bi++)
                {
                    var b = indices[bi];
                    if (bi != ai && welded[b])
                    {
                        continue;
                    }

                    var dx = positions[b * 3] - ax;
                    var dy = positions[b * 3 + 1] - ay;
                    var dz = positions[b * 3 + 2] - az;
                    if (bi != ai && dx * dx + dy * dy + dz * dz > epsilonSq)
                    {
                        continue;
                    }

                    normals[b * 3] = wnx;
                    normals[b * 3 + 1] = wny;
                    normals[b * 3 + 2] = wnz;
                    welded[b] = true;
                }

                // Apply to neighbor bucket vertices too.
                for (var ox = -1; ox <= 1; ox++)
                for (var oy = -1; oy <= 1; oy++)
                for (var oz = -1; oz <= 1; oz++)
                {
                    if (ox == 0 && oy == 0 && oz == 0)
                    {
                        continue;
                    }

                    var neighborKey = (bucketKey.Item1 + ox, bucketKey.Item2 + oy, bucketKey.Item3 + oz);
                    if (!buckets.TryGetValue(neighborKey, out var neighborList))
                    {
                        continue;
                    }

                    foreach (var b in neighborList)
                    {
                        if (welded[b])
                        {
                            continue;
                        }

                        var dx = positions[b * 3] - ax;
                        var dy = positions[b * 3 + 1] - ay;
                        var dz = positions[b * 3 + 2] - az;
                        if (dx * dx + dy * dy + dz * dz > epsilonSq)
                        {
                            continue;
                        }

                        normals[b * 3] = wnx;
                        normals[b * 3 + 1] = wny;
                        normals[b * 3 + 2] = wnz;
                        welded[b] = true;
                    }
                }
            }
        }
    }

    internal static void RecalculateBounds(NifRenderableModel model)
    {
        model.MinX = float.MaxValue;
        model.MinY = float.MaxValue;
        model.MinZ = float.MaxValue;
        model.MaxX = float.MinValue;
        model.MaxY = float.MinValue;
        model.MaxZ = float.MinValue;

        foreach (var sub in model.Submeshes)
        {
            model.ExpandBounds(sub.Positions);
        }
    }
}
