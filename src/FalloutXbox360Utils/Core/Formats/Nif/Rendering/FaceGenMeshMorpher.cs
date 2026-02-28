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
