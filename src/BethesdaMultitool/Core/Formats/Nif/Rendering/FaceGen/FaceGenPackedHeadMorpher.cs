using BethesdaMultitool.Core.Formats.Nif.GeometryAnalysis;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;

/// <summary>
///     Applies FaceGen EGM morphs to packed partition-order head geometry by fanning mesh-order
///     deltas out onto every packed destination occurrence referenced by the vertex map.
/// </summary>
internal static class FaceGenPackedHeadMorpher
{
    /// <summary>
    ///     Morphs <paramref name="packedPositions" /> in place by applying the EGM symmetric/asymmetric
    ///     coefficient deltas to every packed vertex the topology's vertex map references.
    /// </summary>
    public static void Apply(
        float[] packedPositions,
        PackedTopologyData topology,
        EgmParser egm,
        float[]? symmetricCoeffs,
        float[]? asymmetricCoeffs)
    {
        var packedVertexCount = Math.Min(
            topology.PackedVertexCount,
            Math.Min(topology.VertexMap.Length, packedPositions.Length / 3));
        if (packedVertexCount <= 0)
        {
            return;
        }

        Accumulate(packedPositions, topology, egm.SymmetricMorphs, symmetricCoeffs, packedVertexCount);
        Accumulate(packedPositions, topology, egm.AsymmetricMorphs, asymmetricCoeffs, packedVertexCount);
    }

    private static void Accumulate(
        float[] packedPositions,
        PackedTopologyData topology,
        EgmMorph[] morphs,
        float[]? coefficients,
        int packedVertexCount)
    {
        if (coefficients == null || morphs.Length == 0)
        {
            return;
        }

        var count = Math.Min(coefficients.Length, morphs.Length);
        for (var morphIndex = 0; morphIndex < count; morphIndex++)
        {
            var coefficient = coefficients[morphIndex];
            if (MathF.Abs(coefficient) < 1e-7f)
            {
                continue;
            }

            var morph = morphs[morphIndex];
            var scale = morph.Scale * coefficient;
            if (MathF.Abs(scale) < 1e-7f)
            {
                continue;
            }

            var deltas = morph.Deltas;
            var meshVertexCount = Math.Min(topology.MeshVertexCount, deltas.Length / 3);
            for (var packedVertexIndex = 0; packedVertexIndex < packedVertexCount; packedVertexIndex++)
            {
                var meshVertexIndex = topology.VertexMap[packedVertexIndex];
                if (meshVertexIndex >= meshVertexCount)
                {
                    continue;
                }

                var packedOffset = packedVertexIndex * 3;
                var meshOffset = meshVertexIndex * 3;
                packedPositions[packedOffset + 0] += deltas[meshOffset + 0] * scale;
                packedPositions[packedOffset + 1] += deltas[meshOffset + 1] * scale;
                packedPositions[packedOffset + 2] += deltas[meshOffset + 2] * scale;
            }
        }
    }
}
