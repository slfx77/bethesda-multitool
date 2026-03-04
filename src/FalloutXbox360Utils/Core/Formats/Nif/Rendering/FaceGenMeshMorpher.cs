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
    /// </summary>
    /// <param name="model">Model whose vertex positions will be mutated in-place.</param>
    /// <param name="egm">Parsed EGM morph data.</param>
    /// <param name="symmetricCoeffs">50 symmetric morph coefficients (FGGS, merged NPC+race).</param>
    /// <param name="asymmetricCoeffs">30 asymmetric morph coefficients (FGGA, merged NPC+race).</param>
    public static void Apply(
        NifRenderableModel model,
        EgmParser egm,
        float[]? symmetricCoeffs,
        float[]? asymmetricCoeffs)
    {
        // Find the largest submesh (head mesh is typically the largest geometry block)
        var submesh = FindLargestSubmesh(model);
        if (submesh == null)
            return;

        var nifVertexCount = submesh.VertexCount;
        var egmVertexCount = egm.VertexCount;

        // Engine uses min(EGM, NIF) — confirmed by decompilation
        var vertexCount = Math.Min(nifVertexCount, egmVertexCount);

        // Apply symmetric morphs (50 bases)
        if (symmetricCoeffs != null)
        {
            var count = Math.Min(symmetricCoeffs.Length, egm.SymmetricMorphs.Length);
            for (var m = 0; m < count; m++)
            {
                var coeff = symmetricCoeffs[m];
                if (MathF.Abs(coeff) < 1e-7f)
                    continue;

                ApplyMorphDeltas(submesh.Positions, egm.SymmetricMorphs[m], coeff, vertexCount);
            }
        }

        // Apply asymmetric morphs (30 bases)
        if (asymmetricCoeffs != null)
        {
            var count = Math.Min(asymmetricCoeffs.Length, egm.AsymmetricMorphs.Length);
            for (var m = 0; m < count; m++)
            {
                var coeff = asymmetricCoeffs[m];
                if (MathF.Abs(coeff) < 1e-7f)
                    continue;

                ApplyMorphDeltas(submesh.Positions, egm.AsymmetricMorphs[m], coeff, vertexCount);
            }
        }

        // Recalculate smooth vertex normals from the morphed geometry.
        RecalculateNormals(submesh);

        // Recalculate model bounds after morphing
        RecalculateBounds(model);
    }

    private static void ApplyMorphDeltas(float[] positions, EgmMorph morph, float coefficient, int vertexCount)
    {
        var scale = morph.Scale * coefficient;
        var deltas = morph.Deltas;

        for (var v = 0; v < vertexCount; v++)
        {
            var pi = v * 3;
            positions[pi] += deltas[pi] * scale;
            positions[pi + 1] += deltas[pi + 1] * scale;
            positions[pi + 2] += deltas[pi + 2] * scale;
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
    private static void RecalculateNormals(RenderableSubmesh submesh)
    {
        var normals = submesh.Normals;
        if (normals == null)
            return;

        var positions = submesh.Positions;
        var triangles = submesh.Triangles;

        // Zero out normals
        Array.Clear(normals);

        // Accumulate area-weighted face normals to each vertex
        for (var t = 0; t < triangles.Length; t += 3)
        {
            var i0 = triangles[t] * 3;
            var i1 = triangles[t + 1] * 3;
            var i2 = triangles[t + 2] * 3;

            // Edge vectors
            var e1x = positions[i1] - positions[i0];
            var e1y = positions[i1 + 1] - positions[i0 + 1];
            var e1z = positions[i1 + 2] - positions[i0 + 2];
            var e2x = positions[i2] - positions[i0];
            var e2y = positions[i2 + 1] - positions[i0 + 1];
            var e2z = positions[i2 + 2] - positions[i0 + 2];

            // Cross product = area-weighted face normal
            var nx = e1y * e2z - e1z * e2y;
            var ny = e1z * e2x - e1x * e2z;
            var nz = e1x * e2y - e1y * e2x;

            // Accumulate to all 3 vertices
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

        // Normalize
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

    private static void RecalculateBounds(NifRenderableModel model)
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
