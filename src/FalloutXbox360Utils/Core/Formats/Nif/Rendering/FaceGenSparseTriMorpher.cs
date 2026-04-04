namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Applies FaceGen TRI differential morphs (lip sync, expression) to renderable head geometry.
///     Unlike EGM morphs (which are dense per-vertex deltas), TRI differential records use
///     sparse int16 deltas scaled by a per-record float factor.
/// </summary>
internal static class FaceGenSparseTriMorpher
{
    /// <summary>
    ///     Apply weighted TRI differential morph deltas to all submeshes of a renderable model.
    ///     Each weight key maps to a named differential record in the TRI file (e.g., "Aah", "BigAah").
    ///     Returns true if any positions were modified.
    /// </summary>
    public static bool ApplyDifferentialWeights(
        NifRenderableModel model,
        TriParser tri,
        IReadOnlyDictionary<string, float> weights)
    {
        if (model.Submeshes.Count == 0 || weights.Count == 0)
        {
            return false;
        }

        var anyChanged = false;

        foreach (var (name, weight) in weights)
        {
            if (MathF.Abs(weight) < 1e-7f)
            {
                continue;
            }

            if (!tri.TryGetDifferentialRecord(name, out var record))
            {
                continue;
            }

            if (MathF.Abs(record.Scale) < 1e-7f)
            {
                continue;
            }

            var deltas = tri.ReadDifferentialRecordDeltas(record);
            if (deltas == null || deltas.Length == 0)
            {
                continue;
            }

            var scaledWeight = weight * record.Scale;

            foreach (var submesh in model.Submeshes)
            {
                var positions = submesh.Positions;
                var vertexCount = positions.Length / 3;
                var deltaVertexCount = deltas.Length / 3;
                var count = Math.Min(vertexCount, deltaVertexCount);

                for (var i = 0; i < count * 3; i++)
                {
                    positions[i] += deltas[i] * scaledWeight;
                }

                if (count > 0)
                {
                    anyChanged = true;
                }
            }
        }

        return anyChanged;
    }

    /// <summary>
    ///     Resolve differential morph weights for a static NPC (baked FaceGen).
    ///     Static NPCs use pre-baked geometry — no runtime lip sync or expression morphs —
    ///     so this returns an empty dictionary.
    /// </summary>
    public static IReadOnlyDictionary<string, float> ResolveStaticNpcWeights(
        NpcAppearance _npc,
        string _triNifPath)
    {
        return new Dictionary<string, float>();
    }
}
