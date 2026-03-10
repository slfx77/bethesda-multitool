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
